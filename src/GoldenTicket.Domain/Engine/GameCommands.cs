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
/// Keeps a subset of an open ticket offer. <paramref name="ReturnOrder"/> preserves an explicitly
/// supplied order for rejections (DESIGN 6.4); empty means "the order they were offered".
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
/// Submits physical evidence for the pending claim. Manual attestation and automatic camera
/// recognition have distinct durable provenance; both use the same claim transaction and replay.
/// </summary>
public sealed record SubmitClaimEvidence(
    CommandEnvelope Envelope,
    OperationId OperationId,
    EvidenceKind Evidence,
    string Operator,
    string Reason,
    bool RequireScoreMarkerConfirmation = false) : GameCommand(Envelope);

/// <summary>Records a fresh camera check of the score marker required by a committed claim.</summary>
public sealed record ConfirmScoreMarkerMove(
    CommandEnvelope Envelope,
    OperationId OperationId,
    string Detector,
    string EvidenceSummary) : GameCommand(Envelope);

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

// ---- Save, pack away and rebuild (DESIGN 19.8) -------------------------------------------

/// <summary>
/// Suspends play and captures one consistent checkpoint. DESIGN 19.8: this is a coordinator
/// operation around the current foreground action, not another gameplay action, so it may suspend a
/// partial draw, an open ticket offer or an authorised placement without forcing the turn to finish.
/// </summary>
public sealed record SaveAndPackAway(CommandEnvelope Envelope, string Name) : GameCommand(Envelope);

/// <summary>
/// Abandons a save that has not yet produced a checkpoint. DESIGN 19.8: returning to the suspended
/// operation requires fresh board reconciliation, which the operator confirms.
/// </summary>
public sealed record CancelPackAwayPreparation(
    CommandEnvelope Envelope,
    string Reason) : GameCommand(Envelope);

/// <summary>
/// Writes the checkpoint for an in-flight save request (DESIGN 19.8 step 6, first transaction). It
/// is a separate durable boundary from the request so a crash between them leaves the match paused
/// with no safe-to-pack result issued.
/// </summary>
public sealed record CommitPackAwayCheckpoint(
    CommandEnvelope Envelope,
    CheckpointId CheckpointId) : GameCommand(Envelope);

/// <summary>
/// Records the outcome of reading the committed checkpoint back (DESIGN 19.8 step 6). The
/// coordinator performs the read; the rules engine only records what it found, so the engine stays
/// free of storage concerns.
/// </summary>
public sealed record RecordCheckpointReadback(
    CommandEnvelope Envelope,
    CheckpointId CheckpointId,
    bool Succeeded,
    string? FailureReason) : GameCommand(Envelope);

/// <summary>Starts guided reconstruction against a verified checkpoint's immutable target.</summary>
public sealed record BeginBoardRebuild(
    CommandEnvelope Envelope,
    CheckpointId CheckpointId) : GameCommand(Envelope);

/// <summary>
/// The operator's attestation that the rebuilt board matches the whole saved target. The target hash
/// is echoed back so an attestation cannot be applied to a different checkpoint.
/// </summary>
public sealed record AttestBoardRebuild(
    CommandEnvelope Envelope,
    CheckpointId CheckpointId,
    string PhysicalTargetHash,
    string Operator) : GameCommand(Envelope);

/// <summary>
/// Returns to the saved operation exactly once. DESIGN 19.8: resume does not itself commit a pending
/// route; the ordinary protocol runs again afterwards.
/// </summary>
public sealed record ResumePackedGame(
    CommandEnvelope Envelope,
    CheckpointId CheckpointId) : GameCommand(Envelope);

/// <summary>
/// Accepts the reviewed continuation policy for the paused supply state (DESIGN 6.4). The policy id
/// is echoed back so an operator cannot accept a policy they were not shown.
/// </summary>
public sealed record ResolveRulesDecision(
    CommandEnvelope Envelope,
    string Code,
    string PolicyId,
    string Operator) : GameCommand(Envelope);

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
