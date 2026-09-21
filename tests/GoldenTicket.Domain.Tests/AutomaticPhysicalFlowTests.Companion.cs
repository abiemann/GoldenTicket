using System.Reflection;
using System.Text.Json;
using GoldenTicket.Application;
using GoldenTicket.CompanionHost;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed partial class AutomaticPhysicalFlowTests
{
    [Fact]
    public async Task Phone_pays_for_the_confirmed_route_without_revealing_payment_on_the_laptop()
    {
        var model = await StartCompanionMatchAsync();
        try
        {
            var bridge = GetCompanionBridge(model);
            var game = GetCoordinator(model);
            var active = game.Public.ActiveSeatId;
            var privateBeforeDetection = await bridge.ReadPrivateAsync(active, game.Public.StateVersion,
                TestContext.Current.CancellationToken);
            var proposal = await DetectCompanionRouteAsync(model);
            Assert.False(model.ShowBoardFirstClaimProposal);
            Assert.Null(model.PrivateSeat);
            var snapshot = await bridge.ReadPublicAsync(TestContext.Current.CancellationToken);
            Assert.True(snapshot.CanControl);
            Assert.Null(snapshot.Guidance);
            Assert.True(snapshot.BoardInteraction!.UseCameraClaims);
            Assert.True(snapshot.BoardInteraction.CardActionsBlocked);
            var detected = Assert.IsType<CompanionDetectedRoute>(snapshot.BoardInteraction.DetectedRoute);
            Assert.True(detected.Ready);
            Assert.Equal(proposal.ProposalId, detected.ProposalId);
            Assert.Equal(proposal.RouteId.Value, detected.RouteId);
            Assert.Contains(privateBeforeDetection!.Actions.Claims, claim => claim.RouteId == proposal.RouteId);
            var publicJson = JsonSerializer.Serialize(snapshot.BoardInteraction);
            Assert.DoesNotContain("Payment", publicJson);
            Assert.DoesNotContain("Hand", publicJson);
            Assert.DoesNotContain("CardId", publicJson);
            var before = await game.GetSeatViewAsync(active, TestContext.Current.CancellationToken);

            var pay = CompanionPayment(proposal);
            var receipt = await bridge.ExecuteAsync(active, pay, TestContext.Current.CancellationToken);
            Assert.True(receipt.Accepted, receipt.Message);
            Assert.Equal(active, game.Public.RouteOwners[proposal.RouteId]);
            Assert.Null(game.Public.PendingClaim);
            var after = await game.GetSeatViewAsync(active, TestContext.Current.CancellationToken);
            Assert.Equal(before.Hand.Length - pay.Payment!.Total, after.Hand.Length);
            Assert.Equal(before.TrainsRemaining - detected.Length, after.TrainsRemaining);
            Assert.True(model.ShowScoreMarkerDetectionPrompt);
            Assert.Equal("Scoring", model.Game.GuidanceTurn);
            Assert.Null(model.PrivateSeat);
            Assert.Null(model.BoardFirstProposal);
            var scoring = await bridge.ReadPublicAsync(TestContext.Current.CancellationToken);
            Assert.False(scoring.CanControl);
            Assert.Null(scoring.RevealSeatId);
            Assert.Equal(new CompanionGuidance(model.Game.GuidanceSeat, model.Game.GuidanceInstruction),
                scoring.Guidance);

            var committed = await game.ComputeStateHashAsync(TestContext.Current.CancellationToken);
            Assert.False((await bridge.ExecuteAsync(active, pay, TestContext.Current.CancellationToken)).Accepted);
            Assert.Equal(committed, await game.ComputeStateHashAsync(TestContext.Current.CancellationToken));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Theory]
    [InlineData("proposal")]
    [InlineData("route")]
    [InlineData("seat")]
    [InlineData("session")]
    [InlineData("version")]
    [InlineData("command-id")]
    [InlineData("payment")]
    [InlineData("missing")]
    [InlineData("wrong-color")]
    [InlineData("stale")]
    [InlineData("orientation")]
    [InlineData("crop")]
    [InlineData("model")]
    [InlineData("epoch")]
    public async Task Phone_payment_rejects_a_changed_proposal_turn_or_camera_proof_without_spending(string change)
    {
        var model = await StartCompanionMatchAsync();
        try
        {
            var bridge = GetCompanionBridge(model);
            var game = GetCoordinator(model);
            var proposal = await DetectCompanionRouteAsync(model);
            var pay = CompanionPayment(proposal);
            var seat = proposal.SeatId;
            pay = change switch
            {
                "proposal" => pay with { DetectedClaimId = Guid.NewGuid().ToString("N") },
                "route" => pay with { RouteId = "not-the-detected-route" },
                "session" => pay with { SessionId = "another-match" },
                "version" => pay with { ExpectedStateVersion = pay.ExpectedStateVersion + 1 },
                "command-id" => pay with { CommandId = "invalid" },
                "payment" => pay with { Payment = new(TrainCardKind.Black, 99, 0) },
                _ => pay
            };
            if (change == "seat") seat = game.Seats.Last().SeatId;
            if (change == "orientation") model.Camera.IsGameTablePreviewUpright = false;
            if (change is "missing" or "wrong-color" or "stale" or "crop" or "model" or "epoch")
                PublishTrains(model.Camera, proposal.RouteId.Value, 5,
                    change == "stale" ? DateTimeOffset.UtcNow.AddSeconds(-3) : DateTimeOffset.UtcNow.AddSeconds(5),
                    change == "wrong-color" ? MarkerColor.Red : MarkerColor.Blue,
                    count: change == "missing" ? 0 : int.MaxValue,
                    cropRevision: change == "crop" ? 2 : 1,
                    modelRevision: change == "model" ? 2 : 1,
                    epoch: change == "epoch" ? 2 : 1);
            if (change is "stale" or "orientation")
            {
                var snapshot = await bridge.ReadPublicAsync(TestContext.Current.CancellationToken);
                Assert.True(snapshot.BoardInteraction!.UseCameraClaims);
                Assert.False(snapshot.BoardInteraction.DetectedRoute!.Ready);
            }
            var before = await game.ComputeStateHashAsync(TestContext.Current.CancellationToken);
            var receipt = await bridge.ExecuteAsync(seat, pay, TestContext.Current.CancellationToken);
            Assert.False(receipt.Accepted);
            Assert.Equal(before, await game.ComputeStateHashAsync(TestContext.Current.CancellationToken));
            Assert.Null(game.Public.PendingClaim);
            Assert.False(game.Public.RouteOwners.ContainsKey(proposal.RouteId));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Phone_cannot_bypass_camera_payment_with_a_manual_claim_or_card_draw()
    {
        var model = await StartCompanionMatchAsync();
        try
        {
            var bridge = GetCompanionBridge(model);
            var game = GetCoordinator(model);
            var proposal = await DetectCompanionRouteAsync(model);
            var before = await game.ComputeStateHashAsync(TestContext.Current.CancellationToken);
            var manual = CompanionPayment(proposal) with { Kind = "planClaim", DetectedClaimId = null };
            Assert.False((await bridge.ExecuteAsync(proposal.SeatId, manual, TestContext.Current.CancellationToken)).Accepted);
            foreach (var kind in new[] { "drawTrain", "drawTickets", "keepTickets" })
            {
                var command = new CompanionCommand(Guid.NewGuid().ToString("N"), game.SessionId.Value,
                    game.Public.StateVersion, kind);
                Assert.False((await bridge.ExecuteAsync(proposal.SeatId, command, TestContext.Current.CancellationToken)).Accepted);
                Assert.Equal(before, await game.ComputeStateHashAsync(TestContext.Current.CancellationToken));
            }
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Phone_payment_keeps_the_reservation_pending_if_the_board_changes_during_the_write(bool extra)
    {
        var store = new DelayedPaymentStore();
        var model = await StartCompanionMatchAsync(store);
        try
        {
            var bridge = GetCompanionBridge(model);
            var game = GetCoordinator(model);
            var proposal = await DetectCompanionRouteAsync(model);
            var before = await game.GetSeatViewAsync(proposal.SeatId, TestContext.Current.CancellationToken);
            store.DelayNextCommit = true;
            var payment = bridge.ExecuteAsync(proposal.SeatId, CompanionPayment(proposal), TestContext.Current.CancellationToken);
            await store.CommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            PublishTrains(model.Camera, proposal.RouteId.Value, 5, DateTimeOffset.UtcNow.AddSeconds(5),
                MarkerColor.Blue, count: extra ? int.MaxValue : 0, extraTrain: extra);
            store.ReleaseCommit.TrySetResult();
            var receipt = await payment;
            Assert.True(receipt.Accepted);
            Assert.DoesNotContain("scoring", receipt.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(game.Public.PendingClaim);
            Assert.False(game.Public.RouteOwners.ContainsKey(proposal.RouteId));
            Assert.False(model.ShowScoreMarkerDetectionPrompt);
            var after = await game.GetSeatViewAsync(proposal.SeatId, TestContext.Current.CancellationToken);
            Assert.Equal(before.Hand, after.Hand);
            Assert.Equal(before.TrainsRemaining, after.TrainsRemaining);
            Assert.Contains("board changed", model.Game.GuidanceInstruction, StringComparison.OrdinalIgnoreCase);
        }
        finally { store.ReleaseCommit.TrySetResult(); await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Camera_free_phone_game_retains_manual_claims()
    {
        var model = await StartCompanionMatchAsync();
        try
        {
            var bridge = GetCompanionBridge(model);
            var game = GetCoordinator(model);
            var seat = game.Public.ActiveSeatId;
            var view = await bridge.ReadPrivateAsync(seat, game.Public.StateVersion, TestContext.Current.CancellationToken);
            var route = view!.Actions.Claims.First();
            var snapshot = await bridge.ReadPublicAsync(TestContext.Current.CancellationToken);
            Assert.False(snapshot.BoardInteraction!.UseCameraClaims);
            var command = new CompanionCommand(Guid.NewGuid().ToString("N"), game.SessionId.Value,
                game.Public.StateVersion, "planClaim", RouteId: route.RouteId.Value, Payment: route.Payments[0]);
            Assert.True((await bridge.ExecuteAsync(seat, command, TestContext.Current.CancellationToken)).Accepted);
            Assert.Equal(route.RouteId, game.Public.PendingClaim!.RouteId);
            Assert.Null(model.BoardFirstProposal);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Phone_draws_wait_for_new_clean_frames_and_public_polling_remains_responsive(bool cancel)
    {
        var model = await StartCompanionMatchAsync();
        try
        {
            var bridge = GetCompanionBridge(model);
            var game = GetCoordinator(model);
            var active = game.Public.ActiveSeatId;
            model.Camera.IsGameTablePreviewUpright = true;
            var at = DateTimeOffset.UtcNow;
            PublishTrains(model.Camera, "duluth--omaha--a", 1, at, MarkerColor.Blue, count: 0);
            PublishTrains(model.Camera, "duluth--omaha--a", 2, at.AddSeconds(1.1), MarkerColor.Blue, count: 0);
            var before = await game.ComputeStateHashAsync(TestContext.Current.CancellationToken);
            var version = game.Public.StateVersion;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var draw = bridge.ExecuteAsync(active, new(Guid.NewGuid().ToString("N"), game.SessionId.Value,
                version, "drawTrain"), cancellation.Token);
            Assert.False(draw.IsCompleted);
            var poll = await bridge.ReadPublicAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.True(poll.CanControl);
            Assert.True(poll.BoardInteraction!.CardActionsBlocked);
            Assert.Equal(before, await game.ComputeStateHashAsync(TestContext.Current.CancellationToken));
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await draw);
                Assert.Equal(before, await game.ComputeStateHashAsync(TestContext.Current.CancellationToken));
            }
            else
            {
                at = DateTimeOffset.UtcNow;
                PublishTrains(model.Camera, "duluth--omaha--a", 3, at, MarkerColor.Blue, count: 0);
                Assert.False(draw.IsCompleted);
                PublishTrains(model.Camera, "duluth--omaha--a", 4, at.AddSeconds(1.1), MarkerColor.Blue, count: 0);
                Assert.True((await draw).Accepted);
                Assert.Equal(version + 1, game.Public.StateVersion);
                Assert.Equal(TurnPhase.AwaitingSecondTrainCard, game.Public.TurnPhase);
            }
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static ICompanionGameBridge GetCompanionBridge(MainViewModel model) =>
        (ICompanionGameBridge)typeof(MainViewModel).GetProperty("CompanionBridge",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;

    private static CompanionCommand CompanionPayment(BoardFirstClaimProposal proposal) =>
        new(Guid.NewGuid().ToString("N"), proposal.SessionId.Value, proposal.StateVersion, "payDetectedRoute",
            RouteId: proposal.RouteId.Value, Payment: proposal.Payments[0].Option, DetectedClaimId: proposal.ProposalId);

    private static async Task<MainViewModel> StartCompanionMatchAsync(ISessionStore? store = null)
    {
        var model = new MainViewModel(ManifestLoader.LoadClassicUs(), store ?? new InMemorySessionStore());
        model.Setup.ManualVerificationAccepted = true;
        model.Setup.Seats.RemoveAt(2);
        model.Setup.Seats[1].IsComputer = false;
        await model.StartMatchAsync();
        var bridge = GetCompanionBridge(model);
        var game = GetCoordinator(model);
        foreach (var seat in game.Seats)
        {
            var view = await bridge.ReadPrivateAsync(seat.SeatId, game.Public.StateVersion, TestContext.Current.CancellationToken);
            var command = new CompanionCommand(Guid.NewGuid().ToString("N"), game.SessionId.Value,
                game.Public.StateVersion, "keepTickets", KeptTickets: view!.OfferedTickets.Take(2).Select(ticket => ticket.Id).ToArray());
            Assert.True((await bridge.ExecuteAsync(seat.SeatId, command, TestContext.Current.CancellationToken)).Accepted);
        }
        Assert.Equal(TurnPhase.TurnStart, game.Public.TurnPhase);
        Assert.Null(model.PrivateSeat);
        return model;
    }

    private static async Task<BoardFirstClaimProposal> DetectCompanionRouteAsync(MainViewModel model)
    {
        var game = GetCoordinator(model);
        var route = (await game.GetLegalActionsAsync(game.Public.ActiveSeatId, TestContext.Current.CancellationToken))
            .Claims.First(claim => RoutePlacementVerifier.Supports(claim.RouteId.Value, claim.Length));
        var at = DateTimeOffset.UtcNow;
        model.Camera.IsGameTablePreviewUpright = true;
        PublishBlueTrains(model.Camera, route.RouteId.Value, 1, at);
        await WaitUntilAsync(() => typeof(MainViewModel)
            .GetField("_boardFirstLegalActions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model) is not null);
        PublishBlueTrains(model.Camera, route.RouteId.Value, 2, at.AddSeconds(1.1));
        PublishBlueTrains(model.Camera, route.RouteId.Value, 3, at.AddSeconds(2.2));
        PublishBlueTrains(model.Camera, route.RouteId.Value, 4, at.AddSeconds(3.3));
        return Assert.IsType<BoardFirstClaimProposal>(model.BoardFirstProposal);
    }
}
