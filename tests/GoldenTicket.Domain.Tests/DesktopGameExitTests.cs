using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Manifest;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopGameExitTests
{
    [Fact]
    public async Task EscapeMenuCanOpenAndCloseDuringPlay()
    {
        var store = new InMemorySessionStore();
        var model = NewModel(store);
        try
        {
            model.OpenGameExitMenu();
            Assert.False(model.IsGameExitMenuOpen);

            await model.StartMatchAsync();
            Assert.True(model.Game.IsPlaying);

            model.OpenGameExitMenu();
            Assert.True(model.IsGameExitMenuOpen);
            Assert.Contains("Expected from confirmed routes", model.GameExitInventorySummary);
            Assert.False(model.CanRevealPrivateSeat);

            model.CloseGameExitMenu();
            Assert.False(model.IsGameExitMenuOpen);
            Assert.Equal(GameScreenStage.Playing, model.Game.Stage);
            Assert.Equal(Screen.Table, model.GameplayScreen);
            Assert.Single(await store.ListSessionsAsync(CancellationToken.None));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task SaveGameWithoutCameraKeepsTheCurrentGameAndAutosave()
    {
        var store = new InMemorySessionStore();
        var model = NewModel(store);
        try
        {
            await model.StartMatchAsync();
            await model.CommitTicketsAsync();
            model.OpenGameExitMenu();
            Assert.True(model.IsGameExitMenuOpen);

            await model.SaveGameToMenuCommand.ExecuteAsync(null);

            Assert.True(model.IsGameExitMenuOpen);
            Assert.True(model.Game.IsPlaying);
            Assert.Equal(Screen.Table, model.GameplayScreen);
            Assert.Contains("camera", model.GameExitStatus!, StringComparison.OrdinalIgnoreCase);
            Assert.Single(await store.ListSessionsAsync(CancellationToken.None));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task QuitToMenuDiscardsOnlyTheCurrentAutosaveAndRemovesItFromReloadChoices()
    {
        var store = new InMemorySessionStore();
        var model = NewModel(store);
        try
        {
            await model.StartMatchAsync();
            Assert.Single(await store.ListSessionsAsync(CancellationToken.None));
            model.OpenGameExitMenu();
            Assert.True(model.IsGameExitMenuOpen);

            await model.QuitToMenuCommand.ExecuteAsync(null);

            Assert.False(model.IsGameExitMenuOpen);
            Assert.Equal(GameScreenStage.Welcome, model.Game.Stage);
            Assert.Equal(Screen.Setup, model.GameplayScreen);
            Assert.Empty(await store.ListSessionsAsync(CancellationToken.None));
            Assert.Empty(model.Setup.SavedSessions);
            Assert.False(model.Game.HasPreviousGame);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static MainViewModel NewModel(InMemorySessionStore store)
    {
        var model = new MainViewModel(ManifestLoader.LoadClassicUs(), store);
        model.Setup.ManualVerificationAccepted = true;
        model.SetGameLayerVisible(true);
        return model;
    }
}
