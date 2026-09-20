using System.Collections.Immutable;
using System.Text.Json.Serialization;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Domain.Events;

/// <summary>
/// A line of the public table history. DESIGN 5.2: this is an allowlisted public fact, built from
/// the event's non-secret fields only, never from a raw serialisation of the event.
/// </summary>
public sealed record PublicEventEntry(string Kind, SeatId? Seat, string Text);

/// <summary>
/// One durable, typed, versioned domain event (DESIGN 8.2). Events carry explicit outcomes,
/// including the results of every random choice, so <see cref="Engine.GameReducer"/> can replay a
/// journal without re-running randomness or vision (invariant 12).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionCreated), nameof(SessionCreated))]
[JsonDerivedType(typeof(InitialHandsDealt), nameof(InitialHandsDealt))]
[JsonDerivedType(typeof(SetupOfferCreated), nameof(SetupOfferCreated))]
[JsonDerivedType(typeof(TicketSelectionCommitted), nameof(TicketSelectionCommitted))]
[JsonDerivedType(typeof(SetupReturnsRecycled), nameof(SetupReturnsRecycled))]
[JsonDerivedType(typeof(SetupCompleted), nameof(SetupCompleted))]
[JsonDerivedType(typeof(TurnStarted), nameof(TurnStarted))]
[JsonDerivedType(typeof(FaceUpCardTaken), nameof(FaceUpCardTaken))]
[JsonDerivedType(typeof(BlindCardDrawn), nameof(BlindCardDrawn))]
[JsonDerivedType(typeof(MarketRefilled), nameof(MarketRefilled))]
[JsonDerivedType(typeof(MarketReset), nameof(MarketReset))]
[JsonDerivedType(typeof(DeckReshuffled), nameof(DeckReshuffled))]
[JsonDerivedType(typeof(TicketOfferCreated), nameof(TicketOfferCreated))]
[JsonDerivedType(typeof(ClaimPlanned), nameof(ClaimPlanned))]
[JsonDerivedType(typeof(ManualVerificationRecorded), nameof(ManualVerificationRecorded))]
[JsonDerivedType(typeof(CameraVerificationRecorded), nameof(CameraVerificationRecorded))]
[JsonDerivedType(typeof(ClaimCommitted), nameof(ClaimCommitted))]
[JsonDerivedType(typeof(ClaimCancellationRequested), nameof(ClaimCancellationRequested))]
[JsonDerivedType(typeof(ClaimCancelled), nameof(ClaimCancelled))]
[JsonDerivedType(typeof(TurnCompleted), nameof(TurnCompleted))]
[JsonDerivedType(typeof(FinalRoundStarted), nameof(FinalRoundStarted))]
[JsonDerivedType(typeof(FinalScoringCompleted), nameof(FinalScoringCompleted))]
[JsonDerivedType(typeof(RulesDecisionRaised), nameof(RulesDecisionRaised))]
[JsonDerivedType(typeof(RulesDecisionResolved), nameof(RulesDecisionResolved))]
[JsonDerivedType(typeof(PackAwayRequested), nameof(PackAwayRequested))]
[JsonDerivedType(typeof(PackAwayPreparationCancelled), nameof(PackAwayPreparationCancelled))]
[JsonDerivedType(typeof(PackAwayCheckpointCommitted), nameof(PackAwayCheckpointCommitted))]
[JsonDerivedType(typeof(PackAwayCheckpointVerified), nameof(PackAwayCheckpointVerified))]
[JsonDerivedType(typeof(PackAwayCheckpointFaulted), nameof(PackAwayCheckpointFaulted))]
[JsonDerivedType(typeof(BoardRebuildStarted), nameof(BoardRebuildStarted))]
[JsonDerivedType(typeof(BoardRebuildAttested), nameof(BoardRebuildAttested))]
[JsonDerivedType(typeof(PackedGameResumed), nameof(PackedGameResumed))]
public abstract record GameEvent
{
    /// <summary>Schema version of the event contract, for controlled save upgrades.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Storage and projection classification (DESIGN 5.2, 19.2).</summary>
    [JsonIgnore]
    public abstract EventVisibility Visibility { get; }

    /// <summary>
    /// The public table-history line for this event, or null when the event has no public
    /// consequence. A private event may still return a redacted line; it must never include a card
    /// identity, ticket identity, deck order or random state.
    /// </summary>
    public virtual PublicEventEntry? ToPublicEntry(BoardManifest manifest) => null;

