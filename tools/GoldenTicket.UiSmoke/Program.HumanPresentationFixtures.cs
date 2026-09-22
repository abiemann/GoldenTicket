using System.Reflection;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Ai;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Randomness;

internal static partial class Program
{
    private static void HoldComputerTurnForPresentation(MainViewModel model)
    {
        var coordinator = (GameCoordinator)typeof(MainViewModel)
            .GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        typeof(MainViewModel).GetField("_driver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(model, new ComputerSeatDriver(coordinator, new PresentationClaimPolicy(), aiSeed: 1));
    }

    private sealed class PresentationClaimPolicy : IAiPolicy
    {
        public ValueTask<AiDecision> ChooseAsync(SeatView view, BoardManifest manifest,
            DecisionBudget budget, DeterministicRandom random, CancellationToken cancellationToken)
        {
            // Keep this UI fixture on a computer's physical placement step. A normal AI can
            // finish a card-drawing turn immediately, legitimately returning to the human.
            var route = LegalActionCalculator.For(view, manifest).Claims
                .OrderBy(claim => claim.Length).ThenBy(claim => claim.RouteId.Value).First();
            return ValueTask.FromResult<AiDecision>(new AiClaimRoute(route.RouteId, route.Payments[0]));
        }
    }
}
