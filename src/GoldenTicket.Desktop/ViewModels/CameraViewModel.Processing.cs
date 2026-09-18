using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoldenTicket.Vision;
using Microsoft.Win32;

namespace GoldenTicket.Desktop.ViewModels;

public sealed record ProcessorOption(FrameComputeMode Value, string Label);
public sealed record PreviewPieceOutline(bool IsPlayerMarker, IReadOnlyList<NormalizedPoint> SensorOutline);

public sealed partial class CameraViewModel
{
    private readonly FrameProcessor _processor = new();
    private readonly PieceCandidateDetector _pieceDetector = new();
    private readonly object _detectionGate = new();
    private readonly string? _processingSettingsPath;
    private Task? _initialization;
    private Task _frameWork = Task.CompletedTask;
    private bool _processingFrame;
    private bool _processorReady;
    private FrameProcessingBackend? _activeBackend;
    private long _processingRevision;
    private long _referenceRevision;
    private CameraFrame? _lastRawPreview;
    private CameraFrame? _lastEnhancedPreview;
    private CameraFrame? _outlinedFrame;

    public IReadOnlyList<ProcessorOption> ProcessorModes { get; } =
    [
        new(FrameComputeMode.Auto, "Auto · prefer GPU"),
        new(FrameComputeMode.Cpu, "CPU only"),
        new(FrameComputeMode.Gpu, "GPU · CPU fallback")
    ];

    [ObservableProperty] private ProcessorOption _selectedProcessor = null!;
    [ObservableProperty] private string _computeStatus = "Checking processor…";
    [ObservableProperty] private string _computeBadge = "Processor…";
    [ObservableProperty] private string _computeExplanation = "A local hardware check selects GPU processing when available, otherwise CPU. No downloads are required.";
    [ObservableProperty] private string _processingText = "Processing target: 4K. Camera source resolution will be reported when preview starts.";
    [ObservableProperty] private bool _isProcessorBusy;
    [ObservableProperty] private bool _useEnhancedPreview = true;
    [ObservableProperty] private bool _showPieceOutlines = true;
    [ObservableProperty] private bool _hasPieceReference;
    [ObservableProperty] private string _detectionText = "Waiting for a board crop before showing ML piece outlines.";
    [ObservableProperty] private IReadOnlyList<PreviewPieceOutline> _pieceOutlines = [];
    public bool CanApplyProcessor => !IsBusy && !IsProcessorBusy && !_disposed;

    partial void OnIsProcessorBusyChanged(bool value) => OnPropertyChanged(nameof(CanApplyProcessor));

    partial void OnUseEnhancedPreviewChanged(bool value)
    {
        var frame = value ? _lastEnhancedPreview : _lastRawPreview;
        if (frame is not null) Preview = ToBitmap(frame);
    }

    partial void OnShowPieceOutlinesChanged(bool value)
    {
        // Invalidate work already in flight even if the user quickly turns outlines back on.
        _modelRevision++;
        ClearGameTableAnalysis();
        ClearDetectionPreview();
        DetectionText = value ? "Waiting for a fresh image for ML piece outlines." : "Piece outlines are off.";
        _previewSequence = -1;
    }

    /// <summary>Called by the window at launch; unit fixtures do not initialize real hardware implicitly.</summary>
    public Task InitializeProcessingAsync()
    {
        if (_disposed || _processorReady) return Task.CompletedTask;
        return _initialization ??= InitializeProcessorCoreAsync(SelectedProcessor.Value);
    }

    private async Task InitializeProcessorCoreAsync(FrameComputeMode mode)
    {
        _processorReady = false;
        IsProcessorBusy = true;
        ComputeStatus = "Checking processor…";
        ComputeBadge = "Processor…";
        try
        {
            var status = await _processor.InitializeAsync(mode, _lifetime.Token);
            if (_disposed) return;
            _processorReady = true;
            UpdateProcessorStatus(status);
            await EnsurePieceModelAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ComputeStatus = "Processing unavailable";
            ComputeBadge = "Processor unavailable";
            ComputeExplanation = "Could not initialize image processing: " + ex.Message;
        }
        finally { IsProcessorBusy = false; }
    }

    [RelayCommand]
    private async Task ApplyProcessorAsync()
    {
        if (_disposed || IsBusy || IsProcessorBusy) return;
        var mode = SelectedProcessor.Value;
        _processingRevision++;
        ResetPieceReference();
        _initialization = InitializeProcessorCoreAsync(mode);
        await _initialization;
        if (_disposed || !_processorReady) return;
        try { Services.FrameProcessingPreferences.Save(_processingSettingsPath, mode); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = "The processor choice is active but could not be saved for next launch: " + ex.Message;
        }
        _previewSequence = -1;
    }

