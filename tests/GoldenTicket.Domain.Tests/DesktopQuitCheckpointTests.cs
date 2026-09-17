using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopQuitCheckpointTests
{
    [Fact]
    public async Task QuitToMenuDiscardsLaterPlayButLeavesPreviousSaveAvailable()
    {
        var store = new InMemorySessionStore();
        var model = new MainViewModel(TestManifest.Manifest, store);
        model.Setup.ManualVerificationAccepted = true;
        model.SetGameLayerVisible(true);
        try
        {
            await model.StartMatchAsync();
            await model.CommitTicketsAsync();
            model.Table.SaveName = "Earlier save";
            await model.SaveAndPackAwayAsync();
            Assert.True(model.Table.IsPackedAway);

            var session = Assert.Single(await store.ListSessionsAsync(CancellationToken.None));
            var saved = await store.RestoreAsync(session.SessionId, TestManifest.Manifest,
                TestManifest.Catalog, CancellationToken.None);
            var savedVersion = saved.State.StateVersion;
            var savedCheckpoint = saved.State.Checkpoint!.CheckpointId;

            await model.BeginRebuildAsync();
            model.Table.RebuildAcknowledged = true;
            await model.AttestRebuildAsync();
            await model.ResumePackedGameAsync();
            await model.RevealPrivateSeatAsync();
            Assert.NotNull(model.PrivateSeat);
            await model.DrawBlindCardAsync();

            var progressed = await store.RestoreAsync(session.SessionId, TestManifest.Manifest,
                TestManifest.Catalog, CancellationToken.None);
            Assert.True(progressed.State.StateVersion > savedVersion);
            Assert.Equal(SessionLifecycle.Active, progressed.State.Lifecycle);

            model.OpenGameExitMenu();
            Assert.True(model.IsGameExitMenuOpen);
            await model.QuitToMenuCommand.ExecuteAsync(null);

            Assert.False(model.IsGameExitMenuOpen);
            Assert.Equal(GameScreenStage.Welcome, model.Game.Stage);
            var remaining = Assert.Single(await store.ListSessionsAsync(CancellationToken.None));
            Assert.Equal(session.SessionId, remaining.SessionId);
            Assert.Equal(SessionLifecycle.PackedAway, remaining.Lifecycle);
            var restored = await store.RestoreAsync(session.SessionId, TestManifest.Manifest,
                TestManifest.Catalog, CancellationToken.None);
            Assert.Equal(savedVersion, restored.State.StateVersion);
            Assert.Equal(savedCheckpoint, restored.State.Checkpoint?.CheckpointId);
            Assert.Single(model.Setup.SavedSessions);
            Assert.True(model.Game.HasPreviousGame);
        }
        finally { await model.DisposeToolsAsync(); }
    }
}