    protected static string RouteText(BoardManifest manifest, RouteId routeId) => manifest.Describe(routeId);
}

// ---- Setup -------------------------------------------------------------------------------

/// <summary>
/// Opens the match. Carries the shuffled deck orders and the random continuation, which is why it
/// is referee-only: DESIGN 5.4 forbids exposing anything that reconstructs future cards.
/// </summary>
public sealed record SessionCreated(
    SessionId SessionId,
    string ProfileId,
    string ManifestHash,
    int RulesPolicyVersion,
    int ShuffleVersion,
    ImmutableArray<Seat> Seats,
    SeatId StartingSeatId,
    VerificationMode VerificationMode,
    ImmutableArray<CardId> TrainDeckOrder,
    ImmutableArray<TicketId> TicketDeckOrder,
    string RandomStateAfter,
    DateTimeOffset CreatedAt) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Referee;

    public override PublicEventEntry? ToPublicEntry(BoardManifest manifest) =>
        new("SessionCreated", null, $"Match started with {Seats.Length} seats.");
}

/// <summary>The opening deal of train cards and the face-up market.</summary>
public sealed record InitialHandsDealt(
    ImmutableDictionary<SeatId, ImmutableArray<CardId>> Hands,
    ImmutableArray<CardId> FaceUp) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Referee;
}

/// <summary>One seat's opening destination-ticket offer.</summary>
public sealed record SetupOfferCreated(SeatId SeatId, ImmutableArray<TicketId> Offered) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Private;
}

/// <summary>
/// A seat's kept and returned tickets. Ticket identities are secret, so only the counts reach the
/// public history.
/// </summary>
public sealed record TicketSelectionCommitted(
    SeatId SeatId,
    ImmutableArray<TicketId> Kept,
    ImmutableArray<TicketId> Returned,
    bool IsSetupSelection) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Private;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("TicketsKept", SeatId,
            $"Kept {Kept.Length} destination ticket{(Kept.Length == 1 ? "" : "s")}" +
            $" and returned {Returned.Length}.");
}

/// <summary>
/// DESIGN 6.4: all initial offers are collected before returns are recycled, and rejected tickets
/// are appended underneath in deterministic seat order.
/// </summary>
public sealed record SetupReturnsRecycled(ImmutableArray<TicketId> AppendedInOrder) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Referee;
}

/// <summary>Setup finished; the first turn may begin.</summary>
public sealed record SetupCompleted(SeatId StartingSeatId) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("SetupCompleted", StartingSeatId, "Setup complete. First turn begins.");
}

// ---- Turn flow ---------------------------------------------------------------------------

public sealed record TurnStarted(SeatId SeatId, int TurnNumber) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("TurnStarted", SeatId, $"Turn {TurnNumber} begins.");
}

public sealed record TurnCompleted(SeatId SeatId, int TurnNumber, TurnAction Action) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("TurnCompleted", SeatId, $"Turn {TurnNumber} complete.");
}

// ---- Train cards -------------------------------------------------------------------------

/// <summary>A face-up card was taken. The identity was already public.</summary>
public sealed record FaceUpCardTaken(SeatId SeatId, int Slot, CardId Card, TrainCardKind Kind, bool EndsTurn)
    : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("FaceUpCardTaken", SeatId,
            $"Took the face-up {Kind} card{(EndsTurn && Kind == TrainCardKind.Locomotive ? " (a locomotive ends the turn)" : "")}.");
}

/// <summary>A blind draw from the deck. The identity is private to the drawing seat.</summary>
public sealed record BlindCardDrawn(SeatId SeatId, CardId Card) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Private;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("BlindCardDrawn", SeatId, "Drew a card from the deck.");
}

/// <summary>One market slot was refilled from the draw pile.</summary>
public sealed record MarketRefilled(int Slot, CardId Card, TrainCardKind Kind) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;
}

/// <summary>
/// DESIGN 6.1: a market holding at least three locomotives is discarded and refilled.
/// </summary>
public sealed record MarketReset(ImmutableArray<CardId> Discarded, int ResetIndex) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("MarketReset", null, "Three locomotives were face up, so the market was replaced.");
}

/// <summary>
/// The exhausted draw pile was rebuilt from the discards. Carries the resulting order and the
/// random continuation, so it is referee-only.
/// </summary>
public sealed record DeckReshuffled(ImmutableArray<CardId> NewOrder, string RandomStateAfter) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Referee;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("DeckReshuffled", null, "The discards were shuffled into a new draw pile.");
}

