using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Vortice.DXGI;

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
    private static readonly object NativeRuntimeGate = new();
    // The native provider retains this library for the process lifetime. Never release it while an
    // ORT environment or another detector may still own a DirectML device.
    private static nint _directMlLibrary;
    private const string DirectMlSha256 = "9c9e6d822561c6c41b90e6994b3e8857cf1d66dbfb1e0c4c799c7c89b4e92da1";

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
            IReadOnlyList<PieceCandidate> candidates;
            try { candidates = DetectCore(resized, token); }
            catch (OnnxRuntimeException error) when (token.IsCancellationRequested)
            { throw new OperationCanceledException("Piece inference was cancelled.", error, token); }
            catch (Exception error) when (_gpu && IsProviderFailure(error) && !token.IsCancellationRequested)
            {
                // A device failure invalidates every partial tile result. Restart the whole board on CPU.
                UseCpu($"DirectML failed during inference: {Brief(error)}");
                candidates = DetectCore(resized, token);
            }
            token.ThrowIfCancellationRequested();
            return new(candidates, ModelId, Backend, timer.Elapsed) { ModelSha256 = ModelSha256 };
        }
    }

    private IReadOnlyList<PieceCandidate> DetectCore(CameraFrame board, CancellationToken token)
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
            // Overlapping views see complete pieces that a neighboring tile cuts in half. Assign each
            // center to the middle of its overlap before NMS, so fragments do not become extra trains.
            boxes.AddRange(tileBoxes.Where(box => PieceModelGeometry.OwnsCenter(box, x, y, xStarts, yStarts)));
        }
        return PieceModelGeometry.Merge(boxes, BoardWidth, BoardHeight, _manifest.NmsThreshold);
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

    private static (int Index, string Name) PreferredAdapter()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var choices = new List<(int Index, string Name, ulong Memory)>();
        for (uint index = 0; index < 32 && factory.EnumAdapters1(index, out var adapter).Success; index++)
        {
            using (adapter)
            {
                var description = adapter.Description1;
                if (!D3D11FrameBackend.IsSoftwareAdapter(description))
                    choices.Add(((int)index, description.Description.TrimEnd('\0').Trim(), description.DedicatedVideoMemory));
            }
        }
        var selected = choices.OrderByDescending(item => item.Memory).FirstOrDefault();
        if (selected.Name is null) throw new NotSupportedException("No hardware graphics adapter is available.");
        return (selected.Index, selected.Name);
    }

    private static bool IsProviderFailure(Exception error) => error is OnnxRuntimeException or
        DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException or
        System.Runtime.InteropServices.ExternalException or TypeInitializationException;

    private static void LoadLocalDirectMl()
    {
        lock (NativeRuntimeGate)
        {
            if (_directMlLibrary != 0) return;
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "DirectML.dll"));
            if (!File.Exists(path)) throw new FileNotFoundException("The app-local DirectML.dll is missing.", path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(DirectMlSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("DirectML.dll does not match the pinned 1.15.4 x64 runtime.");
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
                if (module.ModuleName.Equals("DirectML.dll", StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetFullPath(module.FileName).Equals(path, StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("A different DirectML runtime is already loaded. Restart the app to use its bundled runtime.");
            // Absolute loading is essential for dotnet-hosted tools: ORT lives in runtimes/win-x64/native,
            // so Windows' ordinary dependency search otherwise finds System32 instead of this local DLL.
            _directMlLibrary = NativeLibrary.Load(path);
        }
    }

    private static string Brief(Exception error) => error.Message.Length <= 240 ? error.Message : error.Message[..240];

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
