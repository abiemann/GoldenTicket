using GoldenTicket.Testing;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

/// <summary>Owned synthetic frames and an injected detector; never opens a camera or a real model.</summary>
public sealed class CameraLearningFlowTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Direct_model_detects_without_empty_or_scene_reference_and_maps_the_selected_crop()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        await fixture.ProcessAsync();

        Assert.False(fixture.Camera.HasPieceReference);
        Assert.True(fixture.Camera.SafetyHeld);
        Assert.Equal(1, fixture.Model.Calls);
        Assert.Equal(2, fixture.Camera.PieceOutlines.Count);
        Assert.False(fixture.Camera.PieceOutlines[0].IsPlayerMarker);
        Assert.True(fixture.Camera.PieceOutlines[1].IsPlayerMarker);
        var registration = BoardRegistration.Create(fixture.Frame, fixture.Corners);
        for (var index = 0; index < FakeModel.Candidates[0].Outline.Count; index++)
        {
            var point = FakeModel.Candidates[0].Outline[index];
            Assert.Equal(registration.MapToSensor(point.X, point.Y), fixture.Camera.PieceOutlines[0].SensorOutline[index]);
        }
        Assert.Contains("1 train ·", fixture.Camera.DetectionText);
        Assert.Contains("1 score markers", fixture.Camera.DetectionText);
        Assert.Contains("synthetic-pieces-v1", fixture.Camera.ModelStatus);
        Assert.Equal((fixture.ModelDirectory, false), Assert.Single(fixture.FactoryCalls));
        Assert.True(fixture.Camera.CanSaveDetectionExample);
        var analyzed = Assert.IsType<CameraFrame>(fixture.Model.LastBoard);
        // Score reading must use exactly the analyzed crop, with the same candidate indices.
        Assert.Equal(ScoreMarkerReader.Read(analyzed, FakeModel.Candidates), fixture.Camera.ScoreMarkerReadings);
        Assert.Single(fixture.Camera.ScoreMarkerReadings);
        Assert.Equal((3456, 2160), (analyzed.Width, analyzed.Height));
        Assert.Equal(fixture.Frame.Sequence, analyzed.Sequence);
        Assert.Equal(fixture.Frame.Epoch, analyzed.Epoch);
        Assert.Equal(fixture.Frame.MonotonicTimestamp, analyzed.MonotonicTimestamp);
    }

    [Fact]
    public async Task Image_fitted_train_outline_is_mapped_through_crop_while_model_box_stays_unchanged()
    {
        await using var fixture = new Fixture();
        fixture.Model.ResultCandidates = OrientedCandidates();
        await fixture.InitializeAsync();
        await fixture.ProcessAsync();

        var registration = BoardRegistration.Create(fixture.Frame, fixture.Corners);
        var train = fixture.Model.ResultCandidates[0];
        Assert.Equal(FakeModel.Candidates[0].Outline, train.Outline);
        Assert.NotEqual(train.Outline, train.DisplayOutline);
        Assert.Equal(train.DisplayOutline.Select(p => registration.MapToSensor(p.X, p.Y)),
            fixture.Camera.PieceOutlines[0].SensorOutline);
        Assert.Equal(FakeModel.Candidates[1].Outline.Select(p => registration.MapToSensor(p.X, p.Y)),
            fixture.Camera.PieceOutlines[1].SensorOutline);
    }

    [Fact]
    public async Task Turning_outlines_off_clears_predictions_and_skips_inference_until_enabled()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        await fixture.ProcessAsync();
        Assert.NotEmpty(fixture.Camera.PieceOutlines);

        fixture.Camera.ShowPieceOutlines = false;
        Assert.Empty(fixture.Camera.PieceOutlines);
        AssertNoMarkerScores(fixture.Camera);
        Assert.Empty(fixture.Camera.ScoreMarkerReadings);
        Assert.All(fixture.Camera.MarkerScores, row => Assert.Equal("—", row.ValueText));
        Assert.False(fixture.Camera.CanSaveDetectionExample);
        fixture.Refresh();
        await fixture.ProcessAsync();
        Assert.Equal(1, fixture.Model.Calls);
        Assert.NotNull(fixture.Camera.Preview);
        Assert.Contains("off", fixture.Camera.DetectionText);

        fixture.Camera.ShowPieceOutlines = true;
        Assert.Empty(fixture.Camera.PieceOutlines);
        AssertNoMarkerScores(fixture.Camera);
        fixture.Refresh();
        await fixture.ProcessAsync();
        Assert.Equal(2, fixture.Model.Calls);
        Assert.NotEmpty(fixture.Camera.PieceOutlines);
    }

    [Theory]
    [InlineData("crop")]
    [InlineData("camera")]
    [InlineData("model")]
    [InlineData("toggle")]
    public async Task In_flight_results_cannot_reappear_after_their_identity_changes(string change)
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Model.Pause();
        var processing = fixture.ProcessAsync();
        Task mutation = Task.CompletedTask;
        try
        {
            await fixture.Model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(60), Token);
            switch (change)
            {
                case "crop":
                    // Run the mutation separately so a lock regression fails rather than deadlocks this test.
                    mutation = Task.Run(() => Assert.True(fixture.Camera.MoveBoardCorner(0, new(.08, .08))), Token);
                    await mutation.WaitAsync(TimeSpan.FromSeconds(5), Token);
                    break;
                case "camera":
                    fixture.Refresh(epoch: 2);
                    break;
                case "model":
                    mutation = fixture.Camera.ReloadPieceModelCommand.ExecuteAsync(null);
                    break;
                case "toggle":
                    fixture.Camera.ShowPieceOutlines = false;
                    fixture.Camera.ShowPieceOutlines = true;
                    break;
            }
        }
        finally
        {
            fixture.Model.Release();
            await Task.WhenAll(processing, mutation).WaitAsync(TimeSpan.FromSeconds(60), Token);
        }

        Assert.Empty(fixture.Camera.PieceOutlines);
        AssertNoMarkerScores(fixture.Camera);
        Assert.False(fixture.Camera.CanSaveDetectionExample);
        if (change == "model")
        {
            Assert.Equal(2, fixture.FactoryCalls.Count);
            Assert.Equal(1, fixture.Model.DisposeCalls);
        }
    }

    [Fact]
    public async Task Slow_result_expires_using_source_clock_even_while_capture_delivers_fresh_frames()
    {
        await using var fixture = new Fixture();
        fixture.Camera.UseEnhancedPreview = true;
        await fixture.InitializeAsync();
        await fixture.ProcessAsync();
        Assert.Equal(3456, fixture.Camera.Preview!.PixelWidth);
        fixture.Refresh();
        fixture.Model.Pause();
        var processing = fixture.ProcessAsync();
        try
        {
            await fixture.Model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(60), Token);
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(2001));
            fixture.Refresh(inverted: true);
        }
        finally
        {
            fixture.Model.Release();
            await processing.WaitAsync(TimeSpan.FromSeconds(60), Token);
        }
        Assert.Empty(fixture.Camera.PieceOutlines);
        AssertNoMarkerScores(fixture.Camera);
        Assert.False(fixture.Camera.CanSaveDetectionExample);
        Assert.Contains("fresh processed image", fixture.Camera.DetectionText);
        Assert.Contains("current camera image while ML catches up", fixture.Camera.ProcessingText);
        Assert.Equal(TimeSpan.Zero, fixture.Frame.Age);
        Assert.Equal(TimeSpan.FromMilliseconds(2001), fixture.Model.LastBoard!.Age);
        Assert.Equal(fixture.Frame.Width, fixture.Camera.Preview!.PixelWidth);
        Assert.Equal(fixture.Frame.Bgra32.ToArray(), Pixels(fixture.Camera.Preview));
        fixture.Camera.UseEnhancedPreview = false;
        fixture.Camera.UseEnhancedPreview = true;
        Assert.Equal(fixture.Frame.Width, fixture.Camera.Preview.PixelWidth);
        Assert.Equal(fixture.Frame.Bgra32.ToArray(), Pixels(fixture.Camera.Preview));
    }

    [Fact]
    public async Task Reload_waiting_for_the_model_lock_does_not_reuse_old_weights_or_block_camera_preview()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        await fixture.ProcessAsync();
        Assert.NotEmpty(fixture.Camera.PieceOutlines);
        var gate = typeof(CameraViewModel).GetField("_modelGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Camera)!;
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        // Hold the model lock before Reload can replace the still-installed old model. This
        // reproduces the queued-loader interval without relying on thread-pool scheduling order.
        var holding = Task.Run(() =>
        {
            lock (gate)
            {
                locked.TrySetResult();
                release.Wait(Token);
            }
        }, Token);
        Task reload = Task.CompletedTask, processing = Task.CompletedTask;
        try
        {
            await locked.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            reload = fixture.Camera.ReloadPieceModelCommand.ExecuteAsync(null);
            Assert.True(fixture.Camera.IsModelBusy);
            Assert.Empty(fixture.Camera.PieceOutlines);
            AssertNoMarkerScores(fixture.Camera);
            fixture.Refresh(inverted: true);
            processing = fixture.ProcessAsync();
            await processing.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(1, fixture.Model.Calls);
            Assert.Empty(fixture.Camera.PieceOutlines);
            AssertNoMarkerScores(fixture.Camera);
            Assert.False(fixture.Camera.CanSaveDetectionExample);
            Assert.Equal(fixture.Frame.Bgra32.ToArray(), Pixels(fixture.Camera.Preview!));
            Assert.Contains("Loading the local ML model", fixture.Camera.DetectionText);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(holding, reload, processing).WaitAsync(TimeSpan.FromSeconds(60), Token);
        }
        Assert.Equal(2, fixture.FactoryCalls.Count);
        Assert.Equal(1, fixture.Model.DisposeCalls);
        fixture.Refresh();
        await fixture.ProcessAsync();
        Assert.Equal(1, fixture.Model.Calls);
        Assert.Contains("synthetic-pieces-v2", fixture.Camera.ModelStatus);
        Assert.NotEmpty(fixture.Camera.PieceOutlines);
    }

    [Fact]
    public async Task Inference_failure_clears_old_predictions_and_keeps_raw_preview_available()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        await fixture.ProcessAsync();
        fixture.Model.Failure = new InvalidOperationException("synthetic inference failure");
        fixture.Refresh();
        await fixture.ProcessAsync();

        Assert.Empty(fixture.Camera.PieceOutlines);
        AssertNoMarkerScores(fixture.Camera);
        Assert.False(fixture.Camera.CanSaveDetectionExample);
        Assert.Contains("synthetic inference failure", fixture.Camera.DetectionText);
        var preview = Assert.IsAssignableFrom<BitmapSource>(fixture.Camera.Preview);
        Assert.Equal(fixture.Frame.Width, preview.PixelWidth);
        Assert.Equal(fixture.Frame.Height, preview.PixelHeight);
        Assert.Equal(fixture.Frame.Bgra32.ToArray(), Pixels(preview));
        Assert.True(fixture.Camera.CanExportPhoto);
    }

    [Fact]
    public async Task Model_load_failure_preserves_manual_preview_and_reports_the_actual_problem()
    {
        await using var fixture = new Fixture((_, _) => throw new InvalidDataException("synthetic invalid model hash"));
        await fixture.InitializeAsync();
        await fixture.ProcessAsync();

        Assert.Contains("synthetic invalid model hash", fixture.Camera.ModelStatus);
        Assert.Contains("unavailable", fixture.Camera.DetectionText);
        Assert.NotNull(fixture.Camera.Preview);
        Assert.Empty(fixture.Camera.PieceOutlines);
        AssertNoMarkerScores(fixture.Camera);
        Assert.False(fixture.Camera.CanSaveDetectionExample);
        Assert.True(fixture.Camera.CanExportPhoto);
    }

    [Fact]
    public async Task Disposal_cancels_active_inference_before_disposing_the_model_and_never_publishes_it()
    {
        var fixture = new Fixture();
        try
        {
            await fixture.InitializeAsync();
            fixture.Model.Pause();
            var processing = fixture.ProcessAsync();
            await fixture.Model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(60), Token);
            await fixture.Camera.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);
            await processing;
            Assert.Empty(fixture.Camera.PieceOutlines);
            AssertNoMarkerScores(fixture.Camera);
            Assert.False(fixture.Camera.CanSaveDetectionExample);
            Assert.Equal(1, fixture.Model.DisposeCalls);
            Assert.False(fixture.Model.DisposedDuringDetection);
            await fixture.Camera.DisposeAsync();
            Assert.Equal(1, fixture.Model.DisposeCalls);
        }
        finally
        {
            fixture.Model.Release();
            await fixture.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stopping_or_disposing_clears_scores_before_camera_teardown_finishes(bool dispose)
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        await fixture.ProcessAsync();
        Assert.NotEmpty(fixture.Camera.ScoreMarkerReadings);
        fixture.Refresh();
        fixture.Model.Pause();
        var processing = fixture.ProcessAsync();
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Capture.StopHandler = () => releaseCapture.Task;
        Task closing = Task.CompletedTask;
        try
        {
            await fixture.Model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(60), Token);
            closing = dispose ? fixture.Camera.DisposeAsync().AsTask()
                : fixture.Camera.StopCommand.ExecuteAsync(null);
            Assert.False(closing.IsCompleted);
            Assert.Empty(fixture.Camera.PieceOutlines);
            AssertNoMarkerScores(fixture.Camera);
            fixture.Model.Release();
            await processing.WaitAsync(TimeSpan.FromSeconds(60), Token);
            // Capture still owns its old frame until teardown is released; its late
            // inference must not restore readings after the user's stop/dispose request.
            Assert.Empty(fixture.Camera.PieceOutlines);
            AssertNoMarkerScores(fixture.Camera);
        }
        finally
        {
            fixture.Model.Release();
            releaseCapture.TrySetResult();
            await Task.WhenAll(processing, closing).WaitAsync(TimeSpan.FromSeconds(60), Token);
        }
        Assert.Empty(fixture.Camera.PieceOutlines);
        AssertNoMarkerScores(fixture.Camera);
    }

    [Fact]
    public async Task Review_zip_preserves_exact_analyzed_pixels_and_predictions_with_note_without_overwrite()
    {
        await using var fixture = new Fixture();
        fixture.Model.ResultCandidates = OrientedCandidates();
        var directory = Path.Combine(Path.GetTempPath(), "GoldenTicket-ml-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "review.zip");
        try
        {
            await fixture.InitializeAsync();
            await fixture.ProcessAsync();
            var analyzed = fixture.Model.LastBoard!;
            var original = analyzed.Bgra32.ToArray();
            fixture.Camera.DetectionReviewNote = "Missed the black train near Seattle; blue outline is correct.";
            // The capture has advanced; the saved image must still be the inference evidence.
            fixture.Refresh(inverted: true);
            await fixture.Camera.SaveDetectionExampleToAsync(path, Token);

            using (var archive = ZipFile.OpenRead(path))
            {
                Assert.Equal(new[] { "board.png", "predictions.json" }, archive.Entries.Select(entry => entry.FullName).Order().ToArray());
                using var imageStream = archive.GetEntry("board.png")!.Open();
                using var pngStream = new MemoryStream();
                await imageStream.CopyToAsync(pngStream, Token);
                var png = pngStream.ToArray();
                pngStream.Position = 0;
                var decoded = new PngBitmapDecoder(pngStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                Assert.Equal(original, Pixels(decoded));

                using var jsonStream = archive.GetEntry("predictions.json")!.Open();
                using var json = await JsonDocument.ParseAsync(jsonStream, cancellationToken: Token);
                var root = json.RootElement;
                Assert.False(root.GetProperty("reviewed").GetBoolean());
                Assert.Equal("model-prediction-review", root.GetProperty("purpose").GetString());
                Assert.Equal(fixture.Camera.DetectionReviewNote, root.GetProperty("note").GetString());
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(png)), root.GetProperty("sha256").GetString());
                Assert.Equal(analyzed.Width, root.GetProperty("width").GetInt32());
                Assert.Equal(analyzed.Height, root.GetProperty("height").GetInt32());
                Assert.Equal(analyzed.Sequence, root.GetProperty("frameSequence").GetInt64());
                Assert.NotEqual(fixture.Frame.Sequence, root.GetProperty("frameSequence").GetInt64());
                Assert.Equal(analyzed.Epoch, root.GetProperty("cameraEpoch").GetInt64());
                Assert.Equal(analyzed.CapturedAt, root.GetProperty("capturedAt").GetDateTimeOffset());
                Assert.Equal(fixture.Frame.Width, root.GetProperty("sensorWidth").GetInt32());
                Assert.Equal(fixture.Frame.Height, root.GetProperty("sensorHeight").GetInt32());
                Assert.True(root.GetProperty("cropRevision").GetInt64() > 0);
                Assert.True(root.TryGetProperty("processingRevision", out _));
                Assert.True(root.TryGetProperty("modelRevision", out _));
                Assert.Equal(4, root.GetProperty("corners").GetArrayLength());
                Assert.Equal(fixture.Model.ModelId, root.GetProperty("modelId").GetString());
                Assert.Equal(fixture.Model.ModelSha256, root.GetProperty("modelSha256").GetString());
                Assert.Equal(fixture.Model.Backend, root.GetProperty("backend").GetString());
                Assert.Equal("normalized-board", root.GetProperty("outlineCoordinateSpace").GetString());
                Assert.Equal("board-pixels", root.GetProperty("boxCoordinateSpace").GetString());
                var predictions = root.GetProperty("predictions").EnumerateArray().ToArray();
                Assert.Equal(2, predictions.Length);
                for (var index = 0; index < predictions.Length; index++)
                {
                    var expected = fixture.Model.ResultCandidates[index];
                    var actual = predictions[index];
                    Assert.Equal(index == 0 ? "train" : "player-marker", actual.GetProperty("kind").GetString());
                    Assert.Equal(expected.Confidence, actual.GetProperty("confidence").GetDouble());
                    var fitted = actual.GetProperty("orientedOutline");
                    if (expected.OrientedOutline is { } oriented)
                    {
                        Assert.Equal("local-image-fit", actual.GetProperty("orientedOutlineSource").GetString());
                        Assert.Equal(oriented.Select(p => p.X), fitted.EnumerateArray().Select(p => p.GetProperty("x").GetDouble()));
                        Assert.Equal(oriented.Select(p => p.Y), fitted.EnumerateArray().Select(p => p.GetProperty("y").GetDouble()));
                    }
                    else
                    {
                        Assert.Equal(JsonValueKind.Null, fitted.ValueKind);
                        Assert.Equal(JsonValueKind.Null, actual.GetProperty("orientedOutlineSource").ValueKind);
                    }
                    Assert.Equal(expected.Outline.Min(point => point.X) * analyzed.Width, actual.GetProperty("x").GetDouble());
                    Assert.Equal(expected.Outline.Min(point => point.Y) * analyzed.Height, actual.GetProperty("y").GetDouble());
                    Assert.Equal((expected.Outline.Max(point => point.X) - expected.Outline.Min(point => point.X)) * analyzed.Width,
                        actual.GetProperty("width").GetDouble());
                    Assert.Equal((expected.Outline.Max(point => point.Y) - expected.Outline.Min(point => point.Y)) * analyzed.Height,
                        actual.GetProperty("height").GetDouble());
                    var outline = actual.GetProperty("outline").EnumerateArray().ToArray();
                    Assert.Equal(4, outline.Length);
                    for (var point = 0; point < outline.Length; point++)
                    {
                        Assert.Equal(expected.Outline[point].X, outline[point].GetProperty("x").GetDouble());
                        Assert.Equal(expected.Outline[point].Y, outline[point].GetProperty("y").GetDouble());
                    }
                }
            }
            var saved = await File.ReadAllBytesAsync(path, Token);
            await Assert.ThrowsAsync<IOException>(() => fixture.Camera.SaveDetectionExampleToAsync(path, Token));
            Assert.Equal(saved, await File.ReadAllBytesAsync(path, Token));
            Assert.Equal(original, analyzed.Bgra32.ToArray());
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally
        {
            // This fixture owns the unique directory and its files.
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static void AssertNoMarkerScores(CameraViewModel camera)
    {
        Assert.Empty(camera.ScoreMarkerReadings);
        Assert.All(camera.MarkerScores, row =>
        {
            Assert.Equal("—", row.ValueText);
            Assert.Equal("Waiting", row.StatusText);
        });
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[checked(source.PixelWidth * source.PixelHeight * 4)];
        converted.CopyPixels(bytes, source.PixelWidth * 4, 0);
        return bytes;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private long _sequence;
        private readonly FakeModel _replacement = new("synthetic-pieces-v2");
        public FakeModel Model { get; } = new("synthetic-pieces-v1");
        public ManualFrameTimeProvider Clock { get; } = new();
        public NormalizedPoint[] Corners { get; } = [new(.05, .05), new(.95, .05), new(.95, .95), new(.05, .95)];
        public string ModelDirectory { get; } = Path.Combine(Path.GetTempPath(), "synthetic-piece-model-" + Guid.NewGuid().ToString("N"));
        public ConcurrentQueue<(string Directory, bool PreferGpu)> FactoryCalls { get; } = new();
        public CameraViewModel Camera { get; }
        public FakeCameraCapture Capture => (FakeCameraCapture)Camera.Capture;
        public CameraFrame Frame => Camera.Capture.LatestFrame!;

        public Fixture(Func<string, bool, IPieceModelDetector>? factory = null)
        {
            Camera = new(capture: new FakeCameraCapture(), pieceModelDirectory: ModelDirectory, pieceModelFactory: (directory, preferGpu) =>
            {
                FactoryCalls.Enqueue((directory, preferGpu));
                return factory is not null ? factory(directory, preferGpu) : FactoryCalls.Count == 1 ? Model : _replacement;
            });
            Camera.SelectedProcessor = Camera.ProcessorModes.Single(option => option.Value == FrameComputeMode.Cpu);
            Camera.UseEnhancedPreview = false;
            Capture.ActiveDevice = new CameraDevice("synthetic-ml-camera", "Synthetic ML camera");
            Camera.IsRunning = true;
            Refresh();
        }

        public async Task InitializeAsync()
        {
            await Camera.InitializeProcessingAsync();
            Camera.BeginCornerSelectionCommand.Execute(null);
            foreach (var corner in Corners) Camera.AddBoardCorner(corner);
            Assert.True(Camera.HasBoardCrop, Camera.Problem);
        }

        public void Refresh(long epoch = 1, bool inverted = false)
        {
            const int width = 96, height = 60;
            var bytes = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var index = (y * width + x) * 4;
                var value = (byte)((((x / 6 + y / 6) % 2 == 0) != inverted) ? 170 : 70);
                bytes[index] = value;
                bytes[index + 1] = (byte)(value + 15);
                bytes[index + 2] = (byte)(value - 15);
                bytes[index + 3] = 255;
            }
            Capture.Epoch = epoch;
            Capture.IsRunning = true;
            Capture.LatestFrame = CameraFrame.CopyFromBgra32(width, height, bytes, ++_sequence, epoch, clock: Clock);
        }

        public Task ProcessAsync()
        {
            typeof(CameraViewModel).GetMethod("QueueFrameProcessing", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Camera, [Frame]);
            return (Task)typeof(CameraViewModel).GetField("_frameWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Camera)!;
        }

        public async ValueTask DisposeAsync()
        {
            Model.Release();
            _replacement.Release();
            await Camera.DisposeAsync();
            Model.ReleaseResources();
            _replacement.ReleaseResources();
        }
    }

    private static PieceCandidate[] OrientedCandidates() =>
    [
        FakeModel.Candidates[0] with
        {
            OrientedOutline = [new(.15, .27), new(.27, .25), new(.28, .27), new(.16, .29)]
        },
        FakeModel.Candidates[1]
    ];

    private sealed class FakeModel(string modelId) : IPieceModelDetector
    {
        private readonly ManualResetEventSlim _release = new(true);
        private int _calls;
        private int _active;
        private int _disposeCalls;
        public static readonly PieceCandidate[] Candidates =
        [
            new(PieceCandidateKind.Train, [new(.15, .25), new(.28, .25), new(.28, .29), new(.15, .29)], .91),
            new(PieceCandidateKind.PlayerMarker, [new(.87, .82), new(.92, .82), new(.92, .88), new(.87, .88)], .82)
        ];
        public string ModelId => modelId;
        public IReadOnlyList<PieceCandidate> ResultCandidates { get; set; } = Candidates;
        public string ModelSha256 => new('a', 64);
        public string Backend => "synthetic CPU";
        public string? FallbackReason => null;
        public CameraFrame? LastBoard { get; private set; }
        public Exception? Failure { get; set; }
        public int Calls => Volatile.Read(ref _calls);
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public bool DisposedDuringDetection { get; private set; }
        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Pause()
        {
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _release.Reset();
        }
        public void Release() => _release.Set();
        public LearnedPieceDetection Detect(CameraFrame board, CancellationToken token = default)
        {
            Interlocked.Increment(ref _calls);
            Interlocked.Increment(ref _active);
            try
            {
                LastBoard = board;
                Entered.TrySetResult();
                _release.Wait(token);
                token.ThrowIfCancellationRequested();
                if (Failure is { } failure) throw failure;
                return new(ResultCandidates, ModelId, Backend, TimeSpan.FromMilliseconds(12)) { ModelSha256 = ModelSha256 };
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
