using System.Collections.Immutable;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.Domain.Ai;

/// <summary>
/// DESIGN 15.3/15.5: difficulty changes computation and decision policy, never information access.
/// Every request carries a deadline, and the coordinator revalidates whatever comes back.
/// </summary>
public sealed record DecisionBudget(AiDifficulty Difficulty, TimeSpan Deadline);

/// <summary>A complete legal action a computer seat has chosen.</summary>
public abstract record AiDecision;

/// <summary>Take one train card. <paramref name="Slot"/> is null for a blind draw.</summary>
public sealed record AiDrawTrainCard(int? Slot) : AiDecision;

/// <summary>Claim a route with an explicitly chosen payment (DESIGN 4.3: never a silent wild spend).</summary>
public sealed record AiClaimRoute(RouteId RouteId, PaymentOption Payment) : AiDecision;

/// <summary>Open a destination-ticket offer.</summary>
public sealed record AiDrawTickets : AiDecision;

/// <summary>Keep a subset of an open ticket offer.</summary>
public sealed record AiKeepTickets(ImmutableArray<TicketId> Kept) : AiDecision;

/// <summary>
/// No action could be chosen. DESIGN 15.5: this is recorded rather than stalling the match, and the
/// coordinator falls back to a simple legal policy.
/// </summary>
public sealed record AiNoDecision(string Reason) : AiDecision;

/// <summary>
/// A computer opponent. DESIGN 5.3: the implementation receives an immutable
/// <see cref="SeatView"/> and the public board data, and has no reference to
/// <see cref="Model.GameState"/>, persistence, the deck service, or another seat's view.
/// </summary>
public interface IAiPolicy
{
    ValueTask<AiDecision> ChooseAsync(
        SeatView view,
        BoardManifest manifest,
        DecisionBudget budget,
        DeterministicRandom random,
        CancellationToken cancellationToken);
}
