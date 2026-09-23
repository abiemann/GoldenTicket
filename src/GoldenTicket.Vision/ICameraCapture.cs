namespace GoldenTicket.Vision;

/// <summary>
/// Owns one camera stream and its latest immutable frame. Start, stop, and observed state belong
/// to the same instance. Consumers own and dispose the capture; published frames remain immutable.
/// Device discovery and format probing do not require opening this stream.
/// </summary>
public interface ICameraCapture : IAsyncDisposable
{
    bool IsRunning { get; }
    string? LastError { get; }
    CameraFrame? LatestFrame { get; }
    CameraFormat? NegotiatedFormat { get; }
    CameraFrameDimensions? DeliveredFrameDimensions { get; }
    CameraDevice? ActiveDevice { get; }
    IReadOnlyList<CameraFormat> AvailableFormats { get; }
    long Epoch { get; }

    Task StartAsync(CameraDevice device, CameraCapturePreference preference = CameraCapturePreference.AutoBest,
        CancellationToken cancellationToken = default);
    Task StopAsync();
    CameraFrame GetFreshFrame(TimeSpan maximumAge);
}
