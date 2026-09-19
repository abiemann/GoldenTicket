using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoldenTicket.Vision;
using Microsoft.Win32;

namespace GoldenTicket.Desktop.ViewModels;

public sealed record CameraPreferenceOption(CameraCapturePreference Value, string Label);
public sealed record CameraPhoto(byte[] PngBytes, long FrameSequence, long CameraEpoch,
    DateTimeOffset CapturedAt, int Width, int Height, bool BoardCropped, long BoardCropRevision, string CameraId);

/// <summary>
/// Live capture/crop and scene-comparison tools. This view model deliberately has no coordinator,
/// rules command, or route-verification interface. A photograph is operator evidence only.
/// </summary>
public sealed partial class CameraViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _gameTableCameraRetryTimer;
    private readonly Func<CancellationToken, Task<IReadOnlyList<CameraDevice>>> _enumerateDevices;
    private readonly Func<CameraDevice, CameraCapturePreference, CancellationToken, Task> _startCapture;
    private readonly SceneReferenceMonitor _monitor = new();
    private readonly CancellationTokenSource _lifetime = new();
    private BoardRegistration? _registration;
    private BoardRegistration? _gameTableRegistration;
    private bool _gameTablePreviewRequested;
    public bool IsGameTablePreviewRequested => _gameTablePreviewRequested;
    private bool _gameTableCameraAutoStart;
    private bool _gameTableCameraRecoveryEnabled = true;
    private bool _gameTableCameraRetryBusy;
    private DateTimeOffset _gameTableCameraNoFrameSince = DateTimeOffset.MinValue;
    private long _gameTableCropRevision;
    private bool _gameTableCropBusy;
    private DateTimeOffset _lastGameTableCropAt = DateTimeOffset.MinValue;
    private (long Epoch, int Width, int Height)? _cornerCapture;
    private long _previewSequence = -1;
    private long _cropRevision;
    private bool _disposed;

    public CameraViewModel(string? processingSettingsPath = null, string? pieceModelDirectory = null,
        Func<string, bool, IPieceModelDetector>? pieceModelFactory = null,
        string? boardCornerModelDirectory = null,
        Func<string, bool, IBoardCornerDetector>? boardCornerModelFactory = null,
        Func<CancellationToken, Task<IReadOnlyList<CameraDevice>>>? enumerateDevices = null,
        Func<CameraDevice, CameraCapturePreference, CancellationToken, Task>? startCapture = null)
    {
        _processingSettingsPath = processingSettingsPath;
        _pieceModelDirectory = pieceModelDirectory ?? Path.Combine(AppContext.BaseDirectory, "models", "pieces");
        _pieceModelFactory = pieceModelFactory ?? ((directory, preferGpu) => LearnedPieceDetector.Load(directory, preferGpu));
        _boardCornerModelDirectory = boardCornerModelDirectory ?? Path.Combine(AppContext.BaseDirectory, "models", "board-corners");
        _boardCornerModelFactory = boardCornerModelFactory ?? ((directory, preferGpu) => LearnedBoardCornerDetector.Load(directory, preferGpu));
        Capture = new CameraCaptureService();
        _enumerateDevices = enumerateDevices ?? CameraCaptureService.EnumerateAsync;
        _startCapture = startCapture ?? Capture.StartAsync;
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _previewTimer.Tick += PreviewTick;
        _gameTableCameraRetryTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _gameTableCameraRetryTimer.Tick += GameTableCameraRetryTick;
        SelectedPreference = Preferences.First(option => option.Value == CameraCapturePreference.Balanced1080p);
        SelectedProcessor = ProcessorModes.First(option => option.Value ==
            Services.FrameProcessingPreferences.Load(processingSettingsPath));
    }

    public CameraCaptureService Capture { get; }
    public ObservableCollection<CameraDevice> Devices { get; } = [];
    public ObservableCollection<string> AvailableFormats { get; } = [];
    public ObservableCollection<NormalizedPoint> SelectedCorners { get; } = [];
    public IReadOnlyList<CameraPreferenceOption> Preferences { get; } =
    [
        new(CameraCapturePreference.Balanced1080p, "1080p preferred · best available"),
        new(CameraCapturePreference.HighDetail2160p, "4K preferred · best available"),
        new(CameraCapturePreference.SharedCurrent, "Shared · current Windows format")
    ];

    [ObservableProperty] private CameraDevice? _selectedDevice;
    [ObservableProperty] private CameraPreferenceOption _selectedPreference = null!;
    [ObservableProperty] private BitmapSource? _preview;
    [ObservableProperty] private BitmapSource? _boardPreview;
    [ObservableProperty] private BitmapSource? _gameTablePreview;
    // Placement coordinates apply only to the upright crop accepted by game setup.
    [ObservableProperty] private bool _isGameTablePreviewUpright;
    [ObservableProperty] private string _gameTablePreviewStatus = "Waiting for the live board view.";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _safetyHeld = true;
    [ObservableProperty] private bool _selectingCorners;
    [ObservableProperty] private bool _hasBoardCrop;
    [ObservableProperty] private string _status = "Camera stopped. Connect a USB camera, or enable USB webcam mode on your Pixel, then refresh the list.";
    [ObservableProperty] private string _formatText = "No camera format negotiated";
    [ObservableProperty] private string _comparisonText = "No scene reference. Calibrated route checks use fresh game-table piece detections.";
    [ObservableProperty] private string _cropText = StartCropInstruction;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private string? _lastExportPath;
    public bool CanExportPhoto => IsRunning && HasBoardCrop && !IsBusy && !_disposed;
    public bool CanCapturePhoto => CanExportPhoto && !SafetyHeld;
    /// <summary>A fresh accepted game-table view can be photographed without a separate utility-screen crop.</summary>
    public bool CanCaptureGameTablePhoto => !_disposed && IsRunning && !IsBusy && Capture.IsRunning &&
        IsGameTablePreviewUpright && _gameTableRegistration is { } registration &&
        GameTableAnalysis is { } analysis && analysis.Board.Age <= TimeSpan.FromSeconds(2) &&
        analysis.CropRevision == _gameTableCropRevision && analysis.ModelRevision == _modelRevision &&
        Capture.LatestFrame is { } frame && frame.Age <= TimeSpan.FromSeconds(1) &&
        frame.Epoch == analysis.Board.Epoch && frame.Sequence >= analysis.Board.Sequence &&
        registration.Matches(frame) && Capture.ActiveDevice is not null;

    partial void OnIsRunningChanged(bool value)
    {
        NotifyPhotoAvailability();
        OnPropertyChanged(nameof(ShowGameBoardNotice));
        OnPropertyChanged(nameof(HasFreshGameBoardCorners));
        OnPropertyChanged(nameof(CanStartGameWithBoard));
    }
    partial void OnHasBoardCropChanged(bool value) => NotifyPhotoAvailability();
    partial void OnIsBusyChanged(bool value) => NotifyPhotoAvailability();
    partial void OnSafetyHeldChanged(bool value) => OnPropertyChanged(nameof(CanCapturePhoto));

    private void NotifyPhotoAvailability()
    {
        OnPropertyChanged(nameof(CanExportPhoto));
        OnPropertyChanged(nameof(CanCapturePhoto));
        OnPropertyChanged(nameof(CanCaptureGameTablePhoto));
        OnPropertyChanged(nameof(CanApplyProcessor));
        OnPropertyChanged(nameof(CanDetectBoardCorners));
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        if (IsBusy || _disposed) return;
        IsBusy = true;
        Problem = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var selectedId = SelectedDevice?.Id;
            var devices = await _enumerateDevices(timeout.Token);
            Devices.Clear();
            foreach (var device in devices) Devices.Add(device);
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == selectedId) ?? Devices.FirstOrDefault();
            Status = Devices.Count == 0
                ? "No camera found. Set the Pixel USB connection to Webcam, or connect a UVC camera, then refresh."
                : $"{Devices.Count} camera(s) found. Choose the overhead camera and start preview.";
        }
        catch (Exception ex)
        {
            Devices.Clear();
            SelectedDevice = null;
            Problem = "Could not list cameras: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsBusy || _disposed) return;
        if (SelectedDevice is null) { Problem = "Refresh the camera list and select a camera first."; return; }
        IsBusy = true;
        Problem = null;
        Status = "Starting camera… Windows may request camera permission.";
        try
        {
            InvalidateGameTablePreview();
            if (_gameTableCameraRetryBusy)
                GameTablePreviewStatus = "Webcam found. Starting the live board view…";
            ClearRegistration();
            await InitializeProcessingAsync();
            await _startCapture(SelectedDevice, SelectedPreference.Value, _lifetime.Token);
            IsRunning = true;
            _gameTableCameraNoFrameSince = DateTimeOffset.MinValue;
            FormatText = Capture.NegotiatedFormat?.ToString() ?? "Waiting for first frame";
            AvailableFormats.Clear();
            foreach (var format in Capture.AvailableFormats) AvailableFormats.Add(format.ToString());
            Status = "Live preview. Keep the whole board visible and clear your hands. ML will select its four corners.";
            _previewSequence = -1;
            _previewTimer.Start();
            if (_gameTablePreviewRequested)
                GameTablePreviewStatus = "Waiting for the webcam image…";
        }
        catch (Exception ex)
        {
            IsRunning = false;
            SafetyHeld = true;
            Preview = null;
            BoardPreview = null;
            Problem = ex.Message;
            Status = "Camera could not start. The game state has not changed.";
            if (_gameTablePreviewRequested)
                GameTablePreviewStatus = "Could not start the webcam. Check its connection; the game will keep trying.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (IsBusy || _disposed) return;
        IsBusy = true;
        // Clear readings and invalidate pending inference before camera teardown can wait.
        InvalidateGameTablePreview();
        ClearRegistration();
        try
        {
            _previewTimer.Stop();
            await Capture.StopAsync();
            IsRunning = false;
            ClearGameBoardFraming();
            GameBoardFramingStatus = "Waiting for the camera to find all four board corners.";
            Preview = null;
            BoardPreview = null;
            Status = "Camera stopped. Manual whole-board confirmation remains available.";
            FormatText = "No camera format negotiated";
        }
        catch (Exception ex) { Problem = "Could not stop camera: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private void PreviewTick(object? sender, EventArgs args)
    {
        if (_disposed) return;
        if (GameTableAnalysis is { } analysis && analysis.Board.Age > TimeSpan.FromSeconds(2))
            ClearGameTableAnalysis();
        if (_outlinedFrame is { } outlined && outlined.Age > TimeSpan.FromSeconds(2))
        {
            ClearDetectionPreview();
            DetectionText = "Waiting for a fresh processed image before outlining pieces.";
        }
        var frame = Capture.LatestFrame;
        if (!Capture.IsRunning || frame is null || frame.Age > TimeSpan.FromSeconds(2))
        {
            ClearGameTableAnalysis();
            if (_gameTableRegistration is not null)
            {
                GameTablePreview = null;
            }
            if (_gameTablePreviewRequested)
                GameTablePreviewStatus = Capture.IsRunning
                    ? "Waiting for the webcam image…"
                    : GameTableCameraConnectionMessage;
            ExpireGameBoardFraming(null);
            ClearDetectionPreview();
            _monitor.MarkStale();
            SafetyHeld = true;
            ComparisonText = "Camera unavailable or stale. Photo capture and scene comparison are held.";
            if (!Capture.IsRunning)
            {
                IsRunning = false;
                Preview = null;
                BoardPreview = null;
                Status = Capture.LastError ?? "The camera stopped. Reconnect it and start again.";
            }
            return;
        }
        ExpireGameBoardFraming(frame);
        if (frame.Sequence == _previewSequence) return;
        _previewSequence = frame.Sequence;
        try
        {
            // During play, spend inference time on the accepted board rather than a second
            // utility-screen enhancement/model pass. Keep its camera preview current and raw.
            if (Preview is null || !_processorReady || _gameTablePreviewRequested && IsGameTablePreviewUpright)
                Preview = ToBitmap(frame);
            if (!CornersMatch(frame)) ClearRegistration();
            if (_registration is { } registration)
            {
                if (!registration.Matches(frame)) ClearRegistration();
                else BoardPreview = ToBitmap(registration.Rectify(frame, 480, 300));
            }
            CheckLiveBoardAlignment(frame);
            QueueGameTablePreview(frame);
            QueueGameTableAnalysis(frame);
            var comparison = _monitor.Observe(frame);
            SafetyHeld = comparison.SafetyHeld;
            ComparisonText = comparison.State switch
            {
                SceneReferenceState.NoReference => "No scene reference. Set one after checking the whole board and clearing your hands.",
                SceneReferenceState.Stabilizing => "Waiting for a stable camera view…",
                SceneReferenceState.SimilarToReference => "Scene resembles the reference. That alone does not verify trains; the game table checks fresh pieces.",
                SceneReferenceState.SceneChanged => "The scene changed. Put the camera back, clear obstructions, or check the board before setting a new reference.",
                SceneReferenceState.CameraRestarted => "Camera or format changed. Select the board corners again and set a new reference.",
                SceneReferenceState.InsufficientDetail => "Too little visible detail. Check focus, lighting, camera cover and board framing.",
                _ => "Waiting for fresh camera frames."
            };
            if (_gameBoardFramingActive) QueueGameBoardFraming(frame);
            else if (!_gameTablePreviewRequested) QueueAutomaticCornerDetection(frame);
            if (!(_gameTablePreviewRequested && IsGameTablePreviewUpright))
                QueueFrameProcessing(frame);
        }
        catch (Exception ex)
        {
            SafetyHeld = true;
            _monitor.MarkStale();
            Problem = "Could not process the current camera frame: " + ex.Message;
        }
    }

    [RelayCommand]
    private void SetReference()
    {
        if (IsBusy || _disposed) return;
        try
        {
            var frame = Capture.GetFreshFrame(TimeSpan.FromSeconds(1));
            _monitor.SetReference(frame);
            SafetyHeld = true;
            Problem = null;
            ComparisonText = "Reference captured. Waiting for several fresh, stable frames.";
            Status = "A scene reference compares camera framing only. It does not identify trains or verify route ownership.";
        }
        catch (Exception ex) { Problem = ex.Message; }
    }

    [RelayCommand]
    private void ClearReference()
    {
        _monitor.Clear();
        SafetyHeld = true;
        ComparisonText = "Scene reference cleared. Route placement continues with explicit manual confirmation.";
    }

    [RelayCommand]
    private void BeginCornerSelection()
    {
        if (IsBusy || _disposed) return;
        try
        {
            var frame = Capture.GetFreshFrame(TimeSpan.FromSeconds(1));
            CancelCornerDetection();
            _autoCornerCapture = (frame.Epoch, frame.Width, frame.Height);
            CornerDetectionStatus = "Manual selection. Click the four outer corners, including the score track.";
            InvalidateCrop();
            _cornerCapture = (frame.Epoch, frame.Width, frame.Height);
            SelectedCorners.Clear();
            SelectingCorners = true;
            CropText = "Click 1: top-left corner of the board in the live image.";
            Problem = null;
        }
        catch (Exception ex) { Problem = ex.Message; }
    }

    public void AddBoardCorner(NormalizedPoint point)
    {
        if (_disposed || IsBusy || !SelectingCorners || SelectedCorners.Count >= 4 || !IsFinite(point) || !CanEditCurrentCapture()) return;
        CancelCornerDetection();
        InvalidateCrop();
        point = ClampPoint(point);
        SelectedCorners.Add(point);
        UpdateBoardCrop();
    }

    /// <summary>Moves an existing handle, retaining all handles even while the crop geometry is invalid.</summary>
    public bool MoveBoardCorner(int index, NormalizedPoint point)
    {
        if (_disposed || IsBusy || index < 0 || index >= SelectedCorners.Count || !IsFinite(point) || !CanEditCurrentCapture()) return false;
        point = ClampPoint(point);
        if (SelectedCorners[index] == point) return false;
        CancelCornerDetection();
        // Invalidate before notifying the view or encoding another photo with obsolete geometry.
        InvalidateCrop();
        SelectedCorners[index] = point;
        UpdateBoardCrop();
        return true;
    }

    private static bool IsFinite(NormalizedPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
    private static NormalizedPoint ClampPoint(NormalizedPoint point) => new(Math.Clamp(point.X, 0, 1), Math.Clamp(point.Y, 0, 1));

    private bool CornersMatch(CameraFrame frame) => _cornerCapture is not { } capture ||
        (capture.Epoch == frame.Epoch && capture.Width == frame.Width && capture.Height == frame.Height);

    private bool CanEditCurrentCapture()
    {
        if (_cornerCapture is not { } capture ||
            (capture.Epoch == Capture.Epoch && (Capture.LatestFrame is not { } frame || CornersMatch(frame)))) return true;
        ClearRegistration();
        Problem = "Camera or format changed. Select the four board corners again.";
        return false;
    }

    private void InvalidateCrop()
    {
        ResetPieceReference();
        _registration = null;
        _cropRevision++;
        HasBoardCrop = false;
        BoardPreview = null;
    }

    private void UpdateBoardCrop()
    {
        string[] labels = ["top-left", "top-right", "bottom-right", "bottom-left"];
        if (SelectedCorners.Count < 4)
        {
            CropText = $"Click {SelectedCorners.Count + 1}: {labels[SelectedCorners.Count]} corner of the board. Drag any numbered corner to adjust it.";
            Problem = null;
            return;
        }
        SelectingCorners = false;
        try
        {
            var frame = Capture.GetFreshFrame(TimeSpan.FromSeconds(1));
            if (!CornersMatch(frame))
            {
                ClearRegistration();
                Problem = "Camera or format changed. Select the four board corners again.";
                return;
            }
            var registration = BoardRegistration.Create(frame, SelectedCorners);
            var preview = ToBitmap(registration.Rectify(frame, 480, 300));
            _cornerCapture = (frame.Epoch, frame.Width, frame.Height);
            _registration = registration;
            BoardPreview = preview;
            HasBoardCrop = true;
            AdoptTechnicalBoardCrop(frame, registration);
            CropText = "Board photo crop selected. Drag any numbered corner to adjust it. Check that every route and the complete score track are inside the outline.";
            Problem = null;
        }
        catch (Exception ex)
        {
            CropText = "Crop is not ready. Adjust the numbered corners in clockwise order: top-left, top-right, bottom-right, bottom-left. A fresh camera view is required.";
            Problem = ex.Message;
        }
    }

    public Task<CameraPhoto> CapturePhotoAsync(CancellationToken cancellationToken = default) =>
        CaptureCroppedPhotoAsync(requireSceneReference: true, cancellationToken);

    /// <summary>
    /// Capture the accepted upright GAME TABLE board without the utility-screen crop or image enhancement.
    /// The caller must separately verify the physical train inventory against fresh model observations.
    /// </summary>
    public async Task<CameraPhoto> CaptureGameTablePhotoAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanCaptureGameTablePhoto)
            throw new InvalidOperationException("Wait for a fresh, upright GAME TABLE camera analysis before saving a board photo.");
        var registration = _gameTableRegistration!;
        var cropRevision = _gameTableCropRevision;
        var analysis = GameTableAnalysis!;
        var frame = Capture.GetFreshFrame(TimeSpan.FromSeconds(1));
        var cameraId = Capture.ActiveDevice!.Id;
        if (!registration.Matches(frame) || frame.Epoch != analysis.Board.Epoch ||
            frame.Sequence < analysis.Board.Sequence)
            throw new InvalidOperationException("Camera or board crop changed before the photo could be captured.");

        // The checkpoint records the camera pixels, not the utility preview's sharpening pipeline.
        var cropped = await Task.Run(() => registration.Rectify(frame, 3456, 2160), cancellationToken);
        var png = await cropped.EncodePngAsync(cancellationToken);
        if (_disposed || !IsRunning || !Capture.IsRunning || Capture.Epoch != frame.Epoch ||
            Capture.ActiveDevice?.Id != cameraId ||
            !IsGameTablePreviewUpright || !ReferenceEquals(registration, _gameTableRegistration) ||
            cropRevision != _gameTableCropRevision ||
            GameTableAnalysis is not { } currentAnalysis ||
            currentAnalysis.CropRevision != cropRevision ||
            currentAnalysis.ModelRevision != analysis.ModelRevision ||
            currentAnalysis.Board.Epoch != frame.Epoch ||
            Capture.LatestFrame is not { } latest || latest.Sequence < frame.Sequence ||
            latest.Epoch != frame.Epoch || !registration.Matches(latest))
        {
            CryptographicOperations.ZeroMemory(png);
            throw new InvalidOperationException("Camera, board crop or analysis changed while the photo was captured. Try again.");
        }
        return new(png, cropped.Sequence, cropped.Epoch, cropped.CapturedAt,
            cropped.Width, cropped.Height, true, cropRevision, cameraId);
    }

    /// <summary>A user-requested PNG export needs current crop geometry, independently of scene comparison.</summary>
    public Task<CameraPhoto> CaptureExportPhotoAsync(CancellationToken cancellationToken = default) =>
        CaptureCroppedPhotoAsync(requireSceneReference: false, cancellationToken);

    private async Task<CameraPhoto> CaptureCroppedPhotoAsync(bool requireSceneReference, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (requireSceneReference && (SafetyHeld || _monitor.Current.SafetyHeld))
            throw new InvalidOperationException("Wait for stable camera framing and set a scene reference before attaching a board photo.");
        var registration = _registration ?? throw new InvalidOperationException("Open Camera and select the four board corners before capturing a board photo.");
        var frame = Capture.GetFreshFrame(TimeSpan.FromSeconds(1));
        var cameraId = Capture.ActiveDevice?.Id
            ?? throw new InvalidOperationException("The active camera identity is unavailable. Restart the camera before capturing a photo.");
        var evidenceRevision = _monitor.Current.EvidenceRevision;
        var cropRevision = _cropRevision;
        // Snapshot identity is captured before encoding; callers bind it to their frozen game operation.
        // A 4K canvas with the board's 8:5 shape is 3456×2160. Keep checkpoint evidence
        // unsharpened; manual exports can use the deterministic enhancement pipeline.
        var processingRevision = _processingRevision;
        var cropped = await Task.Run(() => registration.Rectify(frame, 3456, 2160), cancellationToken);
        if (!requireSceneReference)
        {
            var processed = await _processor.ProcessAsync(cropped, cancellationToken);
            if (_disposed || !Capture.IsRunning || Capture.Epoch != frame.Epoch ||
                _cropRevision != cropRevision || processingRevision != _processingRevision)
                throw new InvalidOperationException("Camera, crop or processor changed during export. Try again.");
            cropped = processed.Frame;
            AcceptProcessorStatus(processed.Status);
            processingRevision = _processingRevision;
        }
        var png = await cropped.EncodePngAsync(cancellationToken);
        if (!Capture.IsRunning || Capture.Epoch != frame.Epoch || !ReferenceEquals(registration, _registration) || _cropRevision != cropRevision ||
            (!requireSceneReference && processingRevision != _processingRevision) ||
            (requireSceneReference && (SafetyHeld || _monitor.Current.SafetyHeld || _monitor.Current.EvidenceRevision != evidenceRevision)))
        {
            CryptographicOperations.ZeroMemory(png);
            throw new InvalidOperationException("Camera or crop changed while the photo was captured. Wait for the live preview and try again.");
        }
        return new(png, cropped.Sequence, cropped.Epoch, cropped.CapturedAt, cropped.Width, cropped.Height, true, cropRevision, cameraId);
    }

    public async Task<byte[]> CaptureFreshPngAsync(CancellationToken cancellationToken = default) =>
        (await CapturePhotoAsync(cancellationToken)).PngBytes;

    [RelayCommand]
    private async Task ExportSnapshotAsync()
    {
        if (!CanExportPhoto) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export board reference photo",
            Filter = "PNG image (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = "GoldenTicket-board-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png"
        };
        if (dialog.ShowDialog() != true) return;
        IsBusy = true;
        Problem = null;
        string? temporary = null;
        try
        {
            var photo = await CaptureExportPhotoAsync(_lifetime.Token);
            var target = Path.GetFullPath(dialog.FileName);
            temporary = Path.Combine(Path.GetDirectoryName(target)!, ".goldenticket-photo-" + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllBytesAsync(temporary, photo.PngBytes, _lifetime.Token);
            File.Move(temporary, target, overwrite: true);
            temporary = null;
            LastExportPath = target;
            Status = "Board reference photo exported. Exporting a photo does not save the game; use Save and pack away for a checkpoint.";
        }
        catch (Exception ex) { Problem = "Photo export failed: " + ex.Message; }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            IsBusy = false;
        }
    }

    private const string StartCropInstruction = "ML will try to select the four outer board corners when the preview starts. Use Detect board corners to try again, or Select four board corners to place them yourself.";

    /// <summary>Keep the accepted setup crop for the public table without opening another camera stream.</summary>
    public void BeginGameTablePreview()
    {
        if (!CanStartGameWithBoard || Capture.LatestFrame is not { } frame) return;
        CancelGameTablePreviewHandoff();
        BoardInteractionLog.Write("camera.board.preview-started", new
        {
            frame.Sequence, frame.Epoch, frame.Width, frame.Height
        });
        ClearGameTableAnalysis();
        _gameTablePreviewRequested = true;
        _gameTableCameraNoFrameSince = DateTimeOffset.MinValue;
        var orientation = _gameBoardOrientationIndex ?? 0;
        var padded = BoardCropPadding.Expand(frame, GameBoardCorners);
        var corners = GameBoardOrientations.Enumerate(padded.Corners)[orientation];
        _gameTableRegistration = BoardRegistration.Create(frame, corners);
        _gameTableReference = _acceptedSetupReference;
        _gameTableReferencePhoto = null;
        _gameTablePhotoAlignment = null;
        _lastLiveBoardCheckAt = DateTimeOffset.MinValue;
        IsGameTablePreviewUpright = _gameTableReference is not null &&
            _gameTableReference.IsAligned(_gameTableRegistration.Rectify(frame,
                BoardOrientationReference.Width, BoardOrientationReference.Height));
        _gameTableCropRevision++;
        _lastGameTableCropAt = DateTimeOffset.MinValue;
        GameTablePreviewStatus = IsGameTablePreviewUpright
            ? "Preparing the live board crop…"
            : "The board moved during setup. Checking its corners and orientation again…";
        if (IsGameTablePreviewUpright)
        {
            QueueGameTablePreview(frame);
            QueueGameTableAlignment(frame, _gameTableRegistration);
        }
        else QueueLiveBoardCheck(frame);
    }

    /// <summary>Used for restored games: an accepted technical crop can rejoin this table later.</summary>
    public void RequestGameTablePreview(bool reconnectCamera = false)
    {
        _gameTablePreviewRequested = true;
        // A restored game may open with no camera attached. An already-running play camera
        // should also recover if its stream is disconnected later.
        _gameTableCameraAutoStart = reconnectCamera || Capture.IsRunning;
        if (_gameTableCameraAutoStart) _gameTableCameraRetryTimer.Start();
        if (!Capture.IsRunning)
        {
            GameTablePreviewStatus = _gameTableCameraAutoStart
                ? GameTableCameraConnectionMessage
                : "Open Camera in the utility screens to find the board again.";
            if (_gameTableCameraAutoStart) _ = RetryGameTableCameraAsync();
            return;
        }
        if (_gameTableRegistration is not null) return;
        if (_registration is { } registration && Capture.LatestFrame is { } frame && registration.Matches(frame))
            AdoptTechnicalBoardCrop(frame, registration);
        else
        {
            GameTablePreviewStatus = _gameTableReference is null
                ? "Board orientation is unverified. Open Camera to find the board again."
                : "Checking the board corners and orientation…";
            if (Capture.LatestFrame is { } current) QueueLiveBoardCheck(current);
        }
    }

    public void EndGameTablePreview()
    {
        _gameTablePreviewRequested = false;
        _gameTableCameraAutoStart = false;
        _gameTableCameraNoFrameSince = DateTimeOffset.MinValue;
        _gameTableCameraRetryTimer.Stop();
        _gameTableReference = null;
        _gameTableReferencePhoto = null;
        _gameTablePhotoAlignment = null;
        InvalidateGameTablePreview();
    }

    private const string GameTableCameraConnectionMessage =
        "Please connect a webcam. The game will find it and check the board automatically.";

    public void SetGameTableCameraRecoveryEnabled(bool enabled)
    {
        _gameTableCameraRecoveryEnabled = enabled;
        if (enabled && _gameTablePreviewRequested && _gameTableCameraAutoStart && !Capture.IsRunning)
            _ = RetryGameTableCameraAsync();
    }

    private async void GameTableCameraRetryTick(object? sender, EventArgs args) =>
        await RetryGameTableCameraAsync();

    private async Task RetryGameTableCameraAsync()
    {
        if (!_gameTablePreviewRequested || !_gameTableCameraAutoStart || !_gameTableCameraRecoveryEnabled || _disposed ||
            _gameTableCameraRetryBusy || IsBusy) return;
        _gameTableCameraRetryBusy = true;
        try
        {
            if (Capture.IsRunning)
            {
                if (Capture.LatestFrame is { } frame && frame.Age <= TimeSpan.FromSeconds(2))
                {
                    _gameTableCameraNoFrameSince = DateTimeOffset.MinValue;
                    return;
                }
                if (_gameTableCameraNoFrameSince == DateTimeOffset.MinValue)
                {
                    _gameTableCameraNoFrameSince = DateTimeOffset.UtcNow;
                    return;
                }
                if (DateTimeOffset.UtcNow - _gameTableCameraNoFrameSince < TimeSpan.FromSeconds(12)) return;
                _gameTableCameraNoFrameSince = DateTimeOffset.MinValue;
                await StopAsync();
                if (!_gameTablePreviewRequested || !_gameTableCameraRecoveryEnabled || _disposed ||
                    Capture.IsRunning) return;
            }
            GameTablePreviewStatus = GameTableCameraConnectionMessage;
            await RefreshDevicesAsync();
            if (!_gameTablePreviewRequested || !_gameTableCameraRecoveryEnabled || _disposed ||
                Capture.IsRunning) return;
            if (SelectedDevice is null) return;
            GameTablePreviewStatus = "Webcam found. Starting the live board view…";
            await StartAsync();
            if ((!_gameTablePreviewRequested || !_gameTableCameraRecoveryEnabled) && IsRunning)
                await StopAsync();
        }
        catch (Exception ex)
        {
            // Discovery and startup normally report their own errors. Keep retrying if a device
            // disappears between enumeration and opening its stream.
            Problem = "Could not reconnect the webcam: " + ex.Message;
            GameTablePreviewStatus = GameTableCameraConnectionMessage;
        }
        finally { _gameTableCameraRetryBusy = false; }
    }

    private void InvalidateGameTablePreview()
    {
        CancelGameTablePreviewHandoff();
        ClearGameTableAnalysis();
        _gameTableRegistration = null;
        _gameTableAlignmentPendingRevision = null;
        IsGameTablePreviewUpright = false;
        _gameTableCropRevision++;
        GameTablePreview = null;
        GameTablePreviewStatus = _gameTablePreviewRequested
            ? "Open Camera in the utility screens to find the board again."
            : "Waiting for the live board view.";
        OnPropertyChanged(nameof(CanCaptureGameTablePhoto));
    }

    private void AdoptTechnicalBoardCrop(CameraFrame frame, BoardRegistration registration)
    {
        if (!_gameTablePreviewRequested) return;
        if (_gameTableReference is not { } reference)
        {
            HoldLiveBoardAlignment("Board orientation is unverified. A saved board photo or new setup is needed.");
            return;
        }
        // A manually edited utility crop is authoritative for its *corners*, but image ordering
        // says nothing about physical orientation. Evaluate all four rotations before adopting it.
        var registrations = GameBoardOrientations.Enumerate(registration.Corners)
            .Select(corners => BoardRegistration.Create(frame, corners)).ToArray();
        var crops = registrations.Select(candidate => candidate.Rectify(frame,
            BoardOrientationReference.Width, BoardOrientationReference.Height)).ToArray();
        var orientation = reference.ChooseOrientation(crops);
        if (orientation is null)
        {
            HoldLiveBoardAlignment("The edited crop does not clearly match the upright board. Adjust its corners or lighting.");
            return;
        }
        ClearGameTableAnalysis();
        _gameTableRegistration = registrations[orientation.Value];
        _gameTableCropRevision++;
        _lastGameTableCropAt = DateTimeOffset.MinValue;
        IsGameTablePreviewUpright = true;
        BeginGameTablePreviewHandoff();
        QueueGameTablePreview(frame);
        QueueGameTableAlignment(frame, _gameTableRegistration);
    }

    private void QueueGameTablePreview(CameraFrame frame)
    {
        if (!IsGameTablePreviewUpright || _gameTableRegistration is not { } registration || _gameTableCropBusy) return;
        if (!registration.Matches(frame))
        {
            InvalidateGameTablePreview();
            GameTablePreviewStatus = "Camera format changed. Recheck the board framing.";
            return;
        }
        if (DateTimeOffset.UtcNow - _lastGameTableCropAt < TimeSpan.FromMilliseconds(350)) return;
        _lastGameTableCropAt = DateTimeOffset.UtcNow;
        _ = RenderGameTablePreviewAsync(frame, registration, _gameTableCropRevision);
    }

    private async Task RenderGameTablePreviewAsync(CameraFrame frame, BoardRegistration registration, long revision)
    {
        _gameTableCropBusy = true;
        try
        {
            var bitmap = await Task.Run(() => ToBitmap(registration.Rectify(frame, 960, 600)), _lifetime.Token);
            if (_disposed || !IsGameTablePreviewUpright || revision != _gameTableCropRevision ||
                !ReferenceEquals(registration, _gameTableRegistration) || !Capture.IsRunning ||
                Capture.LatestFrame is not { } latest || latest.Age > TimeSpan.FromSeconds(2) ||
                !registration.Matches(latest)) return;
            var wasHandoff = _gameTableHandoffRevision == revision;
            var handoffMs = wasHandoff
                ? Math.Round((DateTimeOffset.UtcNow - _gameTableHandoffStartedAt).TotalMilliseconds)
                : (double?)null;
            var handoffBlackedOut = wasHandoff && _gameTableHandoffBlackedOut;
            if (wasHandoff) CancelGameTablePreviewHandoff();
            GameTablePreview = bitmap;
            GameTablePreviewStatus = "";
            if (_lastLoggedGameTablePreviewRevision != revision)
            {
                _lastLoggedGameTablePreviewRevision = revision;
                BoardInteractionLog.Write("camera.board.preview-ready", new
                {
                    frame.Sequence, frame.Epoch, cropRevision = revision,
                    handoffMs, handoffBlackedOut
                });
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (revision != _gameTableCropRevision || _disposed) return;
            GameTablePreviewStatus = "The live board crop is unavailable: " + ex.Message;
        }
        finally { _gameTableCropBusy = false; }
    }

    private void ClearRegistration()
    {
        CancelCornerDetection();
        _autoCornerCapture = null;
        CornerDetectionStatus = "Waiting for a fresh camera image to locate the board.";
        InvalidateCrop();
        _cornerCapture = null;
        SelectingCorners = false;
        SelectedCorners.Clear();
        BoardPreview = null;
        _monitor.Clear();
        SafetyHeld = true;
        CropText = StartCropInstruction;
        ComparisonText = "Camera registration and scene reference need to be set for this capture session.";
    }

    private static BitmapSource ToBitmap(CameraFrame frame)
    {
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32,
            null, frame.Bgra32.ToArray(), frame.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        ClearDetectionPreview();
        NotifyPhotoAvailability();
        _lifetime.Cancel();
        _previewTimer.Stop();
        _previewTimer.Tick -= PreviewTick;
        _gameTableCameraRetryTimer.Stop();
        _gameTableCameraRetryTimer.Tick -= GameTableCameraRetryTick;
        try { await Capture.DisposeAsync(); }
        finally
        {
            try
            {
                await _gameTableAnalysisWork;
                await _liveBoardCheckWork;
                await _gameTableAlignmentWork;
                await DisposeCornerDetectionAsync();
                await DisposeProcessingAsync();
            }
            finally { _lifetime.Dispose(); }
        }
    }
}
