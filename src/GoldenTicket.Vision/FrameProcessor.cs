using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;

namespace GoldenTicket.Vision;

public enum FrameComputeMode { Auto, Cpu, Gpu }
public enum FrameProcessingBackend { Cpu, Gpu }

public sealed record FrameProcessorStatus(FrameComputeMode RequestedMode, FrameProcessingBackend Backend,
    string AdapterName, string? FallbackReason);

public sealed record ProcessedCameraFrame(CameraFrame Frame, FrameProcessorStatus Status,
    TimeSpan ProcessingTime, int SourceWidth, int SourceHeight)
{
    public bool IsUpscaled => Frame.Width > SourceWidth || Frame.Height > SourceHeight;
}

/// <summary>
/// Offline, non-generative preprocessing. The original frame remains the evidence source;
/// interpolation and mild edge enhancement cannot recover detail missing from that source.
/// Immediate-context access and disposal are serialized. Callers should keep at most one pending frame.
/// </summary>
public sealed class FrameProcessor : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private D3D11FrameBackend? _gpu;
    private bool _initialized;
    private bool _disposed;
    private FrameProcessorStatus _status = new(FrameComputeMode.Auto, FrameProcessingBackend.Cpu,
        "CPU", "GPU has not been tested yet.");

    public FrameProcessorStatus Status => Volatile.Read(ref _status);

    public async Task<FrameProcessorStatus> InitializeAsync(FrameComputeMode mode = FrameComputeMode.Auto,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Task.Run(() => InitializeCore(mode, cancellationToken), cancellationToken).ConfigureAwait(false);
            return Status;
        }
        finally { _gate.Release(); }
    }

    public async Task<ProcessedCameraFrame> ProcessAsync(CameraFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        CameraFrame.ValidateSize(frame.Width, frame.Height, frame.Bgra32.Length);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => ProcessCore(frame, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public static (int Width, int Height) GetOutputSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > CameraFrame.MaximumWidth || height > CameraFrame.MaximumHeight)
            throw new ArgumentOutOfRangeException(nameof(width));
        var scale = Math.Min((double)CameraFrame.MaximumWidth / width, (double)CameraFrame.MaximumHeight / height);
        return (Math.Clamp((int)Math.Round(width * scale), 1, CameraFrame.MaximumWidth),
            Math.Clamp((int)Math.Round(height * scale), 1, CameraFrame.MaximumHeight));
    }

    private void InitializeCore(FrameComputeMode mode, CancellationToken cancellationToken)
    {
        _gpu?.Dispose();
        _gpu = null;
        _initialized = false;
        _status = new(mode, FrameProcessingBackend.Cpu, "CPU", null);
        cancellationToken.ThrowIfCancellationRequested();
        if (mode != FrameComputeMode.Cpu)
        {
            try
            {
                _gpu = D3D11FrameBackend.CreateValidated(cancellationToken);
                _status = new(mode, FrameProcessingBackend.Gpu, _gpu.AdapterName, null);
            }
            catch (Exception error) when (IsGpuFailure(error))
            {
                _gpu?.Dispose();
                _gpu = null;
                _status = new(mode, FrameProcessingBackend.Cpu, "CPU",
                    "Hardware GPU processing was unavailable or failed its image check; using CPU.");
            }
        }
        _initialized = true;
    }

    private ProcessedCameraFrame ProcessCore(CameraFrame frame, CancellationToken cancellationToken)
    {
        if (!_initialized) InitializeCore(Status.RequestedMode, cancellationToken);
        var started = Stopwatch.GetTimestamp();
        var (width, height) = GetOutputSize(frame.Width, frame.Height);
        byte[] output;
        if (_gpu is { } gpu)
        {
            try { output = gpu.Process(frame, width, height, cancellationToken); }
            catch (Exception error) when (IsGpuFailure(error))
            {
                _gpu.Dispose();
                _gpu = null;
                _status = new(Status.RequestedMode, FrameProcessingBackend.Cpu, "CPU",
                    "The GPU stopped processing images; CPU processing is active. Choose Auto or GPU to retry.");
                output = FrameProcessingKernel.Process(frame, width, height, cancellationToken);
            }
        }
        else output = FrameProcessingKernel.Process(frame, width, height, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var result = CameraFrame.TakeOwnership(width, height, output, frame.Sequence, frame.Epoch,
            frame.CapturedAt, frame.MonotonicTimestamp);
        return new(result, Status, Stopwatch.GetElapsedTime(started), frame.Width, frame.Height);
    }

    internal static bool IsGpuFailure(Exception error) => error is SharpGenException or COMException or
        DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or
        NotSupportedException or InvalidOperationException or TimeoutException;

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _gpu?.Dispose();
            _gpu = null;
        }
        finally { _gate.Release(); }
    }
}
