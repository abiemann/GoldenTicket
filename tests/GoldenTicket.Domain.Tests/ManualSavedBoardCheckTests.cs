using System.Reflection;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Persistence;

namespace GoldenTicket.Domain.Tests;

public sealed class ManualSavedBoardCheckTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_reload_requires_explicit_attestation_and_preserves_saved_turn(bool pendingClaim)
    {
        await using var fixture = await Fixture.CreateAsync(pendingClaim: pendingClaim);
        var model = fixture.Model;
        var game = fixture.Game;
        var savedTurn = game.Public.TurnNumber;
        var savedSeat = game.Public.ActiveSeatId;
        var savedPending = game.Public.PendingClaim;
        var savedHand = await game.GetSeatViewAsync(savedSeat, TestContext.Current.CancellationToken);

        Assert.True(model.ShowManualSavedBoardCheck);
        Assert.True(model.CheckSavedBoardMyselfCommand.CanExecute(null));
        Assert.Contains("Blue scoring marker: 1", model.ManualReloadMarkerInstructions);
        await model.CheckSavedBoardMyselfCommand.ExecuteAsync(null);

        Assert.Equal(Screen.Rebuild, model.GameplayScreen);
        Assert.Equal(SessionLifecycle.Rebuilding, game.Public.Lifecycle);
        Assert.False(model.ShowManualSavedBoardCheck);
        Assert.False(game.Public.RebuildAttested);
        await model.AttestRebuildAsync();
        await model.ResumePackedGameAsync();
        Assert.Equal(SessionLifecycle.Rebuilding, game.Public.Lifecycle);

        model.Table.RebuildAcknowledged = true;
        await model.AttestRebuildAsync();
        var attestation = Assert.Single(fixture.Store.JournalOf(game.SessionId)
            .Select(row => row.Event).OfType<BoardRebuildAttested>());
        Assert.Equal(Environment.UserName, attestation.Operator);
        await model.ResumePackedGameAsync();

        Assert.Equal(SessionLifecycle.Active, game.Public.Lifecycle);
        Assert.True(model.IsResumeTurnAnnouncementOpen);
        Assert.Equal(savedTurn, game.Public.TurnNumber);
        Assert.Equal(savedSeat, game.Public.ActiveSeatId);
        Assert.Equal(savedPending, game.Public.PendingClaim);
        Assert.Empty(game.Public.RouteOwners);
        var resumedHand = await game.GetSeatViewAsync(savedSeat, TestContext.Current.CancellationToken);
        Assert.Equal(savedHand.Hand.ToArray(), resumedHand.Hand.ToArray());
        Assert.Equal(savedHand.ReservedCards.ToArray(), resumedHand.ReservedCards.ToArray());
        Assert.False(model.CheckSavedBoardMyselfCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("operation")]
    [InlineData("storage")]
    public async Task Busy_or_faulted_reload_cannot_enter_manual_rebuild(string blocker)
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        var version = fixture.Game.Public.StateVersion;
        switch (blocker)
        {
            case "busy": model.Busy = "Busy saving"; break;
            case "operation": SetPrivate(model, "_operationInProgress", true); break;
            case "storage": SetPrivate(model, "_mustReload", true); break;
        }

        Assert.False(model.CheckSavedBoardMyselfCommand.CanExecute(null));
        await model.CheckSavedBoardMyselfAsync();
        Assert.Equal(Screen.Table, model.GameplayScreen);
        Assert.Equal(SessionLifecycle.PackedAway, fixture.Game.Public.Lifecycle);
        Assert.Equal(version, fixture.Game.Public.StateVersion);
    }

    [Fact]
    public async Task Active_game_has_no_manual_saved_board_entry()
    {
        await using var fixture = await Fixture.CreateAsync(packAway: false);
        var version = fixture.Game.Public.StateVersion;
        Assert.False(fixture.Model.ShowManualSavedBoardCheck);
        Assert.False(fixture.Model.CheckSavedBoardMyselfCommand.CanExecute(null));
        await fixture.Model.CheckSavedBoardMyselfAsync();
        Assert.Equal(SessionLifecycle.Active, fixture.Game.Public.Lifecycle);
        Assert.Equal(version, fixture.Game.Public.StateVersion);
    }

    [Fact]
    public async Task Manual_confirmation_does_not_replace_required_saved_photo()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        await model.CheckSavedBoardMyselfAsync();
        model.Table.RebuildAcknowledged = true;
        await model.AttestRebuildAsync();
        var game = fixture.Game;
        await File.WriteAllBytesAsync(fixture.Photos.Store.AttachmentPath(game.SessionId,
            game.Public.Checkpoint!.CheckpointId), "damaged board image"u8.ToArray(),
            TestContext.Current.CancellationToken);

        await model.ResumePackedGameAsync();
        Assert.Equal(SessionLifecycle.Rebuilding, game.Public.Lifecycle);
        Assert.False(model.IsResumeTurnAnnouncementOpen);
        Assert.Contains("damaged or unreadable", model.Status);
    }

    private static void SetPrivate(MainViewModel model, string name, object value) =>
        typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(model, value);

    private sealed class Fixture : IAsyncDisposable
    {
        public InMemorySessionStore Store { get; } = new();
        public TestCheckpointPhotos Photos { get; } = new();
        public MainViewModel Model { get; private set; } = null!;
        public GameCoordinator Game => (GameCoordinator)typeof(MainViewModel)
            .GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Model)!;

        public static async Task<Fixture> CreateAsync(bool pendingClaim = false, bool packAway = true)
        {
            var fixture = new Fixture();
            var token = TestContext.Current.CancellationToken;
            var game = await GameCoordinator.CreateAsync(new GameRules(TestManifest.Manifest, TestManifest.Catalog),
                fixture.Store, new SessionSetup(SessionId.New(),
                    [new Seat(new(1), "Player 1", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard),
                     new Seat(new(2), "Player 2", PlayerColor.Yellow, SeatKind.Human, AiDifficulty.Standard)],
                    new(1), VerificationMode.Manual), DeterministicRandom.SeedFrom(91), token);
            foreach (var seat in game.Seats)
            {
                var hand = await game.GetSeatViewAsync(seat.SeatId, token);
                Assert.True((await game.SubmitAsync(new CommitTicketSelection(game.NewEnvelope(seat.SeatId),
                    [.. hand.SetupOffer.Take(2)], []), token)).IsAccepted);
            }
            if (pendingClaim)
            {
                var seat = game.Public.ActiveSeatId;
                var choice = (await game.GetLegalActionsAsync(seat, token)).Claims.First();
                var hand = await game.GetSeatViewAsync(seat, token);
                Assert.True((await game.SubmitAsync(new PlanClaim(game.NewEnvelope(seat), choice.RouteId,
                    LegalActionCalculator.ResolveCards(hand, choice.Payments[0])), token)).IsAccepted);
            }
            if (packAway)
            {
                Assert.True((await game.SaveAndPackAwayAsync("Manual recovery", cancellationToken: token)).SafeToPack);
                var pending = game.Public.PendingClaim;
                var placement = pending is null ? null : new CheckpointPendingPlacement(
                    pending.OperationId, pending.RouteId, pending.SeatId,
                    game.Public.SeatOf(pending.SeatId).Color, pending.TrainCount, 0);
                await fixture.Photos.AttachAsync((await game.GetCheckpointAsync(cancellationToken: token))!, placement);
            }
            fixture.Model = new MainViewModel(TestManifest.Manifest, fixture.Store, fixture.Photos.Store);
            fixture.Model.SetGameLayerVisible(true);
            await fixture.Model.LoadSavedSessionsAsync();
            fixture.Model.Setup.SelectedSavedSession = Assert.Single(fixture.Model.Setup.SavedSessions);
            await fixture.Model.ResumeMatchAsync();
            Assert.True(fixture.Model.Game.IsPlaying, fixture.Model.Setup.SavedMatchMessage);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await Model.DisposeToolsAsync();
            Photos.Dispose();
        }
    }
}
