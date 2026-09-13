using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using static GoldenTicket.Vision.LocalOnnxRuntime;

namespace GoldenTicket.Vision;

public interface IBoardCornerDetector : IDisposable
{
    string ModelId { get; }
    string ModelSha256 => "";
    string Backend { get; }
    string? FallbackReason { get; }
    LearnedBoardCornerDetection Detect(CameraFrame frame, CancellationToken token = default);
}

/// <summary>Corners use full-camera normalized coordinates in TL, TR, BR, BL image order.</summary>
public sealed record LearnedBoardCornerDetection(IReadOnlyList<NormalizedPoint> Corners,
    IReadOnlyList<double> Confidences, string ModelId, string Backend, TimeSpan Elapsed,
    string? RejectionReason = null)
{
    public string ModelSha256 { get; init; } = "";
    public bool Accepted => RejectionReason is null && Corners.Count == 4;
}

/// <summary>
/// Runs the local learned corner heatmaps on a complete raw camera frame, without an existing crop
/// or empty-board reference. Owners run this on a worker and validate camera/edit revisions before
/// publishing a result. Detection never changes the camera or an operator's selection itself.
/// </summary>
public sealed class LearnedBoardCornerDetector : IBoardCornerDetector
{
    private readonly object _gate = new();
    private readonly BoardCornerModelManifest _manifest;
    private readonly byte[] _model;
    private readonly float[] _input = new float[3 * 384 * 384];
    private readonly float[] _output = new float[4 * 192 * 192];
    private InferenceSession? _session;
    private bool _gpu;
    private bool _disposed;
    private string _backend = "CPU";
    private string? _fallbackReason;
    private string? _diagnosticProfileDirectory;

    private LearnedBoardCornerDetector(BoardCornerModelManifest manifest, byte[] model)
    { _manifest = manifest; _model = model; }

    public string ModelId => _manifest.ModelId;
    public string ModelSha256 => _manifest.ModelSha256.ToLowerInvariant();
    public string Backend => Volatile.Read(ref _backend);
    public string? FallbackReason => Volatile.Read(ref _fallbackReason);
    public string? StartupProfilePath { get; private set; }

    /// <summary>Loads and warms up verified local model bytes. No network access or heuristic fallback.</summary>
    public static LearnedBoardCornerDetector Load(string modelDirectory, bool preferGpu = true,
        string? diagnosticProfileDirectory = null)
    {
        var (manifest, model) = BoardCornerModelManifest.Read(modelDirectory);
        string? localRuntimeFailure = null;
        if (preferGpu)
        {
            try { LoadLocalDirectMl(); }
            catch (Exception error) when (IsProviderFailure(error) || error is IOException or UnauthorizedAccessException)
            {
                preferGpu = false;
                localRuntimeFailure = $"The bundled DirectML runtime is unavailable: {Brief(error)}";
            }
        }
        OrtEnv.Instance().DisableTelemetryEvents();
        var detector = new LearnedBoardCornerDetector(manifest, model);
        if (diagnosticProfileDirectory is not null)
        {
            detector._diagnosticProfileDirectory = Path.GetFullPath(diagnosticProfileDirectory);
            Directory.CreateDirectory(detector._diagnosticProfileDirectory);
        }
        try
        {
            if (preferGpu)
            {
                try
                {
                    var adapter = PreferredAdapter();
                    detector._session = detector.CreateSession(adapter.Index);
                    detector._gpu = true;
                    detector._backend = $"DirectML ({adapter.Name}; CPU fallback allowed)";
                    detector.Warmup();
                }
                catch (Exception error) when (IsProviderFailure(error))
                {
                    detector.UseCpu($"DirectML could not initialize: {Brief(error)}");
                    detector.Warmup();
                }
            }
            else
            {
                detector.UseCpu(localRuntimeFailure);
                detector.Warmup();
            }
            return detector;
        }
        catch { detector.Dispose(); throw; }
    }

    public LearnedBoardCornerDetection Detect(CameraFrame frame, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width < 32 || frame.Height < 32)
            throw new ArgumentException("Corner detection needs a camera image at least 32 pixels on each side.", nameof(frame));
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var timer = Stopwatch.StartNew();
            var transform = BoardCornerModelGeometry.FillInput(frame, _input, token);
            try { Run(token); }
            catch (Exception error) when (_gpu && IsProviderFailure(error) && !token.IsCancellationRequested)
            {
                UseCpu($"DirectML failed during corner inference: {Brief(error)}");
                Run(token);
            }
            token.ThrowIfCancellationRequested();
            var decoded = BoardCornerModelGeometry.Decode(_output, transform, _manifest.ConfidenceThreshold, frame);
            token.ThrowIfCancellationRequested();
            return new(decoded.Corners, decoded.Confidences, ModelId, Backend, timer.Elapsed, decoded.RejectionReason)
            { ModelSha256 = ModelSha256 };
        }
    }

    private void Run(CancellationToken token)
    {
        using var input = OrtValue.CreateTensorValueFromMemory(_input, [1, 3, 384, 384]);
        using var output = OrtValue.CreateTensorValueFromMemory(_output, [1, 4, 192, 192]);
        using var options = new RunOptions();
        using var cancellation = token.Register(() => options.Terminate = true);
        try { _session!.Run(options, [_manifest.InputName], [input], [_manifest.OutputName], [output]); }
        catch (OnnxRuntimeException error) when (token.IsCancellationRequested)
        { throw new OperationCanceledException("Board-corner inference was cancelled.", error, token); }
        token.ThrowIfCancellationRequested();
    }

    private void Warmup()
    {
        Array.Fill(_input, 114f / 255);
        Run(CancellationToken.None);
        if (_output.Any(value => !float.IsFinite(value) || value is < 0 or > 1))
            throw new InvalidDataException("The board-corner model produced invalid heatmap probabilities during startup validation.");
        if (_diagnosticProfileDirectory is not null) StartupProfilePath = _session!.EndProfiling();
    }

    private InferenceSession CreateSession(int? adapter)
    {
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableMemoryPattern = false,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
        };
        if (adapter is { } id) options.AppendExecutionProvider_DML(id);
        if (_diagnosticProfileDirectory is not null)
        {
            options.ProfileOutputPathPrefix = Path.Combine(_diagnosticProfileDirectory,
                adapter is null ? "corner-cpu-startup" : "corner-directml-startup");
            options.EnableProfiling = true;
        }
        var session = new InferenceSession(_model, options);
        try
        {
            if (session.InputMetadata.Count != 1 || session.OutputMetadata.Count != 1 ||
                !session.InputMetadata.TryGetValue(_manifest.InputName, out var input) ||
                !session.OutputMetadata.TryGetValue(_manifest.OutputName, out var output) ||
                !input.IsTensor || !output.IsTensor || input.ElementDataType != TensorElementType.Float ||
                output.ElementDataType != TensorElementType.Float || !input.Dimensions.SequenceEqual([1, 3, 384, 384]) ||
                !output.Dimensions.SequenceEqual([1, 4, 192, 192]))
                throw new InvalidDataException("The board-corner model tensors do not match its manifest.");
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    private void UseCpu(string? reason)
    {
        _session?.Dispose();
        _session = null;
        _gpu = false;
        _session = CreateSession(null);
        _backend = "CPU (ONNX Runtime)";
        _fallbackReason = reason;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _session?.Dispose();
            _session = null;
        }
    }
}
