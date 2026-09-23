using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Model;
using GoldenTicket.Persistence;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

/// <summary>Readable, checksummed photo attachments and full PNG validation; no train-recognition claim.</summary>
public sealed class PhotoAttachmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GoldenTicket.PhotoTests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        // The fixture uses only its own fixed temporary root; no caller path is accepted here.
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static PackAwayCheckpoint Checkpoint()
    {
        ImmutableArray<TargetRoute> target = [new(new RouteId("test-route"), new SeatId(1), 3)];
        return new PackAwayCheckpoint(CheckpointId.New(), SessionId.New(), "Evening checkpoint",
            DateTimeOffset.UtcNow.AddMinutes(-1), PackAwayCheckpoint.CurrentFormatVersion, 12, 9, 4,
            "ttr-us-classic-en-v1", "sha256:manifest", "logical-v2:test", TurnPhase.TurnStart,
            null, target, PackAwayCheckpoint.HashTarget(target), TargetProvenance.LogicalStateOnly,
            null, CheckpointStatus.Verified);
    }

    private static CheckpointPhotoCapture Capture() => new(DateTimeOffset.UtcNow, "Board camera", 7, 2, true);

    [Fact]
    public async Task ReferenceSurvivesReopenWithoutChangingAuthoritativeCheckpointTruth()
    {
        var checkpoint = Checkpoint();
        var original = checkpoint;
        var png = WpfPng();
        var capture = Capture();
        var store = new CheckpointPhotoStore(_root);
        var receipt = await store.SaveReferenceAsync(checkpoint, png, capture, Token);
        var restored = await new CheckpointPhotoStore(_root).ReadReferenceAsync(checkpoint, Token);
        Assert.NotNull(restored);
        Assert.Equal(receipt, restored.Reference);
        Assert.Equal(png, restored.PngBytes);
        Assert.Equal(16, receipt.Width);
        Assert.Equal(8, receipt.Height);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(png)), receipt.ImageHash);
        Assert.Equal(original, checkpoint);
        Assert.Null(checkpoint.PhotoHash);
        Assert.Equal(TargetProvenance.LogicalStateOnly, checkpoint.TargetProvenance);
        Assert.Null(restored.Reference.ObservedTrainInventory);
        Assert.Null(restored.Reference.PendingPlacement);
        var envelope = await File.ReadAllBytesAsync(store.AttachmentPath(checkpoint.SessionId, checkpoint.CheckpointId), Token);
        Assert.True(envelope.AsSpan(0, 8).SequenceEqual("GTPHOTO1"u8));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(8)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(12)));
        var plaintext = envelope.AsSpan(52);
        Assert.Equal(plaintext.Length, BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(16)));
        Assert.True(envelope.AsSpan(20, 32).SequenceEqual(SHA256.HashData(plaintext)));
        var metadataLength = BinaryPrimitives.ReadInt32LittleEndian(plaintext);
        var metadata = Encoding.UTF8.GetString(plaintext.Slice(4, metadataLength));
        Assert.Contains(checkpoint.LogicalStateHash, metadata, StringComparison.Ordinal);
        Assert.Contains(capture.CameraId, metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("ObservedTrainInventory", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("PendingPlacement", metadata, StringComparison.Ordinal);
        Assert.True(plaintext[(4 + metadataLength)..].SequenceEqual(png));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.pending", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ObservedTrainColorsRoundTripBesideThePhotoWithoutChangingTheCheckpoint()
    {
        var checkpoint = Checkpoint();
        var inventory = new CheckpointTrainInventory(1, 0, 2, 0, 0,
            CheckpointTrainInventoryProvenance.CameraObserved);
        var store = new CheckpointPhotoStore(_root);
        var png = WpfPng();
        var capture = Capture();

        var saved = await store.SaveReferenceAsync(checkpoint, png, capture, Token, inventory);
        var restored = await new CheckpointPhotoStore(_root).ReadReferenceAsync(checkpoint, Token);

        Assert.Equal(inventory, saved.ObservedTrainInventory);
        Assert.Equal(inventory, restored!.Reference.ObservedTrainInventory);
        Assert.Equal(3, inventory.Total);
        Assert.Equal(saved, await store.SaveReferenceAsync(checkpoint, png, capture, Token, inventory));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveReferenceAsync(checkpoint, png, capture, Token,
                new CheckpointTrainInventory(0, 0, 3, 0, 0,
                    CheckpointTrainInventoryProvenance.CameraObserved)));
        Assert.Null(checkpoint.PhotoHash);
        Assert.Equal(TargetProvenance.LogicalStateOnly, checkpoint.TargetProvenance);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(5, 2)]
    [InlineData(7, 3)]
    public async Task PendingPlacementSlotsRoundTripWithoutClaimingTheRoute(int mask, int count)
    {
        var operation = OperationId.New();
        var checkpoint = Checkpoint() with
        {
            SuspendedTurnPhase = TurnPhase.AwaitingPhysicalPlacement,
            PendingOperationId = operation,
        };
        var pending = new CheckpointPendingPlacement(operation, new RouteId("pending-route"),
            new SeatId(2), PlayerColor.Blue, 3, mask);
        var inventory = new CheckpointTrainInventory(count, 0, 3, 0, 0,
            CheckpointTrainInventoryProvenance.CameraObserved);
        var capture = Capture();
        var png = WpfPng();
        var store = new CheckpointPhotoStore(_root);

        var saved = await store.SaveReferenceAsync(checkpoint, png, capture, Token, inventory, pending);
        var restored = await new CheckpointPhotoStore(_root).ReadReferenceAsync(checkpoint, Token);

        Assert.Equal(saved, restored!.Reference);
        Assert.Equal(pending, restored.Reference.PendingPlacement);
        Assert.Equal(count, restored.Reference.PendingPlacement!.TrainCount);
        Assert.Equal(3, checkpoint.TotalTrainsOnBoard);
        Assert.Equal(3 + count, restored.Reference.ObservedTrainInventory!.Total);
        Assert.DoesNotContain(checkpoint.PhysicalTarget, route => route.RouteId == pending.RouteId);
        Assert.Equal(saved, await store.SaveReferenceAsync(checkpoint, png, capture, Token, inventory, pending));
        if (mask == 1)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.SaveReferenceAsync(checkpoint, png, capture, Token, inventory,
                    pending with { OccupiedSlotMask = 2 }));
    }

    [Fact]
    public async Task NewPendingPhotoRequiresObservedInventoryAndExactSlots()
    {
        var operation = OperationId.New();
        var checkpoint = Checkpoint() with
        {
            SuspendedTurnPhase = TurnPhase.AwaitingPhysicalPlacement,
            PendingOperationId = operation,
        };
        var pending = new CheckpointPendingPlacement(operation, new RouteId("pending-route"),
            new SeatId(2), PlayerColor.Blue, 3, 1);
        var inventory = new CheckpointTrainInventory(1, 0, 3, 0, 0,
            CheckpointTrainInventoryProvenance.CameraObserved);
        var store = new CheckpointPhotoStore(_root);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveReferenceAsync(checkpoint, WpfPng(), Capture(), Token, inventory));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveReferenceAsync(checkpoint, WpfPng(), Capture(), Token,
                pendingPlacement: pending));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveReferenceAsync(checkpoint, WpfPng(), Capture(), Token,
                inventory with { Provenance = CheckpointTrainInventoryProvenance.OperatorAttested }, pending));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task NewPendingPhotoRejectsMissingEvidenceOnReadButVersionOneRemainsReadable()
    {
        var operation = OperationId.New();
        var checkpoint = Checkpoint() with
        {
            SuspendedTurnPhase = TurnPhase.AwaitingPhysicalPlacement,
            PendingOperationId = operation,
        };
        var pending = new CheckpointPendingPlacement(operation, new RouteId("pending-route"),
            new SeatId(2), PlayerColor.Blue, 3, 1);
        var inventory = new CheckpointTrainInventory(1, 0, 3, 0, 0,
            CheckpointTrainInventoryProvenance.CameraObserved);
        var store = new CheckpointPhotoStore(_root);
        var receipt = await store.SaveReferenceAsync(checkpoint, WpfPng(), Capture(), Token,
            inventory, pending);
        Assert.Equal(2, receipt.FormatVersion);
        var path = store.AttachmentPath(checkpoint.SessionId, checkpoint.CheckpointId);
        var original = await File.ReadAllBytesAsync(path, Token);

        await RewriteWithoutPendingEvidenceAsync(path, original, 2);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadReferenceAsync(checkpoint, Token));

        await RewriteWithoutPendingEvidenceAsync(path, original, 1);
        var legacy = await store.ReadReferenceAsync(checkpoint, Token);
        Assert.NotNull(legacy);
        Assert.Equal(1, legacy.Reference.FormatVersion);
        Assert.Null(legacy.Reference.PendingPlacement);
        Assert.Null(legacy.Reference.ObservedTrainInventory);
    }

    private static async Task RewriteWithoutPendingEvidenceAsync(string path, byte[] original,
        int formatVersion)
    {
        var originalPlaintext = original.AsSpan(52);
        var metadataLength = BinaryPrimitives.ReadInt32LittleEndian(originalPlaintext);
        var metadata = JsonNode.Parse(Encoding.UTF8.GetString(
            originalPlaintext.Slice(4, metadataLength)))!.AsObject();
        metadata["FormatVersion"] = formatVersion;
        metadata.Remove("ObservedTrainInventory");
        metadata.Remove("PendingPlacement");
        var json = Encoding.UTF8.GetBytes(metadata.ToJsonString());
        var image = originalPlaintext[(4 + metadataLength)..];
        var plaintext = new byte[4 + json.Length + image.Length];
        BinaryPrimitives.WriteInt32LittleEndian(plaintext, json.Length);
        json.CopyTo(plaintext.AsSpan(4));
        image.CopyTo(plaintext.AsSpan(4 + json.Length));
        var envelope = new byte[52 + plaintext.Length];
        original.AsSpan(0, 52).CopyTo(envelope);
        BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(16), plaintext.Length);
        SHA256.HashData(plaintext).CopyTo(envelope.AsSpan(20, 32));
        plaintext.CopyTo(envelope.AsSpan(52));
        await File.WriteAllBytesAsync(path, envelope, Token);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("phase")]
    [InlineData("claimed-route")]
    [InlineData("seat")]
    [InlineData("color")]
    [InlineData("length")]
    [InlineData("negative-mask")]
    [InlineData("extra-slot")]
    [InlineData("total")]
    [InlineData("color-total")]
    public async Task PendingPlacementMustMatchTheCheckpointAndItsObservedInventory(string mismatch)
    {
        var operation = OperationId.New();
        var checkpoint = Checkpoint() with
        {
            SuspendedTurnPhase = TurnPhase.AwaitingPhysicalPlacement,
            PendingOperationId = operation,
        };
        var pending = new CheckpointPendingPlacement(operation, new RouteId("pending-route"),
            new SeatId(2), PlayerColor.Blue, 3, 1);
        var inventory = new CheckpointTrainInventory(1, 0, 3, 0, 0,
            CheckpointTrainInventoryProvenance.CameraObserved);
        pending = mismatch switch
        {
            "operation" => pending with { OperationId = OperationId.New() },
            "claimed-route" => pending with { RouteId = checkpoint.PhysicalTarget[0].RouteId },
            "seat" => pending with { SeatId = new SeatId(0) },
            "color" => pending with { Color = (PlayerColor)99 },
            "length" => pending with { RouteLength = 31 },
            "negative-mask" => pending with { OccupiedSlotMask = -1 },
            "extra-slot" => pending with { OccupiedSlotMask = 8 },
            _ => pending,
        };
        if (mismatch == "phase") checkpoint = checkpoint with { SuspendedTurnPhase = TurnPhase.TurnStart };
        if (mismatch == "total") inventory = inventory with { Blue = 0 };
        if (mismatch == "color-total") inventory = inventory with { Blue = 0, Green = 4 };

        await Assert.ThrowsAsync<InvalidDataException>(() => new CheckpointPhotoStore(_root)
            .SaveReferenceAsync(checkpoint, WpfPng(), Capture(), Token, inventory, pending));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void PendingPlacementMustMatchTheRestoredPublicOperationBeforeUse()
    {
        var pending = new CheckpointPendingPlacement(OperationId.New(), new RouteId("pending-route"),
            new SeatId(2), PlayerColor.Blue, 3, 5);
        var operation = new GoldenTicket.Domain.Projections.PublicPendingClaim(
            pending.OperationId, pending.SeatId, pending.RouteId, pending.RouteLength, false);

        Assert.True(pending.Matches(operation, PlayerColor.Blue));
        Assert.False(pending.Matches(null, PlayerColor.Blue));
        Assert.False(pending.Matches(operation with { OperationId = OperationId.New() }, PlayerColor.Blue));
        Assert.False(pending.Matches(operation with { RouteId = new RouteId("other") }, PlayerColor.Blue));
        Assert.False(pending.Matches(operation with { SeatId = new SeatId(1) }, PlayerColor.Blue));
        Assert.False(pending.Matches(operation with { TrainCount = 2 }, PlayerColor.Blue));
        Assert.False(pending.Matches(operation with { AwaitingRestore = true }, PlayerColor.Blue));
        Assert.False(pending.Matches(operation, PlayerColor.Red));
    }

    [Fact]
    public async Task PendingSlotMaskIsValidatedOnReadDespiteARecomputedEnvelopeChecksum()
    {
        var operation = OperationId.New();
        var checkpoint = Checkpoint() with
        {
            SuspendedTurnPhase = TurnPhase.AwaitingPhysicalPlacement,
            PendingOperationId = operation,
        };
        var pending = new CheckpointPendingPlacement(operation, new RouteId("pending-route"),
            new SeatId(2), PlayerColor.Blue, 3, 1);
        var store = new CheckpointPhotoStore(_root);
        await store.SaveReferenceAsync(checkpoint, WpfPng(), Capture(), Token,
            new CheckpointTrainInventory(1, 0, 3, 0, 0, CheckpointTrainInventoryProvenance.CameraObserved), pending);
        var path = store.AttachmentPath(checkpoint.SessionId, checkpoint.CheckpointId);
        var envelope = await File.ReadAllBytesAsync(path, Token);
        var plaintext = envelope.AsSpan(52);
        var metadataLength = BinaryPrimitives.ReadInt32LittleEndian(plaintext);
        var metadata = plaintext.Slice(4, metadataLength);
        var mask = metadata.IndexOf("\"OccupiedSlotMask\":1"u8);
        Assert.True(mask >= 0);
        metadata[mask + "\"OccupiedSlotMask\":"u8.Length] = (byte)'8';
        SHA256.HashData(plaintext).CopyTo(envelope.AsSpan(20, 32));
        await File.WriteAllBytesAsync(path, envelope, Token);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadReferenceAsync(checkpoint, Token));
    }

    [Theory]
    [InlineData(-1, 4, 0, 0, 0, 1)]
    [InlineData(1, 0, 1, 0, 0, 1)]
    [InlineData(3, 0, 0, 0, 0, 0)]
    public async Task InvalidObservedInventoryCannotBeSaved(
        int blue, int red, int green, int yellow, int black, int provenance)
    {
        var inventory = new CheckpointTrainInventory(blue, red, green, yellow, black,
            (CheckpointTrainInventoryProvenance)provenance);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CheckpointPhotoStore(_root).SaveReferenceAsync(Checkpoint(), WpfPng(), Capture(), Token, inventory));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task ObservedInventoryIsValidatedWhenReadingAnOtherwiseChecksummedAttachment()
    {
        var checkpoint = Checkpoint();
        var store = new CheckpointPhotoStore(_root);
        await store.SaveReferenceAsync(checkpoint, WpfPng(), Capture(), Token,
            new CheckpointTrainInventory(3, 0, 0, 0, 0,
                CheckpointTrainInventoryProvenance.CameraObserved));
        var path = store.AttachmentPath(checkpoint.SessionId, checkpoint.CheckpointId);
        var envelope = await File.ReadAllBytesAsync(path, Token);
        var plaintext = envelope.AsSpan(52);
        var metadataLength = BinaryPrimitives.ReadInt32LittleEndian(plaintext);
        var metadata = plaintext.Slice(4, metadataLength);
        var blueCount = metadata.IndexOf("\"Blue\":3"u8);
        Assert.True(blueCount >= 0);
        metadata[blueCount + "\"Blue\":"u8.Length] = (byte)'2';
        SHA256.HashData(plaintext).CopyTo(envelope.AsSpan(20, 32));
        await File.WriteAllBytesAsync(path, envelope, Token);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadReferenceAsync(checkpoint, Token));
    }

    [Fact]
    public async Task CheckpointWithoutPhotoDoesNotCreateDirectories()
    {
        Assert.Null(await new CheckpointPhotoStore(_root).ReadReferenceAsync(Checkpoint(), Token));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task VersionOnePhotoFormatIsRejected()
    {
        var checkpoint = Checkpoint();
        var store = new CheckpointPhotoStore(_root);
        await store.SaveReferenceAsync(checkpoint, WpfPng(), Capture(), Token);
        var path = store.AttachmentPath(checkpoint.SessionId, checkpoint.CheckpointId);
        var envelope = await File.ReadAllBytesAsync(path, Token);
        BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(8), 1);
        await File.WriteAllBytesAsync(path, envelope, Token);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadReferenceAsync(checkpoint, Token));
    }

    [Fact]
    public async Task DesktopBgraCameraEncodingIsAcceptedAndRoundTrips()
    {
        var pixels = Enumerable.Range(0, 32 * 16 * 4).Select(i => (byte)(i % 251)).ToArray();
        var bitmap = BitmapSource.Create(32, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var checkpoint = Checkpoint();
        var store = new CheckpointPhotoStore(_root);
        await store.SaveReferenceAsync(checkpoint, stream.ToArray(), Capture(), Token);
        Assert.Equal(stream.ToArray(), (await store.ReadReferenceAsync(checkpoint, Token))!.PngBytes);
    }

    [Fact]
    public async Task ActualWinRtCameraPngEncoderIsAcceptedByReferenceStore()
    {
        var checkpoint = Checkpoint();
        var pixels = Enumerable.Range(0, 32 * 16 * 4).Select(i => (byte)(i % 251)).ToArray();
        var frame = CameraFrame.CopyFromBgra32(32, 16, pixels, sequence: 19, epoch: 4);
        var png = await frame.EncodePngAsync(Token);
        var store = new CheckpointPhotoStore(_root);
        await store.SaveReferenceAsync(checkpoint, png,
            new CheckpointPhotoCapture(frame.CapturedAt, "camera-driver", frame.Epoch, 3, true), Token);
        Assert.Equal(png, (await store.ReadReferenceAsync(checkpoint, Token))!.PngBytes);
    }

    [Fact]
    public async Task RetryIsIdempotentButCannotReplaceThePhoto()
    {
        var checkpoint = Checkpoint();
        var store = new CheckpointPhotoStore(_root);
        var png = WpfPng();
        var capture = Capture();
        var first = await store.SaveReferenceAsync(checkpoint, png, capture, Token);
        Assert.Equal(first, await store.SaveReferenceAsync(checkpoint, png, capture, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveReferenceAsync(checkpoint, WpfPng(60), capture, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveReferenceAsync(checkpoint, png, capture with { CameraEpoch = 8 }, Token));
        Assert.Equal(png, (await store.ReadReferenceAsync(checkpoint, Token))!.PngBytes);
    }

    [Fact]
    public async Task ConcurrentRetriesFinalizeOneImmutableVerifiedAttachment()
    {
        var checkpoint = Checkpoint();
        var capture = Capture();
        var png = WpfPng();
        var first = new CheckpointPhotoStore(_root);
        var second = new CheckpointPhotoStore(_root);
        var results = await Task.WhenAll(first.SaveReferenceAsync(checkpoint, png, capture, Token),
            second.SaveReferenceAsync(checkpoint, png, capture, Token));
        Assert.Equal(results[0], results[1]);
        Assert.Single(Directory.EnumerateFiles(_root, "*.gtphoto", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.pending", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("logical")]
    [InlineData("target")]
    [InlineData("version")]
    [InlineData("journal")]
    [InlineData("board")]
    [InlineData("profile")]
    [InlineData("manifest")]
    [InlineData("operation")]
    public async Task AssociationPinsEveryImmutableCheckpointField(string change)
    {
        var checkpoint = Checkpoint();
        var store = new CheckpointPhotoStore(_root);
        await store.SaveReferenceAsync(checkpoint, WpfPng(), Capture(), Token);
        var newTarget = checkpoint.PhysicalTarget.Add(new TargetRoute(new RouteId("another"), new SeatId(2), 2));
        var changed = change switch
        {
            "name" => checkpoint with { Name = "A different checkpoint name" },
            "logical" => checkpoint with { LogicalStateHash = "logical-v2:changed" },
            "target" => checkpoint with { PhysicalTarget = newTarget, PhysicalTargetHash = PackAwayCheckpoint.HashTarget(newTarget) },
            "version" => checkpoint with { SourceStateVersion = 13 },
            "journal" => checkpoint with { SourceJournalSequence = 10 },
            "board" => checkpoint with { BoardRevision = 5 },
            "profile" => checkpoint with { ProfileId = "another-profile" },
            "manifest" => checkpoint with { ManifestHash = "sha256:another-manifest" },
            _ => checkpoint with { PendingOperationId = OperationId.New() }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadReferenceAsync(changed, Token));
    }

    [Fact]
    public async Task CopyingAttachmentToAnotherCheckpointOrSessionCannotRebindIt()
    {
        var checkpoint = Checkpoint();
        var store = new CheckpointPhotoStore(_root);
        await store.SaveReferenceAsync(checkpoint, WpfPng(), Capture(), Token);
        var originalPath = store.AttachmentPath(checkpoint.SessionId, checkpoint.CheckpointId);
        foreach (var other in new[] { checkpoint with { CheckpointId = CheckpointId.New() }, checkpoint with { SessionId = SessionId.New() } })
        {
            var path = store.AttachmentPath(other.SessionId, other.CheckpointId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(originalPath, path);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadReferenceAsync(other, Token));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(-1)]
    public async Task HeaderChecksumAndPayloadCorruptionAreRejected(int offset)
    {
        var checkpoint = Checkpoint();
        var store = new CheckpointPhotoStore(_root);
        await store.SaveReferenceAsync(checkpoint, WpfPng(), Capture(), Token);
        var path = store.AttachmentPath(checkpoint.SessionId, checkpoint.CheckpointId);
        var bytes = await File.ReadAllBytesAsync(path, Token);
        bytes[offset < 0 ? bytes.Length - 1 : offset] ^= 0x40;
        await File.WriteAllBytesAsync(path, bytes, Token);
        var exception = await Record.ExceptionAsync(() => store.ReadReferenceAsync(checkpoint, Token));
        Assert.IsType<InvalidDataException>(exception);
    }

    [Fact]
    public async Task TruncatedOrOversizedAttachmentIsRejectedBeforeDecoding()
    {
        var checkpoint = Checkpoint();
        var store = new CheckpointPhotoStore(_root);
        var path = store.AttachmentPath(checkpoint.SessionId, checkpoint.CheckpointId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, "GTPHOTO1"u8.ToArray(), Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadReferenceAsync(checkpoint, Token));
        await using (var stream = File.OpenWrite(path)) stream.SetLength(CheckpointPhotoStore.MaximumPngBytes + 1024 * 1024);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadReferenceAsync(checkpoint, Token));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:\\outside")]
    [InlineData("1111111111111111111111111111111A")]
    [InlineData("11111111-1111-1111-1111-111111111111")]
    [InlineData("")]
    public void CallerPathsAndNonCanonicalIdsNeverEnterTheFilesystem(string id)
    {
        var store = new CheckpointPhotoStore(_root);
        Assert.Throws<ArgumentException>(() => store.AttachmentPath(new SessionId(id), CheckpointId.New()));
        Assert.Throws<ArgumentException>(() => store.AttachmentPath(SessionId.New(), new CheckpointId(id)));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void DirectoryJunctionsAreRejectedAtConfiguredRootAndSessionPath()
    {
        var sessionId = SessionId.New();
        var target = Path.Combine(_root, "junction-target");
        var sessions = Path.Combine(_root, "sessions");
        var junction = Path.Combine(sessions, sessionId.Value);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(sessions);
        // Junction creation does not require symbolic-link privilege. Both paths are generated
        // inside this fixture, and cleanup removes only the link itself without following it.
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", junction, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(10_000), "Creating a temporary test junction timed out.");
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        try
        {
            Assert.Throws<IOException>(() => new CheckpointPhotoStore(junction));
            Assert.Throws<IOException>(() => new CheckpointPhotoStore(Path.Combine(junction, "child")));
            Assert.Throws<IOException>(() => new CheckpointPhotoStore(_root).AttachmentPath(sessionId, CheckpointId.New()));
        }
        finally { Directory.Delete(junction); }
        Assert.True(Directory.Exists(target));
    }

    [Theory]
    [InlineData("unconfirmed")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("before-checkpoint")]
    [InlineData("epoch")]
    [InlineData("camera")]
    public async Task CaptureMustBeFreshIdentifiedAndExplicitlyAttested(string change)
    {
        var checkpoint = Checkpoint();
        var capture = Capture();
        capture = change switch
        {
            "unconfirmed" => capture with { OperatorConfirmedBoardOnlyAndTarget = false },
            "stale" => capture with { CapturedAt = DateTimeOffset.UtcNow.AddMinutes(-3) },
            "future" => capture with { CapturedAt = DateTimeOffset.UtcNow.AddMinutes(3) },
            "before-checkpoint" => capture with { CapturedAt = checkpoint.CreatedAt.AddMilliseconds(-1) },
            "epoch" => capture with { CameraEpoch = -1 },
            _ => capture with { CameraId = "camera\nforged metadata" }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CheckpointPhotoStore(_root).SaveReferenceAsync(checkpoint, WpfPng(), capture, Token));
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData(CheckpointStatus.CommittedAwaitingReadback)]
    [InlineData(CheckpointStatus.Faulted)]
    public async Task DigitalReadbackMustSucceedBeforeAReferenceCanBeAdded(CheckpointStatus status)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => new CheckpointPhotoStore(_root)
            .SaveReferenceAsync(Checkpoint() with { Status = status }, WpfPng(), Capture(), Token));
    }

    [Fact]
    public async Task TargetIntegrityIsCheckedBeforeImageStorage()
    {
        var checkpoint = Checkpoint() with { PhysicalTargetHash = "sha256:tampered" };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CheckpointPhotoStore(_root).SaveReferenceAsync(checkpoint, WpfPng(), Capture(), Token));
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("crc")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("dimensions")]
    [InlineData("pixels")]
    [InlineData("filter")]
    [InlineData("inflate")]
    [InlineData("metadata")]
    [InlineData("short-pixels")]
    [InlineData("bad-adler")]
    public async Task InvalidPngCannotBecomeAStoredReference(string corruption)
    {
        var png = Png(2, 1, [0, 30, 40, 50, 60, 70, 80]);
        png = corruption switch
        {
            "crc" => Mutate(png, 29),
            "truncated" => png[..^3],
            "trailing" => [.. png, 0],
            "dimensions" => Png(9000, 1, [0, 1, 2, 3]),
            "pixels" => Png(8192, 8192, [0, 1, 2, 3]),
            "filter" => Png(2, 1, [5, 30, 40, 50, 60, 70, 80]),
            "inflate" => Png(1, 1, [0, 30, 40, 50, 60, 70, 80]),
            "metadata" => Png(2, 1, [0, 30, 40, 50, 60, 70, 80], embeddedText: true),
            "short-pixels" => Png(2, 1, [0, 30, 40, 50]),
            "bad-adler" => Png(2, 1, [0, 30, 40, 50, 60, 70, 80], badAdler: true),
            _ => png
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CheckpointPhotoStore(_root).SaveReferenceAsync(Checkpoint(), png, Capture(), Token));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task CancelledSaveDoesNotCreateAnAttachment()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CheckpointPhotoStore(_root).SaveReferenceAsync(Checkpoint(), WpfPng(), Capture(), canceled.Token));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task ViewModelRequiresConfirmationAndShowsOnlyDecodedVerifiedReference()
    {
        var checkpoint = Checkpoint();
        var png = WpfPng();
        var captureCalls = 0;
        var vm = new CheckpointPhotoViewModel(new CheckpointPhotoStore(_root), _ =>
        {
            captureCalls++;
            return Task.FromResult(new CheckpointPhotoCaptureInput(png.ToArray(), Capture() with { OperatorConfirmedBoardOnlyAndTarget = false }));
        });
        await vm.LoadCheckpointAsync(checkpoint, Token);
        Assert.False(vm.CaptureReferenceCommand.CanExecute(null));
        vm.OperatorAcknowledged = true;
        Assert.True(vm.CaptureReferenceCommand.CanExecute(null));
        await vm.CaptureReferenceCommand.ExecuteAsync(null);
        Assert.Equal(1, captureCalls);
        Assert.True(vm.HasPhoto, vm.Status);
        Assert.NotNull(vm.PhotoImage);
        Assert.False(vm.CaptureReferenceCommand.CanExecute(null));
        Assert.False(vm.OperatorAcknowledged);
        await vm.LoadCheckpointAsync(null, Token);
        Assert.False(vm.HasPhoto);
        Assert.Null(vm.PhotoImage);
        Assert.False(vm.HasCheckpoint);
    }

    [Fact]
    public async Task ChangingCheckpointDuringCaptureRejectsTheOldCallback()
    {
        var checkpoint = Checkpoint();
        var pending = new TaskCompletionSource<CheckpointPhotoCaptureInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new CheckpointPhotoViewModel(new CheckpointPhotoStore(_root), _ => pending.Task);
        await vm.LoadCheckpointAsync(checkpoint, Token);
        vm.OperatorAcknowledged = true;
        var capturing = vm.CaptureReferenceCommand.ExecuteAsync(null);
        await vm.LoadCheckpointAsync(null, Token);
        pending.SetResult(new CheckpointPhotoCaptureInput(WpfPng(), Capture()));
        await capturing;
        Assert.False(vm.HasPhoto);
        Assert.False(vm.IsBusy);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task DamagedRequiredPhotoRequestsBackupRecoveryAndDisallowsReplacingEvidence()
    {
        var checkpoint = Checkpoint();
        var store = new CheckpointPhotoStore(_root);
        await store.SaveReferenceAsync(checkpoint, WpfPng(), Capture(), Token);
        var path = store.AttachmentPath(checkpoint.SessionId, checkpoint.CheckpointId);
        await File.WriteAllBytesAsync(path, "corrupt reference"u8.ToArray(), Token);
        var vm = new CheckpointPhotoViewModel(store, _ => throw new InvalidOperationException("Must not capture."));
        await vm.LoadCheckpointAsync(checkpoint, Token);
        vm.OperatorAcknowledged = true;
        Assert.True(vm.HasCheckpoint);
        Assert.True(vm.ReferenceUnavailable);
        Assert.False(vm.HasPhoto);
        Assert.False(vm.CaptureReferenceCommand.CanExecute(null));
        Assert.Contains("Restore the matching image from a backup before resuming", vm.Status);
        Assert.True(checkpoint.IsSafeToPackAway);
    }

    private static byte[] WpfPng(byte baseValue = 30)
    {
        var pixels = Enumerable.Range(0, 16 * 8 * 3).Select(index => (byte)(baseValue + index % 64)).ToArray();
        var bitmap = BitmapSource.Create(16, 8, 96, 96, PixelFormats.Rgb24, null, pixels, 16 * 3);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] Mutate(byte[] bytes, int offset) { bytes[offset] ^= 0x40; return bytes; }

    private static byte[] Png(int width, int height, byte[] decompressed, bool embeddedText = false, bool badAdler = false)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        Chunk(stream, "IHDR", header);
        if (embeddedText) Chunk(stream, "tEXt", "private metadata"u8.ToArray());
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) zip.Write(decompressed);
        var data = compressed.ToArray();
        if (badAdler) data[^1] ^= 0x40;
        Chunk(stream, "IDAT", data);
        Chunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number);
        var payload = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(payload);
        uint crc = uint.MaxValue;
        foreach (var value in payload)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        stream.Write(number);
    }
}
