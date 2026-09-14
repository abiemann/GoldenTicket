using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Manifest;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopGameScreenTests
{
    [Fact]
    public async Task NewGameMenuConfiguresFiveSharedSeatRowsAndMatchedPortraits()
    {
        var model = NewModel();
        try
        {
            Assert.Equal(GameScreenStage.Welcome, model.Game.Stage);
            Assert.False(model.Game.HasPreviousGame);
            await model.Game.ActivateSelectedAsync();
            Assert.Equal(GameScreenStage.PlayerCount, model.Game.Stage);

            model.Game.SelectCount(5);
            await model.Game.ActivateSelectedAsync();
            Assert.Equal(GameScreenStage.AiSelection, model.Game.Stage);
            Assert.Equal(5, model.Game.SeatChoices.Count);
            Assert.Equal(5, model.Setup.Seats.Count);
            Assert.All(model.Setup.Seats, seat => Assert.False(seat.IsComputer));
            Assert.Equal("Player 4", model.Game.SeatChoices[3].Label);
            Assert.EndsWith("character-04-human-256.png", model.Game.SeatChoices[3].PortraitUri);

            model.Game.SelectSeat(3);
            await model.Game.ActivateSelectedAsync();
            Assert.True(model.Setup.Seats[3].IsComputer);
            Assert.Equal("Computer 4", model.Setup.Seats[3].DisplayName);
            Assert.Equal("Computer 4", model.Game.SeatChoices[3].Label);
            Assert.EndsWith("character-04-robot-256.png", model.Game.SeatChoices[3].PortraitUri);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoHumansOrTwoComputersCanStartFromTheGameScreen(bool allComputer)
    {
        var model = NewModel();
        try
        {
            await model.Game.ActivateSelectedAsync();
            await model.Game.ActivateSelectedAsync();
            Assert.Equal(2, model.Game.SeatChoices.Count);
            if (allComputer)
            {
                model.Game.SelectSeat(0);
                await model.Game.ActivateSelectedAsync();
                model.Game.SelectSeat(1);
                await model.Game.ActivateSelectedAsync();
            }
            model.Setup.ManualVerificationAccepted = true;
            model.Game.SelectSeat(2);
            await model.Game.ActivateSelectedAsync();

            Assert.Equal(GameScreenStage.Playing, model.Game.Stage);
            Assert.Equal(Screen.Table, model.GameplayScreen);
            Assert.Equal(allComputer ? 0 : 2, model.Setup.HumanSeatCount);
            Assert.All(model.Setup.Seats, seat => Assert.Equal(allComputer, seat.IsComputer));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task PreviousGameAppearsAfterSaveAndReloadsItsExistingSession()
    {
        var store = new InMemorySessionStore();
        var first = NewModel(store);
        var second = NewModel(store);
        try
        {
            first.Setup.ManualVerificationAccepted = true;
            await first.StartMatchAsync();
            await second.LoadSavedSessionsAsync();
            Assert.True(second.Game.HasPreviousGame);

            second.Game.SelectWelcome(1);
            await second.Game.ActivateSelectedAsync();
            Assert.Equal(GameScreenStage.Playing, second.Game.Stage);
            Assert.True(second.NeedsBoardReconciliation);
        }
        finally
        {
            await first.DisposeToolsAsync();
            await second.DisposeToolsAsync();
        }
    }

    [Fact]
    public async Task GameActionsRemainAvailableAfterReturningFromATechnicalTool()
    {
        var model = NewModel();
        try
        {
            model.SetGameLayerVisible(true);
            await model.Game.ActivateSelectedAsync();
            await model.Game.ActivateSelectedAsync();
            model.Setup.ManualVerificationAccepted = true;
            model.Screen = Screen.Camera;
            model.Game.SelectSeat(2);
            await model.Game.ActivateSelectedAsync();

            Assert.Equal(GameScreenStage.Playing, model.Game.Stage);
            Assert.Equal(Screen.Camera, model.Screen);
            Assert.Equal(Screen.Table, model.GameplayScreen);
            Assert.True(model.CanRevealPrivateSeat);

            model.SetGameLayerVisible(false);
            Assert.False(model.CanRevealPrivateSeat);
            model.SetGameLayerVisible(true);
            Assert.True(model.CanRevealPrivateSeat);
            Assert.Equal(Screen.Camera, model.Screen);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static MainViewModel NewModel(InMemorySessionStore? store = null) =>
        new(ManifestLoader.LoadClassicUs(), store ?? new InMemorySessionStore());
}
