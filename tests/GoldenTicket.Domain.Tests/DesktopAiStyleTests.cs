using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Manifest;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopAiStyleTests
{
    [Fact]
    public async Task ComputerStylesAreIndependentAndTogglingDoesNotChangeRoles()
    {
        var model = NewModel();
        try
        {
            await model.Game.ActivateSelectedAsync();
            var first = model.Game.SeatChoices[0];
            var second = model.Game.SeatChoices[3];
            var human = model.Game.SeatChoices[1];
            foreach (var computer in new[] { first, second })
            {
                model.Game.CycleSeat(computer);
                model.Game.CycleSeat(computer);
                Assert.True(computer.IsComputer);
                Assert.Equal(AiDifficulty.Standard, computer.Difficulty);
            }
            model.Game.CycleSeat(human);
            model.Game.ToggleAiStyle(human);
            model.Game.ToggleAiStyle(model.Game.SeatChoices[2]);
            model.Game.ToggleAiStyle(first);

            Assert.True(first.IsAggressive);
            Assert.Equal("Computer 1", first.Label);
            Assert.Equal(AiDifficulty.Standard, second.Difficulty);
            Assert.False(human.IsComputer);
            Assert.Equal(AiDifficulty.Standard, human.Difficulty);
            Assert.Equal(AiDifficulty.Standard, model.Game.SeatChoices[2].Difficulty);
            Assert.Equal(3, model.Game.SelectedSeatCount);
            Assert.True(model.Game.CanPlay);

            model.Game.ToggleAiStyle(first);
            Assert.False(first.IsAggressive);
            Assert.Equal(CharacterRole.Computer, first.Role);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task ReselectingComputerAndStartingANewRosterResetToStandard()
    {
        var model = NewModel();
        try
        {
            await model.Game.ActivateSelectedAsync();
            var computer = model.Game.SeatChoices[0];
            model.Game.CycleSeat(computer);
            model.Game.CycleSeat(computer);
            model.Game.ToggleAiStyle(computer);
            model.Game.CycleSeat(computer);
            Assert.False(computer.IsComputer);
            Assert.Equal(AiDifficulty.Standard, computer.Difficulty);
            model.Game.CycleSeat(computer);
            model.Game.CycleSeat(computer);
            Assert.False(computer.IsAggressive);

            model.Game.ToggleAiStyle(computer);
            model.Game.ShowWelcome();
            model.Game.ToggleAiStyle(computer);
            Assert.True(computer.IsAggressive); // The style action belongs only to Choose players.
            await model.Game.ActivateSelectedAsync();
            Assert.All(model.Game.SeatChoices, choice =>
            {
                Assert.Equal(CharacterRole.Unselected, choice.Role);
                Assert.Equal(AiDifficulty.Standard, choice.Difficulty);
            });
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task StartingGameMapsStylesToChosenSeatsWithoutInheritingPreviousSetupDifficulty()
    {
        var model = NewModel();
        try
        {
            foreach (var row in model.Setup.Seats) row.Difficulty = AiDifficulty.Challenging;
            await model.Game.ActivateSelectedAsync();
            foreach (var index in new[] { 1, 4 })
            {
                model.Game.CycleSeat(model.Game.SeatChoices[index]);
                model.Game.CycleSeat(model.Game.SeatChoices[index]);
            }
            model.Game.CycleSeat(model.Game.SeatChoices[3]);
            model.Game.ToggleAiStyle(model.Game.SeatChoices[1]);
            model.Game.SelectSeat(5);
            await model.Game.ActivateSelectedAsync();
            model.Game.CancelCameraSetup();
            Assert.True(model.Game.SeatChoices[1].IsAggressive);
            model.Game.SelectSeat(5);
            await model.Game.ActivateSelectedAsync();
            await model.Game.ConfirmCameraSetupAndPlayAsync();

            Assert.Equal(GameScreenStage.Playing, model.Game.Stage);
            Assert.Equal(new[] { PlayerColor.Blue, PlayerColor.Black, PlayerColor.Yellow },
                model.Setup.Seats.Select(seat => seat.Color));
            Assert.Equal(new[] { true, false, true }, model.Setup.Seats.Select(seat => seat.IsComputer));
            Assert.Equal(new[] { AiDifficulty.Aggressive, AiDifficulty.Standard, AiDifficulty.Standard },
                model.Setup.Seats.Select(seat => seat.Difficulty));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static MainViewModel NewModel() =>
        new(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
}
