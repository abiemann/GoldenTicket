using GoldenTicket.Application;
using System.Reflection;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Persistence;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopCompletedSaveTests
{
    [Theory]
    [InlineData(1, false, false)]
    [InlineData(3, false, false)]
    [InlineData(2, true, false)]
    [InlineData(2, false, true)]
    public async Task QuitAfterRestartKeepsLatestSaveWithValidPhoto(
        int laterAttempts, bool corruptPhotos, bool lastAttemptCompleted)
    {
        using var photos = new TestCheckpointPhotos();
        var store = new InMemorySessionStore();
        var original = await ActiveModelAsync(store, photos.Store);
        PackAwayCheckpoint expected;
        try
        {
            expected = await SaveCheckpointAsync(original, store, "Completed save");
            await photos.AttachAsync(expected);
            await ResumeAsync(original);
            for (var attempt = 0; attempt < laterAttempts; attempt++)
            {
                var checkpoint = await SaveCheckpointAsync(original, store, "Later attempt");
                if (lastAttemptCompleted && attempt == laterAttempts - 1)
                {
                    await photos.AttachAsync(checkpoint);
                    expected = checkpoint;
                }
                else if (corruptPhotos)
                {
                    var path = photos.Store.AttachmentPath(checkpoint.SessionId, checkpoint.CheckpointId);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
                }
                // Return to play after the failed photo step; the restart restores this journal.
                await ResumeAsync(original);
            }
        }
        finally { await original.DisposeToolsAsync(); }

        var restarted = await ReloadActiveModelAsync(store, photos.Store);
        try
        {
            restarted.OpenGameExitMenu();
            Assert.True(restarted.IsGameExitMenuOpen);
            await restarted.QuitToMenuCommand.ExecuteAsync(null);

            Assert.Equal(GameScreenStage.Welcome, restarted.Game.Stage);
            Assert.Null(restarted.GameExitStatus);
            var session = Assert.Single(await store.ListSessionsAsync(TestContext.Current.CancellationToken));
            var restored = await store.RestoreAsync(session.SessionId, TestManifest.Manifest,
                TestManifest.Catalog, TestContext.Current.CancellationToken);
            Assert.Equal(SessionLifecycle.PackedAway, restored.State.Lifecycle);
            Assert.Equal(expected.CheckpointId, restored.State.Checkpoint?.CheckpointId);
            Assert.NotNull(await photos.Store.ReadReferenceAsync(expected, TestContext.Current.CancellationToken));
        }
        finally { await restarted.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task QuitAfterRestartDiscardsAttemptsWithoutAnyCompletedPhoto()
    {
        using var photos = new TestCheckpointPhotos();
        var store = new InMemorySessionStore();
        var original = await ActiveModelAsync(store, photos.Store);
        try
        {
            await SaveCheckpointAsync(original, store, "Incomplete save");
            await ResumeAsync(original);
        }
        finally { await original.DisposeToolsAsync(); }

        var restarted = await ReloadActiveModelAsync(store, photos.Store);
        try
        {
            restarted.OpenGameExitMenu();
            await restarted.QuitToMenuCommand.ExecuteAsync(null);
            Assert.Equal(GameScreenStage.Welcome, restarted.Game.Stage);
            Assert.Empty(await store.ListSessionsAsync(TestContext.Current.CancellationToken));
        }
        finally { await restarted.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task TemporarilyUnreadablePhotoPreventsQuitFromDiscardingTheSave()
    {
        using var photos = new TestCheckpointPhotos();
        var store = new InMemorySessionStore();
        var model = await ActiveModelAsync(store, photos.Store);
        try
        {
            var checkpoint = await SaveCheckpointAsync(model, store, "Completed save");
            await photos.AttachAsync(checkpoint);
            await ResumeAsync(model);
            var before = await store.RestoreAsync(checkpoint.SessionId, TestManifest.Manifest,
                TestManifest.Catalog, TestContext.Current.CancellationToken);
            using var locked = new FileStream(photos.Store.AttachmentPath(checkpoint.SessionId, checkpoint.CheckpointId),
                FileMode.Open, FileAccess.Read, FileShare.None);
            model.OpenGameExitMenu();
            await model.QuitToMenuCommand.ExecuteAsync(null);

            Assert.True(model.IsGameExitMenuOpen);
            Assert.Contains("could not be discarded", model.GameExitStatus);
            var after = await store.RestoreAsync(checkpoint.SessionId, TestManifest.Manifest,
                TestManifest.Catalog, TestContext.Current.CancellationToken);
            Assert.Equal(before.State.StateVersion, after.State.StateVersion);
            Assert.Equal(SessionLifecycle.Active, after.State.Lifecycle);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static MainViewModel NewModel(ISessionStore store, CheckpointPhotoStore photos)
    {
        var model = new MainViewModel(TestManifest.Manifest, store, photoStore: photos);
        model.Setup.ManualVerificationAccepted = true;
        model.SetGameLayerVisible(true);
        return model;
    }

    private static async Task<MainViewModel> ActiveModelAsync(ISessionStore store, CheckpointPhotoStore photos)
    {
        var model = NewModel(store, photos);
        await model.StartMatchAsync();
        await model.CommitTicketsAsync();
        return model;
    }

    private static async Task<MainViewModel> ReloadActiveModelAsync(ISessionStore store, CheckpointPhotoStore photos)
    {
        var model = NewModel(store, photos);
        await model.LoadSavedSessionsAsync();
        model.Setup.SelectedSavedSession = Assert.Single(model.Setup.SavedSessions);
        await model.ResumeMatchAsync();
        Assert.True(model.Game.IsPlaying, model.Status);
        return model;
    }

    private static async Task<PackAwayCheckpoint> SaveCheckpointAsync(MainViewModel model, ISessionStore store, string name)
    {
        model.Table.SaveName = name;
        await model.SaveAndPackAwayAsync();
        Assert.True(model.Table.IsPackedAway, model.Status);
        var session = Assert.Single(await store.ListSessionsAsync(TestContext.Current.CancellationToken));
        var restored = await store.RestoreAsync(session.SessionId, TestManifest.Manifest,
            TestManifest.Catalog, TestContext.Current.CancellationToken);
        return Assert.IsType<PackAwayCheckpoint>(restored.State.Checkpoint);
    }

    private static async Task ResumeAsync(MainViewModel model)
    {
        // Build the existing auto-journal state produced before photo-less resume was blocked.
        // Quit must still handle those persisted histories safely after an application restart.
        var coordinator = (GameCoordinator)typeof(MainViewModel)
            .GetField("_coordinator", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(model)!;
        var checkpoint = coordinator.Public.Checkpoint!;
        var token = TestContext.Current.CancellationToken;
        Assert.True((await coordinator.SubmitAsync(
            new BeginBoardRebuild(coordinator.NewEnvelope(), checkpoint.CheckpointId), token)).IsAccepted);
        Assert.True((await coordinator.SubmitAsync(new AttestBoardRebuild(
            coordinator.NewEnvelope(), checkpoint.CheckpointId, checkpoint.PhysicalTargetHash, "test"), token)).IsAccepted);
        Assert.True((await coordinator.SubmitAsync(
            new ResumePackedGame(coordinator.NewEnvelope(), checkpoint.CheckpointId), token)).IsAccepted);
        await (Task)typeof(MainViewModel).GetMethod("RefreshAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(model, [false])!;
        Assert.False(model.Table.IsPackedAway, model.Status);
    }
}