    private void UpdateProcessorStatus(FrameProcessorStatus status)
    {
        _activeBackend = status.Backend;
        var gpu = status.Backend == FrameProcessingBackend.Gpu;
        ComputeBadge = gpu ? "ϟ GPU ϟ" : "▣ CPU";
        ComputeStatus = ComputeBadge + " · image processing";
        ComputeExplanation = $"Requested: {status.RequestedMode}. Active: {status.AdapterName}. " +
            "Resizing and enhancement use this processor. ML inference has its own status under Piece outlines. " +
            (status.FallbackReason is { Length: > 0 } reason ? "CPU fallback: " + reason : "Game rules run on CPU.");
    }

    // Call only after validating the operation's captured revisions. A backend transition
    // invalidates the comparison baseline regardless of which operation noticed the fallback.
    private bool AcceptProcessorStatus(FrameProcessorStatus status)
    {
        var changed = _activeBackend is { } previous && status.Backend != previous;
        if (changed)
        {
            _processingRevision++;
            ResetPieceReference();
            DetectionText = "The processor changed. Waiting for a fresh image for ML piece outlines.";
        }
        UpdateProcessorStatus(status);
        return changed;
    }

    private void QueueFrameProcessing(CameraFrame frame)
    {
        if (_processingFrame || IsBusy || IsProcessorBusy || _disposed) return;
        _processingFrame = true;
        _frameWork = ProcessPreviewFrameAsync(frame);
    }