// ---- Destination tickets -----------------------------------------------------------------

public sealed record TicketOfferCreated(SeatId SeatId, ImmutableArray<TicketId> Offered, int MinimumKeep)
    : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Private;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("TicketOfferCreated", SeatId, $"Drew {Offered.Length} destination tickets.");
}

// ---- Route claims ------------------------------------------------------------------------

/// <summary>
/// A claim was planned and its payment reserved (DESIGN 8.3). Nothing is spent or scored yet. The
/// card instances are private until the claim commits, so cancelling cannot leak a hand.
/// </summary>
public sealed record ClaimPlanned(
    OperationId OperationId,
    SeatId SeatId,
    RouteId RouteId,
    int TrainCount,
    ImmutableArray<CardId> ReservedCards,
    long BaseBoardRevision,
    long CreatedAtStateVersion) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Private;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("ClaimPlanned", SeatId,
            $"Claiming {RouteText(manifest, RouteId)}; place {TrainCount} train{(TrainCount == 1 ? "" : "s")}.");
}

/// <summary>
/// DESIGN 12.7: an operator attested to the whole board explicitly. Recorded with its own evidence
/// type and never counted as an automatic recognition success.
/// </summary>
public sealed record ManualVerificationRecorded(
    OperationId OperationId,
    SeatId SeatId,
    RouteId RouteId,
    string Operator,
    string Reason,
    DateTimeOffset RecordedAt) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("ManualVerificationRecorded", SeatId,
            $"Placement on {RouteText(manifest, RouteId)} confirmed by the operator.");
}

/// <summary>
/// A camera matched the placed trains to the pending route. This is distinct from an operator's
/// whole-board attestation, and its source and evidence summary remain in the durable journal.
/// </summary>
public sealed record CameraVerificationRecorded(
    OperationId OperationId,
    SeatId SeatId,
    RouteId RouteId,
    string Detector,
    string EvidenceSummary,
    DateTimeOffset RecordedAt) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("CameraVerificationRecorded", SeatId,
            $"Placement on {RouteText(manifest, RouteId)} confirmed by the camera.");
}

/// <summary>
/// The atomic commit: the payment is spent, ownership recorded, score updated and the turn ended
/// (DESIGN 8.3). The spent cards go to the public discard pile, so their kinds are public.
/// </summary>
public sealed record ClaimCommitted(
    OperationId OperationId,
    SeatId SeatId,
    RouteId RouteId,
    ImmutableArray<CardId> SpentCards,
    ImmutableArray<TrainCardKind> SpentKinds,
    int Points,
    int TrainsRemaining,
    EvidenceKind Evidence) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("ClaimCommitted", SeatId,
            $"Claimed {RouteText(manifest, RouteId)} for {Points} point{(Points == 1 ? "" : "s")}.");
}

/// <summary>
/// DESIGN 8.4: cancellation requested. If trains were already placed the board must be restored
/// before the reservation is released.
/// </summary>
public sealed record ClaimCancellationRequested(OperationId OperationId, bool RestoreRequired) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("ClaimCancellationRequested", null,
            RestoreRequired
                ? "Claim cancelled. Remove the trains that were just placed."
                : "Claim cancelled.");
}

public sealed record ClaimCancelled(OperationId OperationId, SeatId SeatId, RouteId RouteId) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("ClaimCancelled", SeatId,
            $"The claim on {RouteText(manifest, RouteId)} was released; no cards were spent.");
}

// ---- Endgame -----------------------------------------------------------------------------

public sealed record FinalRoundStarted(
    SeatId TriggeringSeatId,
    int TriggeringTurnNumber,
    ImmutableDictionary<SeatId, int> RemainingTurnsBySeat) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("FinalRoundStarted", TriggeringSeatId, "Final round: every seat takes one more turn.");
}

public sealed record FinalScoringCompleted(FinalResult Result) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("FinalScoringCompleted", null,
            Result.SharedVictory ? "Final scoring complete: shared victory." : "Final scoring complete.");
}

