using System.Collections.ObjectModel;
using System.IO;
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
    private long _previewSequence = -1;
    private long _cropRevision;
    private bool _disposed;

    public CameraViewModel()
    {
        Capture = new CameraCaptureService();
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _previewTimer.Tick += PreviewTick;
        SelectedPreference = Preferences[0];
    }

    public CameraCaptureService Capture { get; }
    public ObservableCollection<CameraDevice> Devices { get; } = [];
    public ObservableCollection<string> AvailableFormats { get; } = [];
    public ObservableCollection<NormalizedPoint> SelectedCorners { get; } = [];
    public IReadOnlyList<CameraPreferenceOption> Preferences { get; } =
    [
        new(CameraCapturePreference.Balanced1080p, "Balanced · up to 1080p"),
        new(CameraCapturePreference.HighDetail2160p, "High detail · up to 4K"),
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
    [ObservableProperty] private string _cropText = "Select all four outer board corners to crop reference photos.";
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private string? _lastExportPath;
    public string ComputeStatus => "▣ CPU · image processing";
    public string ComputeExplanation => "Live preview, perspective crop and scene comparison run locally on the CPU. No train-recognition model or GPU inference backend is installed.";
    public bool CanCapturePhoto => IsRunning && HasBoardCrop && !SafetyHeld && !IsBusy;

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanCapturePhoto));
    partial void OnHasBoardCropChanged(bool value) => OnPropertyChanged(nameof(CanCapturePhoto));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanCapturePhoto));
    partial void OnSafetyHeldChanged(bool value) => OnPropertyChanged(nameof(CanCapturePhoto));

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
            await Capture.StartAsync(SelectedDevice, SelectedPreference.Value, _lifetime.Token);
            IsRunning = true;
            FormatText = Capture.NegotiatedFormat?.ToString() ?? "Waiting for first frame";
            AvailableFormats.Clear();
            foreach (var format in Capture.AvailableFormats) AvailableFormats.Add(format.ToString());
            Status = "Live preview. Fit the whole board in the image, clear your hands, then set a scene reference.";
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
        try
        {
            _previewTimer.Stop();
            await Capture.StopAsync();
            IsRunning = false;
            Preview = null;
            BoardPreview = null;
            ClearRegistration();
            Status = "Camera stopped. Manual whole-board confirmation remains available.";
            FormatText = "No camera format negotiated";
        }
        catch (Exception ex) { Problem = "Could not stop camera: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private void PreviewTick(object? sender, EventArgs args)
    {
        if (_disposed) return;
        var frame = Capture.LatestFrame;
        if (!Capture.IsRunning || frame is null || frame.Age > TimeSpan.FromSeconds(2))
        {
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
        if (frame.Sequence == _previewSequence) return;
        _previewSequence = frame.Sequence;
        try
        {
            Preview = ToBitmap(frame);
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
            Capture.GetFreshFrame(TimeSpan.FromSeconds(1));
            SelectedCorners.Clear();
            _registration = null;
            _cropRevision++;
            BoardPreview = null;
            HasBoardCrop = false;
            SelectingCorners = true;
            CropText = "Click 1: top-left corner of the board in the live image.";
            Problem = null;
        }
        catch (Exception ex) { Problem = ex.Message; }
    }

    public void AddBoardCorner(NormalizedPoint point)
    {
        if (!SelectingCorners || SelectedCorners.Count >= 4) return;
        SelectedCorners.Add(point);
        string[] labels = ["top-left", "top-right", "bottom-right", "bottom-left"];
        if (SelectedCorners.Count < 4)
        {
            CropText = $"Click {SelectedCorners.Count + 1}: {labels[SelectedCorners.Count]} corner of the board.";
            return;
        }
        SelectingCorners = false;
        try
        {
            var frame = Capture.GetFreshFrame(TimeSpan.FromSeconds(1));
            _registration = BoardRegistration.Create(frame, SelectedCorners);
            _cropRevision++;
            BoardPreview = ToBitmap(_registration.Rectify(frame, 480, 300));
            HasBoardCrop = true;
            CropText = "Board photo crop selected. Check the preview includes every route and score edge. This is a manual crop, not verified board registration.";
            Problem = null;
        }
        catch (Exception ex)
        {
            ClearRegistration();
            Problem = ex.Message;
        }
    }

    public async Task<CameraPhoto> CapturePhotoAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (SafetyHeld || _monitor.Current.SafetyHeld)
            throw new InvalidOperationException("Wait for stable camera framing and set a scene reference before attaching a board photo.");
        var registration = _registration ?? throw new InvalidOperationException("Open Camera and select the four board corners before attaching a board photo.");
        var frame = Capture.GetFreshFrame(TimeSpan.FromSeconds(1));
        var cameraId = Capture.ActiveDevice?.Id
            ?? throw new InvalidOperationException("The active camera identity is unavailable. Restart the camera before attaching a photo.");
        var evidenceRevision = _monitor.Current.EvidenceRevision;
        var cropRevision = _cropRevision;
        // Snapshot identity is captured before encoding; callers bind it to their frozen game operation.
        var cropped = await Task.Run(() => registration.Rectify(frame, 1920, 1200), cancellationToken);
        var png = await cropped.EncodePngAsync(cancellationToken);
        if (!Capture.IsRunning || Capture.Epoch != frame.Epoch || !ReferenceEquals(registration, _registration) ||
            SafetyHeld || _monitor.Current.SafetyHeld || _monitor.Current.EvidenceRevision != evidenceRevision)
            throw new InvalidOperationException("Camera or crop changed while the photo was captured. Wait for the live preview and try again.");
        return new(png, cropped.Sequence, cropped.Epoch, cropped.CapturedAt, cropped.Width, cropped.Height, true, cropRevision, cameraId);
    }

    public async Task<byte[]> CaptureFreshPngAsync(CancellationToken cancellationToken = default) =>
        (await CapturePhotoAsync(cancellationToken)).PngBytes;

    [RelayCommand]
    private async Task ExportSnapshotAsync()
    {
        if (IsBusy || _disposed) return;
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
            var photo = await CapturePhotoAsync(_lifetime.Token);
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

    private void ClearRegistration()
    {
        _registration = null;
        _cropRevision++;
        HasBoardCrop = false;
        SelectingCorners = false;
        SelectedCorners.Clear();
        BoardPreview = null;
        _monitor.Clear();
        SafetyHeld = true;
        CropText = "Select all four outer board corners to crop reference photos.";
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
        _lifetime.Cancel();
        _previewTimer.Stop();
        _previewTimer.Tick -= PreviewTick;
        await Capture.DisposeAsync();
        _lifetime.Dispose();
    }
}
