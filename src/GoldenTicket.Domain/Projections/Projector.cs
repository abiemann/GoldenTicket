using System.Collections.Immutable;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Domain.Projections;

/// <summary>
/// Builds the public and per-seat projections. DESIGN 5.2 defines what each projection may contain;
/// building them here, from different data, is what keeps a secret out of a public payload rather
/// than relying on the interface to hide it.
/// </summary>
public static class Projector
{
    public static PublicView ProjectPublic(GameState state)
    {
        var seats = state.Seats
            .Select(seat => new PublicSeatSummary(
                seat.SeatId,
                seat.DisplayName,
                seat.Color,
                seat.Symbol,
                seat.Kind,
                state.TrainStock[seat.SeatId],
                state.RouteScore[seat.SeatId],
                state.HandOf(seat.SeatId).Count,
                state.TicketsOf(seat.SeatId).Count,
                state.SetupOffers.TryGetValue(seat.SeatId, out var setupOffer)
                    ? setupOffer.Length
                    : state.CurrentTicketOffer is { } offer && offer.SeatId == seat.SeatId
                        ? offer.Offered.Length
                        : 0,
                [.. state.RoutesOwnedBy(seat.SeatId)]))
            .ToImmutableArray();

        var faceUp = state.FaceUp
            .Select(card => card is { } value ? state.Catalog.KindOf(value) : (TrainCardKind?)null)
            .ToImmutableArray();

        var pending = state.PendingClaim is { } claim
            ? new PublicPendingClaim(
                claim.OperationId,
                claim.SeatId,
                claim.RouteId,
                state.Manifest.Route(claim.RouteId).Length,
                state.TurnPhase == TurnPhase.RestoreBeforeState)
            : null;

        var finalRound = state.FinalRound is { } round
            ? new FinalRoundSummary(round.TriggeringSeatId, round.RemainingTurnsBySeat)
            : null;

        return new PublicView(
            state.SessionId,
            state.StateVersion,
            state.BoardRevision,
            state.Lifecycle,
            state.TurnPhase,
            state.CurrentTurnAction,
            state.VerificationMode,
            state.ActiveSeatId,
            state.TurnNumber,
            seats,
            faceUp,
            state.TrainDeck.Count,
            state.TrainDiscard.Count,
            state.TicketDeck.Count,
            state.RouteOwners.ToImmutableDictionary(),
            pending,
            finalRound,
            state.FinalResult,
            state.RulesDecision,
            state.Checkpoint is { } checkpoint
                ? new PublicCheckpoint(
                    checkpoint.CheckpointId,
                    checkpoint.Name,
                    checkpoint.CreatedAt,
                    checkpoint.Status,
                    checkpoint.TargetProvenance,
                    checkpoint.SuspendedTurnPhase,
                    checkpoint.PendingOperationId is not null,
                    checkpoint.PhysicalTargetHash,
                    checkpoint.PhysicalTarget)
                : null,
            state.RebuildAttested,
            state.CheckpointFault);
    }

    public static SeatView ProjectSeat(GameState state, SeatId seat) =>
        ProjectSeat(state, seat, ProjectPublic(state));

    /// <summary>Reuses an already built public view when several seat views are projected together.</summary>
    public static SeatView ProjectSeat(GameState state, SeatId seat, PublicView publicView)
    {
        var hand = state.HandOf(seat)
            .Select(card => new HeldCard(card, state.Catalog.KindOf(card)))
            .ToImmutableArray();

        var offer = state.CurrentTicketOffer is { } current && current.SeatId == seat ? current : null;
        var setupOffer = state.SetupOffers.TryGetValue(seat, out var offered) ? offered : [];

        return new SeatView(
            publicView,
            seat,
            hand,
            state.ReservedCardsOf(seat),
            [.. state.TicketsOf(seat)],
            offer,
            setupOffer);
    }
}
