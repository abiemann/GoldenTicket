using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GoldenTicket.Vision;

/// <summary>An owned copy of one camera frame. No capture-owned native object escapes the callback.</summary>
public sealed class CameraFrame
{
    public const int MaximumWidth = 3840;
    public const int MaximumHeight = 2160;
    private readonly byte[] _pixels;
    private TimeProvider Clock { get; init; } = TimeProvider.System;

    private CameraFrame(int width, int height, byte[] pixels, long sequence, long epoch,
        DateTimeOffset capturedAt, long monotonicTimestamp)
    {
        ValidateSize(width, height, pixels.Length);
        Width = width;
        Height = height;
        _pixels = pixels;
        Sequence = sequence;
        Epoch = epoch;
        CapturedAt = capturedAt;
        MonotonicTimestamp = monotonicTimestamp;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => checked(Width * 4);
    public long Sequence { get; }
    public long Epoch { get; }
    public DateTimeOffset CapturedAt { get; }
    public long MonotonicTimestamp { get; }
    public ReadOnlyMemory<byte> Bgra32 => _pixels;
    public TimeSpan Age => Clock.GetElapsedTime(MonotonicTimestamp);
    internal TimeSpan CaptureElapsed => Clock.GetElapsedTime(0, MonotonicTimestamp);

    public static CameraFrame CopyFromBgra32(int width, int height, ReadOnlySpan<byte> pixels,
        long sequence = 1, long epoch = 1, DateTimeOffset? capturedAt = null, TimeProvider? clock = null)
    {
        ValidateSize(width, height, pixels.Length);
        clock ??= TimeProvider.System;
        return new(width, height, pixels.ToArray(), sequence, epoch,
            capturedAt ?? clock.GetUtcNow(), clock.GetTimestamp()) { Clock = clock };
    }

    internal static CameraFrame TakeOwnership(int width, int height, byte[] pixels,
        long sequence, long epoch, DateTimeOffset capturedAt, long monotonicTimestamp) =>
        new(width, height, pixels, sequence, epoch, capturedAt, monotonicTimestamp);

    // Enhancement and rectification own their output buffer, but must retain the
    // source clock and timestamp: processing must never make old evidence fresh.
    internal CameraFrame Derive(int width, int height, byte[] pixels) =>
        new(width, height, pixels, Sequence, Epoch, CapturedAt, MonotonicTimestamp) { Clock = Clock };

    internal static void ValidateSize(int width, int height, int bytes)
    {
        if (width <= 0 || height <= 0 || width > MaximumWidth || height > MaximumHeight ||
            bytes != checked(width * height * 4))
            throw new ArgumentException("A BGRA frame must be between 1×1 and 3840×2160 with exactly four bytes per pixel.");
    }

    /// <summary>Encodes this exact immutable frame; capture continues independently.</summary>
    public async Task<byte[]> EncodePngAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(cancellationToken);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)Width, (uint)Height, 96, 96, _pixels);
        await encoder.FlushAsync().AsTask(cancellationToken);
        if (stream.Size > 48 * 1024 * 1024)
            throw new InvalidDataException("The captured image exceeds the local image size limit.");
        using var input = stream.GetInputStreamAt(0);
        using var reader = new DataReader(input);
        var size = checked((uint)stream.Size);
        if (await reader.LoadAsync(size).AsTask(cancellationToken) != size)
            throw new IOException("The captured image could not be read back completely.");
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
