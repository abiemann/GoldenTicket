using CommunityToolkit.Mvvm.ComponentModel;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class CameraViewModel
{
    private readonly Func<CameraDevice, CancellationToken, Task<IReadOnlyList<CameraFormat>>> _getCameraFormats;
    private readonly SemaphoreSlim _cameraCapabilitiesGate = new(1, 1);
    private CancellationTokenSource? _cameraCapabilitiesCancellation;
    private Task _cameraCapabilitiesWork = Task.CompletedTask;
    private long _cameraCapabilitiesRevision;
    private bool _cameraCapabilitiesEnabled;
    private IReadOnlyList<CameraFormat>? _selectedCameraFormats;
    private CameraFrameDimensions? _lastQualityDimensions;

    [ObservableProperty] private bool _hasNative4K;
    [ObservableProperty] private bool _isCheckingCameraCapabilities;
    [ObservableProperty] private string _cameraCompatibilityMessage = "";
    public bool HasCameraCompatibilityMessage => !string.IsNullOrWhiteSpace(CameraCompatibilityMessage);

    partial void OnCameraCompatibilityMessageChanged(string value) =>
        OnPropertyChanged(nameof(HasCameraCompatibilityMessage));

    partial void OnSelectedDeviceChanged(CameraDevice? value)
    {
        // Picker refreshes can temporarily clear selection. That must not forget which
        // webcam to wait for, or replace it with another camera moved by the collection.
        if (!_refreshingDeviceList && value is not null) RememberCamera(value);
        _cameraCapabilitiesRevision++;
        _cameraCapabilitiesCancellation?.Cancel();
        _selectedCameraFormats = null;
        _lastQualityDimensions = null;
        HasNative4K = false;
        IsCheckingCameraCapabilities = false;
        CameraCompatibilityMessage = "";
        UpdateCameraQualityOptions();
        // Selecting fixture data must not initialize Windows camera hardware. Discovery or an
        // explicit capability request opts in; injected probes are safe to use immediately.
        if (_cameraCapabilitiesEnabled && value is not null && !_disposed)
            _cameraCapabilitiesWork = LoadCameraCapabilitiesAsync(value);
    }

    public Task RefreshSelectedCameraCapabilitiesAsync()
    {
        if (_disposed || SelectedDevice is not { } device) return Task.CompletedTask;
        _cameraCapabilitiesEnabled = true;
        if (IsCheckingCameraCapabilities) return _cameraCapabilitiesWork;
        return _cameraCapabilitiesWork = LoadCameraCapabilitiesAsync(device);
    }

    private async Task LoadCameraCapabilitiesAsync(CameraDevice device)
    {
        var revision = ++_cameraCapabilitiesRevision;
        _cameraCapabilitiesCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _cameraCapabilitiesCancellation = cancellation;
        cancellation.CancelAfter(TimeSpan.FromSeconds(12));
        IsCheckingCameraCapabilities = true;
        if (_selectedCameraFormats is null) CameraCompatibilityMessage = "Checking webcam quality…";
        var entered = false;
        try
        {
            await _cameraCapabilitiesGate.WaitAsync(cancellation.Token);
            entered = true;
            // Querying an already-open device again can contend with exclusive capture.
            var formats = Capture.IsRunning && Capture.ActiveDevice?.Id == device.Id
                ? Capture.AvailableFormats
                : await _getCameraFormats(device, cancellation.Token);
            if (_disposed || revision != _cameraCapabilitiesRevision || SelectedDevice?.Id != device.Id) return;
            ApplyCameraCapabilities(formats);
            UpdateDeliveredCameraQuality();
        }
        catch (Exception ex)
        {
            if (_disposed || revision != _cameraCapabilitiesRevision || SelectedDevice?.Id != device.Id) return;
            _selectedCameraFormats = null;
            HasNative4K = false;
            UpdateCameraQualityOptions();
            CameraCompatibilityMessage = ex is OperationCanceledException
                ? "The webcam quality check timed out. Start preview to check the camera's current format."
                : "Could not check this webcam's quality. Start preview to check the camera's current format.";
        }
        finally
        {
            if (entered) _cameraCapabilitiesGate.Release();
            if (ReferenceEquals(_cameraCapabilitiesCancellation, cancellation))
                _cameraCapabilitiesCancellation = null;
            if (!_disposed && revision == _cameraCapabilitiesRevision) IsCheckingCameraCapabilities = false;
        }
    }

    private void ApplyCameraCapabilities(IReadOnlyList<CameraFormat> formats)
    {
        _selectedCameraFormats = formats.ToArray();
        _lastQualityDimensions = null;
        HasNative4K = CameraFormatPolicy.Supports4K(formats);
        UpdateCameraQualityOptions();
        var usable = formats.Where(CameraFormatPolicy.IsUsableFormat)
            .OrderByDescending(format => (long)format.Width * format.Height).FirstOrDefault();
        CameraCompatibilityMessage = usable is null
            ? "This webcam is not compatible. Gameplay needs at least 720p (1280 × 720). Choose a 1080p or better webcam."
            : CameraFormatPolicy.Supports1080p(formats) ? "" : ReducedCameraQualityMessage(usable.Width, usable.Height);
    }

    private void UpdateCameraQualityOptions()
    {
        var selected = SelectedPreference?.Value ?? CameraCapturePreference.AutoBest;
        var autoLabel = "Auto · best native quality";
        if (_selectedCameraFormats is { } formats &&
            CameraFormatPolicy.RankFormats(formats, CameraCapturePreference.AutoBest).FirstOrDefault() is { } best)
            autoLabel = $"Auto · {best.Width} × {best.Height}";
        List<CameraPreferenceOption> options = [new(CameraCapturePreference.AutoBest, autoLabel),
            new(CameraCapturePreference.Balanced1080p, "1080p preferred")];
        if (HasNative4K) options.Add(new(CameraCapturePreference.HighDetail2160p, "4K preferred"));
        options.Add(new(CameraCapturePreference.Native720p, "720p"));
        options.Add(new(CameraCapturePreference.SharedCurrent, "Shared · current Windows format"));
        Preferences = options;
        OnPropertyChanged(nameof(Preferences));
        SelectedPreference = options.FirstOrDefault(option => option.Value == selected) ?? options[0];
    }

    private void UpdateDeliveredCameraQuality()
    {
        if (!Capture.IsRunning || Capture.ActiveDevice?.Id != SelectedDevice?.Id) return;
        var dimensions = Capture.DeliveredFrameDimensions ?? (Capture.NegotiatedFormat is { } format
            ? new CameraFrameDimensions(format.Width, format.Height) : null);
        if (dimensions is null || dimensions == _lastQualityDimensions) return;
        _lastQualityDimensions = dimensions;
        CameraCompatibilityMessage = CameraFormatPolicy.GetResolutionTier(dimensions.Width, dimensions.Height) switch
        {
            CameraResolutionTier.Incompatible =>
                "This webcam is delivering less than 720p and is not compatible. Choose a 1080p or better webcam.",
            CameraResolutionTier.Hd720p => ReducedCameraQualityMessage(dimensions.Width, dimensions.Height),
            _ => ""
        };
    }

    private static string ReducedCameraQualityMessage(int width, int height) =>
        $"Using {width} × {height}. A 1080p webcam is recommended. Gameplay and train detection may be less reliable in poor lighting.";
}
