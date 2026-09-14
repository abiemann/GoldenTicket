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
    private readonly SceneReferenceMonitor _monitor = new();
    private readonly CancellationTokenSource _lifetime = new();
    private BoardRegistration? _registration;
    private (long Epoch, int Width, int Height)? _cornerCapture;
    private long _previewSequence = -1;
    private long _cropRevision;
    private bool _disposed;

    public CameraViewModel(string? processingSettingsPath = null, string? pieceModelDirectory = null,
        Func<string, bool, IPieceModelDetector>? pieceModelFactory = null,
        string? boardCornerModelDirectory = null,
        Func<string, bool, IBoardCornerDetector>? boardCornerModelFactory = null)
    {
        _processingSettingsPath = processingSettingsPath;
        _pieceModelDirectory = pieceModelDirectory ?? Path.Combine(AppContext.BaseDirectory, "models", "pieces");
        _pieceModelFactory = pieceModelFactory ?? ((directory, preferGpu) => LearnedPieceDetector.Load(directory, preferGpu));
        _boardCornerModelDirectory = boardCornerModelDirectory ?? Path.Combine(AppContext.BaseDirectory, "models", "board-corners");
        _boardCornerModelFactory = boardCornerModelFactory ?? ((directory, preferGpu) => LearnedBoardCornerDetector.Load(directory, preferGpu));
        Capture = new CameraCaptureService();
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _previewTimer.Tick += PreviewTick;
        SelectedPreference = Preferences[0];
        SelectedProcessor = ProcessorModes.First(option => option.Value ==
            Services.FrameProcessingPreferences.Load(processingSettingsPath));
    }

    public CameraCaptureService Capture { get; }
    public ObservableCollection<CameraDevice> Devices { get; } = [];
    public ObservableCollection<string> AvailableFormats { get; } = [];
    public ObservableCollection<NormalizedPoint> SelectedCorners { get; } = [];
    public IReadOnlyList<CameraPreferenceOption> Preferences { get; } =
    [
        new(CameraCapturePreference.HighDetail2160p, "4K preferred · best available"),
        new(CameraCapturePreference.Balanced1080p, "Balanced · up to 1080p"),
        new(CameraCapturePreference.SharedCurrent, "Shared · current Windows format")
    ];

    [ObservableProperty] private CameraDevice? _selectedDevice;
    [ObservableProperty] private CameraPreferenceOption _selectedPreference = null!;
    [ObservableProperty] private BitmapSource? _preview;
    [ObservableProperty] private BitmapSource? _boardPreview;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _safetyHeld = true;
    [ObservableProperty] private bool _selectingCorners;
    [ObservableProperty] private bool _hasBoardCrop;
    [ObservableProperty] private string _status = "Camera stopped. Connect a USB camera, or enable USB webcam mode on your Pixel, then refresh the list.";
    [ObservableProperty] private string _formatText = "No camera format negotiated";
    [ObservableProperty] private string _comparisonText = "No scene reference. Route placement uses manual whole-board confirmation.";
    [ObservableProperty] private string _cropText = StartCropInstruction;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private string? _lastExportPath;
    public bool CanExportPhoto => IsRunning && HasBoardCrop && !IsBusy && !_disposed;
    public bool CanCapturePhoto => CanExportPhoto && !SafetyHeld;

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
            var devices = await CameraCaptureService.EnumerateAsync(timeout.Token);
            Devices.Clear();
            foreach (var device in devices) Devices.Add(device);
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == selectedId) ?? Devices.FirstOrDefault();
            Status = Devices.Count == 0
                ? "No camera found. Set the Pixel USB connection to Webcam, or connect a UVC camera, then refresh."
                : $"{Devices.Count} camera(s) found. Choose the overhead camera and start preview.";
        }
        catch (Exception ex) { Problem = "Could not list cameras: " + ex.Message; }
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
            ClearRegistration();
            await InitializeProcessingAsync();
            await Capture.StartAsync(SelectedDevice, SelectedPreference.Value, _lifetime.Token);
            IsRunning = true;
            FormatText = Capture.NegotiatedFormat?.ToString() ?? "Waiting for first frame";
            AvailableFormats.Clear();
            foreach (var format in Capture.AvailableFormats) AvailableFormats.Add(format.ToString());
            Status = "Live preview. Keep the whole board visible and clear your hands. ML will select its four corners.";
            _previewSequence = -1;
            _previewTimer.Start();
        }
        catch (Exception ex)
        {
            IsRunning = false;
            SafetyHeld = true;
            Preview = null;
            BoardPreview = null;
            Problem = ex.Message;
            Status = "Camera could not start. The game state has not changed.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (IsBusy || _disposed) return;
        IsBusy = true;
        // Clear readings and invalidate pending inference before camera teardown can wait.
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
        if (_outlinedFrame is { } outlined && outlined.Age > TimeSpan.FromSeconds(2))
        {
            ClearDetectionPreview();
            DetectionText = "Waiting for a fresh processed image before outlining pieces.";
        }
        var frame = Capture.LatestFrame;
        if (!Capture.IsRunning || frame is null || frame.Age > TimeSpan.FromSeconds(2))
        {
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
            if (Preview is null || !_processorReady) Preview = ToBitmap(frame);
            if (!CornersMatch(frame)) ClearRegistration();
            if (_registration is { } registration)
            {
                if (!registration.Matches(frame)) ClearRegistration();
                else BoardPreview = ToBitmap(registration.Rectify(frame, 480, 300));
            }
            var comparison = _monitor.Observe(frame);
            SafetyHeld = comparison.SafetyHeld;
            ComparisonText = comparison.State switch
            {
                SceneReferenceState.NoReference => "No scene reference. Set one after checking the whole board and clearing your hands.",
                SceneReferenceState.Stabilizing => "Waiting for a stable camera view…",
                SceneReferenceState.SimilarToReference => "Scene resembles the reference. Trains still require manual whole-board confirmation.",
                SceneReferenceState.SceneChanged => "The scene changed. Put the camera back, clear obstructions, or check the board before setting a new reference.",
                SceneReferenceState.CameraRestarted => "Camera or format changed. Select the board corners again and set a new reference.",
                SceneReferenceState.InsufficientDetail => "Too little visible detail. Check focus, lighting, camera cover and board framing.",
                _ => "Waiting for fresh camera frames."
            };
            if (_gameBoardFramingActive) QueueGameBoardFraming(frame);
            else QueueAutomaticCornerDetection(frame);
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
        try { await Capture.DisposeAsync(); }
        finally
        {
            try
            {
                await DisposeCornerDetectionAsync();
                await DisposeProcessingAsync();
            }
            finally { _lifetime.Dispose(); }
        }
    }
}
