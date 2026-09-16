using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GoldenTicket.Vision;

public sealed record CameraDevice(string Id, string Name);
public enum CameraCapturePreference { Balanced1080p, HighDetail2160p, SharedCurrent }
public sealed record CameraFormat(int Width, int Height, double FramesPerSecond, string Subtype)
{
    public override string ToString() => $"{Width} × {Height} · {FramesPerSecond:0.#} fps · {Subtype}";
}
public sealed record CameraFrameDimensions(int Width, int Height);

/// <summary>Ranks only native formats advertised by a camera; never requests an upscaled size.</summary>
public static class CameraFormatPolicy
{
    public static IReadOnlyList<CameraFormat> RankFormats(IEnumerable<CameraFormat> formats,
        CameraCapturePreference preference)
    {
        ArgumentNullException.ThrowIfNull(formats);
        if (preference is not (CameraCapturePreference.Balanced1080p or CameraCapturePreference.HighDetail2160p))
            throw new ArgumentOutOfRangeException(nameof(preference), "Shared capture keeps the camera's current format.");
        var maxPixels = preference == CameraCapturePreference.HighDetail2160p ? 3840L * 2160 : 1920L * 1080;
        var preferredFramesPerSecond = preference == CameraCapturePreference.Balanced1080p ? 30 : 15;
        return formats.Where(f => FitsFrameBounds(f) && (long)f.Width * f.Height <= maxPixels &&
                double.IsFinite(f.FramesPerSecond) && f.FramesPerSecond is >= 5 and <= 60)
            .Distinct()
            .OrderByDescending(f => preference == CameraCapturePreference.Balanced1080p &&
                f.Width == 1920 && f.Height == 1080)
            .ThenByDescending(f => (long)f.Width * f.Height)
            .ThenBy(f => Math.Abs(f.FramesPerSecond - preferredFramesPerSecond))
            .ThenBy(f => f.Subtype, StringComparer.Ordinal)
            .ToArray();
    }

    internal static bool FitsFrameBounds(CameraFormat format) => format.Width > 0 && format.Height > 0 &&
        format.Width <= CameraFrame.MaximumWidth && format.Height <= CameraFrame.MaximumHeight;
}

