using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed partial class AutomaticPhysicalFlowTests
{
    [Fact]
    public async Task Cancelling_detected_payment_waits_for_train_removal_before_offering_the_route_again()
    {
        var token = TestContext.Current.CancellationToken;
        var model = await StartCompanionMatchAsync();
        try
        {
            var preview = BitmapSource.Create(960, 600, 96, 96, PixelFormats.Bgra32,
                null, new byte[960 * 600 * 4], 960 * 4);
            preview.Freeze();
            model.Camera.GameTablePreview = preview;

            var game = GetCoordinator(model);
            var bridge = GetCompanionBridge(model);
            var proposal = await DetectCompanionRouteAsync(model);
            var before = await game.ComputeStateHashAsync(token);
            var hand = await game.GetSeatViewAsync(proposal.SeatId, token);

            model.CancelBoardFirstClaimCommand.Execute(null);

            Assert.Null(model.BoardFirstProposal);
            Assert.Null(game.Public.PendingClaim);
            Assert.Equal(before, await game.ComputeStateHashAsync(token));
            Assert.Contains("Remove", model.Game.GuidanceInstruction, StringComparison.OrdinalIgnoreCase);
            var removalInstruction = model.Game.GuidanceInstruction;
            Assert.True(model.Game.ShowPlacementTarget);
            Assert.Equal(proposal.RequiredCards, model.Game.PlacementTargets.Count);
            Assert.True((await bridge.ReadPublicAsync(token)).BoardInteraction!.CardActionsBlocked);

            // Enough unchanged observations to detect a new route must not reopen the
            // payment dialog the player just dismissed.
            var at = DateTimeOffset.UtcNow.AddSeconds(5);
            for (var sequence = 5; sequence <= 8; sequence++)
            {
                PublishBlueTrains(model.Camera, proposal.RouteId.Value, sequence,
                    at.AddSeconds((sequence - 5) * 1.1));
                Assert.Null(model.BoardFirstProposal);
                Assert.Equal(removalInstruction, model.Game.GuidanceInstruction);
            }
            Assert.Equal(before, await game.ComputeStateHashAsync(token));
            Assert.Equal(hand.Hand, (await game.GetSeatViewAsync(proposal.SeatId, token)).Hand);

            // A fresh view of the restored board clears the removal instruction and
            // its markers without payment, a turn change, or another click.
            PublishTrains(model.Camera, proposal.RouteId.Value, 9, at.AddSeconds(4.4),
                MarkerColor.Blue, count: 0);
            Assert.Null(model.BoardFirstProposal);
            Assert.False(model.Game.ShowPlacementTarget);
            Assert.Empty(model.Game.PlacementTargets);
            Assert.NotEqual(removalInstruction, model.Game.GuidanceInstruction);
            Assert.False((await bridge.ReadPublicAsync(token)).BoardInteraction!.CardActionsBlocked);

            for (var sequence = 10; sequence <= 13; sequence++)
                PublishBlueTrains(model.Camera, proposal.RouteId.Value, sequence,
                    at.AddSeconds((sequence - 5) * 1.1));
            var replacement = Assert.IsType<BoardFirstClaimProposal>(model.BoardFirstProposal);
            Assert.Equal(proposal.RouteId, replacement.RouteId);
            Assert.NotEqual(proposal.ProposalId, replacement.ProposalId);
            Assert.Null(game.Public.PendingClaim);
            Assert.Equal(before, await game.ComputeStateHashAsync(token));
            Assert.Equal(hand.Hand, (await game.GetSeatViewAsync(proposal.SeatId, token)).Hand);
        }
        finally { await model.DisposeToolsAsync(); }
    }
}
