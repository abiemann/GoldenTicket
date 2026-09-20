using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using static GoldenTicket.Vision.LocalOnnxRuntime;

namespace GoldenTicket.Vision;

public interface IPieceModelDetector : IDisposable
{
    string ModelId { get; }
    string ModelSha256 => "";
    string Backend { get; }
    string? FallbackReason { get; }
    LearnedPieceDetection Detect(CameraFrame board, CancellationToken token = default);
}

public sealed record LearnedPieceDetection(IReadOnlyList<PieceCandidate> Candidates,
    string ModelId, string Backend, TimeSpan Elapsed)
{
    public string ModelSha256 { get; init; } = "";
    public TimeSpan OutlineFittingElapsed { get; init; }
    public int WeakTrainRetryCount { get; init; }
    public int RecoveredTrainCount { get; init; }
}

/// <summary>
/// Offline YOLOX inference on a whole cropped board, independent of an empty-board reference.
/// Load and Detect belong on the owner's single background worker. Camera freshness, crop/model
/// revisions and publication are owned by that worker; this same API also evaluates static photos.
/// </summary>
public sealed class LearnedPieceDetector : IPieceModelDetector
{
    public const int BoardWidth = 1920;
    public const int BoardHeight = 1200;
    private readonly object _gate = new();
    private readonly PieceModelManifest _manifest;
    private readonly byte[] _model;
    private readonly float[] _input = new float[3 * 640 * 640];
    private readonly float[] _output = new float[8400 * 7];
    private InferenceSession? _session;
    private bool _gpu;
    private bool _disposed;
    private string _backend = "CPU";
    private string? _fallbackReason;
    private string? _diagnosticProfileDirectory;
    private LearnedPieceDetector(PieceModelManifest manifest, byte[] model)
    { _manifest = manifest; _model = model; }

    public string ModelId => _manifest.ModelId;
    public string ModelSha256 => _manifest.ModelSha256.ToLowerInvariant();
    public string Backend => Volatile.Read(ref _backend);
    public string? FallbackReason => Volatile.Read(ref _fallbackReason);
    public string? StartupProfilePath { get; private set; }

