using System.Collections.Immutable;
using GoldenTicket.Domain.Events;

namespace GoldenTicket.Domain.Engine;

/// <summary>
/// DESIGN 8.1: every mutating command carries the session, its own id, the acting seat where
/// applicable, and the state version the caller believed it was acting on. The command id is what
/// makes a repeated click, a retry or a post-crash resend return the previous outcome.
/// </summary>
public sealed record CommandEnvelope(
    SessionId SessionId,
    CommandId CommandId,
    long ExpectedStateVersion,
    SeatId? ActorSeatId = null);

public abstract record GameCommand(CommandEnvelope Envelope);

/// <summary>
/// Keeps a subset of an open ticket offer. <paramref name="ReturnOrder"/> preserves the order the
/// acting seat chose for its rejections (DESIGN 6.4); empty means "the order they were offered".
/// </summary>
public sealed record CommitTicketSelection(
    CommandEnvelope Envelope,
    ImmutableArray<TicketId> Kept,
    ImmutableArray<TicketId> ReturnOrder) : GameCommand(Envelope);

/// <summary>Takes one train card. <paramref name="Slot"/> is null for a blind draw.</summary>
public sealed record SelectTrainCard(CommandEnvelope Envelope, int? Slot) : GameCommand(Envelope);

/// <summary>Opens a destination-ticket offer for the active seat.</summary>
public sealed record RequestTicketOffer(CommandEnvelope Envelope) : GameCommand(Envelope);

/// <summary>
/// Reserves a route and its payment. DESIGN 4.3: confirming the payment creates a pending claim and
/// reserves the resources; it does not yet deduct them or award points.
/// </summary>
public sealed record PlanClaim(
    CommandEnvelope Envelope,
    RouteId RouteId,
    ImmutableArray<CardId> Payment) : GameCommand(Envelope);

/// <summary>
/// Submits physical evidence for the pending claim. This build ships the manual-attestation path
/// (DESIGN 23.1); camera evidence replaces only this source later, leaving the command pipeline,
/// transaction semantics and replay result unchanged.
/// </summary>
public sealed record SubmitClaimEvidence(
    CommandEnvelope Envelope,
    OperationId OperationId,
    EvidenceKind Evidence,
    string Operator,
    string Reason) : GameCommand(Envelope);

/// <summary>
/// DESIGN 8.4: requests cancellation. If trains were already placed, the operator must remove them
/// before the reservation is released.
/// </summary>
public sealed record CancelPendingClaim(
    CommandEnvelope Envelope,
    OperationId OperationId,
    bool TrainsWerePlaced) : GameCommand(Envelope);

/// <summary>Confirms the board is back to its before-state so a cancellation can finish.</summary>
public sealed record ConfirmBeforeStateRestored(
    CommandEnvelope Envelope,
    OperationId OperationId) : GameCommand(Envelope);

/// <summary>Why a command was refused. Codes are stable; messages are for people.</summary>
public sealed record CommandRejection(string Code, string Message);

/// <summary>
/// DESIGN 18.2: a transition contains the domain effects and the journal events to commit together.
/// </summary>
public sealed record Transition(ImmutableArray<GameEvent> Events);

/// <summary>Either an accepted transition or an explained refusal. Never a bare boolean.</summary>
public sealed record CommandResult(Transition? Transition, CommandRejection? Rejection)
{
    public bool IsAccepted => Transition is not null;

    public static CommandResult Accept(IEnumerable<GameEvent> events) =>
        new(new Transition([.. events]), null);

    public static CommandResult Reject(string code, string message) =>
        new(null, new CommandRejection(code, message));
}
