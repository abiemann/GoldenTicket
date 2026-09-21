using GoldenTicket.Testing;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Vision;
using System.Reflection;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopGameScreenTests
{
    [Fact]
    public async Task NewGameShowsFiveUnselectedCharactersAndCyclesRolesWithSeparateNumbers()
    {
        var model = NewModel();
        try
        {
            Assert.Equal(GameScreenStage.Welcome, model.Game.Stage);
            Assert.False(model.Game.HasPreviousGame);
            await model.Game.ActivateSelectedAsync();
            Assert.Equal(GameScreenStage.CharacterSelection, model.Game.Stage);
            Assert.Equal(5, model.Game.SeatChoices.Count);
            Assert.Equal(new[] { 1, 4, 2, 5, 3 },
                model.Game.SeatChoices.Select(choice => choice.PortraitNumber));
            Assert.Equal(new[] { PlayerColor.Red, PlayerColor.Blue, PlayerColor.Green,
                PlayerColor.Black, PlayerColor.Yellow },
                model.Game.SeatChoices.Select(choice => choice.TrainColor));
            Assert.All(model.Game.SeatChoices, choice =>
            {
                Assert.Equal(CharacterRole.Unselected, choice.Role);
                Assert.Equal("Select", choice.Label);
                Assert.False(choice.IsSelected);
                Assert.EndsWith("human-256.png", choice.PortraitUri);
            });
            Assert.False(model.Game.CanPlay);

            model.Game.CycleSeat(model.Game.SeatChoices[0]);
            Assert.Equal("Player 1", model.Game.SeatChoices[0].Label);
            model.Game.CycleSeat(model.Game.SeatChoices[0]);
            Assert.Equal("Computer 1", model.Game.SeatChoices[0].Label);
            Assert.EndsWith("character-01-robot-256.png", model.Game.SeatChoices[0].PortraitUri);
            model.Game.CycleSeat(model.Game.SeatChoices[1]);
            Assert.Equal("Player 1", model.Game.SeatChoices[1].Label);
            model.Game.CycleSeat(model.Game.SeatChoices[1]);
            Assert.Equal("Computer 2", model.Game.SeatChoices[1].Label);
            model.Game.CycleSeat(model.Game.SeatChoices[2]);
            Assert.Equal("Player 1", model.Game.SeatChoices[2].Label);
            Assert.Equal(3, model.Game.SelectedSeatCount);
            Assert.True(model.Game.CanPlay);

            model.Game.CycleSeat(model.Game.SeatChoices[0]);
            Assert.Equal(CharacterRole.Unselected, model.Game.SeatChoices[0].Role);
            Assert.Equal("Computer 1", model.Game.SeatChoices[1].Label);
            Assert.Equal(2, model.Game.SelectedSeatCount);
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
            Assert.Equal(5, model.Game.SeatChoices.Count);
            foreach (var index in new[] { 1, 4 })
            {
                model.Game.SelectSeat(index);
                await model.Game.ActivateSelectedAsync();
                if (allComputer) await model.Game.ActivateSelectedAsync();
            }
            Assert.Equal(2, model.Game.SelectedSeatCount);
            model.Game.SelectSeat(5);
            await model.Game.ActivateSelectedAsync();
            await model.Game.ConfirmCameraSetupAndPlayAsync();

            Assert.Equal(GameScreenStage.Playing, model.Game.Stage);
            Assert.Equal(Screen.Table, model.GameplayScreen);
            Assert.Equal(2, model.Setup.Seats.Count);
            Assert.Equal(allComputer ? 0 : 2, model.Setup.HumanSeatCount);
            Assert.All(model.Setup.Seats, seat => Assert.Equal(allComputer, seat.IsComputer));
            Assert.Equal(allComputer ? "Computer 1" : "Player 1", model.Setup.Seats[0].DisplayName);
            Assert.Equal(allComputer ? "Computer 2" : "Player 2", model.Setup.Seats[1].DisplayName);
            Assert.Equal(new[] { PlayerColor.Blue, PlayerColor.Yellow },
                model.Setup.Seats.Select(seat => seat.Color));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task TwoComputersAndOneHumanBuildTheThreeChosenSeats()
    {
        var model = NewModel();
        try
        {
            await model.Game.ActivateSelectedAsync();
            foreach (var index in new[] { 0, 1 })
            {
                model.Game.CycleSeat(model.Game.SeatChoices[index]);
                model.Game.CycleSeat(model.Game.SeatChoices[index]);
            }
            model.Game.CycleSeat(model.Game.SeatChoices[2]);
            Assert.Equal(new[] { "Computer 1", "Computer 2", "Player 1" },
                model.Game.SeatChoices.Take(3).Select(choice => choice.Label));
            model.Game.SelectSeat(5);
            await model.Game.ActivateSelectedAsync();
            await model.Game.ConfirmCameraSetupAndPlayAsync();

            Assert.Equal(GameScreenStage.Playing, model.Game.Stage);
            Assert.Equal(3, model.Setup.Seats.Count);
            Assert.Equal(new[] { "Computer 1", "Computer 2", "Player 1" },
                model.Setup.Seats.Select(seat => seat.DisplayName));
            Assert.Equal(new[] { true, true, false }, model.Setup.Seats.Select(seat => seat.IsComputer));
            Assert.Equal(new[] { PlayerColor.Red, PlayerColor.Blue, PlayerColor.Green },
                model.Setup.Seats.Select(seat => seat.Color));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task AllFiveCharactersCanStartAFullTable()
    {
        var model = NewModel();
        try
        {
            await model.Game.ActivateSelectedAsync();
            foreach (var choice in model.Game.SeatChoices) model.Game.CycleSeat(choice);
            model.Game.SelectSeat(5);
            await model.Game.ActivateSelectedAsync();
            await model.Game.ConfirmCameraSetupAndPlayAsync();

            Assert.Equal(GameScreenStage.Playing, model.Game.Stage);
            Assert.Equal(5, model.Setup.Seats.Count);
            Assert.Equal(5, model.Setup.Seats.Select(seat => seat.Color).Distinct().Count());
            Assert.Equal(new[] { PlayerColor.Red, PlayerColor.Blue, PlayerColor.Green,
                PlayerColor.Black, PlayerColor.Yellow }, model.Setup.Seats.Select(seat => seat.Color));
            Assert.Equal(new[] { "Player 1", "Player 2", "Player 3", "Player 4", "Player 5" },
                model.Setup.Seats.Select(seat => seat.DisplayName));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task PlayRequiresAtLeastTwoChosenCharacters()
    {
        var model = NewModel();
        try
        {
            await model.Game.ActivateSelectedAsync();
            model.Setup.ManualVerificationAccepted = true;
            model.Game.SelectSeat(5);
            await model.Game.ActivateSelectedAsync();
            Assert.Equal(GameScreenStage.CharacterSelection, model.Game.Stage);
            Assert.False(model.Game.CanPlay);
            Assert.Contains("at least 2", model.Game.Message);

            model.Game.CycleSeat(model.Game.SeatChoices[3]);
            Assert.False(model.Game.CanPlay);
            model.Game.CycleSeat(model.Game.SeatChoices[4]);
            Assert.True(model.Game.CanPlay);
            model.Game.IsFaceFlipping = true;
            Assert.False(model.Game.CanPlay);
            await model.Game.ActivateSelectedAsync();
            Assert.Equal(GameScreenStage.CharacterSelection, model.Game.Stage);
            model.Game.IsFaceFlipping = false;
            Assert.True(model.Game.CanPlay);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task CameraSetupHidesRosterUntilPlayAndCancelReturnsToChoices()
    {
        var model = NewModel();
        try
        {
            await model.Game.ActivateSelectedAsync();
            model.Game.CycleSeat(model.Game.SeatChoices[0]);
            model.Game.CycleSeat(model.Game.SeatChoices[1]);
            model.Game.SelectSeat(5);
            await model.Game.ActivateSelectedAsync();
            Assert.True(model.Game.IsCameraSetup);
            Assert.False(model.Game.IsCharacterSelection);
            Assert.False(model.Setup.ManualVerificationAccepted);
            Assert.Equal(GameScreenStage.CameraSetup, model.Game.Stage);

            model.Game.CancelCameraSetup();
            Assert.False(model.Game.IsCameraSetup);
            Assert.Equal(-1, model.Game.SeatSelection);
            Assert.Equal(GameScreenStage.CharacterSelection, model.Game.Stage);

            model.Game.SelectSeat(5);
            await model.Game.ActivateSelectedAsync();
            await model.Game.ConfirmCameraSetupAndPlayAsync();
            Assert.True(model.Setup.ManualVerificationAccepted);
            Assert.Equal(GameScreenStage.Playing, model.Game.Stage);
            Assert.False(model.Game.IsCameraSetup);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task PreviousGameOpensCameraPhaseBeforeRestoringItsSession()
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
            Assert.Equal(GameScreenStage.CameraSetup, second.Game.Stage);
            Assert.True(second.Game.IsReloadCameraSetup);
            Assert.False(second.Game.IsNewGameCameraSetup);
            Assert.Equal("RELOAD GAME", second.Game.CameraSetupActionLabel);
            Assert.False(second.Game.CanContinueCameraSetup);
            Assert.False(second.NeedsBoardReconciliation);

            await second.Game.ConfirmCameraSetupAndPlayAsync();
            Assert.Equal(GameScreenStage.CameraSetup, second.Game.Stage);
            Assert.False(second.NeedsBoardReconciliation);
            second.Game.CancelCameraSetup();
            Assert.Equal(GameScreenStage.Welcome, second.Game.Stage);

            second.Game.SelectWelcome(1);
            await second.Game.ActivateSelectedAsync();
            var camera = second.Camera;
            var capture = (FakeCameraCapture)camera.Capture;
            var cameraType = typeof(CameraViewModel);
            var pixels = new byte[320 * 180 * 4];
            var frame = CameraFrame.CopyFromBgra32(320, 180, pixels, 1, 1);
            capture.Epoch = 1L;
            capture.IsRunning = true;
            capture.LatestFrame = frame;
            camera.IsRunning = true;
            camera.BeginReloadBoardFraming();
            cameraType.GetField("_gameBoardCapture", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(camera, (1L, 320, 180));
            cameraType.GetField("_gameBoardAcceptedAt", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(camera, DateTimeOffset.UtcNow);
            cameraType.GetProperty(nameof(CameraViewModel.GameBoardCorners))!.GetSetMethod(true)!
                .Invoke(camera, [new NormalizedPoint[]
                { new(.1, .1), new(.9, .1), new(.9, .9), new(.1, .9) }]);
            Assert.True(second.Game.CanContinueCameraSetup);

            await second.Game.ConfirmCameraSetupAndPlayAsync();
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
            model.Game.CycleSeat(model.Game.SeatChoices[0]);
            model.Game.CycleSeat(model.Game.SeatChoices[1]);
            model.Screen = Screen.Camera;
            model.Game.SelectSeat(5);
            await model.Game.ActivateSelectedAsync();
            await model.Game.ConfirmCameraSetupAndPlayAsync();

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
        new(ManifestLoader.LoadClassicUs(), store ?? new InMemorySessionStore(), camera: new CameraViewModel(capture: new FakeCameraCapture()));
}
