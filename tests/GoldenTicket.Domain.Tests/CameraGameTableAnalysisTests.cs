using GoldenTicket.Testing;
using System.Reflection;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class CameraGameTableAnalysisTests
{
    private static readonly NormalizedPoint[] Corners =
        [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];

    [Fact]
    public async Task Accepted_board_publishes_fresh_model_result_even_when_utility_outlines_are_off()
    {
        await using var fixture = new Fixture();
        fixture.Camera.ShowPieceOutlines = false;
        await fixture.AnalyzeAsync();

        var result = Assert.IsType<GameTableAnalysis>(fixture.Camera.GameTableAnalysis);
        Assert.Equal((1920, 1200), (result.Board.Width, result.Board.Height));
        Assert.Equal(fixture.Frame.Sequence, result.Board.Sequence);
        Assert.Equal(fixture.Frame.Epoch, result.Board.Epoch);
        Assert.Equal(fixture.Frame.CapturedAt, result.Board.CapturedAt);
        Assert.Equal("synthetic-game-table", result.ModelId);
        Assert.Single(result.Candidates);
        Assert.Empty(result.Scores);
    }

    [Fact]
    public async Task Changed_crop_discards_a_model_result_that_finishes_late()
    {
        await using var fixture = new Fixture();
        fixture.Model.Pause();
        var work = fixture.AnalyzeAsync();
        await fixture.Model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        fixture.Camera.EndGameTablePreview();
        fixture.Model.Release();
        await work;

        Assert.Null(fixture.Camera.GameTableAnalysis);
    }

    [Fact]
    public async Task Expired_board_result_is_removed_before_another_camera_frame_arrives()
    {
        await using var fixture = new Fixture();
        await fixture.AnalyzeAsync();
        Assert.NotNull(fixture.Camera.GameTableAnalysis);

        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        typeof(CameraViewModel).GetMethod("PreviewTick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Camera, [null, EventArgs.Empty]);

        Assert.Null(fixture.Camera.GameTableAnalysis);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public CameraViewModel Camera { get; } = new(capture: new FakeCameraCapture());
        public FakeCameraCapture Capture => (FakeCameraCapture)Camera.Capture;
        public ManualFrameTimeProvider Clock { get; } = new();
        public FakeModel Model { get; } = new();
        public CameraFrame Frame { get; }

        public Fixture()
        {
            const int width = 320, height = 200;
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 80;
                pixels[i + 1] = 110;
                pixels[i + 2] = 140;
                pixels[i + 3] = 255;
            }
            Frame = CameraFrame.CopyFromBgra32(width, height, pixels, sequence: 7, epoch: 3, clock: Clock);
            Capture.LatestFrame = Frame;
            Capture.Epoch = 3L;
            Capture.IsRunning = true;
            Camera.IsRunning = true;
            Set("_gameTableRegistration", BoardRegistration.Create(Frame, Corners));
            Set("_gameTablePreviewRequested", true);
            Set("_pieceModel", Model);
            Camera.IsGameTablePreviewUpright = true;
        }

        public Task AnalyzeAsync()
        {
            typeof(CameraViewModel).GetMethod("QueueGameTableAnalysis", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Camera, [Frame]);
            return (Task)typeof(CameraViewModel)
                .GetField("_gameTableAnalysisWork", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Camera)!;
        }

        private void Set(string field, object value) => typeof(CameraViewModel)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Camera, value);

        public async ValueTask DisposeAsync()
        {
            Model.Release();
            await Camera.DisposeAsync();
            Model.DisposeResources();
        }
    }

    private sealed class FakeModel : IPieceModelDetector
    {
        private readonly ManualResetEventSlim _release = new(true);
        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ModelId => "synthetic-game-table";
        public string Backend => "synthetic CPU";
        public string? FallbackReason => null;
        public void Pause()
        {
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _release.Reset();
        }
        public void Release() => _release.Set();
        public LearnedPieceDetection Detect(CameraFrame board, CancellationToken token = default)
        {
            Entered.TrySetResult();
            _release.Wait(token);
            return new([new PieceCandidate(PieceCandidateKind.Train,
                [new(.6, .6), new(.64, .6), new(.64, .62), new(.6, .62)], .9)],
                ModelId, Backend, TimeSpan.FromMilliseconds(1));
        }
        public void Dispose() { }
        public void DisposeResources() => _release.Dispose();
    }
}
