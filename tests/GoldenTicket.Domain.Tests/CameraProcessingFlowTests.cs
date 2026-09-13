using System.Reflection;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class CameraProcessingFlowTests
{
    [Fact]
    public async Task Preview_reports_delivered_and_processed_sizes_and_never_changes_source_pixels()
    {
        await using var fixture = new Fixture();
        var raw = fixture.Frame;
        var bytes = raw.Bgra32.ToArray();
        await fixture.InitializeCpuAsync();
        await fixture.ProcessAsync();
        Assert.NotNull(fixture.Camera.Preview);
        Assert.Equal(3840, fixture.Camera.Preview.PixelWidth);
        Assert.Equal(2160, fixture.Camera.Preview.PixelHeight);
        Assert.Contains("640 × 360", fixture.Camera.FormatText);
        Assert.Contains("upscaled", fixture.Camera.ProcessingText);
        Assert.Contains("CPU", fixture.Camera.ComputeStatus);
        Assert.Equal(bytes, raw.Bgra32.ToArray());
        fixture.Camera.UseEnhancedPreview = false;
        Assert.Equal(640, fixture.Camera.Preview.PixelWidth);
        fixture.Camera.UseEnhancedPreview = true;
        Assert.Equal(3840, fixture.Camera.Preview.PixelWidth);
    }

    [Fact]
    public async Task Empty_reference_is_cleared_when_crop_changes_and_stale_preview_clears_outlines()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeCpuAsync();
        fixture.SelectCrop();
        fixture.Refresh();
        await fixture.Camera.CaptureEmptyBoardCommand.ExecuteAsync(null);
        Assert.True(fixture.Camera.HasPieceReference, fixture.Camera.Problem);
        fixture.Refresh();
        Assert.True(fixture.Camera.MoveBoardCorner(0, new(.02, .02)));
        Assert.False(fixture.Camera.HasPieceReference);
        fixture.Camera.PieceOutlines = [new(false, [new(.1,.1),new(.2,.1),new(.2,.2),new(.1,.2)])];
        fixture.Refresh(stale: true);
        typeof(CameraViewModel).GetMethod("PreviewTick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(fixture.Camera, [null, EventArgs.Empty]);
        Assert.Empty(fixture.Camera.PieceOutlines);
    }

    [Fact]
    public async Task Old_outlines_expire_even_when_camera_capture_is_still_fresh()
    {
        await using var fixture = new Fixture();
        fixture.Refresh(stale: true);
        typeof(CameraViewModel).GetField("_outlinedFrame", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(fixture.Camera, fixture.Frame);
        fixture.Camera.PieceOutlines = [new(false, [new(.1,.1),new(.2,.1),new(.2,.2),new(.1,.2)])];
        fixture.Refresh();
        // Hold processing to simulate a slow worker while capture continues to deliver frames.
        fixture.Camera.IsBusy = true;
        typeof(CameraViewModel).GetMethod("PreviewTick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(fixture.Camera, [null, EventArgs.Empty]);
        Assert.Empty(fixture.Camera.PieceOutlines);
        Assert.Contains("fresh processed image", fixture.Camera.DetectionText);
    }

    [Fact]
    public async Task Clearing_a_reference_during_capture_cannot_be_undone_by_late_completion()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeCpuAsync();
        fixture.SelectCrop();
        fixture.Refresh();
        var pending = fixture.Camera.CaptureEmptyBoardCommand.ExecuteAsync(null);
        fixture.Camera.ClearPieceReferenceCommand.Execute(null);
        await pending;
        Assert.False(fixture.Camera.HasPieceReference);
        Assert.Empty(fixture.Camera.PieceOutlines);
    }

    [Fact]
    public async Task Loading_a_board_export_does_not_create_pieces_on_an_unchanged_synthetic_board()
    {
        var path = Path.Combine(Path.GetTempPath(), "GoldenTicket-empty-" + Guid.NewGuid().ToString("N") + ".png");
        await using var fixture = new Fixture();
        try
        {
            await fixture.InitializeCpuAsync();
            fixture.SelectCrop();
            fixture.Refresh();
            var photo = await fixture.Camera.CaptureExportPhotoAsync(TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(path, photo.PngBytes, TestContext.Current.CancellationToken);
            fixture.Refresh();
            await fixture.Camera.LoadEmptyBoardPhotoAsync(path);
            Assert.True(fixture.Camera.HasPieceReference, fixture.Camera.Problem);
            fixture.Refresh();
            await fixture.ProcessAsync();
            Assert.Empty(fixture.Camera.PieceOutlines);
            Assert.DoesNotContain("processing paused", fixture.Camera.ProcessingText, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private long _sequence;
        // These are flow/identity checks, not hosted-runner speed benchmarks.
        // Advance time explicitly for stale evidence; retain the real CPU pipeline.
        private readonly ManualFrameTimeProvider _clock = new();
        public CameraViewModel Camera { get; } = new();
        public CameraFrame Frame => Camera.Capture.LatestFrame!;
        public Fixture()
        {
            Set("<ActiveDevice>k__BackingField", new CameraDevice("synthetic", "Synthetic"));
            Camera.IsRunning = true;
            Refresh();
        }
        public async Task InitializeCpuAsync()
        {
            Camera.SelectedProcessor = Camera.ProcessorModes.Single(option => option.Value == FrameComputeMode.Cpu);
            await Camera.InitializeProcessingAsync();
        }
        public void SelectCrop()
        {
            Refresh();
            Camera.BeginCornerSelectionCommand.Execute(null);
            foreach (var corner in new NormalizedPoint[] { new(0,0),new(1,0),new(1,1),new(0,1) }) Camera.AddBoardCorner(corner);
            Assert.True(Camera.HasBoardCrop, Camera.Problem);
        }
        public void Refresh(bool stale = false)
        {
            const int width = 640, height = 360;
            var bytes = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var value = (byte)(60 + ((x / 6 + y / 6) % 2) * 135);
                bytes[i] = bytes[i + 1] = bytes[i + 2] = value;
                bytes[i + 3] = 255;
            }
            var frame = CameraFrame.CopyFromBgra32(width, height, bytes, ++_sequence, 1, clock: _clock);
            if (stale) _clock.Advance(TimeSpan.FromSeconds(5));
            Set("_epoch", 1L);
            Set("_running", true);
            Set("_latest", frame);
        }
        public async Task ProcessAsync()
        {
            typeof(CameraViewModel).GetMethod("QueueFrameProcessing", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Camera, [Frame]);
            await (Task)typeof(CameraViewModel).GetField("_frameWork", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Camera)!;
        }
        private void Set(string name, object value) => typeof(CameraCaptureService)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Camera.Capture, value);
        public ValueTask DisposeAsync() => Camera.DisposeAsync();
    }
}
