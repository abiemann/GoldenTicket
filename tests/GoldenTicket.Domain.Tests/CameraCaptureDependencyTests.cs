using System.Reflection;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Testing;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class CameraCaptureDependencyTests
{
    private static readonly CameraDevice Device = new("synthetic", "Synthetic camera");
    private static readonly CameraFormat Format = new(1920, 1080, 30, "MJPG");

    [Fact]
    public async Task Selected_capture_supplies_the_stream_state_frames_and_teardown()
    {
        var capture = new FakeCameraCapture { AvailableFormats = [Format] };
        var camera = CreateCamera(capture);
        try
        {
            await camera.StartCommand.ExecuteAsync(null);
            Assert.True(camera.IsRunning, camera.Problem);
            Assert.Same(capture, camera.Capture);
            Assert.Equal(Device, capture.ActiveDevice);
            Assert.Equal(1, capture.StartCalls);

            capture.LatestFrame = CameraFrame.CopyFromBgra32(96, 60, new byte[96 * 60 * 4],
                epoch: capture.Epoch);
            camera.BeginCornerSelectionCommand.Execute(null);
            Assert.True(camera.SelectingCorners, camera.Problem);

            await camera.StopCommand.ExecuteAsync(null);
            Assert.False(camera.IsRunning);
            Assert.False(capture.IsRunning);
            Assert.False(camera.SelectingCorners);
            Assert.Null(capture.LatestFrame);
            Assert.Equal(1, capture.StopCalls);
        }
        finally { await camera.DisposeAsync(); }

        await camera.DisposeAsync();
        Assert.Equal(1, capture.DisposeCalls);
    }

    [Fact]
    public async Task Disposing_during_capture_start_cancels_the_same_stream_without_publishing_running_state()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new FakeCameraCapture
        {
            AvailableFormats = [Format],
            StartHandler = async (_, _, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        var camera = CreateCamera(capture);
        var start = camera.StartCommand.ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await camera.DisposeAsync();
            await start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(camera.IsRunning);
            Assert.False(capture.IsRunning);
            Assert.Null(capture.ActiveDevice);
            Assert.Equal(1, capture.DisposeCalls);
        }
        finally
        {
            release.TrySetResult();
            await start;
            await camera.DisposeAsync();
        }
    }

    private static CameraViewModel CreateCamera(FakeCameraCapture capture)
    {
        var camera = new CameraViewModel(capture: capture,
            getCameraFormats: (_, _) => Task.FromResult<IReadOnlyList<CameraFormat>>([Format]));
        // Only capture orchestration is under test. Real processor/model checks have separate fixtures.
        typeof(CameraViewModel).GetField("_processorReady", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(camera, true);
        camera.SelectedDevice = Device;
        return camera;
    }
}
