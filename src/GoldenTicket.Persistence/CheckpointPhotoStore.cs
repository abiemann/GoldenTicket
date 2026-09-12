using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Persistence;

/// <summary>Human confirmation accompanies an actual, fresh board-camera frame, never a screen capture.</summary>
public sealed record CheckpointPhotoCapture(
    DateTimeOffset CapturedAt,
    string CameraId,
    long CameraEpoch,
    long CalibrationRevision,
    bool OperatorConfirmedBoardOnlyAndTarget);

/// <summary>
/// An immutable, operator-attested reference beside a state-only checkpoint. It does not upgrade
/// the checkpoint to VerifiedBoardPhoto and is never evidence for accepting a game command.
/// </summary>
public sealed record CheckpointPhotoReference(
    int FormatVersion,
    string SessionId,
    string CheckpointId,
    string CheckpointContentHash,
    string LogicalStateHash,
    string PhysicalTargetHash,
    string ProfileId,
    string ManifestHash,
    long SourceStateVersion,
    long SourceJournalSequence,
    long BoardRevision,
    string ImageHash,
    int Width,
    int Height,
    int ByteLength,
    DateTimeOffset StoredAt,
    CheckpointPhotoCapture Capture)
{
    public const string DisplayLabel = "Operator-attested reference photo. Rebuild from the saved route list and check the whole board before resuming.";
}

public sealed record CheckpointPhotoAttachment(CheckpointPhotoReference Reference, byte[] PngBytes);

/// <summary>
/// Optional same-Windows-account reference images. This sidecar deliberately leaves the game
/// journal and legacy checkpoint schema untouched. Losing an image never destroys a digital save.
/// Each attachment is one immutable, authenticated envelope, atomically finalized and read back
/// before success. Its independent random data key is protected with current-user Windows DPAPI.
/// </summary>
public sealed class CheckpointPhotoStore
{
    public const int MaximumPngBytes = 32 * 1024 * 1024;
    public const int MaximumDimension = 8192;
    public const long MaximumPixels = 32_000_000;
    private const int MaximumMetadataBytes = 32 * 1024;
    private const int MaximumProtectedKeyBytes = 16 * 1024;
    private const int MaximumEnvelopeBytes = MaximumPngBytes + MaximumMetadataBytes + MaximumProtectedKeyBytes + 64;
    private const int FixedHeaderLength = 20;
    private static readonly byte[] Magic = "GTPHOTO1"u8.ToArray();
    private static readonly byte[] Entropy = "GoldenTicket.CheckpointPhoto.v1"u8.ToArray();
    private readonly string _root;
    private readonly TimeProvider _clock;

    public CheckpointPhotoStore(string rootDirectory, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = Path.GetFullPath(rootDirectory);
        _clock = clock ?? TimeProvider.System;
        RejectLinks(_root);
    }

    public static CheckpointPhotoStore CreateDefault() => new(SqliteSessionStore.DefaultRoot);

    /// <summary>Only opaque canonical IDs enter a path; the game name and camera name never do.</summary>
    public string AttachmentPath(SessionId sessionId, CheckpointId checkpointId)
    {
        ValidateId(sessionId.Value, nameof(sessionId));
        ValidateId(checkpointId.Value, nameof(checkpointId));
        var path = Path.GetFullPath(Path.Combine(_root, "sessions", sessionId.Value,
            "checkpoint-photos", checkpointId.Value + ".gtphoto"));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("The reference photo must remain inside the saved-game directory.");
        RejectLinks(path);
        return path;
    }

