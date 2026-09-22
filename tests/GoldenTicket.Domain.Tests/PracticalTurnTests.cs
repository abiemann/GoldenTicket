using System.Reflection;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Model;
using GoldenTicket.Persistence;
using GoldenTicket.Testing;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class PracticalTurnTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Take_turn_shows_opening_choices_on_the_game_board_then_covers_the_next_seat(
        bool dropOneTicket)
    {
        var model = NewPracticalMatch();
        try
        {
            await StartAsync(model);
            var firstSeat = model.Table.Seats[0].SeatId;
            AssertCoveredHandoff(model);
            Assert.Contains(model.Table.Seats[0].DisplayName, model.RevealPrompt);

            await model.TakePracticalTurnCommand.ExecuteAsync(null);

            Assert.True(model.HasAcceptedPracticalTurn);
            Assert.True(model.CanUseGameTableControls);
            Assert.False(model.ShowPracticalHandoff);
            Assert.False(model.TakePracticalTurnCommand.CanExecute(null));
            Assert.True(model.ShowSoloOpeningTicketsOnBoard);
            var opening = Assert.IsType<PrivateSeatViewModel>(model.PrivateSeat);
            Assert.Equal(firstSeat, opening.SeatId);
            Assert.Equal(opening.SeatName, model.Game.GuidanceSeat);
            Assert.Equal(3, opening.Offer.Count);
            Assert.Equal(3, model.BoardDestinationLines.Count);
            Assert.NotEmpty(model.BoardDestinationMarkers);
            Assert.False(model.DrawSoloBlindCommand.CanExecute(null));
            AssertTableVisible(model);

            if (dropOneTicket) opening.Offer[0].Keep = false;
            opening.RefreshKeepValidity();
            await model.CommitTicketsCommand.ExecuteAsync(null);

            Assert.Equal(dropOneTicket ? 2 : 3,
                model.Table.Seats.Single(seat => seat.SeatId == firstSeat).TicketCount);
            AssertCoveredHandoff(model);
            Assert.Contains(model.Table.Seats[1].DisplayName, model.RevealPrompt);

            await model.TakePracticalTurnCommand.ExecuteAsync(null);
            Assert.True(model.ShowSoloOpeningTicketsOnBoard);
            Assert.Equal(model.Table.Seats[1].SeatId, model.PrivateSeat!.SeatId);
            Assert.Equal(model.PrivateSeat.SeatName, model.Game.GuidanceSeat);
            Assert.NotSame(opening, model.PrivateSeat);
            AssertTableVisible(model);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Rejected_opening_selection_preserves_acceptance_and_the_pretty_board_offer()
    {
        var model = NewPracticalMatch();
        try
        {
            await StartAsync(model);
            await model.TakePracticalTurnAsync();
            var opening = Assert.IsType<PrivateSeatViewModel>(model.PrivateSeat);
            foreach (var ticket in opening.Offer) ticket.Keep = false;
            opening.Offer[0].Keep = true;
            opening.RefreshKeepValidity();
            var coordinator = Coordinator(model);
            var before = await coordinator.ComputeStateHashAsync(TestContext.Current.CancellationToken);

            await model.CommitTicketsAsync();

            Assert.Equal(before, await coordinator.ComputeStateHashAsync(TestContext.Current.CancellationToken));
            Assert.Same(opening, model.PrivateSeat);
            Assert.NotNull(opening.Message);
            Assert.True(model.HasAcceptedPracticalTurn);
            Assert.True(model.ShowSoloOpeningTicketsOnBoard);
            Assert.False(model.ShowPracticalHandoff);
            Assert.False(model.TakePracticalTurnCommand.CanExecute(null));
            AssertTableVisible(model);

            foreach (var ticket in opening.Offer) ticket.Keep = true;
            opening.RefreshKeepValidity();
            await model.CommitTicketsAsync();
            AssertCoveredHandoff(model);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Accepted_turn_uses_the_game_table_cards_and_never_reveals_another_players_hand()
    {
        var model = NewPracticalMatch();
        try
        {
            await StartActiveMatchAsync(model);
            AssertCoveredHandoff(model);
            await model.TakePracticalTurnAsync();
            Assert.True(model.HasAcceptedPracticalTurn);
            Assert.True(model.IsGameTableHumanTurn);
            Assert.True(model.DrawSoloBlindCommand.CanExecute(null));
            Assert.True(model.DrawSoloTicketsCommand.CanExecute(null));
            Assert.True(model.ShowSoloTrainCards);
            Assert.Equal(4, model.SoloTrainCards.Sum(card => card.Count));
            Assert.Null(model.PrivateSeat);

            var active = model.Game.TableSeats[0];
            var other = model.Game.TableSeats[1];
            var ownCards = model.SoloTrainCards.ToArray();
            await model.ToggleSoloTrainCardsCommand.ExecuteAsync(other);
            await model.ToggleSoloDestinationsCommand.ExecuteAsync(other);
            Assert.Equal(ownCards, model.SoloTrainCards.ToArray());
            Assert.True(model.ShowSoloTrainCards);

            await model.ToggleSoloDestinationsCommand.ExecuteAsync(active);
            Assert.True(model.ShowSoloDestinations);
            Assert.Equal(3, model.SoloDestinationCards.Count);
            Assert.Equal(3, model.BoardDestinationLines.Count);
            Assert.True(model.ShowDestinationMarkersOnBoard);
            await model.ToggleSoloTrainCardsCommand.ExecuteAsync(active);
            Assert.True(model.ShowSoloTrainCards);
            Assert.Equal(4, model.SoloTrainCards.Sum(card => card.Count));
            Assert.Null(model.PrivateSeat);
            AssertTableVisible(model);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task First_face_up_card_keeps_the_turn_open_and_second_awards_the_card_before_handoff()
    {
        var model = NewPracticalMatch();
        try
        {
            await StartActiveMatchAsync(model);
            await model.TakePracticalTurnAsync();
            var coordinator = Coordinator(model);
            var seat = coordinator.Public.ActiveSeatId;
            var originalTurn = coordinator.Public.TurnNumber;
            var originalHand = (await coordinator.GetSeatViewAsync(seat,
                TestContext.Current.CancellationToken)).Hand;
            var first = model.Table.Market.First(slot => slot.Kind != TrainCardKind.Locomotive);
            await WithFreshBoardAsync(model, () => model.DrawSoloFaceUpCommand.ExecuteAsync(first));

            var afterFirst = (await coordinator.GetSeatViewAsync(seat,
                TestContext.Current.CancellationToken)).Hand;
            Assert.Equal(first.Kind, Assert.Single(afterFirst.Except(originalHand)).Kind);
            Assert.Equal(originalTurn, coordinator.Public.TurnNumber);
            Assert.Equal(seat, coordinator.Public.ActiveSeatId);
            Assert.True(model.HasAcceptedPracticalTurn);
            Assert.False(model.ShowPracticalHandoff);
            Assert.True(model.ShowSoloTrainCards);
            Assert.Equal(5, model.SoloTrainCards.Sum(card => card.Count));
            Assert.True(model.DrawSoloBlindCommand.CanExecute(null));
            Assert.False(model.DrawSoloTicketsCommand.CanExecute(null));
            Assert.Null(model.PrivateSeat);

            var second = model.Table.Market.First(slot => model.DrawSoloFaceUpCommand.CanExecute(slot));
            await WithFreshBoardAsync(model, () => model.DrawSoloFaceUpCommand.ExecuteAsync(second));

            var afterSecond = (await coordinator.GetSeatViewAsync(seat,
                TestContext.Current.CancellationToken)).Hand;
            Assert.Equal(second.Kind, Assert.Single(afterSecond.Except(afterFirst)).Kind);
            Assert.Equal(6, afterSecond.Length);
            Assert.Equal(originalTurn + 1, coordinator.Public.TurnNumber);
            Assert.NotEqual(seat, coordinator.Public.ActiveSeatId);
            AssertCoveredHandoff(model);

            await model.TakePracticalTurnAsync();
            Assert.True(model.HasAcceptedPracticalTurn);
            Assert.Equal(4, model.SoloTrainCards.Sum(card => card.Count));
            Assert.Equal(coordinator.Public.ActiveSeatId, model.Table.Seats[1].SeatId);
            Assert.Null(model.PrivateSeat);
            AssertTableVisible(model);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Destination_draw_stays_on_the_board_until_keep_then_requires_the_next_players_consent()
    {
        var model = NewPracticalMatch();
        try
        {
            await StartActiveMatchAsync(model);
            await model.TakePracticalTurnAsync();
            var seat = Coordinator(model).Public.ActiveSeatId;
            await WithFreshBoardAsync(model, () => model.DrawSoloTicketsCommand.ExecuteAsync(null));

            Assert.True(model.HasAcceptedPracticalTurn);
            Assert.True(model.ShowSoloTicketOffer);
            Assert.False(model.ShowPracticalHandoff);
            Assert.False(model.ShowDrawPanels);
            Assert.Null(model.PrivateSeat);
            AssertTableVisible(model);
            foreach (var ticket in model.SoloTicketOffer) ticket.Keep = false;
            Assert.False(model.KeepSoloTicketsCommand.CanExecute(null));
            model.SoloTicketOffer[0].Keep = true;
            await WithFreshBoardAsync(model, () => model.KeepSoloTicketsCommand.ExecuteAsync(null));

            Assert.Equal(4, model.Table.Seats.Single(row => row.SeatId == seat).TicketCount);
            AssertCoveredHandoff(model);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Theory]
    [InlineData("focus")]
    [InlineData("hide")]
    [InlineData("game-layer")]
    [InlineData("mode")]
    public async Task Privacy_transitions_clear_acceptance_and_require_a_new_take_turn(string transition)
    {
        var model = NewPracticalMatch();
        try
        {
            await StartActiveMatchAsync(model);
            await model.TakePracticalTurnAsync();
            Assert.True(model.ShowSoloTrainCards);

            switch (transition)
            {
                case "focus":
                    model.SetWindowActive(false);
                    break;
                case "hide":
                    model.HidePrivateSeat();
                    break;
                case "game-layer":
                    model.SetGameLayerVisible(false);
                    break;
                case "mode":
                    model.Connection.UsePractical = false;
                    break;
            }

            Assert.False(model.HasAcceptedPracticalTurn);
            Assert.False(model.CanUseGameTableControls);
            Assert.False(model.ShowSoloCardPanel);
            Assert.Empty(model.SoloTrainCards);
            Assert.Empty(model.BoardDestinationMarkers);
            Assert.False(model.DrawSoloBlindCommand.CanExecute(null));
            Assert.Null(model.PrivateSeat);

            model.SetWindowActive(true);
            model.SetGameLayerVisible(true);
            model.Connection.UsePractical = true;
            AssertCoveredHandoff(model);
            await model.TakePracticalTurnAsync();
            Assert.True(model.HasAcceptedPracticalTurn);
            Assert.True(model.ShowSoloTrainCards);
            AssertTableVisible(model);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Focus_loss_during_a_pending_second_card_cancels_the_click_without_advancing_the_turn()
    {
        var model = NewPracticalMatch();
        try
        {
            await StartActiveMatchAsync(model);
            await model.TakePracticalTurnAsync();
            await WithFreshBoardAsync(model, () => model.DrawSoloBlindCommand.ExecuteAsync(null));
            var coordinator = Coordinator(model);
            var before = await coordinator.ComputeStateHashAsync(TestContext.Current.CancellationToken);
            var seat = coordinator.Public.ActiveSeatId;
            var pending = model.DrawSoloBlindCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => PendingBoardCheck(model));

            model.SetWindowActive(false);
            await pending;
            Assert.Equal(before, await coordinator.ComputeStateHashAsync(TestContext.Current.CancellationToken));
            Assert.Equal(seat, coordinator.Public.ActiveSeatId);
            Assert.False(model.HasAcceptedPracticalTurn);
            Assert.Empty(model.SoloTrainCards);
            Assert.Null(model.PrivateSeat);

            model.SetWindowActive(true);
            AssertCoveredHandoff(model);
            await model.TakePracticalTurnAsync();
            Assert.True(model.HasAcceptedPracticalTurn);
            Assert.Equal(5, model.SoloTrainCards.Sum(card => card.Count));
            Assert.False(model.DrawSoloTicketsCommand.CanExecute(null));
            await WithFreshBoardAsync(model, () => model.DrawSoloBlindCommand.ExecuteAsync(null));
            Assert.Equal(6, (await coordinator.GetSeatViewAsync(seat,
                TestContext.Current.CancellationToken)).Hand.Length);
            Assert.NotEqual(seat, coordinator.Public.ActiveSeatId);
            AssertCoveredHandoff(model);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Quick_play_cannot_bypass_the_phone_with_local_take_turn_or_draw_commands()
    {
        var model = NewPracticalMatch();
        try
        {
            await StartActiveMatchAsync(model);
            model.Connection.UsePractical = false;
            var coordinator = Coordinator(model);
            var before = await coordinator.ComputeStateHashAsync(TestContext.Current.CancellationToken);
            Assert.False(model.TakePracticalTurnCommand.CanExecute(null));
            Assert.False(model.ShowPracticalHandoff);

            await model.TakePracticalTurnAsync();
            await model.DrawSoloBlindCommand.ExecuteAsync(null);
            await model.DrawSoloFaceUpCommand.ExecuteAsync(model.Table.Market[0]);
            await model.DrawSoloTicketsCommand.ExecuteAsync(null);
            await model.ToggleSoloTrainCardsCommand.ExecuteAsync(model.Game.TableSeats[0]);

            Assert.Equal(before, await coordinator.ComputeStateHashAsync(TestContext.Current.CancellationToken));
            Assert.False(model.HasAcceptedPracticalTurn);
            Assert.False(model.CanUseGameTableControls);
            Assert.False(model.IsGameTableHumanTurn);
            Assert.False(model.ShowSoloCardPanel);
            Assert.False(model.ShowSoloTicketOffer);
            Assert.Null(model.PrivateSeat);
            AssertTableVisible(model);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static MainViewModel NewPracticalMatch()
    {
        var model = new MainViewModel(TestManifest.Manifest, new InMemorySessionStore(),
            camera: new CameraViewModel(capture: new FakeCameraCapture()));
        model.Setup.ManualVerificationAccepted = true;
        foreach (var seat in model.Setup.Seats) seat.IsComputer = false;
        model.Connection.UsePractical = true;
        model.SetGameLayerVisible(true);
        return model;
    }

    private static async Task StartAsync(MainViewModel model)
    {
        await model.StartMatchAsync();
        Assert.Equal(3, model.Table.Seats.Count);
        Assert.True(model.DismissMultiHumanPhoneSetupCommand.CanExecute(null));
        model.DismissMultiHumanPhoneSetupCommand.Execute(null);
    }

    private static async Task StartActiveMatchAsync(MainViewModel model)
    {
        await StartAsync(model);
        foreach (var seat in model.Table.Seats.ToArray())
        {
            AssertCoveredHandoff(model);
            await model.TakePracticalTurnAsync();
            Assert.Equal(seat.SeatId, model.PrivateSeat!.SeatId);
            await model.CommitTicketsAsync();
        }
        Assert.Equal(SessionLifecycle.Active, Coordinator(model).Public.Lifecycle);
    }

    private static void AssertCoveredHandoff(MainViewModel model)
    {
        Assert.True(model.ShowPracticalHandoff);
        Assert.True(model.TakePracticalTurnCommand.CanExecute(null));
        Assert.False(model.HasAcceptedPracticalTurn);
        Assert.False(model.CanUseGameTableControls);
        Assert.False(model.ShowSoloOpeningTicketsOnBoard);
        Assert.False(model.ShowSoloCardPanel);
        Assert.False(model.ShowSoloTicketOffer);
        Assert.False(model.DrawSoloBlindCommand.CanExecute(null));
        Assert.False(model.DrawSoloTicketsCommand.CanExecute(null));
        Assert.Empty(model.SoloTrainCards);
        Assert.Empty(model.SoloDestinationCards);
        Assert.Empty(model.BoardDestinationMarkers);
        Assert.Empty(model.BoardDestinationLines);
        Assert.Null(model.PrivateSeat);
        AssertTableVisible(model);
    }

    private static void AssertTableVisible(MainViewModel model)
    {
        Assert.Equal(Screen.Table, model.Screen);
        Assert.Equal(Screen.Table, model.GameplayScreen);
    }

    private static async Task WithFreshBoardAsync(MainViewModel model, Func<Task> action)
    {
        model.Camera.IsGameTablePreviewUpright = true;
        var pending = action();
        await WaitUntilAsync(() => PendingBoardCheck(model) || pending.IsCompleted);
        Assert.True(PendingBoardCheck(model), "The gameplay action must verify a fresh camera frame.");
        var sequence = model.Camera.GameTableAnalysis?.Board.Sequence ?? 0;
        var at = DateTimeOffset.UtcNow;
        if (model.Camera.GameTableAnalysis?.Board.CapturedAt is { } last && last >= at)
            at = last.AddMilliseconds(10);
        PublishEmptyBoard(model.Camera, sequence + 1, at);
        PublishEmptyBoard(model.Camera, sequence + 2, at.AddSeconds(1.1));
        await pending;
    }

    private static void PublishEmptyBoard(CameraViewModel camera, long sequence, DateTimeOffset at)
    {
        var frame = CameraFrame.CopyFromBgra32(320, 200, new byte[320 * 200 * 4],
            sequence: sequence, epoch: 1, capturedAt: at);
        var analysis = new GameTableAnalysis(frame, [], [], 1, 1, "synthetic-test-model");
        typeof(CameraViewModel).GetProperty(nameof(CameraViewModel.GameTableAnalysis))!
            .GetSetMethod(nonPublic: true)!.Invoke(camera, [analysis]);
    }

    private static bool PendingBoardCheck(MainViewModel model) => typeof(MainViewModel)
        .GetField("_cardBoardCheck", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model) is not null;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition(), "Expected game-table transition was not reached.");
    }

    private static GameCoordinator Coordinator(MainViewModel model) =>
        (GameCoordinator)typeof(MainViewModel)
            .GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
}
