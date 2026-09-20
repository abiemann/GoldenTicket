using System.Reflection;
using System.Text.Json;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Simulator;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopFinalStandingsExitTests
{
    [Fact]
    public async Task BackToMenuPreservesTheCompletedGameAndItsFinalResults()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new InMemorySessionStore();
        var report = await new MatchRunner(TestManifest.Manifest, TestManifest.Catalog)
            .RunAsync(101, 2, AiDifficulty.Standard, store, token);
        Assert.True(report.Completed, report.StoppedBecause);
        var before = await store.RestoreAsync(report.SessionId, TestManifest.Manifest, TestManifest.Catalog, token);
        Assert.Equal(SessionLifecycle.Finished, before.State.Lifecycle);
        Assert.NotNull(before.State.FinalResult);
        var beforeHash = StateHash.Compute(before.State);
        var beforeResult = JsonSerializer.Serialize(before.State.FinalResult);
        var game = await GameCoordinator.RestoreAsync(new GameRules(TestManifest.Manifest, TestManifest.Catalog),
            store, report.SessionId, token);
        var model = Present(game, store);
        try
        {
            Assert.True(model.CanLeaveFinalStandings);
            Assert.True(model.BackToMenuCommand.CanExecute(null));
            await model.BackToMenuCommand.ExecuteAsync(null);

            Assert.Equal(GameScreenStage.Welcome, model.Game.Stage);
            Assert.Equal(Screen.Setup, model.GameplayScreen);
            Assert.False(model.CanLeaveFinalStandings);
            Assert.False(model.IsGameExitMenuOpen);
            Assert.False(model.Connection.IsRunning);
            Assert.False(model.Camera.IsGameTablePreviewRequested);
            Assert.Equal(report.SessionId, Assert.Single(model.Setup.SavedSessions).SessionId);
            Assert.True(model.Game.HasPreviousGame);

            var summary = Assert.Single(await store.ListSessionsAsync(token));
            Assert.Equal(report.SessionId, summary.SessionId);
            Assert.Equal(SessionLifecycle.Finished, summary.Lifecycle);
            var after = await store.RestoreAsync(report.SessionId, TestManifest.Manifest, TestManifest.Catalog, token);
            Assert.Equal(SessionLifecycle.Finished, after.State.Lifecycle);
            Assert.Equal(before.State.StateVersion, after.State.StateVersion);
            Assert.Equal(beforeHash, StateHash.Compute(after.State));
            Assert.Equal(beforeResult, JsonSerializer.Serialize(after.State.FinalResult));
            Assert.Equal(before.Journal, after.Journal);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackToMenuCannotLeaveAnUnfinishedGameEvenIfTheFinalScreenIsSelected(bool finishSetup)
    {
        var token = TestContext.Current.CancellationToken;
        var store = new InMemorySessionStore();
        var game = await GameCoordinator.CreateAsync(new GameRules(TestManifest.Manifest, TestManifest.Catalog), store,
            new SessionSetup(SessionId.New(),
                [new(new(1), "Alex", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard),
                 new(new(2), "Jordan", PlayerColor.Red, SeatKind.Human, AiDifficulty.Standard)],
                new(1), VerificationMode.Manual), DeterministicRandom.SeedFrom(91), token);
        if (finishSetup)
            foreach (var seat in game.Seats)
            {
                var own = await game.GetSeatViewAsync(seat.SeatId, token);
                Assert.True((await game.SubmitAsync(new CommitTicketSelection(game.NewEnvelope(seat.SeatId),
                    [.. own.SetupOffer.Take(2)], []), token)).IsAccepted);
            }
        var expectedLifecycle = finishSetup ? SessionLifecycle.Active : SessionLifecycle.Setup;
        Assert.Equal(expectedLifecycle, game.Public.Lifecycle);
        var beforeHash = await game.ComputeStateHashAsync(token);
        var model = Present(game, store);
        try
        {
            Assert.False(model.CanLeaveFinalStandings);
            Assert.False(model.BackToMenuCommand.CanExecute(null));
            // Also call the command directly so the method's own lifecycle guard is exercised.
            await model.BackToMenuCommand.ExecuteAsync(null);
            Assert.Equal(GameScreenStage.Playing, model.Game.Stage);
            Assert.Equal(Screen.FinalScore, model.GameplayScreen);
            Assert.Equal(expectedLifecycle, Assert.Single(await store.ListSessionsAsync(token)).Lifecycle);
            var after = await store.RestoreAsync(game.SessionId, TestManifest.Manifest, TestManifest.Catalog, token);
            Assert.Equal(beforeHash, StateHash.Compute(after.State));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static MainViewModel Present(GameCoordinator game, InMemorySessionStore store)
    {
        var model = new MainViewModel(TestManifest.Manifest, store);
        // Supply the real replay-verified coordinator without the resume camera workflow. No
        // camera, network listener or user save is touched by this command-level test.
        typeof(MainViewModel).GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(model, game);
        model.SetGameLayerVisible(true);
        model.Screen = Screen.FinalScore;
        model.Game.Stage = GameScreenStage.Playing;
        return model;
    }
}