/// <summary>
/// Video-only WinRT capture with a capacity-one owned frame. Start from the WPF STA/UI context
/// for Windows' consent-sensitive initialization. No callback mutates game state.
/// </summary>
public sealed class CameraCaptureService : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _frameGate = new();
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private CameraFrame? _latest;
    private long _epoch;
    private long _sequence;
    private long _lastCopyTimestamp;
    private volatile bool _running;
    private volatile bool _disposed;
    private string? _error;
    private CameraFrameDimensions? _deliveredFrameDimensions;

    public bool IsRunning => _running;
    public string? LastError => Volatile.Read(ref _error);
    public CameraFrame? LatestFrame => Volatile.Read(ref _latest);
    public CameraFormat? NegotiatedFormat { get; private set; }
    /// <summary>The dimensions actually delivered by Windows, which can differ from the negotiated source format.</summary>
    public CameraFrameDimensions? DeliveredFrameDimensions => Volatile.Read(ref _deliveredFrameDimensions);
    public CameraDevice? ActiveDevice { get; private set; }
    public IReadOnlyList<CameraFormat> AvailableFormats { get; private set; } = [];
    public long Epoch => Interlocked.Read(ref _epoch);

    public static async Task<IReadOnlyList<CameraDevice>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync().AsTask(cancellationToken);
        return groups.Where(g => g.SourceInfos.Any(IsColorVideo))
            .Select(g => new CameraDevice(g.Id, g.DisplayName)).OrderBy(g => g.Name).ToArray();
    }

    public async Task StartAsync(CameraDevice device,
        CameraCapturePreference preference = CameraCapturePreference.Balanced1080p,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!Enum.IsDefined(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync();
            Volatile.Write(ref _error, null);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var token = timeout.Token;
            var groups = await MediaFrameSourceGroup.FindAllAsync().AsTask(token);
            var group = groups.FirstOrDefault(g => g.Id == device.Id)
                ?? throw new InvalidOperationException("This camera is no longer connected. Refresh the camera list and reconnect it.");
            var capture = new MediaCapture();
            _capture = capture;
            capture.Failed += CaptureFailed;
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = preference == CameraCapturePreference.SharedCurrent
                    ? MediaCaptureSharingMode.SharedReadOnly : MediaCaptureSharingMode.ExclusiveControl
            }).AsTask(token);
            var sources = capture.FrameSources.Values
                .Where(s => IsColorVideo(s.Info))
                .OrderByDescending(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord)
                .ToArray();
            if (sources.Length == 0) throw new InvalidOperationException("This camera does not expose a usable color video stream.");
            AvailableFormats = sources.SelectMany(s => s.SupportedFormats).Select(ToFormat).Distinct()
                .OrderByDescending(f => (long)f.Width * f.Height).ThenByDescending(f => f.FramesPerSecond).ToArray();
            var candidates = preference == CameraCapturePreference.SharedCurrent
                ? sources.Select(s => (Source: s, Format: s.CurrentFormat))
                    .Where(c => CameraFormatPolicy.FitsFrameBounds(ToFormat(c.Format)))
                    .OrderByDescending(c => (long)c.Format.VideoFormat.Width * c.Format.VideoFormat.Height).ToArray()
                : CameraFormatPolicy.RankFormats(AvailableFormats, preference)
                    .SelectMany(f => sources.SelectMany(s => s.SupportedFormats
                        .Where(native => ToFormat(native) == f).Select(native => (Source: s, Format: native))))
                    .ToArray();
            if (candidates.Length == 0)
                throw new InvalidOperationException("No advertised camera format fits the selected resolution. Try High detail or Shared current format.");

            Exception? lastFormatError = null;
            foreach (var (source, format) in candidates)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (preference != CameraCapturePreference.SharedCurrent)
                        await source.SetFormatAsync(format).AsTask(token);
                    var negotiated = ToFormat(source.CurrentFormat);
                    if (!CameraFormatPolicy.FitsFrameBounds(negotiated))
                        throw new InvalidOperationException("The camera delivered a format outside the supported 3840×2160 bounds.");
                    if (preference != CameraCapturePreference.SharedCurrent &&
                        CameraFormatPolicy.RankFormats([negotiated], preference).Count == 0)
                        throw new InvalidOperationException("The camera did not honor the requested resolution and frame-rate limits.");
                    // A reader without output dimensions preserves the native source resolution.
                    _reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8).AsTask(token);
                    _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
                    _reader.FrameArrived += FrameArrived;
                    _running = true;
                    var startStatus = await _reader.StartAsync().AsTask(token);
                    if (startStatus != MediaFrameReaderStartStatus.Success)
                        throw new InvalidOperationException($"Windows could not start this camera format ({startStatus}).");
                    if (!_running)
                        throw new InvalidOperationException(LastError ?? "The camera stopped during startup.");
                    NegotiatedFormat = negotiated;
                    ActiveDevice = device;
                    Volatile.Write(ref _error, null);
                    return;
                }
                catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
                {
                    lastFormatError = ex;
                    // Drivers sometimes advertise a mode they cannot start. Try the next native
                    // format/source, retaining the user's resolution preference and overall timeout.
                    await StopReaderAsync();
                }
            }
            throw new InvalidOperationException("Windows could not start any usable camera format. Close other camera apps or try Shared current format.", lastFormatError);
        }
        catch (Exception ex)
        {
            await StopCoreAsync();
            Volatile.Write(ref _error, ExplainError(ex));
            throw new CameraCaptureException(LastError!, ex);
        }
        finally { _lifecycle.Release(); }
    }

    public CameraFrame GetFreshFrame(TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero || maximumAge > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        var frame = LatestFrame;
        if (!_running || frame is null || frame.Epoch != Epoch || frame.Age > maximumAge)
            throw new InvalidOperationException("A fresh camera frame is unavailable. Reconnect or restart the camera and wait for the live preview.");
        return frame;
    }

    private void FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        // Never wait for another callback; drop superseded frames instead of creating a backlog.
        if (!Monitor.TryEnter(_frameGate)) return;
        try
        {
            if (!_running || !ReferenceEquals(sender, _reader)) return;
            var now = Stopwatch.GetTimestamp();
            if (_lastCopyTimestamp != 0 && Stopwatch.GetElapsedTime(_lastCopyTimestamp, now) < TimeSpan.FromMilliseconds(160)) return;
            var epoch = Epoch;
            using var frame = sender.TryAcquireLatestFrame();
            using var original = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (original is null) return;
            var width = original.PixelWidth;
            var height = original.PixelHeight;
            CameraFrame.ValidateSize(width, height, checked(width * height * 4));
            using var converted = SoftwareBitmap.Convert(original, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            var buffer = new Windows.Storage.Streams.Buffer((uint)(width * height * 4));
            converted.CopyToBuffer(buffer);
            var pixels = new byte[width * height * 4];
            using (var data = DataReader.FromBuffer(buffer)) data.ReadBytes(pixels);
            var copy = CameraFrame.TakeOwnership(width, height, pixels, Interlocked.Increment(ref _sequence),
                epoch, DateTimeOffset.UtcNow, now);
            if (_running && epoch == Epoch)
            {
                Volatile.Write(ref _latest, copy);
                Volatile.Write(ref _deliveredFrameDimensions, new(width, height));
                _lastCopyTimestamp = now;
            }
        }
        catch (Exception ex)
        {
            Invalidate(ExplainError(ex));
        }
        finally { Monitor.Exit(_frameGate); }
    }

    private void CaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args)
    {
        if (ReferenceEquals(sender, _capture))
            Invalidate("Camera disconnected or stopped. Check its cable and phone webcam mode, then restart the camera. " + args.Message);
    }

    private void Invalidate(string message)
    {
        _running = false;
        Interlocked.Increment(ref _epoch);
        Volatile.Write(ref _latest, null);
        Volatile.Write(ref _deliveredFrameDimensions, null);
        Volatile.Write(ref _error, message);
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        await StopReaderAsync();
        if (_capture is { } capture)
        {
            _capture = null;
            capture.Failed -= CaptureFailed;
            capture.Dispose();
        }
        NegotiatedFormat = null;
        ActiveDevice = null;
        AvailableFormats = [];
    }

    private async Task StopReaderAsync()
    {
        _running = false;
        Interlocked.Increment(ref _epoch);
        var reader = _reader;
        _reader = null;
        if (reader is not null)
        {
            reader.FrameArrived -= FrameArrived;
            try { await reader.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { Volatile.Write(ref _error, "The camera did not stop cleanly: " + ex.Message); }
            // Drain any callback that already acquired a native frame before disposal.
            lock (_frameGate) reader.Dispose();
        }
        Volatile.Write(ref _latest, null);
        Volatile.Write(ref _deliveredFrameDimensions, null);
        _lastCopyTimestamp = 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
        // Keep the small semaphore alive so concurrent Stop calls can finish safely.
    }

    private static bool IsColorVideo(MediaFrameSourceInfo source) => source.SourceKind == MediaFrameSourceKind.Color &&
        source.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord;
    private static double FrameRate(MediaFrameFormat format) => format.FrameRate.Denominator == 0 ? 0 :
        (double)format.FrameRate.Numerator / format.FrameRate.Denominator;
    private static CameraFormat ToFormat(MediaFrameFormat format) => new((int)format.VideoFormat.Width,
        (int)format.VideoFormat.Height, FrameRate(format), format.Subtype);
    private static string ExplainError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Windows blocked camera access. In Settings → Privacy & security → Camera, enable camera access for desktop apps.",
        OperationCanceledException => "Camera startup was cancelled or timed out. Reconnect the camera, close other camera apps, then try again.",
        _ => "Camera unavailable. " + ex.Message
    };
}

public sealed class CameraCaptureException(string message, Exception inner) : Exception(message, inner);
