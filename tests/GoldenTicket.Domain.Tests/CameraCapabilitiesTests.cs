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
    public async Task Unknown_camera_does_not_offer_4k_or_open_hardware_on_selection()
    {
        await using var camera = new CameraViewModel();
        camera.SelectedDevice = FourKDevice;

        Assert.False(camera.HasNative4K);
        Assert.False(camera.IsCheckingCameraCapabilities);
        Assert.False(camera.IsRunning);
        Assert.DoesNotContain(camera.Preferences, option => option.Value == CameraCapturePreference.HighDetail2160p);
    }

    [Fact]
    public async Task Native_1080p_camera_has_no_4k_option_or_reduced_quality_warning()
    {
        await using var camera = CreateCamera([FullHd, Hd]);
        camera.SelectedDevice = FullHdDevice;
        await camera.RefreshSelectedCameraCapabilitiesAsync();

        Assert.False(camera.HasNative4K);
        Assert.DoesNotContain(camera.Preferences, option => option.Value == CameraCapturePreference.HighDetail2160p);
        Assert.False(camera.HasCameraCompatibilityMessage);
        Assert.False(camera.IsCheckingCameraCapabilities);
    }

    [Fact]
    public async Task Native_4k_camera_exposes_the_4k_choice_without_preferred_wording()
    {
        await using var camera = CreateCamera([FourK, FullHd, Hd]);
        camera.SelectedDevice = FourKDevice;
        await camera.RefreshSelectedCameraCapabilitiesAsync();

        Assert.True(camera.HasNative4K);
        var option = Assert.Single(camera.Preferences, option => option.Value == CameraCapturePreference.HighDetail2160p);
        Assert.Equal("4K · best available", option.Label);
        Assert.False(camera.HasCameraCompatibilityMessage);
    }

    [Fact]
    public async Task A_720p_camera_warns_about_poor_lighting_but_can_start_preview()
    {
        var starts = 0;
        await using var camera = new CameraViewModel(
            getCameraFormats: (_, _) => Task.FromResult<IReadOnlyList<CameraFormat>>([Hd]),
            startCapture: (device, preference, _) =>
            {
                Assert.Equal(HdDevice, device);
                Assert.Equal(CameraCapturePreference.Balanced1080p, preference);
                starts++;
                return Task.CompletedTask;
            });
        SetField(camera, "_processorReady", true);
        camera.SelectedDevice = HdDevice;
        await camera.StartCommand.ExecuteAsync(null);

        Assert.Equal(1, starts);
        Assert.True(camera.IsRunning, camera.Problem);
        Assert.Null(camera.Problem);
        Assert.False(camera.HasNative4K);
        Assert.Equal("720p · best available", camera.SelectedPreference.Label);
        Assert.True(camera.HasCameraCompatibilityMessage);
        Assert.Contains("1080p", camera.CameraCompatibilityMessage);
        Assert.Contains("poor lighting", camera.CameraCompatibilityMessage);
    }

    [Fact]
    public async Task A_sub_720p_camera_is_rejected_before_starting_capture_or_image_processing()
    {
        var starts = 0;
        await using var camera = new CameraViewModel(
            getCameraFormats: (_, _) => Task.FromResult<IReadOnlyList<CameraFormat>>([new(640, 480, 30, "MJPG")]),
            startCapture: (_, _, _) =>
            {
                starts++;
                return Task.CompletedTask;
            });
        camera.SelectedDevice = new("legacy", "Legacy webcam");
        await camera.StartCommand.ExecuteAsync(null);

        Assert.Equal(0, starts);
        Assert.False(camera.IsRunning);
        Assert.False(GetField<bool>(camera, "_processorReady"));
        Assert.Contains("not compatible", camera.Problem);
        Assert.Contains("720p", camera.Problem);
        Assert.True(camera.HasCameraCompatibilityMessage);
    }

    [Fact]
    public async Task Switching_from_4k_to_720p_removes_4k_and_resets_the_selected_quality()
    {
        await using var camera = new CameraViewModel(getCameraFormats: (device, _) =>
            Task.FromResult<IReadOnlyList<CameraFormat>>(device == FourKDevice ? [FourK, FullHd] : [Hd]));
        camera.SelectedDevice = FourKDevice;
        await camera.RefreshSelectedCameraCapabilitiesAsync();
        camera.SelectedPreference = camera.Preferences.Single(option => option.Value == CameraCapturePreference.HighDetail2160p);

        camera.SelectedDevice = HdDevice;
        await camera.RefreshSelectedCameraCapabilitiesAsync();

        Assert.False(camera.HasNative4K);
        Assert.Equal(CameraCapturePreference.Balanced1080p, camera.SelectedPreference.Value);
        Assert.DoesNotContain(camera.Preferences, option => option.Value == CameraCapturePreference.HighDetail2160p);
        Assert.Contains("poor lighting", camera.CameraCompatibilityMessage);
    }

    [Fact]
    public async Task Refreshing_connected_cameras_preserves_the_selected_item_and_4k_preference()
    {
        IReadOnlyList<CameraDevice> connected = [FullHdDevice, FourKDevice];
        await using var camera = new CameraViewModel(
            enumerateDevices: _ => Task.FromResult(connected),
            getCameraFormats: (device, _) => Task.FromResult<IReadOnlyList<CameraFormat>>(
                device.Id == FourKDevice.Id ? [FourK, FullHd] : [FullHd]));
        await camera.RefreshDevicesCommand.ExecuteAsync(null);
        camera.SelectedDevice = camera.Devices.Single(device => device.Id == FourKDevice.Id);
        await camera.RefreshSelectedCameraCapabilitiesAsync();
        camera.SelectedPreference = camera.Preferences.Single(option => option.Value == CameraCapturePreference.HighDetail2160p);
        var selectedItem = camera.SelectedDevice;
        var changes = new List<NotifyCollectionChangedEventArgs>();
        camera.Devices.CollectionChanged += (_, change) => changes.Add(change);
        // Discovery returns new objects and may change their order. The bound picker must
        // retain its selected object instead of briefly losing the camera and 4K setting.
        connected = [new(FourKDevice.Id, FourKDevice.Name), new(FullHdDevice.Id, FullHdDevice.Name)];

        await camera.RefreshDevicesCommand.ExecuteAsync(null);

        Assert.Same(selectedItem, camera.SelectedDevice);
        Assert.Same(selectedItem, camera.Devices[0]);
        Assert.Equal(CameraCapturePreference.HighDetail2160p, camera.SelectedPreference.Value);
        Assert.True(camera.HasNative4K);
        Assert.DoesNotContain(changes, change => change.Action == NotifyCollectionChangedAction.Reset);
        Assert.DoesNotContain(changes, change => change.Action == NotifyCollectionChangedAction.Remove &&
            change.OldItems?.Contains(selectedItem) == true);
    }

    [Fact]
    public async Task Starting_an_incompatible_camera_stops_the_previous_stream_and_invalidates_its_board()
    {
        var starts = 0;
        await using var camera = new CameraViewModel(
            getCameraFormats: (_, _) => Task.FromResult<IReadOnlyList<CameraFormat>>([new(640, 480, 30, "MJPG")]),
            startCapture: (_, _, _) =>
            {
                starts++;
                return Task.CompletedTask;
            });
        var frame = CameraFrame.CopyFromBgra32(1920, 1080, new byte[1920 * 1080 * 4], sequence: 4, epoch: 2);
        SetField(camera.Capture, "_latest", frame);
        SetField(camera.Capture, "_running", true);
        SetField(camera.Capture, "<ActiveDevice>k__BackingField", FullHdDevice);
        SetField(camera.Capture, "<NegotiatedFormat>k__BackingField", FullHd);
        SetField(camera.Capture, "<AvailableFormats>k__BackingField", new CameraFormat[] { FullHd });
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

        Assert.Equal(0, starts);
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
    public async Task A_late_result_from_the_previous_camera_cannot_restore_its_4k_option()
    {
        var delayedFormats = new TaskCompletionSource<IReadOnlyList<CameraFormat>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var camera = new CameraViewModel(getCameraFormats: (device, _) =>
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
        Assert.DoesNotContain(camera.Preferences, option => option.Value == CameraCapturePreference.HighDetail2160p);
        Assert.Equal("720p · best available", camera.SelectedPreference.Label);
        Assert.False(camera.IsCheckingCameraCapabilities);
    }

    [Fact]
    public async Task Disposing_while_probe_is_in_flight_ignores_its_late_result()
    {
        var delayedFormats = new TaskCompletionSource<IReadOnlyList<CameraFormat>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var camera = new CameraViewModel(getCameraFormats: (_, _) => delayedFormats.Task);
        try
        {
            camera.SelectedDevice = FourKDevice;
            var pending = camera.RefreshSelectedCameraCapabilitiesAsync();
            var dispose = camera.DisposeAsync().AsTask();
            delayedFormats.SetResult([FourK, FullHd]);
            await Task.WhenAll(pending, dispose);

            Assert.False(camera.HasNative4K);
            Assert.DoesNotContain(camera.Preferences, option => option.Value == CameraCapturePreference.HighDetail2160p);
        }
        finally
        {
            delayedFormats.TrySetResult([]);
            await camera.DisposeAsync();
        }
    }

    [Fact]
    public async Task Failed_probe_removes_stale_4k_and_explains_how_to_check_the_current_format()
    {
        var fail = false;
        await using var camera = new CameraViewModel(getCameraFormats: (_, _) => fail
            ? Task.FromException<IReadOnlyList<CameraFormat>>(new InvalidOperationException("Camera is busy"))
            : Task.FromResult<IReadOnlyList<CameraFormat>>([FourK, FullHd]));
        camera.SelectedDevice = FourKDevice;
        await camera.RefreshSelectedCameraCapabilitiesAsync();
        Assert.True(camera.HasNative4K);
        fail = true;

        await camera.RefreshSelectedCameraCapabilitiesAsync();

        Assert.False(camera.HasNative4K);
        Assert.False(camera.IsCheckingCameraCapabilities);
        Assert.DoesNotContain(camera.Preferences, option => option.Value == CameraCapturePreference.HighDetail2160p);
        Assert.True(camera.HasCameraCompatibilityMessage);
        Assert.Contains("Could not check", camera.CameraCompatibilityMessage);
        Assert.Contains("Start preview", camera.CameraCompatibilityMessage);
    }

    [Fact]
    public async Task Probe_failure_allows_the_capture_service_to_validate_the_real_format()
    {
        var starts = 0;
        await using var camera = new CameraViewModel(
            getCameraFormats: (_, _) => Task.FromException<IReadOnlyList<CameraFormat>>(new InvalidOperationException("Probe unavailable")),
            startCapture: (_, _, _) =>
            {
                starts++;
                return Task.CompletedTask;
            });
        SetField(camera, "_processorReady", true);
        camera.SelectedDevice = FullHdDevice;

        await camera.StartCommand.ExecuteAsync(null);

        Assert.Equal(1, starts);
        Assert.True(camera.IsRunning, camera.Problem);
        Assert.False(camera.HasNative4K);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_1080p_capable_camera_warns_if_the_negotiated_or_delivered_video_is_only_720p(bool useDeliveredDimensions)
    {
        await using var camera = CreateCamera([FullHd, Hd]);
        camera.SelectedDevice = FullHdDevice;
        await camera.RefreshSelectedCameraCapabilitiesAsync();
        Assert.False(camera.HasCameraCompatibilityMessage);
        SetField(camera.Capture, "<ActiveDevice>k__BackingField", FullHdDevice);
        SetField(camera.Capture, "<NegotiatedFormat>k__BackingField", useDeliveredDimensions ? FullHd : Hd);
        if (useDeliveredDimensions)
            SetField(camera.Capture, "_deliveredFrameDimensions", new CameraFrameDimensions(1280, 720));
        SetField(camera.Capture, "_running", true);

        PreviewTick(camera);

        Assert.True(camera.HasCameraCompatibilityMessage);
        Assert.Contains("1280 × 720", camera.CameraCompatibilityMessage);
        Assert.Contains("poor lighting", camera.CameraCompatibilityMessage);
    }

    private static CameraViewModel CreateCamera(IReadOnlyList<CameraFormat> formats) =>
        new(getCameraFormats: (_, _) => Task.FromResult(formats));

    private static void PreviewTick(CameraViewModel camera) => typeof(CameraViewModel)
        .GetMethod("PreviewTick", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(camera, [null, EventArgs.Empty]);

    private static T GetField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
