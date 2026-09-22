using System.IO;
using System.Reflection;
using System.Windows.Threading;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Vision;
using GoldenTicket.Testing;

namespace GoldenTicket.Domain.Tests;

public sealed class CameraGameTableReconnectTests
{
    [Fact]
    public async Task Restored_table_waits_for_webcam_then_starts_it_when_discovered()
    {
        var device = new CameraDevice("connected-later", "Overhead webcam");
        var connected = false;
        var searches = 0;
        var starts = 0;
        var capture = new FakeCameraCapture
        {
            AvailableFormats = [new(1920, 1080, 30, "MJPG")],
            StartHandler = (selected, _, _) =>
            {
                Assert.Equal(device, selected);
                starts++;
                return Task.CompletedTask;
            }
        };
        await using var camera = new CameraViewModel(capture: capture,
            enumerateDevices: _ =>
            {
                searches++;
                return Task.FromResult<IReadOnlyList<CameraDevice>>(connected ? [device] : []);
            },
            getCameraFormats: (_, _) => Task.FromResult<IReadOnlyList<CameraFormat>>(
                [new(1920, 1080, 30, "MJPG")]));
        // The test only checks hotplug control flow; image processing and board registration
        // are covered by the camera corner tests and do not need real hardware here.
        SetField(camera, "_processorReady", true);

        camera.RequestGameTablePreview(reconnectCamera: true);
        Assert.True(RetryTimer(camera).IsEnabled);
        Assert.Equal(1, searches);
        Assert.Equal(0, starts);
        Assert.Contains("Please connect a webcam", camera.GameTablePreviewStatus);
        PreviewTick(camera);
        Assert.Contains("Please connect a webcam", camera.GameTablePreviewStatus);

        connected = true;
        await RetryAsync(camera);
        Assert.Equal(2, searches);
        Assert.Equal(1, starts);
        Assert.True(camera.IsRunning);
        Assert.Contains("Waiting for the webcam image", camera.GameTablePreviewStatus);

        // A connected device that stops delivering frames also gets reopened.
        SetField(camera, "_gameTableCameraNoFrameSince", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(15));
        await RetryAsync(camera);
        Assert.Equal(3, searches);
        Assert.Equal(2, starts);

        // A later transport failure should use the same recovery path.
        capture.IsRunning = false;
        PreviewTick(camera);
        Assert.Contains(device.Name, camera.GameTablePreviewStatus);
        await RetryAsync(camera);
        Assert.Equal(4, searches);
        Assert.Equal(3, starts);

        camera.EndGameTablePreview();
        Assert.False(RetryTimer(camera).IsEnabled);
        capture.IsRunning = false;
        await RetryAsync(camera);
        Assert.Equal(4, searches);
    }