    private async Task ProcessPreviewFrameAsync(CameraFrame frame)
    {
        var registration = _registration;
        var cropRevision = _cropRevision;
        var processingRevision = _processingRevision;
        var referenceRevision = _referenceRevision;
        var modelRevision = _modelRevision;
        var showOutlines = ShowPieceOutlines;
        try
        {
            await InitializeProcessingAsync();
            if (_disposed || !_processorReady) return;
            var result = await _processor.ProcessAsync(frame, _lifetime.Token);
            if (_disposed || !Capture.IsRunning || frame.Epoch != Capture.Epoch ||
                cropRevision != _cropRevision || processingRevision != _processingRevision ||
                referenceRevision != _referenceRevision) return;
            if (AcceptProcessorStatus(result.Status)) return;
            // Match the training-photo export order: rectify the source, then enhance the crop.
            // Resizing/tiling into the network's tensor is defined by the model manifest.
            CameraFrame? board = null;
            if (registration is not null && showOutlines && !IsModelBusy && _pieceModel is not null)
            {
                var crop = await Task.Run(() => registration.Rectify(frame, 3456, 2160), _lifetime.Token);
                var processedBoard = await _processor.ProcessAsync(crop, _lifetime.Token);
                if (_disposed || cropRevision != _cropRevision || processingRevision != _processingRevision ||
                    modelRevision != _modelRevision || !Capture.IsRunning || frame.Epoch != Capture.Epoch) return;
                if (AcceptProcessorStatus(processedBoard.Status)) return;
                board = processedBoard.Frame;
            }
            var prepared = await Task.Run(() =>
            {
                LearnedPieceDetection? detection = null;
                string? detectionError = null;
                if (board is not null && frame.Age <= TimeSpan.FromSeconds(2))
                {
                    try
                    {
                        lock (_modelGate)
                        {
                            if (!IsModelBusy && modelRevision == _modelRevision)
                                detection = _pieceModel?.Detect(board, _lifetime.Token);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { detectionError = error.Message; }
                }
                return (Enhanced: ToBitmap(result.Frame), Raw: ToBitmap(frame),
                    Board: board is null ? null : ToBitmap(board), Detection: detection, Error: detectionError,
                    Scores: detection is not null && board is not null
                        ? ScoreMarkerReader.Read(board, detection.Candidates) : []);
            }, _lifetime.Token);

            if (_disposed || !Capture.IsRunning || frame.Epoch != Capture.Epoch ||
                cropRevision != _cropRevision ||
                processingRevision != _processingRevision || referenceRevision != _referenceRevision ||
                modelRevision != _modelRevision)
            {
                return;
            }
            if (frame.Age > TimeSpan.FromSeconds(2))
            {
                ClearDetectionPreview();
                DetectionText = "Waiting for a fresh processed image before outlining pieces. ML results older than two seconds are discarded.";
                // Slow ML must not freeze the camera while capture continues. The old result and
                // enhanced image stay discarded; display a current raw frame with its own identity.
                if (Capture.LatestFrame is { } current && Capture.IsRunning &&
                    current.Epoch == Capture.Epoch && current.Epoch == frame.Epoch &&
                    current.Age <= TimeSpan.FromSeconds(2))
                {
                    _lastRawPreview = current;
                    _lastEnhancedPreview = null;
                    Preview = ToBitmap(current);
                    ProcessingText = "Showing current camera image while ML catches up.";
                }
                return;
            }
            _lastRawPreview = frame;
            _lastEnhancedPreview = result.Frame;
            Preview = UseEnhancedPreview ? prepared.Enhanced : prepared.Raw ?? ToBitmap(frame);
            if (prepared.Board is not null) BoardPreview = prepared.Board;
            UpdateProcessorStatus(result.Status);
            FormatText = $"Camera delivered {frame.Width} × {frame.Height}" +
                (Capture.NegotiatedFormat is { } format ? $" · {format.FramesPerSecond:0.#} fps · {format.Subtype}" : "");
            ProcessingText = $"Processing {result.Frame.Width} × {result.Frame.Height}" +
                (result.IsUpscaled ? $" · upscaled from {frame.Width} × {frame.Height}; not native 4K" : " · native source resolution") +
                $" · {result.ProcessingTime.TotalMilliseconds:0} ms. Enhancement adds no new captured detail.";
            if (prepared.Detection is { } detection && registration is not null && !IsModelBusy)
            {
                _outlinedFrame = frame;
                PublishMarkerScores(prepared.Scores);
                PieceOutlines = detection.Candidates.Select(candidate => new PreviewPieceOutline(
                    candidate.Kind == PieceCandidateKind.PlayerMarker,
                    candidate.DisplayOutline.Select(point => registration.MapToSensor(point.X, point.Y)).ToArray())).ToArray();
                ModelStatus = $"ML · {detection.Backend} · {detection.ModelId}" +
                    (_pieceModel?.FallbackReason is { Length: > 0 } reason ? " · " + reason : "");
                DetectionText = $"{TrainCountText.Format(detection.Candidates.Count(candidate => candidate.Kind == PieceCandidateKind.Train))} · " +
                    $"{detection.Candidates.Count(candidate => candidate.Kind == PieceCandidateKind.PlayerMarker)} score markers · " +
                    $"{detection.Elapsed.TotalMilliseconds:0} ms detection. Experimental ML outlines; check for missed or extra pieces.";
                _reviewDetection = new(board!, detection, frame.Width, frame.Height, cropRevision,
                    processingRevision, modelRevision, registration.Corners.ToArray());
                OnPropertyChanged(nameof(CanSaveDetectionExample));
            }
            else
            {
                ClearDetectionPreview();
                DetectionText = IsModelBusy ? "Loading the local ML model; camera preview remains available." :
                    prepared.Error is { } error ? "ML detection unavailable: " + error :
                    !showOutlines ? "Piece outlines are off." : registration is null ?
                    "Detect or select the four board corners to see ML piece outlines." : _pieceModel is null ?
                    "ML model unavailable. See the model status below." : "Waiting for a fresh image for ML piece outlines.";
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed && frame.Epoch == Capture.Epoch && cropRevision == _cropRevision &&
                processingRevision == _processingRevision && referenceRevision == _referenceRevision && modelRevision == _modelRevision)
            {
                ClearDetectionPreview();
                if (Capture.IsRunning && frame.Epoch == Capture.Epoch && frame.Age < TimeSpan.FromSeconds(2))
                    Preview = ToBitmap(frame);
                ProcessingText = "Image processing paused: " + ex.Message;
            }
        }
        finally { _processingFrame = false; }
    }

    private static CameraFrame RectifyForDetection(CameraFrame frame, BoardRegistration registration) =>
        BoardRegistration.Create(frame, registration.Corners).Rectify(frame, 1920, 1200);

    private static string DetectionStateText(PieceDetectionState state) => state switch
    {
        PieceDetectionState.Ready => "Compared with the empty board.",
        PieceDetectionState.SceneChanged => "The view changed too much. Return the camera to its reference position.",
        PieceDetectionState.Moving => "Waiting for the board to settle and hands to clear.",
        PieceDetectionState.InsufficientDetail => "Check focus and lighting.",
        PieceDetectionState.CameraChanged => "Camera changed. Set the empty-board reference again.",
        PieceDetectionState.Stale => "Waiting for a fresh camera image.",
        _ => "An empty-board reference is needed."
    };

    private void ClearDetectionPreview()
    {
        _outlinedFrame = null;
        _reviewDetection = null;
        PieceOutlines = [];
        ClearMarkerScores();
        OnPropertyChanged(nameof(CanSaveDetectionExample));
    }

    private void ResetPieceReference()
    {
        Interlocked.Increment(ref _referenceRevision);
        lock (_detectionGate) _pieceDetector.Clear();
        HasPieceReference = false;
        ClearDetectionPreview();
        _lastRawPreview = null;
        _lastEnhancedPreview = null;
        DetectionText = "Select the board corners to see ML piece outlines. No empty-board reference is needed.";
    }

    [RelayCommand]
    private void ClearPieceReference() => ResetPieceReference();

    [RelayCommand]
    private async Task CaptureEmptyBoardAsync()
    {
        if (!CanExportPhoto || IsProcessorBusy) return;
        IsBusy = true;
        try
        {
            var frame = Capture.GetFreshFrame(TimeSpan.FromSeconds(1));
            var registration = _registration!;
            var revision = _cropRevision;
            var processingRevision = _processingRevision;
            var referenceRevision = _referenceRevision;
            await InitializeProcessingAsync();
            var result = await _processor.ProcessAsync(frame, _lifetime.Token);
            var board = await Task.Run(() => RectifyForDetection(result.Frame, registration), _lifetime.Token);
            if (_disposed || !Capture.IsRunning || frame.Epoch != Capture.Epoch || revision != _cropRevision ||
                processingRevision != _processingRevision || referenceRevision != _referenceRevision)
                throw new InvalidOperationException("Camera, crop or reference changed. Capture the empty-board reference again.");
            AcceptProcessorStatus(result.Status);
            SetPieceReference(board);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Problem = "Empty-board reference could not be captured: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task LoadEmptyBoardAsync()
    {
        if (!CanExportPhoto || IsProcessorBusy) return;
        var dialog = new OpenFileDialog
        {
            Title = "Choose an empty-board crop exported from GoldenTicket",
            Filter = "Board photo (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        await LoadEmptyBoardPhotoAsync(dialog.FileName);
    }

    public async Task LoadEmptyBoardPhotoAsync(string path)
    {
        if (!CanExportPhoto || IsProcessorBusy) return;
        IsBusy = true;
        try
        {
            var frame = Capture.GetFreshFrame(TimeSpan.FromSeconds(1));
            var revision = _cropRevision;
            var processingRevision = _processingRevision;
            var referenceRevision = _referenceRevision;
            var imported = await Task.Run(() => ReadBoardReference(path, frame), _lifetime.Token);
            NormalizedPoint[] corners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            // Exported images can already be enhanced. Normalize their size without sharpening
            // again; repeatedly filtering a reference would introduce artificial differences.
            var board = await Task.Run(() => BoardRegistration.Create(imported, corners).Rectify(imported, 1920, 1200), _lifetime.Token);
            if (_disposed || !Capture.IsRunning || frame.Epoch != Capture.Epoch || revision != _cropRevision ||
                processingRevision != _processingRevision || referenceRevision != _referenceRevision)
                throw new InvalidOperationException("Camera, crop or reference changed. Load the empty-board photo again.");
            SetPieceReference(board);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Problem = "Empty-board photo could not be loaded: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private void SetPieceReference(CameraFrame board)
    {
        Interlocked.Increment(ref _referenceRevision);
        lock (_detectionGate) _pieceDetector.SetReference(board);
        HasPieceReference = true;
        ClearDetectionPreview();
        Problem = null;
        DetectionText = "Empty-board reference ready. Add trains and player score markers, then clear your hands to see candidate outlines.";
        _previewSequence = -1;
    }

    private static CameraFrame ReadBoardReference(string path, CameraFrame current)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 48 * 1024 * 1024) throw new InvalidDataException("Choose a PNG or JPEG board crop no larger than 48 MB.");
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        if (decoder is not (PngBitmapDecoder or JpegBitmapDecoder) || decoder.Frames.Count != 1)
            throw new InvalidDataException("Choose a single PNG or JPEG image.");
        var source = decoder.Frames[0];
        if (source.PixelWidth < 640 || source.PixelHeight < 400 || source.PixelWidth > CameraFrame.MaximumWidth ||
            source.PixelHeight > CameraFrame.MaximumHeight || Math.Abs((double)source.PixelWidth / source.PixelHeight - 1.6) > .05)
            throw new InvalidDataException("Choose the complete, upright empty-board crop exported by GoldenTicket (8:5 shape, up to 4K). Camera screenshots are not board crops.");
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[checked(source.PixelWidth * source.PixelHeight * 4)];
        converted.CopyPixels(bytes, source.PixelWidth * 4, 0);
        return CameraFrame.CopyFromBgra32(source.PixelWidth, source.PixelHeight, bytes,
            Math.Max(0, current.Sequence - 1), current.Epoch);
    }

    private async Task DisposeProcessingAsync()
    {
        if (_initialization is not null) await _initialization;
        await _frameWork;
        if (_modelInitialization is not null) await _modelInitialization;
        await _processor.DisposeAsync();
        lock (_detectionGate) _pieceDetector.Clear();
        lock (_modelGate)
        {
            _pieceModel?.Dispose();
            _pieceModel = null;
        }
        ClearDetectionPreview();
        _lastRawPreview = null;
        _lastEnhancedPreview = null;
    }
}
