using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Persistence;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopResumeTurnTests
{
    [Fact]
    public async Task Committed_claim_restores_its_score_marker_step_before_the_next_turn()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new InMemorySessionStore();
        var saved = await CreateAsync(store, computer: false);
        await PlanClaimAsync(saved);
        var claim = Assert.IsType<GoldenTicket.Domain.Projections.PublicPendingClaim>(saved.Public.PendingClaim);
        Assert.True((await saved.SubmitAsync(new SubmitClaimEvidence(saved.NewEnvelope(claim.SeatId),
            claim.OperationId, EvidenceKind.CameraAutomatic, "synthetic-test-model",
            "Whole board verified.", RequireScoreMarkerConfirmation: true), token)).IsAccepted);
        var pending = Assert.IsType<PendingScoreMarkerMove>(saved.Public.PendingScoreMarkerMove);
        var version = saved.Public.StateVersion;

        var model = await ReloadAsync(store);
        try
        {
            var resumed = Coordinator(model);
            Assert.Equal(pending, resumed.Public.PendingScoreMarkerMove);
            Assert.True(model.NeedsBoardReconciliation);
            Assert.False(model.CanRevealPrivateSeat);

            model.BoardReconciliationAcknowledged = true;
            await model.ConfirmBoardReconciledAsync();
            Assert.False(model.IsResumeTurnAnnouncementOpen);
            Assert.True(model.ShowScoreMarkerDetectionPrompt);
            Assert.Contains($"from {pending.FromPrintedScore} to {pending.ToPrintedScore}",
                model.Game.GuidanceInstruction);
            Assert.Equal(version, resumed.Public.StateVersion);

            model.Camera.IsGameTablePreviewUpright = true;
            var at = DateTimeOffset.UtcNow;
            var color = Enum.Parse<MarkerColor>(resumed.Public.SeatOf(pending.SeatId).Color.ToString());
            Publish(1, pending.FromPrintedScore, at);
            Assert.Equal(pending, resumed.Public.PendingScoreMarkerMove);
            Publish(2, pending.ToPrintedScore, at.AddSeconds(1.1));
            Publish(3, pending.ToPrintedScore, at.AddSeconds(2.2));
            for (var attempt = 0; attempt < 100 &&
                 (resumed.Public.PendingScoreMarkerMove is not null || model.ShowScoreMarkerDetectionPrompt); attempt++)
                await Task.Delay(20, token);
            Assert.Null(resumed.Public.PendingScoreMarkerMove);
            Assert.Equal(version + 1, resumed.Public.StateVersion);
            Assert.False(model.ShowScoreMarkerDetectionPrompt);

            void Publish(long sequence, int score, DateTimeOffset capturedAt)
            {
                var frame = CameraFrame.CopyFromBgra32(320, 200, new byte[320 * 200 * 4],
                    sequence: sequence, epoch: 1, capturedAt: capturedAt);
                typeof(CameraViewModel).GetProperty(nameof(CameraViewModel.GameTableAnalysis))!
                    .SetValue(model.Camera, new GameTableAnalysis(frame, [],
                        [new ScoreMarkerReading(0, color, score, ScoreMarkerReadingStatus.Read,
                            "Printed score track position read.")], 1, 1, "synthetic-test-model"));
            }
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reloaded_human_or_computer_turn_waits_for_OK_and_repeated_OK_is_harmless(bool computer)
    {
        var store = new InMemorySessionStore();
        var saved = await CreateAsync(store, computer);
        var expectedSeat = saved.Public.ActiveSeatId;
        var expectedTurn = saved.Public.TurnNumber;
        var expectedVersion = saved.Public.StateVersion;
        var model = await ReloadAsync(store);
        try
        {
            Assert.True(model.NeedsBoardReconciliation);
            Assert.Equal("Checking...", model.Game.GuidanceSeat);
            model.BoardReconciliationAcknowledged = true;
            await model.ConfirmBoardReconciledAsync();
            var resumed = Coordinator(model);
            Assert.True(model.IsResumeTurnAnnouncementOpen);
            Assert.Equal("Checking...", model.Game.GuidanceSeat);
            Assert.Contains(saved.Public.SeatOf(expectedSeat).DisplayName, model.ResumeTurnAnnouncementText);
            Assert.Contains($"turn {expectedTurn}", model.ResumeTurnAnnouncementText);
            Assert.Equal(expectedSeat, resumed.Public.ActiveSeatId);
            Assert.Equal(expectedVersion, resumed.Public.StateVersion);
            Assert.False(model.CanRevealPrivateSeat);
            await model.RevealPrivateSeatAsync();
            await model.DrawBlindCardAsync();
            model.OpenGameExitMenu();
            Assert.Null(model.PrivateSeat);
            Assert.False(model.IsGameExitMenuOpen);
            Assert.Equal(expectedVersion, resumed.Public.StateVersion);

            await model.AcknowledgeResumeTurnCommand.ExecuteAsync(null);
            Assert.False(model.IsResumeTurnAnnouncementOpen);
            Assert.Equal(resumed.Public.SeatOf(resumed.Public.ActiveSeatId).DisplayName, model.Game.GuidanceSeat);
            if (computer)
                Assert.True(resumed.Public.StateVersion > expectedVersion,
                    "Acknowledgment must let the saved computer turn continue.");
            else
            {
                Assert.Equal(expectedSeat, resumed.Public.ActiveSeatId);
                Assert.Equal(expectedTurn, resumed.Public.TurnNumber);
                Assert.Equal(expectedVersion, resumed.Public.StateVersion);
                Assert.True(model.CanRevealPrivateSeat);
            }
            var afterAcknowledgment = await resumed.ComputeStateHashAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(model.AcknowledgeResumeTurnCommand.CanExecute(null));
            await model.AcknowledgeResumeTurnCommand.ExecuteAsync(null);
            Assert.Equal(afterAcknowledgment, await resumed.ComputeStateHashAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saved_pending_claim_resumes_the_same_turn_without_committing_it(bool computer)
    {
        var store = new InMemorySessionStore();
        using var photos = new TestCheckpointPhotos();
        var saved = await CreateAsync(store, computer);
        await PlanClaimAsync(saved);
        var expected = Assert.IsType<GoldenTicket.Domain.Projections.PublicPendingClaim>(saved.Public.PendingClaim);
        var turn = saved.Public.TurnNumber;
        Assert.True((await saved.SaveAndPackAwayAsync("Unfinished turn", cancellationToken: TestContext.Current.CancellationToken)).SafeToPack);
        var emptyPlacement = new CheckpointPendingPlacement(expected.OperationId, expected.RouteId,
            expected.SeatId, saved.Public.SeatOf(expected.SeatId).Color, expected.TrainCount, 0);
        await photos.AttachAsync((await saved.GetCheckpointAsync(cancellationToken: TestContext.Current.CancellationToken))!,
            emptyPlacement);
        var model = await ReloadAsync(store, photos);
        try
        {
            Assert.Equal("Checking...", model.Game.GuidanceSeat);
            await model.BeginRebuildAsync();
            model.Table.RebuildAcknowledged = true;
            await model.AttestRebuildAsync();
            await model.ResumePackedGameAsync();
            var resumed = Coordinator(model);
            Assert.True(model.IsResumeTurnAnnouncementOpen);
            Assert.Equal("Checking...", model.Game.GuidanceSeat);
            Assert.Contains("unfinished train placement", model.ResumeTurnAnnouncementText);
            Assert.Equal(expected, resumed.Public.PendingClaim);
            Assert.Equal(expected.SeatId, resumed.Public.ActiveSeatId);
            Assert.Equal(turn, resumed.Public.TurnNumber);
            Assert.Empty(resumed.Public.RouteOwners);

            model.Table.WholeBoardAcknowledged = true;
            await model.ConfirmPlacementAsync();
            Assert.Empty(resumed.Public.RouteOwners);
            Assert.Equal(expected, resumed.Public.PendingClaim);

            await model.AcknowledgeResumeTurnCommand.ExecuteAsync(null);
            Assert.False(model.IsResumeTurnAnnouncementOpen);
            Assert.Equal(resumed.Public.SeatOf(expected.SeatId).DisplayName, model.Game.GuidanceSeat);
            Assert.NotNull(model.Table.Placement);
            Assert.Equal(expected, resumed.Public.PendingClaim);
            Assert.Equal(turn, resumed.Public.TurnNumber);
            Assert.Empty(resumed.Public.RouteOwners);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Camera_reload_restores_exact_partial_placement_before_announcing_computer_turn()
    {
        var store = new InMemorySessionStore();
        using var photos = new TestCheckpointPhotos();
        var saved = await CreateAsync(store, computer: true);
        await PlanClaimAsync(saved, minimumLength: 2);
        var pending = saved.Public.PendingClaim!;
        Assert.True(ClassicUsRouteGeometry.TryGetSlots(pending.RouteId.Value, out var slots));
        var metadata = new CheckpointPendingPlacement(pending.OperationId, pending.RouteId,
            pending.SeatId, PlayerColor.Blue, pending.TrainCount, 1);
        Assert.True((await saved.SaveAndPackAwayAsync("Partial computer placement", cancellationToken: TestContext.Current.CancellationToken)).SafeToPack);
        await photos.AttachAsync((await saved.GetCheckpointAsync(cancellationToken: TestContext.Current.CancellationToken))!, metadata);
        var model = await ReloadAsync(store, photos);
        try
        {
            var resumed = Coordinator(model);
            model.Camera.IsGameTablePreviewUpright = true;
            var readings = resumed.Public.Seats.Select((seat, index) =>
                new ScoreMarkerReading(index, Enum.Parse<MarkerColor>(seat.Color.ToString()), 1,
                    ScoreMarkerReadingStatus.Read, "saved score")).ToArray();
            var at = DateTimeOffset.UtcNow;
            var sequence = 0;
            // A same-color train on the wrong pending slot must not match this photo.
            for (var index = 0; index < 10; index++) Publish(slots[1]);
            Assert.Equal(SessionLifecycle.PackedAway, resumed.Public.Lifecycle);
            Assert.False(model.IsResumeTurnAnnouncementOpen);
            Assert.Equal("Checking...", model.Game.GuidanceSeat);

            for (var index = 0; index < 12 && resumed.Public.Lifecycle != SessionLifecycle.Active; index++)
                Publish(slots[0]);
            for (var attempt = 0; attempt < 100 && !model.IsResumeTurnAnnouncementOpen; attempt++)
                await Task.Delay(20, TestContext.Current.CancellationToken);
            Assert.Equal(SessionLifecycle.Active, resumed.Public.Lifecycle);
            Assert.True(model.IsResumeTurnAnnouncementOpen, model.Game.GuidanceInstruction);
            Assert.Equal("Checking...", model.Game.GuidanceSeat);
            Assert.Contains("Computer 1 goes first", model.ResumeTurnAnnouncementText);
            Assert.Equal(pending, resumed.Public.PendingClaim);
            Assert.Empty(resumed.Public.RouteOwners);
            var version = resumed.Public.StateVersion;
            Publish(slots[0]);
            Assert.Equal(version, resumed.Public.StateVersion);
            await model.AcknowledgeResumeTurnCommand.ExecuteAsync(null);
            Assert.Equal("Computer 1", model.Game.GuidanceSeat);
            Assert.Equal(pending, resumed.Public.PendingClaim);
            Assert.Empty(resumed.Public.RouteOwners);

            void Publish(BoardSlotPoint slot)
            {
                var scene = BlueTrainAt(slot, ++sequence, at.AddSeconds(sequence * 1.1));
                typeof(CameraViewModel).GetProperty(nameof(CameraViewModel.GameTableAnalysis))!
                    .SetValue(model.Camera, new GameTableAnalysis(scene.Frame, [scene.Train],
                        readings, 1, 1, "synthetic-test-model"));
            }
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Reconciled_setup_journal_still_advances_computer_opening_choices()
    {
        var store = new InMemorySessionStore();
        var saved = await CreateAsync(store, computer: true, completeSetup: false);
        var model = await ReloadAsync(store);
        try
        {
            model.BoardReconciliationAcknowledged = true;
            await model.ConfirmBoardReconciledAsync();
            Assert.False(model.IsResumeTurnAnnouncementOpen);
            Assert.Equal("Player 2", model.Game.GuidanceSeat);
            Assert.Empty((await Coordinator(model).GetSeatViewAsync(new(1), cancellationToken: TestContext.Current.CancellationToken)).SetupOffer);
            Assert.NotEmpty((await Coordinator(model).GetSeatViewAsync(new(2), cancellationToken: TestContext.Current.CancellationToken)).SetupOffer);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Camera_reload_marks_the_actual_extra_train_and_clears_it_only_on_a_fresh_matching_board(
        bool offRoute)
    {
        var store = new InMemorySessionStore();
        using var photos = new TestCheckpointPhotos();
        var saved = await CreateAsync(store, computer: false);
        var savedSeat = saved.Public.ActiveSeatId;
        var savedTurn = saved.Public.TurnNumber;
        Assert.True((await saved.SaveAndPackAwayAsync("Board with no claimed routes",
            cancellationToken: TestContext.Current.CancellationToken)).SafeToPack);
        await photos.AttachAsync((await saved.GetCheckpointAsync(
            cancellationToken: TestContext.Current.CancellationToken))!);
        var model = await ReloadAsync(store, photos);
        try
        {
            var resumed = Coordinator(model);
            var version = resumed.Public.StateVersion;
            Assert.True(ClassicUsRouteGeometry.TryGetSlots("little-rock--saint-louis", out var routeSlots));
            var location = offRoute ? new BoardSlotPoint(.476, .9135, 1, 0) : routeSlots[0];
            var preview = BitmapSource.Create(960, 600, 96, 96, PixelFormats.Bgra32,
                null, new byte[960 * 600 * 4], 960 * 4);
            preview.Freeze();
            model.Camera.GameTablePreview = preview;
            model.Camera.IsGameTablePreviewUpright = true;
            var readings = resumed.Public.Seats.Select((seat, index) =>
                new ScoreMarkerReading(index, Enum.Parse<MarkerColor>(seat.Color.ToString()), 1,
                    ScoreMarkerReadingStatus.Read, "saved score")).ToArray();
            var at = DateTimeOffset.UtcNow;
            long sequence = 0;
            for (var index = 0; index < 12 && model.Game.PlacementTargets.Count == 0; index++)
                Publish(++sequence, includeExtra: true);

            var target = Assert.Single(model.Game.PlacementTargets);
            Assert.True(model.Game.ShowPlacementTarget);
            Assert.Equal(Math.Round(location.X * 960), target.X, precision: 5);
            Assert.Equal(Math.Round(location.Y * 600), target.Y, precision: 5);
            Assert.Contains("yellow spheres", model.Game.GuidanceInstruction);
            if (!offRoute)
                Assert.Contains("Little Rock - Saint Louis", model.Game.GuidanceInstruction);
            Assert.Equal(SessionLifecycle.PackedAway, resumed.Public.Lifecycle);
            Assert.False(model.IsResumeTurnAnnouncementOpen);
            Assert.Equal(version, resumed.Public.StateVersion);

            // A repeated capture and a stale clean capture are not proof of correction.
            Publish(sequence, includeExtra: false);
            Assert.Equal(target, Assert.Single(model.Game.PlacementTargets));
            Publish(++sequence, includeExtra: false, stale: true);
            Assert.Equal(target, Assert.Single(model.Game.PlacementTargets));
            Assert.False(model.IsResumeTurnAnnouncementOpen);

            // Remove the cue on the first fresh clean frame, but retain the two-frame
            // verification gate before resuming the saved turn.
            Publish(++sequence, includeExtra: false);
            Assert.Empty(model.Game.PlacementTargets);
            Assert.False(model.Game.ShowPlacementTarget);
            Assert.Equal(SessionLifecycle.PackedAway, resumed.Public.Lifecycle);
            Assert.False(model.IsResumeTurnAnnouncementOpen);
            Assert.Equal(version, resumed.Public.StateVersion);

            Publish(++sequence, includeExtra: false);
            for (var attempt = 0; attempt < 100 && !model.IsResumeTurnAnnouncementOpen; attempt++)
                await Task.Delay(20, TestContext.Current.CancellationToken);
            Assert.True(model.IsResumeTurnAnnouncementOpen, model.Game.GuidanceInstruction);
            Assert.Equal(SessionLifecycle.Active, resumed.Public.Lifecycle);
            Assert.Equal(savedSeat, resumed.Public.ActiveSeatId);
            Assert.Equal(savedTurn, resumed.Public.TurnNumber);
            Assert.Empty(resumed.Public.RouteOwners);
            Assert.Empty(model.Game.PlacementTargets);

            void Publish(long captureSequence, bool includeExtra, bool stale = false)
            {
                var clock = stale ? new ManualFrameTimeProvider() : null;
                var scene = BlueTrainAt(location, captureSequence,
                    at.AddSeconds(captureSequence * 1.1), clock);
                clock?.Advance(TimeSpan.FromSeconds(3));
                typeof(CameraViewModel).GetProperty(nameof(CameraViewModel.GameTableAnalysis))!
                    .SetValue(model.Camera, new GameTableAnalysis(scene.Frame,
                        includeExtra ? [scene.Train] : [], readings, 1, 1, "synthetic-reload-test"));
            }
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Clearing_an_inactive_check_keeps_the_reload_blockers_visible()
    {
        var model = new MainViewModel(TestManifest.Manifest, new InMemorySessionStore());
        try
        {
            var preview = BitmapSource.Create(960, 600, 96, 96, PixelFormats.Bgra32,
                null, new byte[960 * 600 * 4], 960 * 4);
            preview.Freeze();
            model.Camera.GameTablePreview = preview;
            model.Camera.IsGameTablePreviewUpright = true;
            var problem = new BoardInventoryObservation(BoardInventoryState.UnexpectedTrain,
                new Dictionary<MarkerColor, int>())
            {
                UnexpectedDetections = [new(.465, .905, .022, .017, .95, null, null)]
            };
            Update("restore", problem);
            var marker = Assert.Single(model.Game.PlacementTargets);

            // Gameplay observers run while the reload gate is active. Their resets
            // must not clear the separate board-check warning that is blocking reload.
            Update("card", null);
            Update("placement", null);
            Update("save", null);
            Assert.Equal(marker, Assert.Single(model.Game.PlacementTargets));
            Assert.True(model.Game.ShowPlacementTarget);

            Update("restore",
                new(BoardInventoryState.WaitingForFreshFrame, new Dictionary<MarkerColor, int>()));
            Assert.Equal(marker, Assert.Single(model.Game.PlacementTargets));
            Update("restore",
                new(BoardInventoryState.Stabilizing, new Dictionary<MarkerColor, int>()));
            Assert.Empty(model.Game.PlacementTargets);
            Assert.False(model.Game.ShowPlacementTarget);

            void Update(string source, BoardInventoryObservation? observation) =>
                typeof(GameScreenViewModel).GetMethod("UpdateInventoryProblemMarkers",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model.Game,
                    [source, observation, null]);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Theory]
    [InlineData(BoardInventoryState.MissingTrains, 0)]
    [InlineData(BoardInventoryState.MissingTrains, 1)]
    [InlineData(BoardInventoryState.Ambiguous, 0)]
    [InlineData(BoardInventoryState.Ambiguous, 1)]
    public async Task Inventory_problem_marks_only_the_identified_route_space(BoardInventoryState state, int slot)
    {
        var model = new MainViewModel(TestManifest.Manifest, new InMemorySessionStore());
        try
        {
            var preview = BitmapSource.Create(960, 600, 96, 96, PixelFormats.Bgra32,
                null, new byte[960 * 600 * 4], 960 * 4);
            preview.Freeze();
            model.Camera.GameTablePreview = preview;
            model.Camera.IsGameTablePreviewUpright = true;
            const string routeId = "boston--new-york--a";
            var problem = new BoardInventoryObservation(state, new Dictionary<MarkerColor, int>(), routeId)
            {
                UnverifiedSlotMask = 1 << slot
            };
            var update = typeof(GameScreenViewModel).GetMethod("UpdateInventoryProblemMarkers",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            update.Invoke(model.Game, ["card", problem, null]);

            Assert.True(PlacementBoardOverlay.TryGetTargets(TestManifest.Manifest, new RouteId(routeId), 2,
                out var slots));
            var target = Assert.Single(model.Game.PlacementTargets);
            Assert.Equal(slots[slot].X, target.X);
            Assert.Equal(slots[slot].Y, target.Y);
            Assert.Contains($"space {slot + 1}", target.ProblemDescription);

            // A partially rebuilt checkpoint must never ask for spaces not saved as occupied.
            update.Invoke(model.Game, ["card", problem, 1 << (1 - slot)]);
            Assert.Empty(model.Game.PlacementTargets);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static async Task<GameCoordinator> CreateAsync(ISessionStore store, bool computer,
        bool completeSetup = true)
    {
        var token = TestContext.Current.CancellationToken;
        var game = await GameCoordinator.CreateAsync(new GameRules(TestManifest.Manifest, TestManifest.Catalog),
            store, new SessionSetup(SessionId.New(),
                [new Seat(new(1), computer ? "Computer 1" : "Player 1", PlayerColor.Blue,
                    computer ? SeatKind.Computer : SeatKind.Human, AiDifficulty.Standard),
                 new Seat(new(2), "Player 2", PlayerColor.Yellow, SeatKind.Human, AiDifficulty.Standard)],
                new(1), VerificationMode.Manual), DeterministicRandom.SeedFrom(91), token);
        if (completeSetup)
            foreach (var seat in game.Seats)
            {
                var hand = await game.GetSeatViewAsync(seat.SeatId, token);
                Assert.True((await game.SubmitAsync(new CommitTicketSelection(game.NewEnvelope(seat.SeatId),
                    [.. hand.SetupOffer.Take(2)], []), token)).IsAccepted);
            }
        return game;
    }

    private static async Task PlanClaimAsync(GameCoordinator game, int minimumLength = 1)
    {
        var token = TestContext.Current.CancellationToken;
        var seat = game.Public.ActiveSeatId;
        var actions = await game.GetLegalActionsAsync(seat, token);
        var choice = actions.Claims.First(claim => TestManifest.Manifest.Route(claim.RouteId).Length >= minimumLength);
        var hand = await game.GetSeatViewAsync(seat, token);
        Assert.True((await game.SubmitAsync(new PlanClaim(game.NewEnvelope(seat), choice.RouteId,
            LegalActionCalculator.ResolveCards(hand, choice.Payments[0])), token)).IsAccepted);
    }

    private static async Task<MainViewModel> ReloadAsync(ISessionStore store, TestCheckpointPhotos? photos = null)
    {
        var model = new MainViewModel(TestManifest.Manifest, store, photos?.Store);
        model.SetGameLayerVisible(true);
        await model.LoadSavedSessionsAsync();
        model.Setup.SelectedSavedSession = Assert.Single(model.Setup.SavedSessions);
        await model.ResumeMatchAsync();
        Assert.True(model.Game.IsPlaying, model.Setup.SavedMatchMessage);
        return model;
    }

    private static GameCoordinator Coordinator(MainViewModel model) =>
        (GameCoordinator)typeof(MainViewModel).GetField("_coordinator",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;

    private static (CameraFrame Frame, PieceCandidate Train) BlueTrainAt(BoardSlotPoint slot,
        long sequence, DateTimeOffset at, TimeProvider? clock = null)
    {
        const int width = 960, height = 600;
        var pixels = Enumerable.Repeat((byte)180, width * height * 4).ToArray();
        var x = (int)Math.Round(slot.X * width);
        var y = (int)Math.Round(slot.Y * height);
        for (var py = y - 7; py <= y + 7; py++)
        for (var px = x - 11; px <= x + 11; px++)
        {
            var offset = (py * width + px) * 4;
            pixels[offset] = 195;
            pixels[offset + 1] = 75;
            pixels[offset + 2] = 20;
        }
        var train = new PieceCandidate(PieceCandidateKind.Train,
            [new((double)(x - 11) / width, (double)(y - 7) / height),
             new((double)(x + 11) / width, (double)(y - 7) / height),
             new((double)(x + 11) / width, (double)(y + 7) / height),
             new((double)(x - 11) / width, (double)(y + 7) / height)], .95);
        return (CameraFrame.CopyFromBgra32(width, height, pixels, sequence, 1, at, clock), train);
    }
}
