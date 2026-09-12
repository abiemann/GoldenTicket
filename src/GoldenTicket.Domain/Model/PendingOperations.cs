using System.Collections.Immutable;

namespace GoldenTicket.Domain.Model;

/// <summary>
/// A route claim whose payment is reserved but not yet spent (DESIGN 7.1, 8.3). The reserved cards
/// stay in the owning hand with a reservation flag until commit, so card conservation still treats
/// them as one location.
/// </summary>
public sealed record PendingClaim(
    OperationId OperationId,
    SeatId SeatId,
    RouteId RouteId,
    ImmutableArray<CardId> ReservedCards,
    long BaseBoardRevision,
    long CreatedAtStateVersion)
{
    public int TrainsRequired => ReservedCards.Length;
}

/// <summary>
/// Tickets currently on offer to one seat. The exact offered instances persist across crashes,
/// privacy handoffs and pauses (DESIGN 8.5); they are never redrawn.
/// </summary>
public sealed record TicketOffer(
    SeatId SeatId,
    ImmutableArray<TicketId> Offered,
    int MinimumKeep,
    bool IsSetupOffer);

/// <summary>
/// DESIGN 6.1: after a seat finishes a turn with at most the threshold number of trains, every
/// seat including the trigger takes one more turn. DESIGN 9.3: this bookkeeping is attached to
/// completed domain turns, never to camera frames, so recovery cannot consume a final turn twice.
/// </summary>
public sealed record FinalRound(
    SeatId TriggeringSeatId,
    int TriggeringTurnNumber,
    ImmutableDictionary<SeatId, int> RemainingTurnsBySeat)
{
    public bool IsExhausted => RemainingTurnsBySeat.Values.All(remaining => remaining <= 0);
}