    /// <summary>Loads verified local bytes and warms up the selected provider. It never downloads files.</summary>
    public static LearnedPieceDetector Load(string modelDirectory, bool preferGpu = true,
        string? diagnosticProfileDirectory = null)
    {
        var (manifest, model) = PieceModelManifest.Read(modelDirectory);
        string? localRuntimeFailure = null;
        if (preferGpu)
        {
            try { LoadLocalDirectMl(); }
            catch (Exception error) when (IsProviderFailure(error) || error is IOException or UnauthorizedAccessException)
            {
                // The CPU execution provider does not need DirectML. A missing/bad bundled GPU
                // runtime must never silently select an arbitrary system copy for GPU inference.
                preferGpu = false;
                localRuntimeFailure = $"The bundled DirectML runtime is unavailable: {Brief(error)}";
            }
        }
        // This app's only ORT environment is local inference; disable its process-wide telemetry.
        OrtEnv.Instance().DisableTelemetryEvents();
        var detector = new LearnedPieceDetector(manifest, model);
        if (diagnosticProfileDirectory is not null)
        {
            // Only the explicit diagnostic caller opts into local runtime profile files.
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

    public LearnedPieceDetection Detect(CameraFrame board, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (board.Width < 320 || board.Height < 200 || Math.Abs((double)board.Width / board.Height - 1.6) > .02)
            throw new ArgumentException("ML piece outlines need the whole board cropped to its 8:5 aspect ratio.", nameof(board));
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var timer = Stopwatch.StartNew();
            var resized = PieceModelGeometry.Resize(board, BoardWidth, BoardHeight, token);
            CoreDetection detection;
            try { detection = DetectCore(resized, token); }
            catch (OnnxRuntimeException error) when (token.IsCancellationRequested)
            { throw new OperationCanceledException("Piece inference was cancelled.", error, token); }
            catch (Exception error) when (_gpu && IsProviderFailure(error) && !token.IsCancellationRequested)
            {
                // A device failure invalidates every partial tile result. Restart the whole board on CPU.
                UseCpu($"DirectML failed during inference: {Brief(error)}");
                detection = DetectCore(resized, token);
            }
            token.ThrowIfCancellationRequested();
            // Preserve model boxes/confidences for NMS, evaluation and review. The optional
            // image fit comes from the same frame for display and guarded color sampling,
            // without an empty-board reference. Route positions still use the model boxes.
            var fittingStarted = Stopwatch.GetTimestamp();
            var candidates = TrainOutlineFitter.Refine(resized, detection.Candidates, token);
            var fittingElapsed = Stopwatch.GetElapsedTime(fittingStarted);
            token.ThrowIfCancellationRequested();
            return new(candidates, ModelId, Backend, timer.Elapsed)
            {
                ModelSha256 = ModelSha256, OutlineFittingElapsed = fittingElapsed,
                WeakTrainRetryCount = detection.RetryCount, RecoveredTrainCount = detection.RecoveredCount
            };
        }
    }

    private sealed record CoreDetection(IReadOnlyList<PieceCandidate> Candidates,
        int RetryCount, int RecoveredCount);

    private CoreDetection DetectCore(CameraFrame board, CancellationToken token)
    {
        var boxes = new List<PieceModelBox>();
        using var input = OrtValue.CreateTensorValueFromMemory(_input, [1, 3, 640, 640]);
        using var output = OrtValue.CreateTensorValueFromMemory(_output, [1, 8400, 7]);
        using var options = new RunOptions();
        using var cancellation = token.Register(() => options.Terminate = true);
        var xStarts = PieceModelGeometry.TileStarts(BoardWidth);
        var yStarts = PieceModelGeometry.TileStarts(BoardHeight);
        foreach (var y in yStarts)
        foreach (var x in xStarts)
        {
            var tileBoxes = DetectTile(x, y);
            // Overlapping views see complete pieces that a neighboring tile cuts in half. Assign each
            // center to the middle of its overlap before NMS, so fragments do not become extra trains.
            boxes.AddRange(tileBoxes.Where(box => PieceModelGeometry.OwnsCenter(box, x, y, xStarts, yStarts)));
        }

        // Weak proposals only choose where to look again in this same frame. A shifted tile
        // must independently produce a strong matching train; neither a route nor history
        // supplies occupancy. Keep the extra inference bounded and final NMS unchanged.
        var retries = WeakTrainRecovery.Select(boxes, _manifest.ConfidenceThreshold);
        var recovered = 0;
        foreach (var retry in retries)
        {
            var retryBoxes = DetectTile(retry.X, retry.Y);
            if (WeakTrainRecovery.Accept(retry, retryBoxes, boxes, _manifest.ConfidenceThreshold) is { } train)
            {
                boxes.Add(train);
                recovered++;
            }
        }
        return new(PieceModelGeometry.Merge(boxes, BoardWidth, BoardHeight, _manifest.NmsThreshold),
            retries.Count, recovered);

        List<PieceModelBox> DetectTile(int x, int y)
        {
            token.ThrowIfCancellationRequested();
            PieceModelGeometry.FillInput(board, x, y, _input, token);
            try { _session!.Run(options, [_manifest.InputName], [input], [_manifest.OutputName], [output]); }
            catch (OnnxRuntimeException error) when (token.IsCancellationRequested)
            { throw new OperationCanceledException("Piece inference was cancelled.", error, token); }
            token.ThrowIfCancellationRequested();
            if (_output.Any(value => !float.IsFinite(value)))
                throw new InvalidDataException("The piece model produced non-finite detection values.");
            var tileBoxes = new List<PieceModelBox>();
            PieceModelGeometry.Decode(_output, x, y, _manifest.ConfidenceThreshold, tileBoxes);
            return tileBoxes;
        }
    }

    private void Warmup()
    {
        Array.Fill(_input, 114);
        using var input = OrtValue.CreateTensorValueFromMemory(_input, [1, 3, 640, 640]);
        using var output = OrtValue.CreateTensorValueFromMemory(_output, [1, 8400, 7]);
        using var options = new RunOptions();
        _session!.Run(options, [_manifest.InputName], [input], [_manifest.OutputName], [output]);
        if (_output.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("The piece model produced non-finite values during startup validation.");
        if (_diagnosticProfileDirectory is not null) StartupProfilePath = _session.EndProfiling();
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
        // DirectML requires sequential execution and memory-pattern optimization disabled.
        // https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html
        if (adapter is { } id) options.AppendExecutionProvider_DML(id);
        if (_diagnosticProfileDirectory is not null)
        {
            options.ProfileOutputPathPrefix = Path.Combine(_diagnosticProfileDirectory,
                adapter is null ? "cpu-startup" : "directml-startup");
            options.EnableProfiling = true;
        }
        var session = new InferenceSession(_model, options);
        try
        {
            if (session.InputMetadata.Count != 1 || session.OutputMetadata.Count != 1 ||
                !session.InputMetadata.TryGetValue(_manifest.InputName, out var input) ||
                !session.OutputMetadata.TryGetValue(_manifest.OutputName, out var output) ||
                !input.IsTensor || !output.IsTensor || input.ElementDataType != TensorElementType.Float ||
                output.ElementDataType != TensorElementType.Float || !input.Dimensions.SequenceEqual([1, 3, 640, 640]) ||
                !output.Dimensions.SequenceEqual([1, 8400, 7]))
                throw new InvalidDataException("The piece model input/output tensors do not match its manifest.");
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