    [Fact]
    public async Task A_table_without_a_restore_does_not_open_hardware_implicitly()
    {
        var searches = 0;
        await using var camera = new CameraViewModel(capture: new FakeCameraCapture(), enumerateDevices: _ =>
        {
            searches++;
            return Task.FromResult<IReadOnlyList<CameraDevice>>([]);
        });
        camera.RequestGameTablePreview();
        Assert.Equal(0, searches);
        Assert.False(RetryTimer(camera).IsEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unplugged_overhead_camera_waits_without_switching_to_the_laptop_then_reconnects(
        bool changedDeviceId)
    {
        await using var fixture = new ReconnectFixture();
        await fixture.StartOverheadAsync();
        fixture.Connected = [ReconnectFixture.Laptop];
        fixture.Capture.IsRunning = false;
        PreviewTick(fixture.Camera);
        Assert.Contains(ReconnectFixture.Overhead.Name, fixture.Camera.GameTablePreviewStatus);

        await RetryAsync(fixture.Camera);
        await RetryAsync(fixture.Camera);
        Assert.Null(fixture.Camera.SelectedDevice);
        Assert.False(fixture.Capture.IsRunning);
        Assert.Equal([ReconnectFixture.Overhead.Id], fixture.StartedIds);
        Assert.Contains(ReconnectFixture.Overhead.Name, fixture.Camera.Status);
        Assert.Contains(ReconnectFixture.Overhead.Name, fixture.Camera.GameTablePreviewStatus);

        var returned = changedDeviceId
            ? new CameraDevice("overhead-new-usb-id", ReconnectFixture.Overhead.Name)
            : ReconnectFixture.Overhead;
        fixture.Connected = [ReconnectFixture.Laptop, returned];
        await RetryAsync(fixture.Camera);

        Assert.Equal(returned, fixture.Camera.SelectedDevice);
        Assert.Equal(returned, fixture.Capture.ActiveDevice);
        Assert.True(fixture.Camera.IsRunning);
        Assert.Equal([ReconnectFixture.Overhead.Id, returned.Id], fixture.StartedIds);
        Assert.Contains("Waiting for the webcam image", fixture.Camera.GameTablePreviewStatus);
    }

    [Fact]
    public async Task Exact_device_identity_wins_even_when_another_camera_has_the_same_name()
    {
        await using var fixture = new ReconnectFixture();
        await fixture.StartOverheadAsync();
        var sameName = new CameraDevice("different-camera", ReconnectFixture.Overhead.Name);
        fixture.Connected = [sameName, ReconnectFixture.Laptop, ReconnectFixture.Overhead];
        fixture.Capture.IsRunning = false;

        await RetryAsync(fixture.Camera);

        Assert.Equal(ReconnectFixture.Overhead, fixture.Camera.SelectedDevice);
        Assert.Equal(ReconnectFixture.Overhead, fixture.Capture.ActiveDevice);
        Assert.Equal([ReconnectFixture.Overhead.Id, ReconnectFixture.Overhead.Id], fixture.StartedIds);
    }

    [Fact]
    public async Task Ambiguous_camera_names_wait_until_the_user_explicitly_chooses_one()
    {
        await using var fixture = new ReconnectFixture();
        await fixture.StartOverheadAsync();
        var first = new CameraDevice("first-new-id", ReconnectFixture.Overhead.Name);
        var second = new CameraDevice("second-new-id", ReconnectFixture.Overhead.Name);
        fixture.Connected = [ReconnectFixture.Laptop, first, second];
        fixture.Capture.IsRunning = false;

        await RetryAsync(fixture.Camera);

        Assert.Null(fixture.Camera.SelectedDevice);
        Assert.False(fixture.Capture.IsRunning);
        Assert.Equal([ReconnectFixture.Overhead.Id], fixture.StartedIds);
        Assert.Contains(ReconnectFixture.Overhead.Name, fixture.Camera.GameTablePreviewStatus);

        fixture.Camera.SelectedDevice = second;
        await RetryAsync(fixture.Camera);

        Assert.Equal(second, fixture.Capture.ActiveDevice);
        Assert.Equal([ReconnectFixture.Overhead.Id, second.Id], fixture.StartedIds);
    }

    [Fact]
    public async Task Explicit_alternative_camera_remains_selected_when_the_original_returns()
    {
        await using var fixture = new ReconnectFixture();
        await fixture.StartOverheadAsync();
        fixture.Connected = [ReconnectFixture.Laptop];
        fixture.Capture.IsRunning = false;
        await RetryAsync(fixture.Camera);
        Assert.Null(fixture.Camera.SelectedDevice);

        fixture.Camera.SelectedDevice = ReconnectFixture.Laptop;
        await RetryAsync(fixture.Camera);
        Assert.Equal(ReconnectFixture.Laptop, fixture.Capture.ActiveDevice);

        fixture.Capture.IsRunning = false;
        fixture.Connected = [ReconnectFixture.Overhead, ReconnectFixture.Laptop];
        await RetryAsync(fixture.Camera);

        Assert.Equal(ReconnectFixture.Laptop, fixture.Camera.SelectedDevice);
        Assert.Equal(ReconnectFixture.Laptop, fixture.Capture.ActiveDevice);
        Assert.Equal([ReconnectFixture.Overhead.Id, ReconnectFixture.Laptop.Id, ReconnectFixture.Laptop.Id],
            fixture.StartedIds);
    }

    [Fact]
    public async Task Failed_device_discovery_does_not_forget_which_camera_should_reconnect()
    {
        await using var fixture = new ReconnectFixture();
        await fixture.StartOverheadAsync();
        fixture.Capture.IsRunning = false;
        fixture.EnumerationFails = true;
        await RetryAsync(fixture.Camera);
        Assert.Null(fixture.Camera.SelectedDevice);
        Assert.Equal([ReconnectFixture.Overhead.Id], fixture.StartedIds);
        Assert.Contains(ReconnectFixture.Overhead.Name, fixture.Camera.GameTablePreviewStatus);

        fixture.EnumerationFails = false;
        fixture.Connected = [ReconnectFixture.Laptop];
        await RetryAsync(fixture.Camera);
        Assert.Null(fixture.Camera.SelectedDevice);
        Assert.Equal([ReconnectFixture.Overhead.Id], fixture.StartedIds);

        fixture.Connected = [ReconnectFixture.Laptop, ReconnectFixture.Overhead];
        await RetryAsync(fixture.Camera);
        Assert.Equal(ReconnectFixture.Overhead, fixture.Capture.ActiveDevice);
        Assert.Equal([ReconnectFixture.Overhead.Id, ReconnectFixture.Overhead.Id], fixture.StartedIds);
    }

    [Fact]
    public async Task Remembered_camera_survives_restart_absence_and_a_changed_usb_identity()
    {
        var path = Path.Combine(Path.GetTempPath(), $"goldenticket-camera-{Guid.NewGuid():N}.json");
        var returned = new CameraDevice("overhead-new-usb-id", ReconnectFixture.Overhead.Name);
        try
        {
            await using (var initial = new ReconnectFixture(path))
            {
                await initial.Camera.RefreshDevicesCommand.ExecuteAsync(null);
                Assert.Equal(ReconnectFixture.Laptop, initial.Camera.SelectedDevice);
                initial.Camera.SelectedDevice = ReconnectFixture.Overhead;
                initial.Camera.SelectedDevice = null;
                await initial.Camera.RefreshDevicesCommand.ExecuteAsync(null);
                Assert.Equal(ReconnectFixture.Overhead, initial.Camera.SelectedDevice);
                Assert.True(File.Exists(path));
            }

            await using (var reopened = new ReconnectFixture(path))
            {
                reopened.Connected = [ReconnectFixture.Laptop];
                reopened.Camera.RequestGameTablePreview(reconnectCamera: true);
                await RetryAsync(reopened.Camera);
                Assert.Null(reopened.Camera.SelectedDevice);
                Assert.Empty(reopened.StartedIds);
                Assert.Contains(ReconnectFixture.Overhead.Name, reopened.Camera.GameTablePreviewStatus);

                // A null selection from an unavailable picker must not replace the saved preference.
                reopened.Camera.SelectedDevice = null;
                reopened.Connected = [ReconnectFixture.Laptop, returned];
                await RetryAsync(reopened.Camera);
                Assert.Equal(returned, reopened.Camera.SelectedDevice);
                Assert.Equal([returned.Id], reopened.StartedIds);
            }

            await using (var afterIdentityChange = new ReconnectFixture(path))
            {
                // Two matching names are safe here only if the successfully recovered ID was saved.
                afterIdentityChange.Connected = [ReconnectFixture.Overhead, ReconnectFixture.Laptop, returned];
                await afterIdentityChange.Camera.RefreshDevicesCommand.ExecuteAsync(null);
                Assert.Equal(returned, afterIdentityChange.Camera.SelectedDevice);
                Assert.Empty(afterIdentityChange.StartedIds);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Manual_selection_during_refresh_keeps_its_identity_after_the_old_probe_finishes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"goldenticket-camera-{Guid.NewGuid():N}.json");
        var delayedFormats = new TaskCompletionSource<IReadOnlyList<CameraFormat>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new FakeCameraCapture();
        var camera = new CameraViewModel(capture: capture, cameraSettingsPath: path,
            enumerateDevices: _ => Task.FromResult<IReadOnlyList<CameraDevice>>(
                [ReconnectFixture.Laptop, ReconnectFixture.Overhead]),
            getCameraFormats: (device, _) => device.Id == ReconnectFixture.Laptop.Id
                ? delayedFormats.Task
                : Task.FromResult<IReadOnlyList<CameraFormat>>([new(1920, 1080, 30, "MJPG")]));
        Task? refresh = null;
        try
        {
            refresh = camera.RefreshDevicesCommand.ExecuteAsync(null);
            Assert.False(refresh.IsCompleted);
            Assert.True(camera.IsCheckingCameraCapabilities);
            Assert.Equal(ReconnectFixture.Laptop, camera.SelectedDevice);

            camera.SelectedDevice = ReconnectFixture.Overhead;
            var currentProbe = camera.RefreshSelectedCameraCapabilitiesAsync();
            delayedFormats.SetResult([new(1920, 1080, 30, "MJPG")]);
            await Task.WhenAll(refresh, currentProbe).WaitAsync(TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Equal(ReconnectFixture.Overhead, camera.SelectedDevice);
            Assert.False(camera.IsCheckingCameraCapabilities);

            // A late refresh must not quietly save its old camera as the next reconnect target.
            await using (var reopened = new ReconnectFixture(path))
            {
                await reopened.Camera.RefreshDevicesCommand.ExecuteAsync(null);
                Assert.Equal(ReconnectFixture.Overhead, reopened.Camera.SelectedDevice);
                Assert.Empty(reopened.StartedIds);
            }
            await camera.RefreshDevicesCommand.ExecuteAsync(null);
            Assert.Equal(ReconnectFixture.Overhead, camera.SelectedDevice);
            Assert.Equal(0, capture.StartCalls);
        }
        finally
        {
            delayedFormats.TrySetResult([]);
            try
            {
                if (refresh is not null)
                    await refresh.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                await camera.DisposeAsync();
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{not-json")]
    [InlineData("{\"Id\":\"\",\"Name\":\"Overhead webcam\"}")]
    [InlineData("{\"Id\":\"old-id\"}")]
    public async Task Missing_or_invalid_preferences_allow_initial_selection_and_can_be_replaced(string? savedText)
    {
        var path = Path.Combine(Path.GetTempPath(), $"goldenticket-camera-{Guid.NewGuid():N}.json");
        try
        {
            if (savedText is not null) File.WriteAllText(path, savedText);
            await using (var initial = new ReconnectFixture(path))
            {
                await initial.Camera.RefreshDevicesCommand.ExecuteAsync(null);
                Assert.Equal(ReconnectFixture.Laptop, initial.Camera.SelectedDevice);
                Assert.Null(initial.Camera.Problem);
                Assert.Empty(initial.StartedIds);
                initial.Camera.SelectedDevice = ReconnectFixture.Overhead;
            }
            await using (var reopened = new ReconnectFixture(path))
            {
                await reopened.Camera.RefreshDevicesCommand.ExecuteAsync(null);
                Assert.Equal(ReconnectFixture.Overhead, reopened.Camera.SelectedDevice);
                Assert.Empty(reopened.StartedIds);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class ReconnectFixture : IAsyncDisposable
    {
        public static CameraDevice Laptop { get; } = new("laptop", "Laptop camera");
        public static CameraDevice Overhead { get; } = new("overhead", "Overhead webcam");
        public IReadOnlyList<CameraDevice> Connected { get; set; } = [Laptop, Overhead];
        public bool EnumerationFails { get; set; }
        public List<string> StartedIds { get; } = [];
        public FakeCameraCapture Capture { get; }
        public CameraViewModel Camera { get; }

        public ReconnectFixture(string? settingsPath = null)
        {
            Capture = new FakeCameraCapture
            {
                AvailableFormats = [new(1920, 1080, 30, "MJPG")],
                StartHandler = (device, _, _) =>
                {
                    StartedIds.Add(device.Id);
                    return Task.CompletedTask;
                }
            };
            Camera = new CameraViewModel(capture: Capture,
                enumerateDevices: _ => EnumerationFails
                    ? Task.FromException<IReadOnlyList<CameraDevice>>(new IOException("Synthetic discovery failure"))
                    : Task.FromResult(Connected),
                getCameraFormats: (_, _) => Task.FromResult<IReadOnlyList<CameraFormat>>(
                    [new(1920, 1080, 30, "MJPG")]), cameraSettingsPath: settingsPath);
            SetField(Camera, "_processorReady", true);
        }

        public async Task StartOverheadAsync()
        {
            await Camera.RefreshDevicesCommand.ExecuteAsync(null);
            Assert.Equal(Laptop, Camera.SelectedDevice);
            Camera.SelectedDevice = Overhead;
            await Camera.StartCommand.ExecuteAsync(null);
            Assert.Equal([Overhead.Id], StartedIds);
            Camera.RequestGameTablePreview(reconnectCamera: true);
        }

        public ValueTask DisposeAsync() => Camera.DisposeAsync();
    }

    private static Task RetryAsync(CameraViewModel camera) => (Task)typeof(CameraViewModel)
        .GetMethod("RetryGameTableCameraAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(camera, null)!;

    private static DispatcherTimer RetryTimer(CameraViewModel camera) =>
        (DispatcherTimer)typeof(CameraViewModel)
            .GetField("_gameTableCameraRetryTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(camera)!;

    private static void PreviewTick(CameraViewModel camera) => typeof(CameraViewModel)
        .GetMethod("PreviewTick", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(camera, [null, EventArgs.Empty]);

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
