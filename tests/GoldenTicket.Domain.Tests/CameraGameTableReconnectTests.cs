using System.Reflection;
using System.Windows.Threading;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Vision;

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
        CameraViewModel? instance = null;
        await using var camera = instance = new CameraViewModel(
            enumerateDevices: _ =>
            {
                searches++;
                return Task.FromResult<IReadOnlyList<CameraDevice>>(connected ? [device] : []);
            },
            startCapture: (selected, _, _) =>
            {
                Assert.Equal(device, selected);
                starts++;
                SetField(instance!.Capture, "_running", true);
                return Task.CompletedTask;
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
        SetField(camera.Capture, "_running", false);
        PreviewTick(camera);
        Assert.Contains("Please connect a webcam", camera.GameTablePreviewStatus);
        await RetryAsync(camera);
        Assert.Equal(4, searches);
        Assert.Equal(3, starts);

        camera.EndGameTablePreview();
        Assert.False(RetryTimer(camera).IsEnabled);
        SetField(camera.Capture, "_running", false);
        await RetryAsync(camera);
        Assert.Equal(4, searches);
    }

    [Fact]
    public async Task A_table_without_a_restore_does_not_open_hardware_implicitly()
    {
        var searches = 0;
        await using var camera = new CameraViewModel(enumerateDevices: _ =>
        {
            searches++;
            return Task.FromResult<IReadOnlyList<CameraDevice>>([]);
        });
        camera.RequestGameTablePreview();
        Assert.Equal(0, searches);
        Assert.False(RetryTimer(camera).IsEnabled);
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