    public async Task<CheckpointPhotoReference> SaveReferenceAsync(
        PackAwayCheckpoint checkpoint,
        ReadOnlyMemory<byte> png,
        CheckpointPhotoCapture capture,
        CancellationToken cancellationToken = default)
    {
        ValidateCheckpoint(checkpoint);
        ArgumentNullException.ThrowIfNull(capture);
        ValidateCapture(capture, checkpoint, requireFresh: true);
        cancellationToken.ThrowIfCancellationRequested();
        // Copy before hashing/validation so a reused camera buffer cannot change underneath a save.
        if (png.Length is <= 0 or > MaximumPngBytes)
            throw new InvalidDataException("The board PNG must be between 1 byte and 32 MiB.");
        var image = png.ToArray();
        byte[]? plaintext = null;
        string? temporaryPath = null;
        try
        {
            var (width, height) = BoardPngValidator.Validate(image);
            var reference = new CheckpointPhotoReference(1, checkpoint.SessionId.Value,
                checkpoint.CheckpointId.Value, ContentHash(checkpoint), checkpoint.LogicalStateHash,
                checkpoint.PhysicalTargetHash, checkpoint.ProfileId, checkpoint.ManifestHash,
                checkpoint.SourceStateVersion, checkpoint.SourceJournalSequence, checkpoint.BoardRevision,
                Hash(image), width, height, image.Length, _clock.GetUtcNow(), capture);
            var path = AttachmentPath(checkpoint.SessionId, checkpoint.CheckpointId);
            if (File.Exists(path))
                return await ReadMatchingExistingAsync(checkpoint, reference, cancellationToken);

            var metadata = JsonSerializer.SerializeToUtf8Bytes(reference);
            if (metadata.Length > MaximumMetadataBytes)
                throw new InvalidDataException("The photo metadata exceeds its storage limit.");
            plaintext = new byte[4 + metadata.Length + image.Length];
            BinaryPrimitives.WriteInt32LittleEndian(plaintext, metadata.Length);
            metadata.CopyTo(plaintext, 4);
            image.CopyTo(plaintext, 4 + metadata.Length);
            var envelope = Encrypt(plaintext);
            var directory = Path.GetDirectoryName(path)!;
            RejectLinks(directory);
            Directory.CreateDirectory(directory);
            RejectLinks(directory);
            temporaryPath = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".pending");
            RejectLinks(temporaryPath);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(envelope, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(temporaryPath);
            RejectLinks(path);
            try
            {
                File.Move(temporaryPath, path, overwrite: false);
                temporaryPath = null;
            }
            catch (IOException) when (File.Exists(path))
            {
                // Two callers racing the same checkpoint still cannot replace its first image.
                return await ReadMatchingExistingAsync(checkpoint, reference, cancellationToken);
            }
            var readback = await ReadReferenceAsync(checkpoint, cancellationToken)
                ?? throw new IOException("The saved reference photo disappeared during readback.");
            try
            {
                if (readback.Reference != reference || !readback.PngBytes.AsSpan().SequenceEqual(image))
                    throw new InvalidDataException("The saved reference photo did not match its readback.");
                return readback.Reference;
            }
            finally { CryptographicOperations.ZeroMemory(readback.PngBytes); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(image);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (temporaryPath is not null)
            {
                // Only our uniquely named pending file is removable. A finalized image is retained.
                RejectLinks(temporaryPath);
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>Missing references are normal for legacy saves; corrupt or mismatched ones fail closed.</summary>
    public async Task<CheckpointPhotoAttachment?> ReadReferenceAsync(
        PackAwayCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ValidateCheckpoint(checkpoint);
        var path = AttachmentPath(checkpoint.SessionId, checkpoint.CheckpointId);
        if (!File.Exists(path)) return null;
        byte[] envelope;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                         64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length is <= FixedHeaderLength or > MaximumEnvelopeBytes)
                throw new InvalidDataException("The stored reference photo has an invalid size.");
            envelope = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(envelope, cancellationToken);
        }
        var plaintext = Decrypt(envelope);
        try
        {
            if (plaintext.Length < 4) throw new InvalidDataException("Photo metadata is missing.");
            var metadataLength = BinaryPrimitives.ReadInt32LittleEndian(plaintext);
            if (metadataLength is <= 0 or > MaximumMetadataBytes || metadataLength > plaintext.Length - 4)
                throw new InvalidDataException("Photo metadata has an invalid size.");
            CheckpointPhotoReference reference;
            try
            {
                reference = JsonSerializer.Deserialize<CheckpointPhotoReference>(plaintext.AsSpan(4, metadataLength))
                    ?? throw new InvalidDataException("Photo metadata is missing.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("The reference photo metadata is damaged.", ex);
            }
            ValidateAssociation(reference, checkpoint);
            var image = plaintext.AsSpan(4 + metadataLength);
            if (image.Length != reference.ByteLength || Hash(image) != reference.ImageHash)
                throw new InvalidDataException("The reference photo checksum or byte length is incorrect.");
            var (width, height) = BoardPngValidator.Validate(image);
            if (width != reference.Width || height != reference.Height)
                throw new InvalidDataException("The reference photo dimensions do not match its metadata.");
            return new CheckpointPhotoAttachment(reference, image.ToArray());
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private async Task<CheckpointPhotoReference> ReadMatchingExistingAsync(PackAwayCheckpoint checkpoint,
        CheckpointPhotoReference requested, CancellationToken cancellationToken)
    {
        var existing = await ReadReferenceAsync(checkpoint, cancellationToken)
            ?? throw new IOException("The saved reference photo disappeared during readback.");
        try
        {
            if (existing.Reference.ImageHash != requested.ImageHash || existing.Reference.Capture != requested.Capture)
                throw new InvalidOperationException("This checkpoint already has its immutable reference photo. Save a new checkpoint for a different photo.");
            return existing.Reference;
        }
        finally { CryptographicOperations.ZeroMemory(existing.PngBytes); }
    }

    private void ValidateAssociation(CheckpointPhotoReference reference, PackAwayCheckpoint checkpoint)
    {
        if (reference.FormatVersion != 1 || reference.SessionId != checkpoint.SessionId.Value ||
            reference.CheckpointId != checkpoint.CheckpointId.Value ||
            reference.CheckpointContentHash != ContentHash(checkpoint) ||
            reference.LogicalStateHash != checkpoint.LogicalStateHash ||
            reference.PhysicalTargetHash != checkpoint.PhysicalTargetHash ||
            reference.ProfileId != checkpoint.ProfileId || reference.ManifestHash != checkpoint.ManifestHash ||
            reference.SourceStateVersion != checkpoint.SourceStateVersion ||
            reference.SourceJournalSequence != checkpoint.SourceJournalSequence ||
            reference.BoardRevision != checkpoint.BoardRevision || reference.Capture is null ||
            reference.StoredAt < reference.Capture.CapturedAt - TimeSpan.FromSeconds(5))
            throw new InvalidDataException("The reference photo does not belong to this exact saved checkpoint.");
        ValidateCapture(reference.Capture, checkpoint, requireFresh: false);
    }

    private void ValidateCapture(CheckpointPhotoCapture capture, PackAwayCheckpoint checkpoint, bool requireFresh)
    {
        if (!capture.OperatorConfirmedBoardOnlyAndTarget)
            throw new InvalidDataException("Confirm the photo shows only the board and matches every committed saved route.");
        if (string.IsNullOrWhiteSpace(capture.CameraId) || capture.CameraId.Length > 1024 ||
            capture.CameraId.Any(char.IsControl) || capture.CameraEpoch < 0 || capture.CalibrationRevision < 0 ||
            capture.CapturedAt < checkpoint.CreatedAt)
            throw new InvalidDataException("The capture metadata must identify a frame captured after this checkpoint was frozen.");
        var now = _clock.GetUtcNow();
        if (requireFresh && (capture.CapturedAt > now + TimeSpan.FromSeconds(5) ||
                            now - capture.CapturedAt > TimeSpan.FromMinutes(2)))
            throw new InvalidDataException("Capture a fresh board photo before attaching it to the checkpoint.");
    }

    private static void ValidateCheckpoint(PackAwayCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ValidateId(checkpoint.SessionId.Value, nameof(checkpoint.SessionId));
        ValidateId(checkpoint.CheckpointId.Value, nameof(checkpoint.CheckpointId));
        if (checkpoint.FormatVersion != PackAwayCheckpoint.CurrentFormatVersion ||
            checkpoint.Status != CheckpointStatus.Verified ||
            checkpoint.TargetProvenance != TargetProvenance.LogicalStateOnly || checkpoint.PhotoHash is not null ||
            checkpoint.PhysicalTarget.IsDefault ||
            PackAwayCheckpoint.HashTarget(checkpoint.PhysicalTarget) != checkpoint.PhysicalTargetHash)
            throw new InvalidDataException("A reference photo requires a verified state-only checkpoint with an intact physical target.");
    }

    private static string ContentHash(PackAwayCheckpoint checkpoint) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(checkpoint with { Status = CheckpointStatus.Verified }));
    private static string Hash(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void ValidateId(string value, string name)
    {
        if (!Guid.TryParseExact(value, "N", out var id) || id.ToString("N") != value)
            throw new ArgumentException("A saved-game identifier must be a lowercase, 32-digit GUID.", name);
    }

    private static void RejectLinks(string path)
    {
        // Inspect every existing ancestor as well as the final file, including the configured root.
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Reference photo storage cannot use a symbolic link or directory junction.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static byte[] Encrypt(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Reference photos use Windows current-user DPAPI.");
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var protectedKey = ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);
            var headerLength = FixedHeaderLength + protectedKey.Length + 12;
            var result = new byte[headerLength + 16 + plaintext.Length];
            Magic.CopyTo(result, 0);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), 1);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12), protectedKey.Length);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16), plaintext.Length);
            protectedKey.CopyTo(result, FixedHeaderLength);
            RandomNumberGenerator.Fill(result.AsSpan(headerLength - 12, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(result.AsSpan(headerLength - 12, 12), plaintext,
                result.AsSpan(headerLength + 16), result.AsSpan(headerLength, 16), result.AsSpan(0, headerLength));
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] Decrypt(byte[] envelope)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Reference photos use Windows current-user DPAPI.");
        if (envelope.Length < FixedHeaderLength || !envelope.AsSpan(0, 8).SequenceEqual(Magic) ||
            BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(8)) != 1)
            throw new InvalidDataException("The reference photo format is unsupported.");
        var keyLength = BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(12));
        var plaintextLength = BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(16));
        if (keyLength is <= 0 or > MaximumProtectedKeyBytes || plaintextLength is <= 4 or > MaximumPngBytes + MaximumMetadataBytes + 4 ||
            (long)FixedHeaderLength + keyLength + 12 + 16 + plaintextLength != envelope.Length)
            throw new InvalidDataException("The reference photo envelope is truncated or has invalid bounds.");
        var headerLength = FixedHeaderLength + keyLength + 12;
        var key = ProtectedData.Unprotect(envelope.AsSpan(FixedHeaderLength, keyLength).ToArray(), Entropy, DataProtectionScope.CurrentUser);
        var plaintext = new byte[plaintextLength];
        try
        {
            if (key.Length != 32) throw new CryptographicException("The reference photo key has an invalid length.");
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(envelope.AsSpan(headerLength - 12, 12), envelope.AsSpan(headerLength + 16),
                envelope.AsSpan(headerLength, 16), plaintext, envelope.AsSpan(0, headerLength));
            return plaintext;
        }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
