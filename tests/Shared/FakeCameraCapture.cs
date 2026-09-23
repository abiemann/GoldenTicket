using GoldenTicket.Vision;

namespace GoldenTicket.Testing;

/// <summary>Controllable capture stream for synthetic frame and lifecycle tests; never opens hardware.</summary>
internal sealed class FakeCameraCapture : ICameraCapture
{
    public bool IsRunning { get; set; }
    public string? LastError { get; set; }
    public CameraFrame? LatestFrame { get; set; }
    public CameraFormat? NegotiatedFormat { get; set; }
    public CameraFrameDimensions? DeliveredFrameDimensions { get; set; }
    public CameraDevice? ActiveDevice { get; set; }
    public IReadOnlyList<CameraFormat> AvailableFormats { get; set; } = [];
    public long Epoch { get; set; }
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public bool IsDisposed { get; private set; }
    public Func<CameraDevice, CameraCapturePreference, CancellationToken, Task>? StartHandler { get; set; }
    public Func<Task>? StopHandler { get; set; }

    public async Task StartAsync(CameraDevice device,
        CameraCapturePreference preference = CameraCapturePreference.AutoBest,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        StartCalls++;
        if (StartHandler is not null) await StartHandler(device, preference, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ActiveDevice = device;
        Epoch++;
        IsRunning = true;
    }

    public async Task StopAsync()
    {
        StopCalls++;
        if (StopHandler is not null) await StopHandler();
        IsRunning = false;
        LatestFrame = null;
        ActiveDevice = null;
        NegotiatedFormat = null;
        DeliveredFrameDimensions = null;
    }

    public CameraFrame GetFreshFrame(TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero || maximumAge > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        if (!IsRunning || LatestFrame is not { } frame || frame.Epoch != Epoch || frame.Age > maximumAge)
            throw new InvalidOperationException("A fresh camera frame is unavailable.");
        return frame;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        DisposeCalls++;
        await StopAsync();
    }
}
