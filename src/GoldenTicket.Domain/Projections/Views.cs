using System.Collections.Immutable;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Domain.Projections;

/// <summary>Publicly permitted facts about one seat (DESIGN 5.2).</summary>
public sealed record PublicSeatSummary(
    SeatId SeatId,
    string DisplayName,
    PlayerColor Color,
    string Symbol,
    SeatKind Kind,
    int TrainsRemaining,
    int RouteScore,
    int TrainCardCount,
    int TicketCount,
    ImmutableArray<RouteId> ClaimedRoutes);

/// <summary>The seat and route of a pending claim. The reserved cards are deliberately absent.</summary>
public sealed record PublicPendingClaim(
    OperationId OperationId,
    SeatId SeatId,
    RouteId RouteId,
    int TrainCount,
    bool AwaitingRestore);

public sealed record FinalRoundSummary(
    SeatId TriggeringSeatId,
    ImmutableDictionary<SeatId, int> RemainingTurnsBySeat);

/// <summary>
/// Everything the public table screen and narration may use. DESIGN 5.2: blind-card identities,
/// unplayed tickets, ordered future cards and private previews must not appear here, and DESIGN 4.1
/// requires this to be built from a different projection rather than hidden with zero opacity.
/// </summary>
public sealed record PublicView(
    SessionId SessionId,
    long StateVersion,
    long BoardRevision,
    SessionLifecycle Lifecycle,
    TurnPhase TurnPhase,
    TurnAction CurrentTurnAction,
    VerificationMode VerificationMode,
    SeatId ActiveSeatId,
    int TurnNumber,
    ImmutableArray<PublicSeatSummary> Seats,
    ImmutableArray<TrainCardKind?> FaceUp,
    int TrainDeckCount,
    int TrainDiscardCount,
    int TicketDeckCount,
    ImmutableDictionary<RouteId, SeatId> RouteOwners,
    PublicPendingClaim? PendingClaim,
    FinalRoundSummary? FinalRound,
    FinalResult? FinalResult,
    RulesDecision? RulesDecision,
    PublicCheckpoint? Checkpoint,
    bool RebuildAttested,
    string? CheckpointFault)
{
    public PublicSeatSummary SeatOf(SeatId id) => Seats.First(s => s.SeatId == id);

    /// <summary>DESIGN 9.2: gameplay commands and AI submissions are refused in these lifecycles.</summary>
    public bool IsGameplaySuspended =>
        Lifecycle is SessionLifecycle.PreparingPackAway
            or SessionLifecycle.PackedAway
            or SessionLifecycle.Rebuilding;
}

/// <summary>
/// The public face of a saved checkpoint (DESIGN 19.8). The saved board arrangement is public
/// information - it is what the rebuild instructions show - so the target travels with it. Hands,
/// tickets, deck order and the logical-state fingerprint deliberately do not.
/// </summary>
public sealed record PublicCheckpoint(
    CheckpointId CheckpointId,
    string Name,
    DateTimeOffset CreatedAt,
    CheckpointStatus Status,
    TargetProvenance Provenance,
    TurnPhase SuspendedTurnPhase,
    bool HasPendingOperation,
    string PhysicalTargetHash,
    ImmutableArray<TargetRoute> PhysicalTarget)
{
    public int RouteCount => PhysicalTarget.Length;

    public int TotalTrainsOnBoard => PhysicalTarget.Sum(route => route.Length);

    /// <summary>Only a verified checkpoint may be reported as safe to pack away.</summary>
    public bool IsSafeToPackAway => Status == CheckpointStatus.Verified;
}

/// <summary>A card instance the owning seat can see.</summary>
public readonly record struct HeldCard(CardId Id, TrainCardKind Kind);

/// <summary>
/// DESIGN 5.3: a fresh immutable seat view is built before every decision. The AI receives this
/// value and an action interface; it has no reference to <see cref="GameState"/>, persistence, the
/// deck service, or another seat's view.
/// </summary>
public sealed record SeatView(
    PublicView Public,
    SeatId SeatId,
    ImmutableArray<HeldCard> Hand,
    ImmutableArray<CardId> ReservedCards,
    ImmutableArray<TicketId> Tickets,
    TicketOffer? Offer,
    ImmutableArray<TicketId> SetupOffer)
{
    /// <summary>Cards not reserved by a pending claim, which a new action may spend.</summary>
    public IEnumerable<HeldCard> Available =>
        ReservedCards.IsEmpty ? Hand : Hand.Where(card => !ReservedCards.Contains(card.Id));

    public int CountOf(TrainCardKind kind) => Available.Count(card => card.Kind == kind);

    public bool IsActive => Public.ActiveSeatId == SeatId;

    public int TrainsRemaining => Public.SeatOf(SeatId).TrainsRemaining;
}