/// <summary>
/// DESIGN 6.4: an operator accepted the reviewed continuation policy for a paused supply state. The
/// policy version is recorded, so a match can never silently adopt a policy revised later.
/// RestoredTurnPhase is recorded by new writers. Its absence retains the historical counter-based
/// phase restoration so already resolved saves keep the state hashes written by their original app.
/// </summary>
public sealed record RulesDecisionResolved(
    string Code,
    string PolicyId,
    int PolicyVersion,
    string Operator,
    DateTimeOffset ResolvedAt,
    TurnPhase? RestoredTurnPhase = null) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("RulesDecisionResolved", null,
            $"Resolved: {RulesContinuations.For(Code)?.Title ?? PolicyId}. Play continues.");
}

/// <summary>DESIGN 6.4: an unresolved supply state. The match pauses with its exact cause.</summary>
public sealed record RulesDecisionRaised(string Code, string Explanation) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("RulesDecisionRaised", null, $"Paused: {Explanation}");
}

// ---- Save, pack away and rebuild (DESIGN 19.8) -------------------------------------------

/// <summary>
/// Step 1: the save request is serialised on the writer queue, the suspended phase is recorded and
/// the lifecycle becomes <see cref="SessionLifecycle.PreparingPackAway"/>. Reservations and the
/// pending operation are kept exactly as they were.
/// </summary>
public sealed record PackAwayRequested(
    CommandId RequestId,
    CheckpointId CheckpointId,
    string Name,
    TurnPhase SuspendedTurnPhase,
    OperationId? PendingOperationId) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("PackAwayRequested", null,
            "Saving. Clear your hands from the board; play is paused and nothing will be spent.");
}

/// <summary>
/// The save was abandoned before a checkpoint existed. DESIGN 19.8: returning to the suspended
/// operation requires fresh board reconciliation, not an old accepted result.
/// </summary>
public sealed record PackAwayPreparationCancelled(CommandId RequestId, string Reason) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("PackAwayPreparationCancelled", null, $"Save cancelled: {Reason}. Check the board before playing on.");
}

/// <summary>
/// Step 6, first transaction: the checkpoint is durable and the lifecycle is
/// <see cref="SessionLifecycle.PackedAway"/>, but readback has not run, so this is not yet a
/// safe-to-pack result.
/// </summary>
public sealed record PackAwayCheckpointCommitted(PackAwayCheckpoint Checkpoint) : GameEvent
{
    // The checkpoint carries the logical-state fingerprint, which is derived from hands, deck order
    // and private offers, so the UI must not expose this payload. The public history line below
    // is built from allowlisted fields and is unaffected.
    public override EventVisibility Visibility => EventVisibility.Referee;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("PackAwayCheckpointCommitted", null, $"Checkpoint '{Checkpoint.Name}' written. Validating it.");
}

/// <summary>Step 6, second transaction: readback succeeded. Only now is the game safe to pack away.</summary>
public sealed record PackAwayCheckpointVerified(CheckpointId CheckpointId) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("PackAwayCheckpointVerified", null, "Saved. You can pack the game away.");
}

/// <summary>
/// Readback failed. DESIGN 19.8: the match stays packed and faulted; play does not resume and no
/// success is reported.
/// </summary>
public sealed record PackAwayCheckpointFaulted(CheckpointId CheckpointId, string Reason) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("PackAwayCheckpointFaulted", null, $"The save could not be validated: {Reason}");
}

/// <summary>Guided reconstruction began against the checkpoint's immutable target.</summary>
public sealed record BoardRebuildStarted(CheckpointId CheckpointId) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("BoardRebuildStarted", null, "Rebuilding the board from the saved position.");
}

/// <summary>
/// The operator attested that the rebuilt board matches the whole saved target. DESIGN 19.8 allows
/// this in place of camera agreement under the existing manual-verification policy.
/// </summary>
public sealed record BoardRebuildAttested(
    CheckpointId CheckpointId,
    string Operator,
    string PhysicalTargetHash,
    DateTimeOffset RecordedAt) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("BoardRebuildAttested", null, "The rebuilt board was confirmed against the saved position.");
}

/// <summary>
/// The saved operation is restored exactly once, with a new state version. DESIGN 19.8: resume does
/// not itself commit a pending route; the ordinary protocol runs again against fresh evidence.
/// </summary>
public sealed record PackedGameResumed(CheckpointId CheckpointId, TurnPhase RestoredTurnPhase) : GameEvent
{
    public override EventVisibility Visibility => EventVisibility.Public;

    public override PublicEventEntry ToPublicEntry(BoardManifest manifest) =>
        new("PackedGameResumed", null, "The saved game is back on the table.");
}
