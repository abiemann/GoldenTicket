using GoldenTicket.Testing;
using System.Collections.Concurrent;
using System.Reflection;
using System.Windows.Media.Imaging;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

/// <summary>Owned camera frames and an injected corner model; never opens hardware or real model files.</summary>
public sealed class CameraCornerLearningFlowTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reload_camera_phase_accepts_corners_without_new_game_piece_rules()
    {
        await using var fixture = new Fixture();
        fixture.Camera.BeginReloadBoardFraming();

        await fixture.CheckGameBoardAsync();

        Assert.True(fixture.Camera.HasFreshGameBoardCorners);
        Assert.False(fixture.Camera.CanStartGameWithBoard);
        Assert.Contains("You can reload", fixture.Camera.GameBoardFramingStatus);
        Assert.False(fixture.Camera.HasBoardCrop);

        fixture.Camera.EndGameBoardFraming();
        Assert.False(fixture.Camera.HasFreshGameBoardCorners);
    }

    [Fact]
    public async Task Game_setup_keeps_success_through_one_miss_then_warns_after_a_second_miss()
    {
        await using var fixture = new Fixture();
        fixture.Camera.BeginGameBoardFraming();
        Assert.False(fixture.Camera.CanStartGameWithBoard);

        await fixture.CheckGameBoardAsync();
        Assert.True(fixture.Camera.HasFreshGameBoardCorners);
        Assert.False(fixture.Camera.CanStartGameWithBoard);
        Assert.Equal(FakeModel.Corners, fixture.Camera.GameBoardCorners);
        Assert.Empty(fixture.Camera.SelectedCorners);
        Assert.False(fixture.Camera.HasBoardCrop);

        fixture.Model.RejectionReason = "synthetic partly hidden board";
        fixture.Refresh();
        await fixture.CheckGameBoardAsync();
        Assert.True(fixture.Camera.HasFreshGameBoardCorners);
        Assert.Contains("Piece detection unavailable", fixture.Camera.GameBoardFramingStatus);
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
        Assert.True(fixture.Camera.HasFreshGameBoardCorners);
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(fixture.Camera.CanStartGameWithBoard);
        fixture.Refresh(epoch: 2);
        Assert.False(fixture.Camera.CanStartGameWithBoard);
        await fixture.CheckGameBoardAsync();
        Assert.True(fixture.Camera.HasFreshGameBoardCorners);
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
        Assert.Contains("Piece detection unavailable", fixture.Camera.GameBoardFramingStatus);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Model.RejectionReason = null;
        fixture.Refresh();
        await fixture.CheckGameBoardAsync();
        Assert.True(fixture.Camera.HasFreshGameBoardCorners);
        Assert.Contains("Piece detection unavailable", fixture.Camera.GameBoardFramingStatus);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Game_setup_requires_both_selected_score_colors_on_printed_one(int orientationIndex)
    {
        using var pieces = new FakePieceModel();
        await using var fixture = new Fixture(pieceFactory: (_, _) => pieces);
        var orientedCorners = GameBoardOrientations.Enumerate(FakeModel.Corners)[orientationIndex];
        fixture.FramePainter = (pixels, width, height) =>
        {
            PaintScorePiece(pixels, width, height, orientedCorners, .017, .9266, (138, 55, 48));
            PaintScorePiece(pixels, width, height, orientedCorners, .055, .9266, (0, 48, 100));
        };
        fixture.Refresh();
        fixture.Camera.BeginGameBoardFraming([MarkerColor.Red, MarkerColor.Blue]);
        await fixture.CheckGameBoardAsync();
        Assert.True(fixture.Camera.HasFreshGameBoardCorners);
        Assert.True(fixture.Camera.CanStartGameWithBoard, fixture.Camera.GameBoardFramingStatus);
        Assert.Equal("", fixture.Camera.GameBoardFramingStatus);
        Assert.Equal(4, pieces.Calls);

        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(fixture.Camera.CanStartGameWithBoard);
        fixture.Refresh();
        await fixture.CheckGameBoardAsync();
        Assert.True(fixture.Camera.CanStartGameWithBoard, fixture.Camera.GameBoardFramingStatus);

        pieces.ShowTrain = true;
        fixture.Refresh();
        await fixture.CheckGameBoardAsync();
        Assert.False(fixture.Camera.CanStartGameWithBoard);
        Assert.Contains("Remove all trains", fixture.Camera.GameBoardFramingStatus);
        pieces.ShowTrain = false;
        fixture.Refresh();
        await fixture.CheckGameBoardAsync();
        Assert.True(fixture.Camera.CanStartGameWithBoard, fixture.Camera.GameBoardFramingStatus);

        fixture.FramePainter = (pixels, width, height) =>
            PaintScorePiece(pixels, width, height, orientedCorners, .017, .9266, (138, 55, 48));
        fixture.Refresh();
        await fixture.CheckGameBoardAsync();
        Assert.False(fixture.Camera.CanStartGameWithBoard);
        Assert.Contains("blue", fixture.Camera.GameBoardFramingStatus);
    }

    [Fact]
    public async Task Game_setup_keeps_corners_ready_when_marker_analysis_outlasts_the_corner_timer()
    {
        using var pieces = new FakePieceModel();
        await using var fixture = new Fixture(pieceFactory: (_, _) => pieces);
        fixture.FramePainter = (pixels, width, height) =>
        {
            PaintScorePiece(pixels, width, height, FakeModel.Corners, .017, .9266, (138, 55, 48));
            PaintScorePiece(pixels, width, height, FakeModel.Corners, .055, .9266, (0, 48, 100));
        };
        fixture.Refresh();
        // Simulate slow analysis without a wall-clock sleep: the source frame remains fresh.
        pieces.BeforeDetect = () => fixture.SetField("_gameBoardAcceptedAt", DateTimeOffset.UtcNow.AddSeconds(-3));
        fixture.Camera.BeginGameBoardFraming([MarkerColor.Red, MarkerColor.Blue]);

        await fixture.CheckGameBoardAsync();

        Assert.True(fixture.Camera.HasFreshGameBoardCorners);
        Assert.True(fixture.Camera.CanStartGameWithBoard, fixture.Camera.GameBoardFramingStatus);
    }

    [Fact]
    public async Task Rotating_a_live_board_half_a_turn_holds_then_restores_Miami_bottom_right()
    {
        using var pieces = new FakePieceModel();
        await using var fixture = new Fixture(pieceFactory: (_, _) => pieces);
        NormalizedPoint[] corners = [new(.1, .12), new(.9, .12), new(.9, .88), new(.1, .88)];
        fixture.Model.DetectedCorners = corners;
        var halfTurn = false;
        fixture.FramePainter = (pixels, width, height) =>
        {
            PaintAsymmetricBoard(pixels, width, height, halfTurn);
            if (!halfTurn)
            {
                PaintScorePiece(pixels, width, height, corners, .017, .9266, (138, 55, 48));
                PaintScorePiece(pixels, width, height, corners, .055, .9266, (0, 48, 100));
            }
        };
        fixture.Refresh();
        fixture.Camera.BeginGameBoardFraming([MarkerColor.Red, MarkerColor.Blue]);
        await fixture.CheckGameBoardAsync();
        Assert.True(fixture.Camera.CanStartGameWithBoard, fixture.Camera.GameBoardFramingStatus);
        fixture.Camera.BeginGameTablePreview();
        fixture.Camera.EndGameBoardFraming();
        await fixture.WaitForGameTableAlignmentAsync();
        Assert.True(fixture.Camera.IsGameTablePreviewUpright);

        halfTurn = true;
        fixture.Refresh();
        typeof(CameraViewModel).GetField("_lastLiveBoardCheckAt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.Camera, DateTimeOffset.UtcNow);
        typeof(CameraViewModel).GetMethod("CheckLiveBoardAlignment", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Camera, [fixture.Frame]);
        Assert.False(fixture.Camera.IsGameTablePreviewUpright);
        Assert.Null(fixture.Camera.GameTablePreview);

        await (Task)typeof(CameraViewModel)
            .GetMethod("DetectLiveBoardAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Camera, [fixture.Frame])!;
        Assert.True(fixture.Camera.IsGameTablePreviewUpright, fixture.Camera.GameTablePreviewStatus);
        var restored = (BoardRegistration)typeof(CameraViewModel)
            .GetField("_gameTableRegistration", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Camera)!;
        Assert.Equal(corners[2], restored.Corners[0], NormalizedPointComparer.Instance);
        Assert.Equal(corners[0], restored.Corners[2], NormalizedPointComparer.Instance);

        NormalizedPoint[] adjusted = [new(.096, .116), new(.904, .116), new(.904, .884), new(.096, .884)];
        var manual = BoardRegistration.Create(fixture.Frame, adjusted);
        typeof(CameraViewModel).GetMethod("AdoptTechnicalBoardCrop", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Camera, [fixture.Frame, manual]);
        Assert.True(fixture.Camera.IsGameTablePreviewUpright, fixture.Camera.GameTablePreviewStatus);
        restored = (BoardRegistration)typeof(CameraViewModel)
            .GetField("_gameTableRegistration", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Camera)!;
        Assert.Equal(adjusted[2], restored.Corners[0], NormalizedPointComparer.Instance);
    }

    [Fact]
    public async Task Reload_refines_detected_corners_against_the_saved_board_photo()
    {
        await using var fixture = new Fixture();
        NormalizedPoint[] corners = [new(.1, .12), new(.9, .12), new(.9, .88), new(.1, .88)];
        fixture.FramePainter = (pixels, width, height) =>
        {
            PaintAsymmetricBoard(pixels, width, height, false);
            // Printed artwork has texture within each region, not only flat color blocks.
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var detail = 28 * Math.Sin(x * .065) + 28 * Math.Cos(y * .081 + x * .021);
                var offset = (y * width + x) * 4;
                for (var channel = 0; channel < 3; channel++)
                    pixels[offset + channel] = (byte)Math.Clamp(pixels[offset + channel] + detail, 0, 255);
            }
        };
        fixture.Refresh(width: 1600, height: 900);
        var expected = BoardCropPadding.Expand(fixture.Frame, corners).Corners;
        var photo = BoardRegistration.Create(fixture.Frame, expected).Rectify(fixture.Frame, 1280, 800);
        var bitmap = BitmapSource.Create(photo.Width, photo.Height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, photo.Bgra32.ToArray(), photo.Stride);
        fixture.Camera.SetGameTableReference(bitmap);
        fixture.Model.DetectedCorners = corners.Select(point =>
            new NormalizedPoint(point.X + .003, point.Y + .002)).ToArray();

        fixture.Camera.RequestGameTablePreview();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await (Task)typeof(CameraViewModel).GetField("_liveBoardCheckWork", flags)!
            .GetValue(fixture.Camera)!;

        Assert.True(fixture.Camera.IsGameTablePreviewUpright, fixture.Camera.GameTablePreviewStatus);
        var registration = (BoardRegistration)typeof(CameraViewModel)
            .GetField("_gameTableRegistration", flags)!.GetValue(fixture.Camera)!;
        var expectedRegistration = BoardRegistration.Create(fixture.Frame, expected);
        ClassicUsRouteGeometry.TryGetSlots("duluth--omaha--a", out var slots);
        Assert.All(slots, slot =>
        {
            var actual = registration.MapToSensor(slot.X, slot.Y);
            var target = expectedRegistration.MapToSensor(slot.X, slot.Y);
            Assert.InRange(Math.Abs(target.X - actual.X), 0, .0015);
            Assert.InRange(Math.Abs(target.Y - actual.Y), 0, .0015);
        });
        fixture.Camera.EndGameTablePreview();
        Assert.Null(typeof(CameraViewModel).GetField("_gameTablePhotoAlignment", flags)!
            .GetValue(fixture.Camera));
    }

    [Fact]
    public async Task Minor_corner_recalibration_keeps_the_board_visible_until_a_1500ms_handoff_expires()
    {
        using var pieces = new FakePieceModel();
        await using var fixture = new Fixture(pieceFactory: (_, _) => pieces);
        NormalizedPoint[] corners = [new(.1, .12), new(.9, .12), new(.9, .88), new(.1, .88)];
        fixture.Model.DetectedCorners = corners;
        fixture.FramePainter = (pixels, width, height) =>
        {
            PaintAsymmetricBoard(pixels, width, height, false);
            PaintScorePiece(pixels, width, height, corners, .017, .9266, (138, 55, 48));
            PaintScorePiece(pixels, width, height, corners, .055, .9266, (0, 48, 100));
        };
        fixture.Refresh();
        fixture.Camera.BeginGameBoardFraming([MarkerColor.Red, MarkerColor.Blue]);
        await fixture.CheckGameBoardAsync();
        Assert.True(fixture.Camera.CanStartGameWithBoard, fixture.Camera.GameBoardFramingStatus);
        fixture.Camera.BeginGameTablePreview();
        fixture.Camera.EndGameBoardFraming();
        await fixture.WaitForGameTableAlignmentAsync();
        for (var attempt = 0; attempt < 50 && fixture.Camera.GameTablePreview is null; attempt++)
            await Task.Delay(20, cancellationToken: TestContext.Current.CancellationToken);
        BitmapSource previous = Assert.IsAssignableFrom<BitmapSource>(fixture.Camera.GameTablePreview);

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var cropBusy = typeof(CameraViewModel).GetField("_gameTableCropBusy", flags)!;
        var revision = typeof(CameraViewModel).GetField("_gameTableCropRevision", flags)!;
        var oldRevision = (long)revision.GetValue(fixture.Camera)!;
        cropBusy.SetValue(fixture.Camera, true); // Hold the replacement render until the handoff is checked.
        fixture.Model.DetectedCorners = corners.Select(point =>
            new NormalizedPoint(point.X + .003, point.Y + .003)).ToArray();
        fixture.Refresh();
        var blankTransitions = 0;
        fixture.Camera.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CameraViewModel.GameTablePreview) &&
                fixture.Camera.GameTablePreview is null) blankTransitions++;
        };
        await (Task)typeof(CameraViewModel).GetMethod("DetectLiveBoardAsync", flags)!
            .Invoke(fixture.Camera, [fixture.Frame])!;
        Assert.Equal(oldRevision + 1, (long)revision.GetValue(fixture.Camera)!);
        Assert.Same(previous, fixture.Camera.GameTablePreview);
        Assert.Equal(0, blankTransitions);
        var handoffTimer = (System.Windows.Threading.DispatcherTimer)typeof(CameraViewModel)
            .GetField("_gameTableHandoffTimer", flags)!.GetValue(fixture.Camera)!;
        Assert.Equal(TimeSpan.FromMilliseconds(1500), handoffTimer.Interval);

        typeof(CameraViewModel).GetMethod("OnGameTableHandoffExpired", flags)!
            .Invoke(fixture.Camera, [null, EventArgs.Empty]);
        Assert.Null(fixture.Camera.GameTablePreview);
        Assert.Equal(1, blankTransitions);
        cropBusy.SetValue(fixture.Camera, false);
        typeof(CameraViewModel).GetMethod("QueueGameTablePreview", flags)!
            .Invoke(fixture.Camera, [fixture.Frame]);
        for (var attempt = 0; attempt < 50 && fixture.Camera.GameTablePreview is null; attempt++)
            await Task.Delay(20, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(fixture.Camera.GameTablePreview);
        Assert.Equal(string.Empty, fixture.Camera.GameTablePreviewStatus);
    }

    [Fact]
    public async Task Reload_uses_canonical_route_coordinates_even_when_the_saved_photo_is_biased()
    {
        await using var fixture = new Fixture();
        fixture.FramePainter = (pixels, width, height) => PaintCanonicalBoard(pixels, width, height);
        fixture.Refresh(width: 1600, height: 900);
        var expected = BoardRegistration.Create(fixture.Frame, CanonicalSensorCorners());
        var biased = BoardRegistration.Create(fixture.Frame, CanonicalSensorCorners(.004, .002));
        fixture.Camera.SetGameTableReference(ToBitmap(biased.Rectify(fixture.Frame, 1280, 800)));
        fixture.Model.DetectedCorners = biased.Corners;

        fixture.Camera.RequestGameTablePreview();
        await fixture.WaitForLiveBoardCheckAsync();

        Assert.True(fixture.Camera.IsGameTablePreviewUpright, fixture.Camera.GameTablePreviewStatus);
        AssertCanonicalRouteMapping(expected, fixture.GameTableRegistration);
        ClassicUsRouteGeometry.TryGetSlots("los-angeles--san-francisco--b", out var slots);
        Assert.All(slots, slot => Assert.True(
            Math.Abs(fixture.GameTableRegistration.MapToSensor(slot.X, slot.Y).X -
                biased.MapToSensor(slot.X, slot.Y).X) > .002));
    }

    [Theory]
    [InlineData(.006, -.003)]
    [InlineData(.014, -.006)]
    public async Task Brief_loss_of_board_detail_reacquires_Boston_trains_in_canonical_coordinates(
        double detectedDx, double detectedDy)
    {
        const string routeId = "boston--new-york--a";
        ClassicUsRouteGeometry.TryGetSlots(routeId, out var slots);
        using var pieces = new FakePieceModel
        {
            Trains = slots.Select(slot => new PieceCandidate(PieceCandidateKind.Train,
                [new(slot.X - .005, slot.Y - .007), new(slot.X + .005, slot.Y - .007),
                    new(slot.X + .005, slot.Y + .007), new(slot.X - .005, slot.Y + .007)], .95)).ToArray()
        };
        await using var fixture = new Fixture(pieceFactory: (_, _) => pieces);
        var detailLost = false;
        fixture.FramePainter = (pixels, width, height) =>
        {
            if (detailLost) return; // Temporary featureless frame during webcam refocusing.
            PaintCanonicalBoard(pixels, width, height);
            var sensor = BoardRegistration.Create(CameraFrame.CopyFromBgra32(width, height, pixels),
                CanonicalSensorCorners());
            foreach (var slot in slots)
            {
                var point = sensor.MapToSensor(slot.X, slot.Y);
                var cx = (int)Math.Round(point.X * (width - 1));
                var cy = (int)Math.Round(point.Y * (height - 1));
                for (var y = cy - 8; y <= cy + 8; y++)
                for (var x = cx - 8; x <= cx + 8; x++)
                {
                    var offset = (y * width + x) * 4;
                    pixels[offset] = 25;
                    pixels[offset + 1] = 205;
                    pixels[offset + 2] = 245;
                }
            }
        };
        fixture.Refresh(width: 1600, height: 900);
        fixture.Camera.SetGameTableReference(ToBitmap(
            BoardRegistration.Create(fixture.Frame, CanonicalSensorCorners()).Rectify(fixture.Frame, 1280, 800)));
        fixture.Model.DetectedCorners = CanonicalSensorCorners();
        fixture.SetField("_pieceModel", pieces);
        fixture.Camera.RequestGameTablePreview();
        await fixture.WaitForLiveBoardCheckAsync();
        await fixture.WaitForGameTableAnalysisAsync();
        var before = Assert.IsType<GameTableAnalysis>(fixture.Camera.GameTableAnalysis);

        detailLost = true;
        fixture.Refresh(width: 1600, height: 900);
        fixture.SetField("_lastLiveBoardCheckAt", DateTimeOffset.UtcNow);
        typeof(CameraViewModel).GetMethod("CheckLiveBoardAlignment", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Camera, [fixture.Frame]);
        Assert.False(fixture.Camera.IsGameTablePreviewUpright);
        Assert.Null(fixture.Camera.GameTableAnalysis);
        await fixture.DetectLiveBoardAsync();
        Assert.False(fixture.Camera.IsGameTablePreviewUpright);

        detailLost = false;
        fixture.Refresh(width: 1600, height: 900);
        // Autofocus can change the corner proposal even though the board and trains did not move.
        fixture.Model.DetectedCorners = CanonicalSensorCorners(detectedDx, detectedDy);
        fixture.SetField("_lastGameTableAnalysisAt", DateTimeOffset.MinValue);
        await fixture.DetectLiveBoardAsync();
        await fixture.WaitForGameTableAnalysisAsync();
        var recovered = Assert.IsType<GameTableAnalysis>(fixture.Camera.GameTableAnalysis);
        Assert.True(fixture.Camera.IsGameTablePreviewUpright, fixture.Camera.GameTablePreviewStatus);
        Assert.True(recovered.Board.Sequence > before.Board.Sequence);
        Assert.True(recovered.CropRevision > before.CropRevision);
        Assert.Equal(fixture.Frame.Sequence, recovered.Board.Sequence);
        var expected = BoardRegistration.Create(fixture.Frame, CanonicalSensorCorners());
        Assert.All(slots, slot =>
        {
            var actual = fixture.GameTableRegistration.MapToSensor(slot.X, slot.Y);
            var target = expected.MapToSensor(slot.X, slot.Y);
            Assert.InRange(Math.Abs(actual.X - target.X), 0, .0015);
            Assert.InRange(Math.Abs(actual.Y - target.Y), 0, .0015);
        });

        var verifier = new RoutePlacementVerifier();
        Assert.Equal(RoutePlacementState.Stabilizing, verifier.Observe(recovered.Board,
            recovered.Candidates, routeId, MarkerColor.Yellow, 2, "recovered-claim",
            recovered.CropRevision, recovered.ModelRevision).State);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1.1));
        fixture.Refresh(width: 1600, height: 900);
        fixture.SetField("_lastGameTableAnalysisAt", DateTimeOffset.MinValue);
        fixture.QueueGameTableAnalysis();
        await fixture.WaitForGameTableAnalysisAsync();
        var stable = Assert.IsType<GameTableAnalysis>(fixture.Camera.GameTableAnalysis);
        Assert.True(verifier.Observe(stable.Board, stable.Candidates, routeId, MarkerColor.Yellow,
            2, "recovered-claim", stable.CropRevision, stable.ModelRevision).Confirmed);
        Assert.Equal(0, new RoutePlacementVerifier().Observe(stable.Board, stable.Candidates,
            "boston--new-york--b", MarkerColor.Yellow, 2, "wrong-lane",
            stable.CropRevision, stable.ModelRevision).MatchedCount);
    }

    [Fact]
    public async Task Manual_game_crop_cannot_publish_piece_evidence_until_alignment_finishes()
    {
        using var pieces = new FakePieceModel();
        await using var fixture = new Fixture(pieceFactory: (_, _) => pieces);
        fixture.FramePainter = (pixels, width, height) => PaintCanonicalBoard(pixels, width, height);
        fixture.Refresh(width: 1600, height: 900);
        var expected = BoardRegistration.Create(fixture.Frame, CanonicalSensorCorners());
        fixture.Camera.SetGameTableReference(ToBitmap(expected.Rectify(fixture.Frame, 1280, 800)));
        fixture.SetField("_registration", BoardRegistration.Create(fixture.Frame, CanonicalSensorCorners(.004, .002)));
        fixture.SetField("_pieceModel", pieces);
        var context = new HeldContinuationContext();
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            fixture.Camera.RequestGameTablePreview();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }

        try
        {
            await context.Posted.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            fixture.QueueGameTableAnalysis();
            Assert.Equal("board-alignment-pending", fixture.GetField("_lastGameTableAnalysisBlock"));
            Assert.Null(fixture.Camera.GameTableAnalysis);
            Assert.Equal(0, pieces.Calls);
        }
        finally { context.Release(); }

        await fixture.WaitForGameTableAlignmentAsync();
        await fixture.WaitForGameTableAnalysisAsync();
        AssertCanonicalRouteMapping(expected, fixture.GameTableRegistration);
        var analysis = Assert.IsType<GameTableAnalysis>(fixture.Camera.GameTableAnalysis);
        Assert.Equal(fixture.GetField("_gameTableCropRevision"), analysis.CropRevision);
        Assert.Null(fixture.GetField("_gameTableAlignmentPendingRevision"));
        Assert.Equal(1, pieces.Calls);
    }

    [Fact]
    public async Task Failed_initial_alignment_releases_pending_gate_and_rechecks_orientation_before_analysis()
    {
        using var pieces = new FakePieceModel();
        await using var fixture = new Fixture(pieceFactory: (_, _) => pieces);
        var boardVisible = true;
        fixture.FramePainter = (pixels, width, height) =>
        {
            if (boardVisible) PaintCanonicalBoard(pixels, width, height);
        };
        fixture.Refresh(width: 1600, height: 900);
        var expected = BoardRegistration.Create(fixture.Frame, CanonicalSensorCorners());
        fixture.Camera.SetGameTableReference(ToBitmap(expected.Rectify(fixture.Frame, 1280, 800)));
        fixture.SetField("_registration", BoardRegistration.Create(fixture.Frame, CanonicalSensorCorners(.004, .002)));
        fixture.SetField("_pieceModel", pieces);
        fixture.Model.DetectedCorners = CanonicalSensorCorners();
        var context = new HeldContinuationContext();
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            fixture.Camera.RequestGameTablePreview();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }

        try
        {
            await context.Posted.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            boardVisible = false;
            fixture.Refresh(width: 1600, height: 900);
        }
        finally { context.Release(); }

        await fixture.WaitForGameTableAlignmentAsync();
        await fixture.WaitForLiveBoardCheckAsync();
        Assert.Null(fixture.GetField("_gameTableAlignmentPendingRevision"));
        Assert.False(fixture.Camera.IsGameTablePreviewUpright);
        Assert.Null(fixture.Camera.GameTableAnalysis);
        Assert.Equal(0, pieces.Calls);

        boardVisible = true;
        fixture.Refresh(width: 1600, height: 900);
        await fixture.DetectLiveBoardAsync();
        await fixture.WaitForGameTableAnalysisAsync();

        Assert.True(fixture.Camera.IsGameTablePreviewUpright, fixture.Camera.GameTablePreviewStatus);
        Assert.NotNull(fixture.Camera.GameTableAnalysis);
        Assert.Null(fixture.GetField("_gameTableAlignmentPendingRevision"));
    }

    [Fact]
    public async Task Delayed_periodic_alignment_cannot_replace_a_newer_manual_game_crop()
    {
        await using var fixture = new Fixture();
        var horizontalShift = 0d;
        fixture.FramePainter = (pixels, width, height) =>
            PaintCanonicalBoard(pixels, width, height, horizontalShift);
        fixture.Refresh(width: 1600, height: 900);
        var first = BoardRegistration.Create(fixture.Frame, CanonicalSensorCorners());
        fixture.Camera.SetGameTableReference(ToBitmap(first.Rectify(fixture.Frame, 1280, 800)));
        fixture.Model.DetectedCorners = CanonicalSensorCorners();
        fixture.Camera.RequestGameTablePreview();
        await fixture.WaitForLiveBoardCheckAsync();

        fixture.Model.Pause(ignoreCancellation: true);
        var oldCheck = fixture.DetectLiveBoardAsync();
        await fixture.Model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        BoardRegistration? newer = null;
        object? revision = null;
        try
        {
            horizontalShift = .006;
            fixture.Refresh(width: 1600, height: 900);
            fixture.AdoptGameTableCrop(BoardRegistration.Create(fixture.Frame,
                CanonicalSensorCorners(horizontalShift)));
            await fixture.WaitForGameTableAlignmentAsync();
            newer = fixture.GameTableRegistration;
            revision = fixture.GetField("_gameTableCropRevision");
            AssertCanonicalRouteMapping(BoardRegistration.Create(fixture.Frame,
                CanonicalSensorCorners(horizontalShift)), newer);
        }
        finally
        {
            fixture.Model.Release();
            await oldCheck.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }

        Assert.Same(newer, fixture.GameTableRegistration);
        Assert.Equal(revision, fixture.GetField("_gameTableCropRevision"));
        Assert.Null(fixture.GetField("_gameTableAlignmentPendingRevision"));
        Assert.True(fixture.Camera.IsGameTablePreviewUpright);
    }

    private static NormalizedPoint[] CanonicalSensorCorners(double dx = 0, double dy = 0) =>
        [new(.1 + dx, .12 + dy), new(.9 + dx, .12 + dy),
            new(.9 + dx, .88 + dy), new(.1 + dx, .88 + dy)];

    private static BitmapSource ToBitmap(CameraFrame frame)
    {
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, frame.Bgra32.ToArray(), frame.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static void AssertCanonicalRouteMapping(BoardRegistration expected, BoardRegistration actual)
    {
        ClassicUsRouteGeometry.TryGetSlots("los-angeles--san-francisco--b", out var slots);
        Assert.All(slots, slot =>
        {
            var target = expected.MapToSensor(slot.X, slot.Y);
            var point = actual.MapToSensor(slot.X, slot.Y);
            Assert.InRange(Math.Abs(point.X - target.X), 0, .0015);
            Assert.InRange(Math.Abs(point.Y - target.Y), 0, .0015);
        });
    }

    private static void PaintCanonicalBoard(byte[] pixels, int width, int height, double dx = 0)
    {
        var reference = ClassicUsBoardAlignment.ReferenceFrame;
        var artwork = reference.Bgra32.Span;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var u = (x / (double)(width - 1) - .1 - dx) / .8;
            var v = (y / (double)(height - 1) - .12) / .76;
            if (u is < 0 or > 1 || v is < 0 or > 1) continue;
            var rx = u * (reference.Width - 1);
            var ry = v * (reference.Height - 1);
            var left = (int)rx;
            var top = (int)ry;
            var right = Math.Min(left + 1, reference.Width - 1);
            var bottom = Math.Min(top + 1, reference.Height - 1);
            var fx = rx - left;
            var fy = ry - top;
            var gray = (byte)Math.Round(
                artwork[top * reference.Stride + left * 4] * (1 - fx) * (1 - fy) +
                artwork[top * reference.Stride + right * 4] * fx * (1 - fy) +
                artwork[bottom * reference.Stride + left * 4] * (1 - fx) * fy +
                artwork[bottom * reference.Stride + right * 4] * fx * fy);
            var offset = (y * width + x) * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = gray;
            pixels[offset + 3] = 255;
        }
    }

    private sealed class HeldContinuationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        private volatile bool _released;
        public TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Post(SendOrPostCallback callback, object? state)
        {
            _callbacks.Enqueue((callback, state));
            Posted.TrySetResult();
            if (_released) Release();
        }

        public void Release()
        {
            _released = true;
            while (_callbacks.TryDequeue(out var item)) item.Callback(item.State);
        }
    }

    private static void PaintAsymmetricBoard(byte[] pixels, int width, int height, bool halfTurn)
    {
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var u = (x / (double)(width - 1) - .1) / .8;
            var v = (y / (double)(height - 1) - .12) / .76;
            if (u is < 0 or > 1 || v is < 0 or > 1) continue;
            if (halfTurn) { u = 1 - u; v = 1 - v; }
            var column = (int)(u * 13);
            var row = (int)(v * 9);
            var offset = (y * width + x) * 4;
            pixels[offset] = (byte)(40 + (column * 23 + row * 17 + column * row * 3) % 170);
            pixels[offset + 1] = (byte)(30 + (column * 19 + row * 31 + column * row * 5) % 180);
            pixels[offset + 2] = (byte)(50 + (column * 29 + row * 11 + column * row * 7) % 160);
            pixels[offset + 3] = 255;
        }
    }

    private sealed class NormalizedPointComparer : IEqualityComparer<NormalizedPoint>
    {
        public static readonly NormalizedPointComparer Instance = new();
        public bool Equals(NormalizedPoint left, NormalizedPoint right) =>
            Math.Abs(left.X - right.X) < .003 && Math.Abs(left.Y - right.Y) < .003;
        public int GetHashCode(NormalizedPoint point) => 0;
    }

    private static void PaintScorePiece(byte[] pixels, int width, int height,
        IReadOnlyList<NormalizedPoint> corners, double u, double v, (byte R, byte G, byte B) color)
    {
        var blank = CameraFrame.CopyFromBgra32(width, height, pixels);
        var point = BoardRegistration.Create(blank, corners).MapToSensor(u, v);
        var centerX = (int)Math.Round(point.X * (width - 1));
        var centerY = (int)Math.Round(point.Y * (height - 1));
        for (var y = Math.Max(0, centerY - 3); y <= Math.Min(height - 1, centerY + 3); y++)
        for (var x = Math.Max(0, centerX - 3); x <= Math.Min(width - 1, centerX + 3); x++)
        {
            var offset = (y * width + x) * 4;
            pixels[offset] = color.B;
            pixels[offset + 1] = color.G;
            pixels[offset + 2] = color.R;
        }
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
        public FakeCameraCapture Capture => (FakeCameraCapture)Camera.Capture;
        public CameraFrame Frame => Camera.Capture.LatestFrame!;
        public Action<byte[], int, int>? FramePainter { get; set; }
        public object? Registration => Field("_registration").GetValue(Camera);
        public BoardRegistration GameTableRegistration =>
            (BoardRegistration)Field("_gameTableRegistration").GetValue(Camera)!;
        public object? GetField(string name) => Field(name).GetValue(Camera);
        public void SetField(string name, object value) => Field(name).SetValue(Camera, value);
        public Task WaitForGameTableAlignmentAsync() => ((Task)GetField("_gameTableAlignmentWork")!)
            .WaitAsync(TimeSpan.FromSeconds(10), Token);
        public Task WaitForLiveBoardCheckAsync() => ((Task)GetField("_liveBoardCheckWork")!)
            .WaitAsync(TimeSpan.FromSeconds(10), Token);
        public Task WaitForGameTableAnalysisAsync() => ((Task)GetField("_gameTableAnalysisWork")!)
            .WaitAsync(TimeSpan.FromSeconds(10), Token);
        public Task DetectLiveBoardAsync() => (Task)typeof(CameraViewModel)
            .GetMethod("DetectLiveBoardAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Camera, [Frame])!;
        public void AdoptGameTableCrop(BoardRegistration registration) => typeof(CameraViewModel)
            .GetMethod("AdoptTechnicalBoardCrop", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Camera, [Frame, registration]);
        public void QueueGameTableAnalysis() => typeof(CameraViewModel)
            .GetMethod("QueueGameTableAnalysis", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Camera, [Frame]);

        public Fixture(Func<string, bool, IBoardCornerDetector>? factory = null,
            Func<string, bool, IPieceModelDetector>? pieceFactory = null)
        {
            Camera = new(capture: new FakeCameraCapture(), pieceModelDirectory: ModelDirectory + "-unused-pieces", boardCornerModelDirectory: ModelDirectory,
                pieceModelFactory: pieceFactory,
                boardCornerModelFactory: (directory, preferGpu) =>
                {
                    FactoryCalls.Enqueue((directory, preferGpu));
                    return factory is null ? Model : factory(directory, preferGpu);
                });
            Camera.SelectedProcessor = Camera.ProcessorModes.Single(option => option.Value == FrameComputeMode.Cpu);
            Capture.ActiveDevice = new CameraDevice("synthetic-corner-camera", "Synthetic corner camera");
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
            FramePainter?.Invoke(pixels, width, height);
            Capture.Epoch = epoch;
            Capture.IsRunning = true;
            Capture.LatestFrame = CameraFrame.CopyFromBgra32(width, height, pixels, ++_sequence, epoch, clock: Clock);
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

        public async ValueTask DisposeAsync()
        {
            Model.Release();
            await Camera.DisposeAsync();
            Model.ReleaseResources();
        }
    }

    private sealed class FakePieceModel : IPieceModelDetector
    {
        public string ModelId => "synthetic-score-pieces";
        public string Backend => "synthetic CPU";
        public string? FallbackReason => null;
        public int Calls { get; private set; }
        public bool ShowTrain { get; set; }
        public Action? BeforeDetect { get; set; }
        public IReadOnlyList<PieceCandidate> Trains { get; init; } = [];

        public LearnedPieceDetection Detect(CameraFrame board, CancellationToken token = default)
        {
            BeforeDetect?.Invoke();
            Calls++;
            var candidates = new List<PieceCandidate>();
            if (IsRed(board, .017, .9266)) candidates.Add(Marker(.017, .9266));
            if (IsBlue(board, .055, .9266)) candidates.Add(Marker(.055, .9266));
            if (ShowTrain) candidates.Add(new(PieceCandidateKind.Train,
                [new(.45, .45), new(.55, .45), new(.55, .55), new(.45, .55)], .95));
            candidates.AddRange(Trains);
            return new(candidates, ModelId, Backend, TimeSpan.FromMilliseconds(1));
        }

        private static bool IsRed(CameraFrame board, double x, double y)
        {
            var (r, g, b) = Sample(board, x, y);
            return r > 110 && g < 95 && b < 95;
        }

        private static bool IsBlue(CameraFrame board, double x, double y)
        {
            var (r, g, b) = Sample(board, x, y);
            return b > 70 && r < 55 && g < 90;
        }

        private static (byte R, byte G, byte B) Sample(CameraFrame board, double x, double y)
        {
            var px = (int)Math.Round(x * (board.Width - 1));
            var py = (int)Math.Round(y * (board.Height - 1));
            var offset = py * board.Stride + px * 4;
            var bytes = board.Bgra32.Span;
            return (bytes[offset + 2], bytes[offset + 1], bytes[offset]);
        }

        private static PieceCandidate Marker(double x, double y) => new(PieceCandidateKind.PlayerMarker,
            [new(x - .012, y - .018), new(x + .012, y - .018),
                new(x + .012, y + .018), new(x - .012, y + .018)], .95);

        public void Dispose() { }
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
