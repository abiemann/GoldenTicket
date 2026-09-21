using System.Collections.Immutable;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.Domain.Model;

/// <summary>
/// The referee's complete view of one match (DESIGN 7.1). Nothing outside the domain assembly may
/// mutate it: every change arrives as a durable event applied by <see cref="Engine.GameReducer"/>,
/// which is what makes replay reach the same state and hashes (invariant 12).
/// </summary>
public sealed class GameState
{
    internal readonly Dictionary<RouteId, SeatId> RouteOwnersInternal = [];
    internal readonly Dictionary<SeatId, int> TrainStockInternal = [];
    internal readonly Dictionary<SeatId, int> RouteScoreInternal = [];
    internal readonly List<CardId> TrainDeckInternal = [];
    internal readonly List<CardId> TrainDiscardInternal = [];
    internal readonly List<CardId?> FaceUpInternal = [];
    internal readonly Dictionary<SeatId, List<CardId>> TrainHandsInternal = [];
    internal readonly List<TicketId> TicketDeckInternal = [];
    internal readonly Dictionary<SeatId, List<TicketId>> TicketHandsInternal = [];
    internal readonly Dictionary<SeatId, ImmutableArray<TicketId>> SetupOffersInternal = [];

    /// <summary>
    /// Tickets returned during setup, held aside until every seat has chosen. DESIGN 6.4 collects
    /// all initial offers before recycling returns; they are a real location for conservation.
    /// </summary>
    internal readonly Dictionary<SeatId, ImmutableArray<TicketId>> PendingTicketReturnsInternal = [];

    public GameState(
        SessionId sessionId,
        BoardManifest manifest,
        CardCatalog catalog,
        ImmutableArray<Seat> seats,
        VerificationMode verificationMode)
    {
        SessionId = sessionId;
        Manifest = manifest;
        Catalog = catalog;
        Seats = seats;
        VerificationMode = verificationMode;

        foreach (var seat in seats)
        {
            TrainStockInternal[seat.SeatId] = manifest.RulesConstants.StartingTrainsPerSeat;
            RouteScoreInternal[seat.SeatId] = 0;
            TrainHandsInternal[seat.SeatId] = [];
            TicketHandsInternal[seat.SeatId] = [];
        }

        for (var i = 0; i < manifest.RulesConstants.FaceUpMarketSize; i++)
            FaceUpInternal.Add(null);

        // Expose live views without exposing the mutable collections behind them. An
        // IReadOnlyList/Dictionary interface alone would still allow callers to cast back.
        RouteOwners = new ReadOnlyDictionaryView<RouteId, SeatId>(RouteOwnersInternal);
        TrainStock = new ReadOnlyDictionaryView<SeatId, int>(TrainStockInternal);
        RouteScore = new ReadOnlyDictionaryView<SeatId, int>(RouteScoreInternal);
        TrainDeck = new ReadOnlyListView<CardId>(TrainDeckInternal);
        TrainDiscard = new ReadOnlyListView<CardId>(TrainDiscardInternal);
        FaceUp = new ReadOnlyListView<CardId?>(FaceUpInternal);
        TicketDeck = new ReadOnlyListView<TicketId>(TicketDeckInternal);
        SetupOffers = new ReadOnlyDictionaryView<SeatId, ImmutableArray<TicketId>>(SetupOffersInternal);
        PendingTicketReturns = new ReadOnlyDictionaryView<SeatId, ImmutableArray<TicketId>>(PendingTicketReturnsInternal);
    }

    // ---- Identity and compatibility -------------------------------------------------------

    public SessionId SessionId { get; }

    /// <summary>The reviewed data package this match is pinned to. Never part of the saved state.</summary>
    public BoardManifest Manifest { get; }

    public CardCatalog Catalog { get; }

    public ImmutableArray<Seat> Seats { get; }

    // ---- Versions (DESIGN 7.1) ------------------------------------------------------------

    /// <summary>Advances on every authoritative transaction, including pending-operation changes.</summary>
    public long StateVersion { get; internal set; }

    /// <summary>Orders durable events.</summary>
    public long JournalSequence { get; internal set; }

    /// <summary>Advances when expected physical ownership changes.</summary>
    public long BoardRevision { get; internal set; }

    // ---- Gates and phase ------------------------------------------------------------------

    public SessionLifecycle Lifecycle { get; internal set; } = SessionLifecycle.Setup;

    public VerificationMode VerificationMode { get; internal set; }

    public TurnPhase TurnPhase { get; internal set; } = TurnPhase.SetupTicketSelection;

    /// <summary>The action family chosen for the current turn, once the seat has committed to one.</summary>
    public TurnAction CurrentTurnAction { get; internal set; } = TurnAction.None;

    /// <summary>How many train cards the active seat has taken so far this turn (0, 1 or 2).</summary>
    public int TrainCardsTakenThisTurn { get; internal set; }

