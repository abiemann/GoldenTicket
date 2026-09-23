using GoldenTicket.Testing;
using System.Collections.Specialized;
using System.Reflection;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class CameraCapabilitiesTests
{
    private static readonly CameraDevice FullHdDevice = new("full-hd", "1080p webcam");
    private static readonly CameraDevice FourKDevice = new("four-k", "4K webcam");
    private static readonly CameraDevice HdDevice = new("hd", "720p webcam");
    private static readonly CameraFormat FullHd = new(1920, 1080, 30, "MJPG");
    private static readonly CameraFormat FourK = new(3840, 2160, 30, "MJPG");
    private static readonly CameraFormat Hd = new(1280, 720, 30, "MJPG");

    [Fact]
    public async Task Unknown_camera_offers_no_unverified_resolution_or_opens_hardware_on_selection()
    {
        await using var camera = new CameraViewModel(capture: new FakeCameraCapture());
        camera.SelectedDevice = FourKDevice;

        Assert.False(camera.HasNative4K);
        Assert.False(camera.IsCheckingCameraCapabilities);
        Assert.False(camera.IsRunning);
        Assert.Equal(new[] { CameraCapturePreference.AutoBest, CameraCapturePreference.SharedCurrent },
            camera.Preferences.Select(option => option.Value));
    }

    [Fact]
    public async Task Native_1080p_camera_offers_only_its_advertised_resolutions_without_warning()
    {
        await using var camera = CreateCamera([FullHd, Hd]);
        camera.SelectedDevice = FullHdDevice;
        await camera.RefreshSelectedCameraCapabilitiesAsync();

        Assert.False(camera.HasNative4K);
        AssertPreferences(camera,
            (CameraCapturePreference.AutoBest, "Auto (1080p)"),
            (CameraCapturePreference.Native720p, "720p"),
            (CameraCapturePreference.SharedCurrent, "Shared · current Windows format"));
        Assert.False(camera.HasCameraCompatibilityMessage);
        Assert.False(camera.IsCheckingCameraCapabilities);
    }

    [Fact]
    public async Task Native_4k_camera_offers_auto_and_only_supported_explicit_resolutions()
    {
        await using var camera = CreateCamera([FourK, FullHd, Hd]);
        camera.SelectedDevice = FourKDevice;
        await camera.RefreshSelectedCameraCapabilitiesAsync();

        Assert.True(camera.HasNative4K);
        AssertPreferences(camera,
            (CameraCapturePreference.AutoBest, "Auto (2160p)"),
            (CameraCapturePreference.Balanced1080p, "1080p"),
            (CameraCapturePreference.Native720p, "720p"),
            (CameraCapturePreference.SharedCurrent, "Shared · current Windows format"));
        Assert.Equal(CameraCapturePreference.AutoBest, camera.SelectedPreference.Value);
        Assert.Equal("Auto (2160p)", camera.SelectedPreference.Label);
        Assert.False(camera.HasCameraCompatibilityMessage);
    }

    [Fact]
    public async Task Unusable_advertised_resolutions_do_not_create_explicit_quality_choices()
    {
        await using var camera = CreateCamera(
            [FourK, new(1920, 1080, 4, "MJPG"), new(1280, 720, 61, "MJPG")]);
        camera.SelectedDevice = FourKDevice;
        await camera.RefreshSelectedCameraCapabilitiesAsync();

        AssertPreferences(camera,
            (CameraCapturePreference.AutoBest, "Auto (2160p)"),
            (CameraCapturePreference.SharedCurrent, "Shared · current Windows format"));
    }

    [Theory]
    [InlineData(3840, 2160, "2160p")]
    [InlineData(2560, 1440, "1440p")]
    public async Task Auto_default_starts_a_camera_with_only_a_high_resolution_native_format(int width, int height,
        string tier)
    {
        var format = new CameraFormat(width, height, 30, "MJPG");
        var device = new CameraDevice("single-format", "Single format camera");
        var capture = new FakeCameraCapture { AvailableFormats = [format] };
        capture.StartHandler = (started, preference, _) =>
        {
            Assert.Equal(device, started);
            Assert.Equal(CameraCapturePreference.AutoBest, preference);
            capture.NegotiatedFormat = format;
            capture.DeliveredFrameDimensions = new(width, height);
            return Task.CompletedTask;
        };
        await using var camera = new CameraViewModel(capture: capture,
            getCameraFormats: (_, _) => Task.FromResult<IReadOnlyList<CameraFormat>>([format]));
        SetField(camera, "_processorReady", true);
        camera.SelectedDevice = device;

        await camera.StartCommand.ExecuteAsync(null);

        Assert.True(camera.IsRunning, camera.Problem);
        Assert.Equal(1, capture.StartCalls);
        Assert.Equal(CameraCapturePreference.AutoBest, camera.SelectedPreference.Value);
        Assert.Equal($"Auto ({tier})", camera.SelectedPreference.Label);
        Assert.Equal(new[] { CameraCapturePreference.AutoBest, CameraCapturePreference.SharedCurrent },
            camera.Preferences.Select(option => option.Value));
    }

    [Fact]
    public async Task Native_720p_choice_starts_native_hd_on_a_full_hd_camera_without_a_warning()
    {
        var capture = new FakeCameraCapture { AvailableFormats = [FullHd, Hd] };
        capture.StartHandler = (device, preference, _) =>
        {
            Assert.Equal(FullHdDevice, device);
            Assert.Equal(CameraCapturePreference.Native720p, preference);
            capture.NegotiatedFormat = Hd;
            capture.DeliveredFrameDimensions = new(1280, 720);
            return Task.CompletedTask;
        };
        await using var camera = new CameraViewModel(capture: capture,
            getCameraFormats: (_, _) => Task.FromResult<IReadOnlyList<CameraFormat>>([FullHd, Hd]));
        SetField(camera, "_processorReady", true);
        camera.SelectedDevice = FullHdDevice;
        await camera.RefreshSelectedCameraCapabilitiesAsync();
        camera.SelectedPreference = camera.Preferences.Single(option => option.Value == CameraCapturePreference.Native720p);

        await camera.StartCommand.ExecuteAsync(null);

        Assert.True(camera.IsRunning, camera.Problem);
        Assert.Null(camera.Problem);
        Assert.Equal(CameraCapturePreference.Native720p, camera.SelectedPreference.Value);
        Assert.Contains("1280 × 720", camera.FormatText);
        Assert.False(camera.HasCameraCompatibilityMessage);
        Assert.Equal("", camera.CameraCompatibilityMessage);
    }

    [Fact]
    public async Task Native_720p_choice_survives_camera_rediscovery_but_is_not_the_next_app_default()
    {
        IReadOnlyList<CameraDevice> connected = [FullHdDevice];
        await using var camera = new CameraViewModel(capture: new FakeCameraCapture(),
            enumerateDevices: _ => Task.FromResult(connected),
            getCameraFormats: (_, _) => Task.FromResult<IReadOnlyList<CameraFormat>>([FullHd, Hd]));
        await camera.RefreshDevicesCommand.ExecuteAsync(null);
        camera.SelectedPreference = camera.Preferences.Single(option => option.Value == CameraCapturePreference.Native720p);
        connected = [];
        await camera.RefreshDevicesCommand.ExecuteAsync(null);
        Assert.Null(camera.SelectedDevice);
        Assert.Equal(new[] { CameraCapturePreference.AutoBest, CameraCapturePreference.SharedCurrent },
            camera.Preferences.Select(option => option.Value));
        Assert.Equal(CameraCapturePreference.AutoBest, camera.SelectedPreference.Value);
        connected = [FullHdDevice];
        await camera.RefreshDevicesCommand.ExecuteAsync(null);
        Assert.Equal(FullHdDevice, camera.SelectedDevice);
        Assert.Equal(CameraCapturePreference.Native720p, camera.SelectedPreference.Value);

        await using var restarted = CreateCamera([FullHd, Hd]);
        Assert.Equal(CameraCapturePreference.AutoBest, restarted.SelectedPreference.Value);
    }

    [Fact]
    public async Task Native_720p_without_a_native_hd_mode_is_not_offered()
    {
        await using var camera = CreateCamera([FullHd]);
        camera.SelectedDevice = FullHdDevice;
        await camera.RefreshSelectedCameraCapabilitiesAsync();

        AssertPreferences(camera,
            (CameraCapturePreference.AutoBest, "Auto (1080p)"),
            (CameraCapturePreference.SharedCurrent, "Shared · current Windows format"));
        Assert.Equal(CameraCapturePreference.AutoBest, camera.SelectedPreference.Value);
    }

    [Fact]
    public async Task A_720p_camera_starts_preview_without_a_compatibility_warning()
    {
        var capture = new FakeCameraCapture
        {
            AvailableFormats = [Hd],
            StartHandler = (device, preference, _) =>
            {
                Assert.Equal(HdDevice, device);
                Assert.Equal(CameraCapturePreference.AutoBest, preference);
                return Task.CompletedTask;
            }
        };
        await using var camera = new CameraViewModel(capture: capture,
            getCameraFormats: (_, _) => Task.FromResult<IReadOnlyList<CameraFormat>>([Hd]));
        SetField(camera, "_processorReady", true);
        camera.SelectedDevice = HdDevice;
        await camera.StartCommand.ExecuteAsync(null);

        Assert.Equal(1, capture.StartCalls);
        Assert.True(camera.IsRunning, camera.Problem);
        Assert.Null(camera.Problem);
        Assert.False(camera.HasNative4K);
        Assert.Equal("Auto (720p)", camera.SelectedPreference.Label);
        AssertPreferences(camera,
            (CameraCapturePreference.AutoBest, "Auto (720p)"),
            (CameraCapturePreference.SharedCurrent, "Shared · current Windows format"));
        Assert.False(camera.HasCameraCompatibilityMessage);
        Assert.Equal("", camera.CameraCompatibilityMessage);
    }

    [Fact]
    public async Task A_sub_720p_camera_is_rejected_before_starting_capture_or_image_processing()
    {
        var capture = new FakeCameraCapture();
        await using var camera = new CameraViewModel(capture: capture,
            getCameraFormats: (_, _) => Task.FromResult<IReadOnlyList<CameraFormat>>([new(640, 480, 30, "MJPG")]));
        camera.SelectedDevice = new("legacy", "Legacy webcam");
        await camera.StartCommand.ExecuteAsync(null);

        Assert.Equal(0, capture.StartCalls);
        Assert.False(camera.IsRunning);
        Assert.False(GetField<bool>(camera, "_processorReady"));
        Assert.Contains("not compatible", camera.Problem);
        Assert.Contains("1280 × 720", camera.Problem);
        Assert.Contains("720p", camera.Problem);
        Assert.True(camera.HasCameraCompatibilityMessage);
    }

    [Fact]
    public async Task Switching_from_4k_to_720p_removes_1080p_and_resets_the_selected_quality()
    {
        await using var camera = new CameraViewModel(capture: new FakeCameraCapture(), getCameraFormats: (device, _) =>
            Task.FromResult<IReadOnlyList<CameraFormat>>(device == FourKDevice ? [FourK, FullHd] : [Hd]));
        camera.SelectedDevice = FourKDevice;
        await camera.RefreshSelectedCameraCapabilitiesAsync();
        camera.SelectedPreference = camera.Preferences.Single(option => option.Value == CameraCapturePreference.Balanced1080p);

        camera.SelectedDevice = HdDevice;
        await camera.RefreshSelectedCameraCapabilitiesAsync();

        Assert.False(camera.HasNative4K);
        Assert.Equal(CameraCapturePreference.AutoBest, camera.SelectedPreference.Value);
        AssertPreferences(camera,
            (CameraCapturePreference.AutoBest, "Auto (720p)"),
            (CameraCapturePreference.SharedCurrent, "Shared · current Windows format"));
        Assert.False(camera.HasCameraCompatibilityMessage);
        Assert.Equal("", camera.CameraCompatibilityMessage);
    }

    [Fact]
    public async Task Refreshing_connected_cameras_preserves_the_selected_item_and_1080p_preference()
    {
        IReadOnlyList<CameraDevice> connected = [FullHdDevice, FourKDevice];
        await using var camera = new CameraViewModel(capture: new FakeCameraCapture(),
            enumerateDevices: _ => Task.FromResult(connected),
            getCameraFormats: (device, _) => Task.FromResult<IReadOnlyList<CameraFormat>>(
                device.Id == FourKDevice.Id ? [FourK, FullHd] : [FullHd]));
        await camera.RefreshDevicesCommand.ExecuteAsync(null);
        camera.SelectedDevice = camera.Devices.Single(device => device.Id == FourKDevice.Id);
        await camera.RefreshSelectedCameraCapabilitiesAsync();
        camera.SelectedPreference = camera.Preferences.Single(option => option.Value == CameraCapturePreference.Balanced1080p);
        var selectedItem = camera.SelectedDevice;
        var changes = new List<NotifyCollectionChangedEventArgs>();
        camera.Devices.CollectionChanged += (_, change) => changes.Add(change);
        // Discovery returns new objects and may change their order. The bound picker must
        // retain its selected object instead of briefly losing the camera and 1080p setting.
        connected = [new(FourKDevice.Id, FourKDevice.Name), new(FullHdDevice.Id, FullHdDevice.Name)];

        await camera.RefreshDevicesCommand.ExecuteAsync(null);

        Assert.Same(selectedItem, camera.SelectedDevice);
        Assert.Same(selectedItem, camera.Devices[0]);
        Assert.Equal(CameraCapturePreference.Balanced1080p, camera.SelectedPreference.Value);
        Assert.True(camera.HasNative4K);
        Assert.DoesNotContain(changes, change => change.Action == NotifyCollectionChangedAction.Reset);
        Assert.DoesNotContain(changes, change => change.Action == NotifyCollectionChangedAction.Remove &&
            change.OldItems?.Contains(selectedItem) == true);
    }

    [Fact]
    public async Task Starting_an_incompatible_camera_stops_the_previous_stream_and_invalidates_its_board()
    {
        var capture = new FakeCameraCapture();
        await using var camera = new CameraViewModel(capture: capture,
            getCameraFormats: (_, _) => Task.FromResult<IReadOnlyList<CameraFormat>>([new(640, 480, 30, "MJPG")]));
        var frame = CameraFrame.CopyFromBgra32(1920, 1080, new byte[1920 * 1080 * 4], sequence: 4, epoch: 2);
        capture.LatestFrame = frame;
        capture.IsRunning = true;
        capture.ActiveDevice = FullHdDevice;
        capture.NegotiatedFormat = FullHd;
        capture.AvailableFormats = [FullHd];
        SetField(camera, "_gameTableRegistration", BoardRegistration.Create(frame,
            [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        SetField(camera, "_gameTablePreviewRequested", true);
        SetField(camera, "_gameTableAnalysis", new GameTableAnalysis(frame, [], [], 1, 1, "synthetic"));
        camera.IsRunning = true;
        camera.IsGameTablePreviewUpright = true;
        camera.SelectedDevice = new("legacy", "Legacy webcam");
        Assert.True(camera.Capture.IsRunning);
        Assert.NotNull(camera.GameTableAnalysis);

        await camera.StartCommand.ExecuteAsync(null);

        Assert.Equal(0, capture.StartCalls);
        Assert.False(camera.Capture.IsRunning);
        Assert.Null(camera.Capture.ActiveDevice);
        Assert.Null(camera.Capture.LatestFrame);
        Assert.False(camera.IsRunning);
        Assert.False(camera.IsGameTablePreviewUpright);
        Assert.Null(camera.GameTableAnalysis);
        Assert.False(GetField<bool>(camera, "_processorReady"));
        Assert.Contains("not compatible", camera.Problem);
    }

    [Fact]
    public async Task A_late_result_from_the_previous_camera_cannot_restore_its_resolution_options()
    {
        var delayedFormats = new TaskCompletionSource<IReadOnlyList<CameraFormat>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var camera = new CameraViewModel(capture: new FakeCameraCapture(), getCameraFormats: (device, _) =>
            device == FourKDevice ? delayedFormats.Task : Task.FromResult<IReadOnlyList<CameraFormat>>([Hd]));
        camera.SelectedDevice = FourKDevice;
        var oldRequest = camera.RefreshSelectedCameraCapabilitiesAsync();
        Assert.True(camera.IsCheckingCameraCapabilities);

        camera.SelectedDevice = HdDevice;
        var currentRequest = camera.RefreshSelectedCameraCapabilitiesAsync();
        Assert.False(camera.HasNative4K);
        delayedFormats.SetResult([FourK, FullHd]);
        await Task.WhenAll(oldRequest, currentRequest);

        Assert.Equal(HdDevice, camera.SelectedDevice);
        Assert.False(camera.HasNative4K);
        AssertPreferences(camera,
            (CameraCapturePreference.AutoBest, "Auto (720p)"),
            (CameraCapturePreference.SharedCurrent, "Shared · current Windows format"));
        Assert.Equal("Auto (720p)", camera.SelectedPreference.Label);
        Assert.False(camera.IsCheckingCameraCapabilities);
    }

    [Fact]
    public async Task Disposing_while_probe_is_in_flight_ignores_its_late_result()
    {
        var delayedFormats = new TaskCompletionSource<IReadOnlyList<CameraFormat>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var camera = new CameraViewModel(capture: new FakeCameraCapture(), getCameraFormats: (_, _) => delayedFormats.Task);
        try
        {
            camera.SelectedDevice = FourKDevice;
            var pending = camera.RefreshSelectedCameraCapabilitiesAsync();
            var dispose = camera.DisposeAsync().AsTask();
            delayedFormats.SetResult([FourK, FullHd]);
            await Task.WhenAll(pending, dispose);

            Assert.False(camera.HasNative4K);
            Assert.Equal(new[] { CameraCapturePreference.AutoBest, CameraCapturePreference.SharedCurrent },
                camera.Preferences.Select(option => option.Value));
        }
        finally
        {
            delayedFormats.TrySetResult([]);
            await camera.DisposeAsync();
        }
    }

    [Fact]
    public async Task Failed_probe_removes_stale_resolution_options_and_explains_how_to_check_the_current_format()
    {
        var fail = false;
        await using var camera = new CameraViewModel(capture: new FakeCameraCapture(), getCameraFormats: (_, _) => fail
            ? Task.FromException<IReadOnlyList<CameraFormat>>(new InvalidOperationException("Camera is busy"))
            : Task.FromResult<IReadOnlyList<CameraFormat>>([FourK, FullHd]));
        camera.SelectedDevice = FourKDevice;
        await camera.RefreshSelectedCameraCapabilitiesAsync();
        Assert.True(camera.HasNative4K);
        Assert.Contains(camera.Preferences, option => option.Value == CameraCapturePreference.Balanced1080p);
        fail = true;

        await camera.RefreshSelectedCameraCapabilitiesAsync();

        Assert.False(camera.HasNative4K);
        Assert.False(camera.IsCheckingCameraCapabilities);
        Assert.Equal(new[] { CameraCapturePreference.AutoBest, CameraCapturePreference.SharedCurrent },
            camera.Preferences.Select(option => option.Value));
        Assert.True(camera.HasCameraCompatibilityMessage);
        Assert.Contains("Could not check", camera.CameraCompatibilityMessage);
        Assert.Contains("Start preview", camera.CameraCompatibilityMessage);
    }

    [Fact]
    public async Task Probe_failure_allows_the_capture_service_to_validate_the_real_format()
    {
        var capture = new FakeCameraCapture { AvailableFormats = [FullHd] };
        await using var camera = new CameraViewModel(capture: capture,
            getCameraFormats: (_, _) => Task.FromException<IReadOnlyList<CameraFormat>>(new InvalidOperationException("Probe unavailable")));
        SetField(camera, "_processorReady", true);
        camera.SelectedDevice = FullHdDevice;

        await camera.StartCommand.ExecuteAsync(null);

        Assert.Equal(1, capture.StartCalls);
        Assert.True(camera.IsRunning, camera.Problem);
        Assert.False(camera.HasNative4K);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_1080p_capable_camera_accepts_negotiated_or_delivered_720p_without_a_warning(bool useDeliveredDimensions)
    {
        await using var camera = CreateCamera([FullHd, Hd]);
        camera.SelectedDevice = FullHdDevice;
        await camera.RefreshSelectedCameraCapabilitiesAsync();
        Assert.False(camera.HasCameraCompatibilityMessage);
        ((FakeCameraCapture)camera.Capture).ActiveDevice = FullHdDevice;
        ((FakeCameraCapture)camera.Capture).NegotiatedFormat = useDeliveredDimensions ? FullHd : Hd;
        if (useDeliveredDimensions)
            ((FakeCameraCapture)camera.Capture).DeliveredFrameDimensions = new CameraFrameDimensions(1280, 720);
        ((FakeCameraCapture)camera.Capture).IsRunning = true;

        PreviewTick(camera);

        Assert.False(camera.HasCameraCompatibilityMessage);
        Assert.Equal("", camera.CameraCompatibilityMessage);
    }

    [Fact]
    public async Task An_1080p_capable_camera_reports_a_delivered_frame_below_720p_as_incompatible()
    {
        await using var camera = CreateCamera([FullHd]);
        camera.SelectedDevice = FullHdDevice;
        await camera.RefreshSelectedCameraCapabilitiesAsync();
        var capture = (FakeCameraCapture)camera.Capture;
        capture.ActiveDevice = FullHdDevice;
        capture.NegotiatedFormat = FullHd;
        capture.DeliveredFrameDimensions = new CameraFrameDimensions(640, 480);
        capture.IsRunning = true;

        PreviewTick(camera);

        Assert.True(camera.HasCameraCompatibilityMessage);
        Assert.Contains("less than 720p", camera.CameraCompatibilityMessage);
        Assert.Contains("1280 × 720", camera.CameraCompatibilityMessage);
    }

    private static CameraViewModel CreateCamera(IReadOnlyList<CameraFormat> formats) =>
        new(capture: new FakeCameraCapture(), getCameraFormats: (_, _) => Task.FromResult(formats));

    private static void AssertPreferences(CameraViewModel camera,
        params (CameraCapturePreference Value, string Label)[] expected) =>
        Assert.Equal(expected, camera.Preferences.Select(option => (option.Value, option.Label)).ToArray());

    private static void PreviewTick(CameraViewModel camera) => typeof(CameraViewModel)
        .GetMethod("PreviewTick", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(camera, [null, EventArgs.Empty]);

    private static T GetField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
