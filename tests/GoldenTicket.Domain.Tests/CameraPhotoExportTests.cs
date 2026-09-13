using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class CameraPhotoExportTests
{
    private static readonly NormalizedPoint[] Corners = [new(.05, .05), new(.95, .05), new(.95, .95), new(.05, .95)];
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_export_encodes_current_crop_without_clearing_checkpoint_safety(bool changedScene)
    {
        await using var fixture = new CaptureFixture();
        fixture.SelectCrop();
        if (changedScene) fixture.ChangeScene();
        var camera = fixture.Camera;
        Assert.Equal(changedScene ? SceneReferenceState.SceneChanged : SceneReferenceState.NoReference,
            fixture.Monitor.Current.State);
        Assert.True(camera.SafetyHeld);
        Assert.True(camera.CanExportPhoto);
        Assert.False(camera.CanCapturePhoto);
        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.CapturePhotoAsync(Token));

        fixture.Refresh(inverted: changedScene);
        var source = camera.Capture.LatestFrame!;
        var photo = await camera.CaptureExportPhotoAsync(Token);
        try
        {
            using var stream = new MemoryStream(photo.PngBytes, writable: false);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.Equal(3456, decoder.Frames[0].PixelWidth);
            Assert.Equal(2160, decoder.Frames[0].PixelHeight);
            Assert.Equal(source.Sequence, photo.FrameSequence);
            Assert.Equal(source.Epoch, photo.CameraEpoch);
            Assert.Equal(source.CapturedAt, photo.CapturedAt);
            Assert.Equal("synthetic-board-camera", photo.CameraId);
            Assert.True(photo.BoardCropped);
        }
        finally { CryptographicOperations.ZeroMemory(photo.PngBytes); }

        Assert.True(camera.SafetyHeld);
        Assert.False(camera.CanCapturePhoto);
        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.CapturePhotoAsync(Token));
    }

    [Fact]
    public async Task Checkpoint_capture_checks_monitor_even_when_displayed_hold_is_cleared()
    {
        await using var fixture = new CaptureFixture();
        fixture.SelectCrop();
        fixture.Camera.SafetyHeld = false;
        Assert.Equal(SceneReferenceState.NoReference, fixture.Monitor.Current.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Camera.CapturePhotoAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_invalid_crop_cannot_export(bool invalidGeometry)
    {
        await using var fixture = new CaptureFixture();
        if (invalidGeometry)
        {
            fixture.SelectCrop();
            Assert.True(fixture.Camera.MoveBoardCorner(0, new(.99, .99)));
        }
        Assert.False(fixture.Camera.HasBoardCrop);
        Assert.False(fixture.Camera.CanExportPhoto);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Camera.CaptureExportPhotoAsync(Token));
    }

    [Fact]
    public async Task Stale_frame_is_rejected_even_with_a_previously_valid_crop()
    {
        await using var fixture = new CaptureFixture();
        fixture.SelectCrop();
        fixture.Refresh(stale: true);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Camera.CaptureExportPhotoAsync(Token));
        Assert.Contains("fresh camera frame", failure.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Camera_restart_or_format_change_rejects_obsolete_crop(bool formatChange)
    {
        await using var fixture = new CaptureFixture();
        fixture.SelectCrop();
        fixture.Refresh(epoch: formatChange ? 1 : 2, width: formatChange ? 100 : 96);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Camera.CaptureExportPhotoAsync(Token));
        Assert.Contains("camera restarted or changed format", failure.Message);
    }

    [Fact]
    public async Task Stopped_capture_does_not_export_its_last_frame()
    {
        await using var fixture = new CaptureFixture();
        fixture.SelectCrop();
        fixture.SetCaptureField("_running", false);
        fixture.Camera.IsRunning = false;
        Assert.False(fixture.Camera.CanExportPhoto);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Camera.CaptureExportPhotoAsync(Token));
    }

    [Fact]
    public async Task Busy_state_disables_export_and_reenables_it_while_scene_is_still_held()
    {
        await using var fixture = new CaptureFixture();
        fixture.SelectCrop();
        var availability = new List<bool>();
        fixture.Camera.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CameraViewModel.CanExportPhoto))
                availability.Add(fixture.Camera.CanExportPhoto);
        };
        fixture.Camera.IsBusy = true;
        Assert.False(fixture.Camera.CanExportPhoto);
        fixture.Camera.IsBusy = false;
        Assert.True(fixture.Camera.CanExportPhoto);
        Assert.True(fixture.Camera.SafetyHeld);
        Assert.Equal([false, true], availability);
    }

    // Owned synthetic frames only: never enumerate, start, or access the user's physical camera.
    private sealed class CaptureFixture : IAsyncDisposable
    {
        private long _sequence;
        public CameraViewModel Camera { get; } = new();
        public SceneReferenceMonitor Monitor => (SceneReferenceMonitor)typeof(CameraViewModel)
            .GetField("_monitor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Camera)!;

        public CaptureFixture()
        {
            SetCaptureField("<ActiveDevice>k__BackingField", new CameraDevice("synthetic-board-camera", "Synthetic board camera"));
            Refresh();
            Camera.IsRunning = true;
        }

        public void SelectCrop()
        {
            Refresh();
            Camera.BeginCornerSelectionCommand.Execute(null);
            foreach (var corner in Corners) Camera.AddBoardCorner(corner);
            Assert.True(Camera.HasBoardCrop, Camera.Problem);
        }

        public void ChangeScene()
        {
            Refresh();
            Camera.SetReferenceCommand.Execute(null);
            Assert.Null(Camera.Problem);
            Refresh(inverted: true);
            Camera.SafetyHeld = Monitor.Observe(Camera.Capture.LatestFrame!).SafetyHeld;
            Assert.Equal(SceneReferenceState.SceneChanged, Monitor.Current.State);
        }

        public void Refresh(bool inverted = false, bool stale = false, long epoch = 1, int width = 96)
        {
            const int height = 60;
            var bytes = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var bright = ((x / 4 + y / 4) % 2 == 0) != inverted;
                bytes[offset] = bytes[offset + 1] = bytes[offset + 2] = (byte)(bright ? 240 : 20);
                bytes[offset + 3] = 255;
            }
            var frame = CameraFrame.CopyFromBgra32(width, height, bytes, ++_sequence, epoch);
            if (stale)
                frame = (CameraFrame)typeof(CameraFrame).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()
                    .Invoke([width, height, bytes, _sequence, epoch, DateTimeOffset.UtcNow.AddSeconds(-5), Stopwatch.GetTimestamp() - 5 * Stopwatch.Frequency]);
            SetCaptureField("_epoch", epoch);
            SetCaptureField("_running", true);
            SetCaptureField("_latest", frame);
        }

        public void SetCaptureField(string name, object value) => typeof(CameraCaptureService)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Camera.Capture, value);

        public ValueTask DisposeAsync() => Camera.DisposeAsync();
    }
}