    public int ActiveSeatIndex { get; internal set; }

    public int TurnNumber { get; internal set; }

    public Seat ActiveSeat => Seats[ActiveSeatIndex];

    public SeatId ActiveSeatId => ActiveSeat.SeatId;

    /// <summary>Set when the profile cannot resolve a supply state (DESIGN 6.4).</summary>
    public RulesDecision? RulesDecision { get; internal set; }

    /// <summary>
    /// Continuation policies an operator has accepted for this match, by code (DESIGN 6.4). Once a
    /// policy is accepted the same position applies it instead of stopping play again, and the
    /// acceptance is in the journal with the policy version it was taken under.
    /// </summary>
    public ImmutableDictionary<string, string> AcceptedRulesPolicies { get; internal set; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// How many seats have passed in a row because they had no legal action. A full round of them
    /// means nothing further can happen, which is the only way the pass policy can terminate.
    /// </summary>
    public int ConsecutivePasses { get; internal set; }

    // ---- Board ----------------------------------------------------------------------------

    public IReadOnlyDictionary<RouteId, SeatId> RouteOwners { get; }

    public IReadOnlyDictionary<SeatId, int> TrainStock { get; }

    public IReadOnlyDictionary<SeatId, int> RouteScore { get; }

    // ---- Supply ---------------------------------------------------------------------------

    /// <summary>Face-down train deck; index 0 is the top card.</summary>
    public IReadOnlyList<CardId> TrainDeck { get; }

    public IReadOnlyList<CardId> TrainDiscard { get; }

    /// <summary>The face-up market. A null slot means the supply could not fill it.</summary>
    public IReadOnlyList<CardId?> FaceUp { get; }

    public IReadOnlyList<TicketId> TicketDeck { get; }

    // ---- Private holdings -----------------------------------------------------------------

    public IReadOnlyList<CardId> HandOf(SeatId seat) => new ReadOnlyListView<CardId>(TrainHandsInternal[seat]);

    public IReadOnlyList<TicketId> TicketsOf(SeatId seat) => new ReadOnlyListView<TicketId>(TicketHandsInternal[seat]);

    public IReadOnlyDictionary<SeatId, ImmutableArray<TicketId>> SetupOffers { get; }

    public IReadOnlyDictionary<SeatId, ImmutableArray<TicketId>> PendingTicketReturns { get; }

    public TicketOffer? CurrentTicketOffer { get; internal set; }

    // ---- Operations -----------------------------------------------------------------------

    public PendingClaim? PendingClaim { get; internal set; }

    public FinalRound? FinalRound { get; internal set; }

    public RandomState RandomState { get; internal set; }

    public FinalResult? FinalResult { get; internal set; }

    // ---- Pack away and rebuild (DESIGN 7.1, 19.8) -------------------------------------------

    /// <summary>
    /// The checkpoint this session is currently packed against, while the lifecycle is
    /// <see cref="SessionLifecycle.PackedAway"/> or <see cref="SessionLifecycle.Rebuilding"/>. It
    /// carries the frozen source version, the suspended turn phase and the physical target, so the
    /// session does not keep a second mutable copy of any of them. Resuming clears this reference;
    /// the durable checkpoint record itself is retained (DESIGN 19.7).
    /// </summary>
    public PackAwayCheckpoint? Checkpoint { get; internal set; }

    /// <summary>
    /// The save currently being captured. DESIGN 19.8 step 1: a client retry resolves the same
    /// request instead of opening a second save operation, and a restart mid-capture finds the same
    /// identity, name and suspended phase waiting.
    /// </summary>
    public PackAwayRequest? PackAwayRequest { get; internal set; }

    /// <summary>
    /// True once the operator has attested that the rebuilt board matches the checkpoint's target.
    /// DESIGN 19.8 requires a fresh check at the moment Resume is pressed, so this is cleared by any
    /// event that could invalidate it.
    /// </summary>
    public bool RebuildAttested { get; internal set; }

    /// <summary>Set when a committed checkpoint failed readback (DESIGN 19.8 step 6).</summary>
    public string? CheckpointFault { get; internal set; }

    /// <summary>
    /// DESIGN 9.2: these lifecycles disable gameplay commands, AI submissions and ordinary move
    /// inference, allowing only the appropriate save, rebuild and recovery controls.
    /// </summary>
    public bool IsGameplaySuspended =>
        Lifecycle is SessionLifecycle.PreparingPackAway
            or SessionLifecycle.PackedAway
            or SessionLifecycle.Rebuilding;

    // ---- Derived helpers ------------------------------------------------------------------

    public Seat SeatOf(SeatId id) => Seats.First(s => s.SeatId == id);

    public int SeatIndexOf(SeatId id) => Seats.IndexOf(Seats.First(s => s.SeatId == id));

    public IEnumerable<RouteId> RoutesOwnedBy(SeatId seat) =>
        RouteOwnersInternal.Where(pair => pair.Value == seat).Select(pair => pair.Key);

    /// <summary>Cards in the seat's hand that a pending claim has reserved and may not re-spend.</summary>
    public ImmutableArray<CardId> ReservedCardsOf(SeatId seat) =>
        PendingClaim is { } claim && claim.SeatId == seat ? claim.ReservedCards : [];

    /// <summary>Cards the seat may still spend on a new action.</summary>
    public IEnumerable<CardId> AvailableCardsOf(SeatId seat)
    {
        var reserved = ReservedCardsOf(seat);
        return reserved.IsEmpty
            ? HandOf(seat)
            : TrainHandsInternal[seat].Where(card => !reserved.Contains(card));
    }

    /// <summary>
    /// DESIGN 6.1: no player may own both lanes of a parallel route, and with few enough players
    /// either claim closes its twin entirely.
    /// </summary>
    public bool IsParallelLaneBlockedFor(RouteDefinition route, SeatId seat)
    {
        if (route.ParallelGroupId is null) return false;

        var closesTwin = Seats.Length <= Manifest.RulesConstants.ParallelRouteClosedAtOrBelowPlayers;

        foreach (var sibling in Manifest.SiblingLanesOf(route))
        {
            if (!RouteOwnersInternal.TryGetValue(sibling.RouteId, out var owner)) continue;
            if (closesTwin || owner == seat) return true;
        }

        return false;
    }

    public int TotalTrackLengthOwnedBy(SeatId seat) =>
        RoutesOwnedBy(seat).Sum(routeId => Manifest.Route(routeId).Length);

    /// <summary>
    /// A detached copy. The rules engine evaluates a candidate transaction against one so each event
    /// it emits is visible to the next decision without touching authoritative state, and the
    /// coordinator applies a committed transaction to one so a failed write leaves the live state
    /// untouched.
    /// </summary>
    public GameState Fork()
    {
        var copy = new GameState(SessionId, Manifest, Catalog, Seats, VerificationMode)
        {
            StateVersion = StateVersion,
            JournalSequence = JournalSequence,
            BoardRevision = BoardRevision,
            Lifecycle = Lifecycle,
            TurnPhase = TurnPhase,
            CurrentTurnAction = CurrentTurnAction,
            TrainCardsTakenThisTurn = TrainCardsTakenThisTurn,
            ActiveSeatIndex = ActiveSeatIndex,
            TurnNumber = TurnNumber,
            RulesDecision = RulesDecision,
            AcceptedRulesPolicies = AcceptedRulesPolicies,
            ConsecutivePasses = ConsecutivePasses,
            CurrentTicketOffer = CurrentTicketOffer,
            PendingClaim = PendingClaim,
            FinalRound = FinalRound,
            RandomState = RandomState,
            FinalResult = FinalResult,
            Checkpoint = Checkpoint,
            PackAwayRequest = PackAwayRequest,
            RebuildAttested = RebuildAttested,
            CheckpointFault = CheckpointFault,
        };

        foreach (var (routeId, seatId) in RouteOwnersInternal) copy.RouteOwnersInternal[routeId] = seatId;
        foreach (var (seatId, stock) in TrainStockInternal) copy.TrainStockInternal[seatId] = stock;
        foreach (var (seatId, score) in RouteScoreInternal) copy.RouteScoreInternal[seatId] = score;
        foreach (var (seatId, hand) in TrainHandsInternal) copy.TrainHandsInternal[seatId] = [.. hand];
        foreach (var (seatId, tickets) in TicketHandsInternal) copy.TicketHandsInternal[seatId] = [.. tickets];
        foreach (var (seatId, offer) in SetupOffersInternal) copy.SetupOffersInternal[seatId] = offer;

        copy.TrainDeckInternal.AddRange(TrainDeckInternal);
        copy.TrainDiscardInternal.AddRange(TrainDiscardInternal);
        copy.TicketDeckInternal.AddRange(TicketDeckInternal);
        foreach (var (seatId, returns) in PendingTicketReturnsInternal)
            copy.PendingTicketReturnsInternal[seatId] = returns;

        copy.FaceUpInternal.Clear();
        copy.FaceUpInternal.AddRange(FaceUpInternal);

        return copy;
    }
}

/// <summary>
/// A supply state the pinned profile does not resolve (DESIGN 6.4). The match is preserved and the
/// exact cause is reported; no undocumented substitute rule is applied.
/// </summary>
public sealed record RulesDecision(string Code, string Explanation)
{
    /// <summary>
    /// Derived during journal replay from the phase immediately before RulesDecisionRaised. This
    /// distinguishes a completed face-up locomotive draw from a first ordinary/blind card without
    /// changing persisted event formats or hashes of existing paused matches. Restore replays the
    /// journal and never deserializes this continuation from a cached snapshot.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TurnPhase? InterruptedTurnPhase { get; init; }
}
