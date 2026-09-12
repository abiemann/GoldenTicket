using System.Diagnostics;
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

    public bool IsRunning => _running;
    public string? LastError => Volatile.Read(ref _error);
    public CameraFrame? LatestFrame => Volatile.Read(ref _latest);
    public CameraFormat? NegotiatedFormat { get; private set; }
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
            var source = capture.FrameSources.Values
                .Where(s => IsColorVideo(s.Info))
                .OrderByDescending(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord)
                .FirstOrDefault() ?? throw new InvalidOperationException("This camera does not expose a usable color video stream.");
            AvailableFormats = source.SupportedFormats.Select(ToFormat).Distinct()
                .OrderByDescending(f => f.Width * f.Height).ThenByDescending(f => f.FramesPerSecond).ToArray();
            if (preference != CameraCapturePreference.SharedCurrent)
            {
                var maxPixels = preference == CameraCapturePreference.HighDetail2160p ? 3840 * 2160 : 1920 * 1080;
                var format = source.SupportedFormats.Where(f =>
                        f.VideoFormat.Width > 0 && f.VideoFormat.Height > 0 &&
                        f.VideoFormat.Width <= CameraFrame.MaximumWidth && f.VideoFormat.Height <= CameraFrame.MaximumHeight &&
                        (long)f.VideoFormat.Width * f.VideoFormat.Height <= maxPixels &&
                        FrameRate(f) >= 5 && FrameRate(f) <= 60)
                    .OrderByDescending(f => (long)f.VideoFormat.Width * f.VideoFormat.Height)
                    .ThenBy(f => Math.Abs(FrameRate(f) - 15)).FirstOrDefault()
                    ?? throw new InvalidOperationException("No supported camera format fits the selected resolution. Try High detail or Shared current format.");
                await source.SetFormatAsync(format).AsTask(token);
            }
            var negotiated = ToFormat(source.CurrentFormat);
            if (negotiated.Width > CameraFrame.MaximumWidth || negotiated.Height > CameraFrame.MaximumHeight)
                throw new InvalidOperationException("The current camera format exceeds 3840×2160. Select a lower format or close the other camera app.");
            NegotiatedFormat = negotiated;
            _reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8).AsTask(token);
            _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            _reader.FrameArrived += FrameArrived;
            _running = true;
            var startStatus = await _reader.StartAsync().AsTask(token);
            if (startStatus != MediaFrameReaderStartStatus.Success)
                throw new InvalidOperationException($"Windows could not start this camera ({startStatus}). Close other camera apps or try Shared current format.");
            ActiveDevice = device;
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
        if (_capture is { } capture)
        {
            _capture = null;
            capture.Failed -= CaptureFailed;
            capture.Dispose();
        }
        Volatile.Write(ref _latest, null);
        _lastCopyTimestamp = 0;
        NegotiatedFormat = null;
        ActiveDevice = null;
        AvailableFormats = [];
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
