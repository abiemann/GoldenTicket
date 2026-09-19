using System.Reflection;
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
            var afterAcknowledgment = await resumed.ComputeStateHashAsync();
            Assert.False(model.AcknowledgeResumeTurnCommand.CanExecute(null));
            await model.AcknowledgeResumeTurnCommand.ExecuteAsync(null);
            Assert.Equal(afterAcknowledgment, await resumed.ComputeStateHashAsync());
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
        Assert.True((await saved.SaveAndPackAwayAsync("Unfinished turn")).SafeToPack);
        await photos.AttachAsync((await saved.GetCheckpointAsync())!);
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
        Assert.True((await saved.SaveAndPackAwayAsync("Partial computer placement")).SafeToPack);
        await photos.AttachAsync((await saved.GetCheckpointAsync())!, metadata);
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
            Assert.Empty((await Coordinator(model).GetSeatViewAsync(new(1))).SetupOffer);
            Assert.NotEmpty((await Coordinator(model).GetSeatViewAsync(new(2))).SetupOffer);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static async Task<GameCoordinator> CreateAsync(ISessionStore store, bool computer,
        bool completeSetup = true)
    {
        var game = await GameCoordinator.CreateAsync(new GameRules(TestManifest.Manifest, TestManifest.Catalog),
            store, new SessionSetup(SessionId.New(),
                [new Seat(new(1), computer ? "Computer 1" : "Player 1", PlayerColor.Blue,
                    computer ? SeatKind.Computer : SeatKind.Human, AiDifficulty.Standard),
                 new Seat(new(2), "Player 2", PlayerColor.Yellow, SeatKind.Human, AiDifficulty.Standard)],
                new(1), VerificationMode.Manual), DeterministicRandom.SeedFrom(91));
        if (completeSetup)
            foreach (var seat in game.Seats)
            {
                var hand = await game.GetSeatViewAsync(seat.SeatId);
                Assert.True((await game.SubmitAsync(new CommitTicketSelection(game.NewEnvelope(seat.SeatId),
                    [.. hand.SetupOffer.Take(2)], []))).IsAccepted);
            }
        return game;
    }

    private static async Task PlanClaimAsync(GameCoordinator game, int minimumLength = 1)
    {
        var seat = game.Public.ActiveSeatId;
        var actions = await game.GetLegalActionsAsync(seat);
        var choice = actions.Claims.First(claim => TestManifest.Manifest.Route(claim.RouteId).Length >= minimumLength);
        var hand = await game.GetSeatViewAsync(seat);
        Assert.True((await game.SubmitAsync(new PlanClaim(game.NewEnvelope(seat), choice.RouteId,
            LegalActionCalculator.ResolveCards(hand, choice.Payments[0])))).IsAccepted);
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
        long sequence, DateTimeOffset at)
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
        return (CameraFrame.CopyFromBgra32(width, height, pixels, sequence, 1, at), train);
    }
}
