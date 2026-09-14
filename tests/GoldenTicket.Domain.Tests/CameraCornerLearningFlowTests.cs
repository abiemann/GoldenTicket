using System.Collections.Concurrent;
using System.Reflection;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

/// <summary>Owned camera frames and an injected corner model; never opens hardware or real model files.</summary>
public sealed class CameraCornerLearningFlowTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Game_setup_keeps_success_through_one_miss_then_warns_after_a_second_miss()
    {
        await using var fixture = new Fixture();
        fixture.Camera.BeginGameBoardFraming();
        Assert.False(fixture.Camera.CanStartGameWithBoard);

        await fixture.CheckGameBoardAsync();
        Assert.True(fixture.Camera.CanStartGameWithBoard);
        Assert.Equal(FakeModel.Corners, fixture.Camera.GameBoardCorners);
        Assert.Empty(fixture.Camera.SelectedCorners);
        Assert.False(fixture.Camera.HasBoardCrop);

        fixture.Model.RejectionReason = "synthetic partly hidden board";
        fixture.Refresh();
        await fixture.CheckGameBoardAsync();
        Assert.True(fixture.Camera.CanStartGameWithBoard);
        Assert.Equal("All four board corners are visible.", fixture.Camera.GameBoardFramingStatus);
        Assert.Equal(FakeModel.Corners, fixture.Camera.GameBoardCorners);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Refresh();
        await fixture.CheckGameBoardAsync();
        Assert.False(fixture.Camera.CanStartGameWithBoard);
        Assert.Empty(fixture.Camera.GameBoardCorners);
        Assert.Contains("Move the camera", fixture.Camera.GameBoardFramingStatus);
        fixture.Camera.EndGameBoardFraming();
    }

    [Fact]
    public async Task Game_setup_rejects_corners_at_the_camera_edge_and_old_frames()
    {
        await using var fixture = new Fixture();
        fixture.Camera.BeginGameBoardFraming();
        fixture.Model.DetectedCorners = [new(.001, .12), new(.9, .12), new(.9, .88), new(.001, .88)];
        await fixture.CheckGameBoardAsync();
        Assert.False(fixture.Camera.CanStartGameWithBoard);
        Assert.Empty(fixture.Camera.GameBoardCorners);
        Assert.DoesNotContain("Move the camera", fixture.Camera.GameBoardFramingStatus);

        fixture.Clock.Advance(TimeSpan.FromMilliseconds(500));
        fixture.Refresh();
        await fixture.CheckGameBoardAsync();
        Assert.DoesNotContain("Move the camera", fixture.Camera.GameBoardFramingStatus);

        fixture.Clock.Advance(TimeSpan.FromMilliseconds(500));
        fixture.Refresh();
        await fixture.CheckGameBoardAsync();
        Assert.Contains("Move the camera back", fixture.Camera.GameBoardFramingStatus);

        fixture.Model.DetectedCorners = FakeModel.Corners;
        fixture.Refresh();
        await fixture.CheckGameBoardAsync();
        Assert.True(fixture.Camera.CanStartGameWithBoard);
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(fixture.Camera.CanStartGameWithBoard);
        fixture.Refresh(epoch: 2);
        Assert.False(fixture.Camera.CanStartGameWithBoard);
        await fixture.CheckGameBoardAsync();
        Assert.True(fixture.Camera.CanStartGameWithBoard);
        fixture.Camera.EndGameBoardFraming();
        Assert.False(fixture.Camera.CanStartGameWithBoard);
        Assert.Empty(fixture.Camera.GameBoardCorners);
    }

    [Fact]
    public async Task Game_setup_ignores_a_single_transient_miss_after_success()
    {
        await using var fixture = new Fixture();
        fixture.Camera.BeginGameBoardFraming();
        await fixture.CheckGameBoardAsync();

        fixture.Model.RejectionReason = "synthetic one-frame miss";
        fixture.Refresh();
        await fixture.CheckGameBoardAsync();
        Assert.Equal("All four board corners are visible.", fixture.Camera.GameBoardFramingStatus);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Model.RejectionReason = null;
        fixture.Refresh();
        await fixture.CheckGameBoardAsync();
        Assert.True(fixture.Camera.CanStartGameWithBoard);
        Assert.Equal("All four board corners are visible.", fixture.Camera.GameBoardFramingStatus);
    }

    [Fact]
    public async Task A_manual_crop_cannot_unlock_game_setup_when_the_ml_model_is_unavailable()
    {
        await using var fixture = new Fixture((_, _) => throw new FileNotFoundException("synthetic corner model missing"));
        fixture.SelectManualCrop();
        fixture.Camera.BeginGameBoardFraming();
        await fixture.CheckGameBoardAsync();

        Assert.True(fixture.Camera.HasBoardCrop);
        Assert.False(fixture.Camera.CanStartGameWithBoard);
        Assert.Empty(fixture.Camera.GameBoardCorners);
        Assert.Contains("synthetic corner model missing", fixture.Camera.GameBoardFramingStatus);
    }

    [Fact]
    public async Task Closing_game_setup_discards_an_in_flight_corner_result()
    {
        await using var fixture = new Fixture();
        fixture.Camera.BeginGameBoardFraming();
        fixture.Model.Pause(ignoreCancellation: true);
        var check = fixture.CheckGameBoardAsync();
        await fixture.Model.Entered.Task.WaitAsync(Token);

        fixture.Camera.EndGameBoardFraming();
        fixture.Model.Release();
        await check;

        Assert.False(fixture.Camera.CanStartGameWithBoard);
        Assert.Empty(fixture.Camera.GameBoardCorners);
    }

    [Fact]
    public async Task Model_uses_uncropped_frame_and_selects_four_ordered_handles_that_remain_editable()
    {
        await using var fixture = new Fixture();
        var source = fixture.Frame;

        await fixture.DetectAsync();

        Assert.Same(source, fixture.Model.LastFrame);
        Assert.Equal((320, 180), (source.Width, source.Height));
        Assert.Equal((fixture.ModelDirectory, false), Assert.Single(fixture.FactoryCalls));
        Assert.Equal(4, fixture.Camera.SelectedCorners.Count);
        Assert.True(fixture.Camera.SelectedCorners.Min(p => p.X) < FakeModel.Corners.Min(p => p.X));
        Assert.True(fixture.Camera.SelectedCorners.Max(p => p.X) > FakeModel.Corners.Max(p => p.X));
        Assert.True(fixture.Camera.SelectedCorners.Min(p => p.Y) < FakeModel.Corners.Min(p => p.Y));
        Assert.True(fixture.Camera.SelectedCorners.Max(p => p.Y) > FakeModel.Corners.Max(p => p.Y));
        Assert.True(fixture.Camera.HasBoardCrop, fixture.Camera.Problem);
        Assert.False(fixture.Camera.SelectingCorners);
        Assert.False(fixture.Camera.IsCornerDetectionBusy);
        Assert.NotNull(fixture.Camera.BoardPreview);
        Assert.True(fixture.Camera.CanExportPhoto);
        Assert.True(fixture.Camera.SafetyHeld);
        Assert.False(fixture.Camera.HasPieceReference);

        Assert.True(fixture.Camera.MoveBoardCorner(0, new(.12, .15)));
        Assert.Equal(new(.12, .15), fixture.Camera.SelectedCorners[0]);
        Assert.True(fixture.Camera.HasBoardCrop, fixture.Camera.Problem);
    }

    [Fact]
    public async Task Detection_retry_replaces_padding_and_manual_adjustment_is_exact()
    {
        await using var fixture = new Fixture();
        await fixture.DetectAsync();
        var first = fixture.Camera.SelectedCorners.ToArray();
        fixture.Refresh();
        await fixture.DetectAsync();
        Assert.Equal(first, fixture.Camera.SelectedCorners);

        var adjusted = new NormalizedPoint(.13, .16);
        Assert.True(fixture.Camera.MoveBoardCorner(0, adjusted));
        Assert.Equal(adjusted, fixture.Camera.SelectedCorners[0]);
        Assert.Equal(first[1..], fixture.Camera.SelectedCorners.Skip(1));
        var registration = Assert.IsType<BoardRegistration>(fixture.Registration);
        Assert.Equal(fixture.Camera.SelectedCorners, registration.Corners);
    }

    [Fact]
    public async Task Margin_at_camera_boundary_reports_limited_room_and_keeps_a_valid_crop()
    {
        await using var fixture = new Fixture();
        fixture.Model.DetectedCorners = [new(0, .1), new(.9, .1), new(.9, .9), new(0, .9)];
        await fixture.DetectAsync();
        Assert.True(fixture.Camera.HasBoardCrop, fixture.Camera.Problem);
        Assert.Contains("camera edge", fixture.Camera.CropText);
        Assert.All(fixture.Camera.SelectedCorners, point =>
        {
            Assert.InRange(point.X, 0, 1);
            Assert.InRange(point.Y, 0, 1);
        });
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("invalid geometry")]
    [InlineData("inference failure")]
    public async Task Unusable_detection_preserves_the_previous_crop(string outcome)
    {
        await using var fixture = new Fixture();
        fixture.SelectManualCrop();
        var corners = fixture.Camera.SelectedCorners.ToArray();
        var preview = fixture.Camera.BoardPreview;
        var registration = fixture.Registration;
        if (outcome == "rejected") fixture.Model.RejectionReason = "synthetic board is partly outside the image";
        if (outcome == "invalid geometry")
            fixture.Model.DetectedCorners = [new(.1, .1), new(.9, .9), new(.9, .1), new(.1, .9)];
        if (outcome == "inference failure") fixture.Model.Failure = new InvalidOperationException("synthetic corner inference failure");

        await fixture.DetectAsync();

        Assert.Equal(corners, fixture.Camera.SelectedCorners);
        Assert.Same(preview, fixture.Camera.BoardPreview);
        Assert.Same(registration, fixture.Registration);
        Assert.True(fixture.Camera.HasBoardCrop);
        Assert.True(fixture.Camera.CanExportPhoto);
        Assert.False(fixture.Camera.IsCornerDetectionBusy);
    }

    [Fact]
    public async Task Missing_model_preserves_manual_selection_and_allows_manual_adjustment()
    {
        await using var fixture = new Fixture((_, _) => throw new FileNotFoundException("synthetic corner model missing"));
        fixture.SelectManualCrop();
        var corners = fixture.Camera.SelectedCorners.ToArray();

        await fixture.DetectAsync();

        Assert.Equal(corners, fixture.Camera.SelectedCorners);
        Assert.True(fixture.Camera.HasBoardCrop);
        Assert.False(fixture.Camera.IsCornerDetectionBusy);
        Assert.Contains("synthetic corner model missing", fixture.Camera.CornerDetectionStatus);
        Assert.True(fixture.Camera.MoveBoardCorner(1, new(.91, .1)));
        Assert.True(fixture.Camera.HasBoardCrop);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("move")]
    public async Task Delayed_model_cannot_overwrite_a_new_manual_selection_or_adjustment(string mutation)
    {
        await using var fixture = new Fixture();
        fixture.SelectManualCrop();
        fixture.Model.Pause(ignoreCancellation: true);
        var detecting = fixture.DetectAsync();
        NormalizedPoint[] expected = [];
        try
        {
            await fixture.Model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            // The manual action must not wait on the inference lock.
            await Task.Run(() =>
            {
                if (mutation == "begin")
                {
                    fixture.Camera.BeginCornerSelectionCommand.Execute(null);
                    fixture.Camera.AddBoardCorner(new(.2, .15));
                }
                else Assert.True(fixture.Camera.MoveBoardCorner(0, new(.12, .15)));
            }, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            expected = fixture.Camera.SelectedCorners.ToArray();
        }
        finally
        {
            fixture.Model.Release();
            await detecting.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }

        Assert.Equal(expected, fixture.Camera.SelectedCorners);
        Assert.Equal(mutation == "begin", fixture.Camera.SelectingCorners);
        Assert.Equal(mutation == "move", fixture.Camera.HasBoardCrop);
        Assert.False(fixture.Camera.IsCornerDetectionBusy);
        Assert.DoesNotContain("Locating", fixture.Camera.CornerDetectionStatus);
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("format")]
    [InlineData("stale source")]
    [InlineData("stale latest")]
    [InlineData("stopped")]
    public async Task Delayed_result_cannot_select_corners_for_a_changed_or_stale_capture(string mutation)
    {
        await using var fixture = new Fixture();
        fixture.Model.Pause(ignoreCancellation: true);
        var detecting = fixture.DetectAsync();
        try
        {
            await fixture.Model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            switch (mutation)
            {
                case "epoch": fixture.Refresh(epoch: 2); break;
                case "format": fixture.Refresh(width: 400, height: 240); break;
                case "stale source":
                    fixture.Clock.Advance(TimeSpan.FromMilliseconds(2001));
                    fixture.Refresh();
                    break;
                case "stale latest": fixture.Clock.Advance(TimeSpan.FromMilliseconds(2001)); break;
                case "stopped": await fixture.Camera.StopCommand.ExecuteAsync(null); break;
            }
        }
        finally
        {
            fixture.Model.Release();
            await detecting.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }

        Assert.Empty(fixture.Camera.SelectedCorners);
        Assert.False(fixture.Camera.HasBoardCrop);
        Assert.Null(fixture.Camera.BoardPreview);
        Assert.False(fixture.Camera.IsCornerDetectionBusy);
    }

    [Fact]
    public async Task Model_loading_selects_a_fresh_frame_after_loading_finishes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        var model = new FakeModel();
        await using var fixture = new Fixture((_, _) =>
        {
            entered.TrySetResult();
            release.Wait(Token);
            return model;
        });
        var oldFrame = fixture.Frame;
        var detecting = fixture.DetectAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            fixture.Clock.Advance(TimeSpan.FromSeconds(3));
            fixture.Refresh();
        }
        finally
        {
            release.Set();
            await detecting.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }

        Assert.NotSame(oldFrame, model.LastFrame);
        Assert.Same(fixture.Frame, model.LastFrame);
        Assert.True(fixture.Camera.HasBoardCrop, fixture.Camera.Problem);
        model.ReleaseResources();
    }

    [Fact]
    public async Task Automatic_detection_attempts_once_per_capture_and_explicit_retry_remains_available()
    {
        await using var fixture = new Fixture();
        fixture.Model.RejectionReason = "synthetic low confidence";
        await fixture.AutomaticAsync();
        Assert.Equal(1, fixture.Model.Calls);
        Assert.False(fixture.Camera.HasBoardCrop);

        fixture.Refresh();
        await fixture.AutomaticAsync();
        Assert.Equal(1, fixture.Model.Calls);

        fixture.Model.RejectionReason = null;
        await fixture.DetectAsync();
        Assert.Equal(2, fixture.Model.Calls);
        Assert.True(fixture.Camera.HasBoardCrop, fixture.Camera.Problem);
        fixture.Refresh();
        await fixture.AutomaticAsync();
        Assert.Equal(2, fixture.Model.Calls);
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("format")]
    public async Task A_new_capture_identity_can_trigger_another_automatic_attempt(string change)
    {
        await using var fixture = new Fixture();
        fixture.Model.RejectionReason = "synthetic low confidence";
        await fixture.AutomaticAsync();
        Assert.Equal(1, fixture.Model.Calls);

        if (change == "epoch") fixture.Refresh(epoch: 2);
        else fixture.Refresh(width: 400, height: 240);
        fixture.Model.RejectionReason = null;
        await fixture.AutomaticAsync();

        Assert.Equal(2, fixture.Model.Calls);
        Assert.True(fixture.Camera.HasBoardCrop, fixture.Camera.Problem);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Automatic_detection_never_interrupts_manual_corner_placement(bool completed)
    {
        await using var fixture = new Fixture();
        fixture.Camera.BeginCornerSelectionCommand.Execute(null);
        foreach (var corner in Fixture.ManualCorners.Take(completed ? 4 : 1)) fixture.Camera.AddBoardCorner(corner);
        var expected = fixture.Camera.SelectedCorners.ToArray();

        await fixture.AutomaticAsync();
        fixture.Refresh();
        await fixture.AutomaticAsync();

        Assert.Empty(fixture.FactoryCalls);
        Assert.Equal(0, fixture.Model.Calls);
        Assert.Equal(expected, fixture.Camera.SelectedCorners);
        Assert.Equal(completed, fixture.Camera.HasBoardCrop);
    }

    [Fact]
    public async Task Automatic_ticks_do_not_queue_duplicate_work_while_detection_is_running()
    {
        await using var fixture = new Fixture();
        fixture.Model.Pause();
        var detecting = fixture.AutomaticAsync();
        try
        {
            await fixture.Model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            for (var index = 0; index < 4; index++)
            {
                fixture.Refresh();
                fixture.QueueAutomatic();
            }
            Assert.Equal(1, fixture.Model.Calls);
            Assert.Single(fixture.FactoryCalls);
        }
        finally
        {
            fixture.Model.Release();
            await detecting.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }

        Assert.True(fixture.Camera.HasBoardCrop, fixture.Camera.Problem);
        Assert.Equal(1, fixture.Model.Calls);
    }

    [Fact]
    public async Task Disposal_cancels_inference_before_disposing_model_and_does_not_publish_corners()
    {
        await using var fixture = new Fixture();
        fixture.Model.Pause();
        var detecting = fixture.DetectAsync();
        await fixture.Model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        await fixture.Camera.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);
        await detecting.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Empty(fixture.Camera.SelectedCorners);
        Assert.False(fixture.Camera.HasBoardCrop);
        Assert.Equal(1, fixture.Model.DisposeCalls);
        Assert.False(fixture.Model.DisposedDuringDetection);
        await fixture.Camera.DisposeAsync();
        Assert.Equal(1, fixture.Model.DisposeCalls);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private long _sequence;
        public static readonly NormalizedPoint[] ManualCorners = [new(.05, .05), new(.95, .05), new(.95, .95), new(.05, .95)];
        public FakeModel Model { get; } = new();
        public ManualFrameTimeProvider Clock { get; } = new();
        public string ModelDirectory { get; } = Path.Combine(Path.GetTempPath(), "synthetic-corner-model-" + Guid.NewGuid().ToString("N"));
        public ConcurrentQueue<(string Directory, bool PreferGpu)> FactoryCalls { get; } = new();
        public CameraViewModel Camera { get; }
        public CameraFrame Frame => Camera.Capture.LatestFrame!;
        public object? Registration => Field("_registration").GetValue(Camera);

        public Fixture(Func<string, bool, IBoardCornerDetector>? factory = null)
        {
            Camera = new(pieceModelDirectory: ModelDirectory + "-unused-pieces", boardCornerModelDirectory: ModelDirectory,
                boardCornerModelFactory: (directory, preferGpu) =>
                {
                    FactoryCalls.Enqueue((directory, preferGpu));
                    return factory is null ? Model : factory(directory, preferGpu);
                });
            Camera.SelectedProcessor = Camera.ProcessorModes.Single(option => option.Value == FrameComputeMode.Cpu);
            Camera.UseEnhancedPreview = false;
            SetCapture("<ActiveDevice>k__BackingField", new CameraDevice("synthetic-corner-camera", "Synthetic corner camera"));
            Camera.IsRunning = true;
            Refresh();
        }

        public void Refresh(long epoch = 1, int width = 320, int height = 180)
        {
            var pixels = new byte[width * height * 4];
            for (var index = 0; index < pixels.Length; index += 4)
            {
                pixels[index] = 110;
                pixels[index + 1] = 130;
                pixels[index + 2] = 150;
                pixels[index + 3] = 255;
            }
            SetCapture("_epoch", epoch);
            SetCapture("_running", true);
            SetCapture("_latest", CameraFrame.CopyFromBgra32(width, height, pixels, ++_sequence, epoch, clock: Clock));
        }

        public void SelectManualCrop()
        {
            Camera.BeginCornerSelectionCommand.Execute(null);
            foreach (var corner in ManualCorners) Camera.AddBoardCorner(corner);
            Assert.True(Camera.HasBoardCrop, Camera.Problem);
        }

        public Task DetectAsync() => Camera.DetectBoardCornersCommand.ExecuteAsync(null);

        public Task CheckGameBoardAsync() => (Task)typeof(CameraViewModel)
            .GetMethod("DetectBoardCornersCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Camera, [true])!;

        public void QueueAutomatic() => typeof(CameraViewModel)
            .GetMethod("QueueAutomaticCornerDetection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Camera, [Frame]);

        public Task AutomaticAsync()
        {
            QueueAutomatic();
            return (Task?)Field("_cornerWork").GetValue(Camera) ?? Task.CompletedTask;
        }

        private static FieldInfo Field(string name) => typeof(CameraViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        private void SetCapture(string name, object value) => typeof(CameraCaptureService)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Camera.Capture, value);

        public async ValueTask DisposeAsync()
        {
            Model.Release();
            await Camera.DisposeAsync();
            Model.ReleaseResources();
        }
    }

    private sealed class FakeModel : IBoardCornerDetector
    {
        private readonly ManualResetEventSlim _release = new(true);
        private int _calls, _active, _disposeCalls;
        private bool _ignoreCancellation;
        public static readonly NormalizedPoint[] Corners = [new(.1, .12), new(.9, .15), new(.88, .88), new(.12, .9)];
        public string ModelId => "synthetic-board-corners";
        public string ModelSha256 => new('b', 64);
        public string Backend => "synthetic CPU";
        public string? FallbackReason => null;
        public string? RejectionReason { get; set; }
        public Exception? Failure { get; set; }
        public IReadOnlyList<NormalizedPoint> DetectedCorners { get; set; } = Corners;
        public CameraFrame? LastFrame { get; private set; }
        public int Calls => Volatile.Read(ref _calls);
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public bool DisposedDuringDetection { get; private set; }
        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Pause(bool ignoreCancellation = false)
        {
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ignoreCancellation = ignoreCancellation;
            _release.Reset();
        }

        public void Release() => _release.Set();

        public LearnedBoardCornerDetection Detect(CameraFrame frame, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Interlocked.Increment(ref _active);
            try
            {
                LastFrame = frame;
                Entered.TrySetResult();
                _release.Wait(_ignoreCancellation ? CancellationToken.None : cancellationToken);
                if (!_ignoreCancellation) cancellationToken.ThrowIfCancellationRequested();
                if (Failure is { } failure) throw failure;
                return new(DetectedCorners, [.96, .95, .97, .94], ModelId, Backend, TimeSpan.FromMilliseconds(15), RejectionReason)
                    { ModelSha256 = ModelSha256 };
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        public void Dispose()
        {
            if (Volatile.Read(ref _active) != 0) DisposedDuringDetection = true;
            Interlocked.Increment(ref _disposeCalls);
        }

        public void ReleaseResources() => _release.Dispose();
    }
}
