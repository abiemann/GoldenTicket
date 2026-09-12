namespace GoldenTicket.Domain;

/// <summary>
/// Train-card and printed-route colours. DESIGN 6.2: these are card/route colours and are
/// deliberately a different type from <see cref="PlayerColor"/>; a blue player's trains can
/// occupy routes of several printed colours.
/// </summary>
public enum TrainCardKind
{
    Pink = 0,
    White = 1,
    Blue = 2,
    Yellow = 3,
    Orange = 4,
    Black = 5,
    Red = 6,
    Green = 7,

    /// <summary>The wild card. Substitutes for any colour when paying for a route.</summary>
    Locomotive = 8,
}

/// <summary>Physical plastic train colour belonging to a seat.</summary>
public enum PlayerColor
{
    Blue = 0,
    Red = 1,
    Green = 2,
    Yellow = 3,
    Black = 4,
}

/// <summary>Who operates a seat.</summary>
public enum SeatKind
{
    Human = 0,
    Computer = 1,
}

/// <summary>DESIGN 15.3 difficulty levels. Changes computation only, never information access.</summary>
public enum AiDifficulty
{
    Relaxed = 0,
    Standard = 1,
    Challenging = 2,
}

/// <summary>Session lifecycle gate (DESIGN 9.2), limited to the states this build implements.</summary>
public enum SessionLifecycle
{
    Setup = 0,
    Active = 1,
    Finished = 2,

    /// <summary>
    /// A save is being captured. DESIGN 19.8 step 1: gameplay commands, AI submissions and move
    /// inference are rejected while the source state is frozen.
    /// </summary>
    PreparingPackAway = 3,

    /// <summary>
    /// The checkpoint is durable and the pieces may be cleared away. Removing trains after this
    /// point cannot become a game change, even after a crash (DESIGN 19.8 step 7).
    /// </summary>
    PackedAway = 4,

    /// <summary>
    /// The board is being rebuilt against the checkpoint's immutable target. Placing trains here
    /// cannot enter a claim or scoring path (DESIGN 9.2).
    /// </summary>
    Rebuilding = 5,
}

/// <summary>
/// DESIGN 19.8: what the saved physical target was derived from. Only
/// <see cref="LogicalStateOnly"/> is produced in this build, because a verified board photograph
/// needs the camera milestones; the field exists so a save says honestly which one it is.
/// </summary>
public enum TargetProvenance
{
    /// <summary>Committed route ownership only. No photograph, no uncommitted physical progress.</summary>
    LogicalStateOnly = 0,

    /// <summary>A verified board photograph, optionally including a pending placement mask.</summary>
    VerifiedPhoto = 1,
}

/// <summary>
/// DESIGN 19.8 step 6: a checkpoint is committed first and validated second, so a safe-to-pack
/// result is never reported from an unvalidated write.
/// </summary>
public enum CheckpointStatus
{
    /// <summary>Durably written; readback has not yet proved it can be restored.</summary>
    CommittedAwaitingReadback = 0,

    /// <summary>Read back and validated. Only now may the game be reported safe to pack away.</summary>
    Verified = 1,

    /// <summary>Readback failed. The match stays packed and faulted until it is resolved.</summary>
    Faulted = 2,
}

/// <summary>
/// The foreground game operation (DESIGN 9.1), limited to the states this build implements.
/// Camera-specific substates arrive with the vision milestones.
/// </summary>
public enum TurnPhase
{
    /// <summary>Every seat is still choosing which of its dealt tickets to keep.</summary>
    SetupTicketSelection = 0,

    /// <summary>The active seat has not yet chosen an action family this turn.</summary>
    TurnStart = 1,

    /// <summary>
    /// One train card has been taken and its identity is already known to the acting seat.
    /// DESIGN 6.2: this intermediate result is durable before the next choice is offered.
    /// </summary>
    AwaitingSecondTrainCard = 2,

    /// <summary>A ticket offer is on the table and the acting seat must keep at least the minimum.</summary>
    AwaitingTicketKeep = 3,

    /// <summary>
    /// A claim is planned and its payment reserved; the operator must place the physical trains
    /// and the placement must be verified before anything is spent or scored.
    /// </summary>
    AwaitingPhysicalPlacement = 4,

    /// <summary>A pending claim is being cancelled and the board must be restored first (DESIGN 8.4).</summary>
    RestoreBeforeState = 5,

    /// <summary>All scheduled turns are done; exact final scoring runs next.</summary>
    FinalScoring = 6,

    /// <summary>The match is over and the result is durable.</summary>
    Finished = 7,

    /// <summary>
    /// DESIGN 6.4: a supply state the pinned profile does not resolve. The match is preserved and
    /// the exact cause is shown rather than silently inventing a different rule or hanging.
    /// </summary>
    RulesDecisionRequired = 8,
}

/// <summary>The action family chosen for a turn.</summary>
public enum TurnAction
{
    None = 0,
    DrawTrainCards = 1,
    ClaimRoute = 2,
    DrawTickets = 3,
}

/// <summary>DESIGN 12.7 / 21.4: how a physical placement was established.</summary>
public enum EvidenceKind
{
    /// <summary>An operator attested to the whole board explicitly. Not counted as a vision success.</summary>
    ManualAttestation = 0,

    /// <summary>Camera evidence accepted automatically. Reserved for the vision milestones.</summary>
    CameraAutomatic = 1,
}

/// <summary>DESIGN 9.2: whether physical truth comes from the camera or an operator attestation.</summary>
public enum VerificationMode
{
    /// <summary>Operator confirms each placement explicitly. Camera verification is unavailable.</summary>
    Manual = 0,

    /// <summary>Camera evidence gates the commit. Reserved for the vision milestones.</summary>
    CameraVerified = 1,
}

/// <summary>
/// DESIGN 5.2 / 8.2: every durable event carries a visibility classification so public
/// projections and narration cannot accidentally carry a secret.
/// </summary>
public enum EventVisibility
{
    /// <summary>Safe for the public table screen, the event history and narration.</summary>
    Public = 0,

    /// <summary>Contains information only the owning seat may see.</summary>
    Private = 1,

    /// <summary>Referee-only: deck order, random state, or full supply detail.</summary>
    Referee = 2,
}
