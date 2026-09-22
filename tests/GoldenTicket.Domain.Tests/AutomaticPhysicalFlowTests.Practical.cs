using GoldenTicket.Application;
using GoldenTicket.CompanionHost;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Testing;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed partial class AutomaticPhysicalFlowTests
{
    [Fact]
    public async Task Practical_turn_pays_the_camera_detected_route_on_the_game_board_using_exact_selected_cards()
    {
        var model = await StartPracticalCameraMatchAsync();
        try
        {
            var game = GetCoordinator(model);
            var proposal = await DetectCompanionRouteAsync(model);
            Assert.True(model.HasAcceptedPracticalTurn);
            Assert.True(model.ShowBoardFirstClaimProposal);
            Assert.Null(model.PrivateSeat);
            Assert.Equal(Screen.Table, model.Screen);
            Assert.Equal(Screen.Table, model.GameplayScreen);
            var before = await game.GetSeatViewAsync(proposal.SeatId, TestContext.Current.CancellationToken);

            var payment = proposal.Payments[0].Option;
            foreach (var card in proposal.Cards.Where(card => card.Kind == payment.Color)
                         .Take(payment.ColorCards)) card.IsSelected = true;
            foreach (var card in proposal.Cards.Where(card => card.Kind == TrainCardKind.Locomotive)
                         .Take(payment.Locomotives)) card.IsSelected = true;
            Assert.True(proposal.CanConfirmPayment);
            var selected = proposal.SelectedCardIds;

            await model.ConfirmBoardFirstClaimCommand.ExecuteAsync(null);

            var reserved = await game.GetSeatViewAsync(proposal.SeatId, TestContext.Current.CancellationToken);
            Assert.NotNull(game.Public.PendingClaim);
            Assert.Equal(proposal.RouteId, game.Public.PendingClaim.RouteId);
            Assert.Equal(proposal.SeatId, game.Public.ActiveSeatId);
            Assert.Equal(TurnPhase.AwaitingPhysicalPlacement, game.Public.TurnPhase);
            Assert.Equal(selected.OrderBy(id => id.Value), reserved.ReservedCards.OrderBy(id => id.Value));
            Assert.Equal(before.Hand, reserved.Hand);
            Assert.Equal(before.TrainsRemaining, reserved.TrainsRemaining);
            Assert.False(game.Public.RouteOwners.ContainsKey(proposal.RouteId));
            Assert.False(model.ShowPracticalHandoff);
            Assert.False(model.ShowBoardFirstClaimProposal);
            Assert.False(model.DrawSoloBlindCommand.CanExecute(null));
            Assert.Null(model.PrivateSeat);
            Assert.Equal(Screen.Table, model.Screen);
            Assert.Equal(Screen.Table, model.GameplayScreen);

            var after = await game.ComputeStateHashAsync(TestContext.Current.CancellationToken);
            await model.ConfirmBoardFirstClaimCommand.ExecuteAsync(null);
            Assert.Equal(after, await game.ComputeStateHashAsync(TestContext.Current.CancellationToken));

            var at = DateTimeOffset.UtcNow.AddSeconds(6);
            PublishBlueTrains(model.Camera, proposal.RouteId.Value, 5, at);
            Assert.NotNull(game.Public.PendingClaim);
            PublishBlueTrains(model.Camera, proposal.RouteId.Value, 6, at.AddSeconds(1.1));
            await WaitUntilAsync(() => model.ShowScoreMarkerDetectionPrompt);
            Assert.Equal(proposal.SeatId, game.Public.RouteOwners[proposal.RouteId]);
            Assert.Null(game.Public.PendingClaim);
            var paid = await game.GetSeatViewAsync(proposal.SeatId, TestContext.Current.CancellationToken);
            Assert.Equal(before.Hand.Length - selected.Length, paid.Hand.Length);
            Assert.Equal(selected.OrderBy(id => id.Value),
                before.Hand.Except(paid.Hand).Select(card => card.Id).OrderBy(id => id.Value));
            Assert.False(model.ShowPracticalHandoff);
            Assert.Null(model.PrivateSeat);

            var score = game.Public.SeatOf(proposal.SeatId).RouteScore;
            PublishScore(model.Camera, 7, at.AddSeconds(2.2), MarkerColor.Blue, score % 100 + 1);
            PublishScore(model.Camera, 8, at.AddSeconds(3.3), MarkerColor.Blue, score % 100 + 1);
            await WaitUntilAsync(() => model.ShowPracticalHandoff);
            Assert.NotEqual(proposal.SeatId, game.Public.ActiveSeatId);
            Assert.False(model.HasAcceptedPracticalTurn);
            Assert.False(model.ShowSoloCardPanel);
            Assert.Empty(model.SoloTrainCards);
            Assert.Null(model.PrivateSeat);
            Assert.Equal(Screen.Table, model.Screen);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Practical_payment_privacy_reset_discards_unpaid_selection_and_allows_taking_the_turn_again(
        bool changeMode)
    {
        var model = await StartPracticalCameraMatchAsync();
        try
        {
            var game = GetCoordinator(model);
            var proposal = await DetectCompanionRouteAsync(model);
            Assert.True(model.ShowBoardFirstClaimProposal);
            proposal.Cards[0].IsSelected = true;
            var before = await game.ComputeStateHashAsync(TestContext.Current.CancellationToken);

            if (changeMode) model.Connection.UsePractical = false;
            else model.SetWindowActive(false);

            Assert.False(model.HasAcceptedPracticalTurn);
            Assert.False(model.ShowBoardFirstClaimProposal);
            Assert.Null(model.BoardFirstProposal);
            Assert.Null(model.PrivateSeat);
            await model.ConfirmBoardFirstClaimCommand.ExecuteAsync(null);
            Assert.Equal(before, await game.ComputeStateHashAsync(TestContext.Current.CancellationToken));
            Assert.Null(game.Public.PendingClaim);

            model.Connection.UsePractical = true;
            model.SetWindowActive(true);
            Assert.True(model.ShowPracticalHandoff);
            Assert.True(model.TakePracticalTurnCommand.CanExecute(null));
            await model.TakePracticalTurnAsync();
            Assert.True(model.HasAcceptedPracticalTurn);

            var replacement = await DetectCompanionRouteAsync(model);
            Assert.Equal(proposal.RouteId, replacement.RouteId);
            Assert.NotEqual(proposal.ProposalId, replacement.ProposalId);
            Assert.Equal(0, replacement.SelectedCount);
            Assert.True(model.ShowBoardFirstClaimProposal);
            Assert.Null(model.PrivateSeat);
            Assert.Equal(Screen.Table, model.Screen);
            Assert.Equal(before, await game.ComputeStateHashAsync(TestContext.Current.CancellationToken));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static async Task<MainViewModel> StartPracticalCameraMatchAsync()
    {
        // Prepare a two-human match without opening hardware or publishing a companion host.
        var model = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore(),
            camera: new CameraViewModel(capture: new FakeCameraCapture()));
        model.Setup.ManualVerificationAccepted = true;
        model.Setup.Seats.RemoveAt(2);
        model.Setup.Seats[1].IsComputer = false;
        await model.StartMatchAsync();
        var bridge = GetCompanionBridge(model);
        var game = GetCoordinator(model);
        foreach (var seat in game.Seats)
        {
            var view = await bridge.ReadPrivateAsync(seat.SeatId, game.Public.StateVersion,
                TestContext.Current.CancellationToken);
            var keep = new CompanionCommand(Guid.NewGuid().ToString("N"), game.SessionId.Value,
                game.Public.StateVersion, "keepTickets",
                KeptTickets: view!.OfferedTickets.Take(2).Select(ticket => ticket.Id).ToArray());
            Assert.True((await bridge.ExecuteAsync(seat.SeatId, keep,
                TestContext.Current.CancellationToken)).Accepted);
        }
        model.Connection.UsePractical = true;
        model.SetGameLayerVisible(true);
        model.DismissMultiHumanPhoneSetupCommand.Execute(null);
        Assert.True(model.ShowPracticalHandoff);
        await model.TakePracticalTurnAsync();
        Assert.True(model.HasAcceptedPracticalTurn);
        return model;
    }
}
