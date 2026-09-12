using System.Collections.Immutable;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.Domain.Tests;

/// <summary>The reviewed data package, loaded once for the whole test run.</summary>
public static class TestManifest
{
    public static readonly BoardManifest Manifest = ManifestLoader.LoadClassicUs();

    public static readonly CardCatalog Catalog = CardCatalog.FromManifest(Manifest);

    public static string RepositoryRoot { get; } = FindRoot();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GoldenTicket.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The repository root was not found from the test output directory.");
    }
}

/// <summary>
/// Drives the rules engine directly, keeping the journal so every test can assert both the resulting
/// state and that replaying the journal reaches the same place (DESIGN 22.2).
/// </summary>
public sealed class RulesHarness
{
    private readonly List<JournaledEvent> _journal = [];
    private long _sequence = -1;

    private RulesHarness(GameRules rules, GameState state, Transition transition)
    {
        Rules = rules;
        State = state;
        Record(state.StateVersion, transition.Events);
    }

    public GameRules Rules { get; }

    public GameState State { get; private set; }

    public IReadOnlyList<JournaledEvent> Journal => _journal;

    public BoardManifest Manifest => TestManifest.Manifest;

    public ImmutableArray<Seat> Seats => State.Seats;

    /// <summary>Creates a table. Every seat is human unless <paramref name="computerSeats"/> says otherwise.</summary>
    public static RulesHarness Create(int seatCount = 3, ulong seed = 42, params int[] computerSeats)
    {
        var colors = Enum.GetValues<PlayerColor>();
        var seats = ImmutableArray.CreateBuilder<Seat>(seatCount);

        for (var index = 0; index < seatCount; index++)
        {
            seats.Add(new Seat(
                new SeatId(index + 1),
                $"Seat {index + 1}",
                colors[index],
                computerSeats.Contains(index + 1) ? SeatKind.Computer : SeatKind.Human,
                AiDifficulty.Standard));
        }

        var built = seats.ToImmutable();
        var rules = new GameRules(TestManifest.Manifest, TestManifest.Catalog);

        var (state, transition) = rules.CreateSession(
            new SessionSetup(SessionId.New(), built, built[0].SeatId, VerificationMode.Manual),
            DeterministicRandom.SeedFrom(seed));

        return new RulesHarness(rules, state, transition);
    }

    public CommandEnvelope Envelope(SeatId? actor = null) =>
        new(State.SessionId, CommandId.New(), State.StateVersion, actor);

    public CommandEnvelope Envelope(CommandId commandId, SeatId? actor = null) =>
        new(State.SessionId, commandId, State.StateVersion, actor);

    /// <summary>Validates and, when accepted, applies a command exactly as the coordinator would.</summary>
    public CommandResult Submit(GameCommand command)
    {
        var result = Rules.ValidateAndApply(State, command);
        if (!result.IsAccepted) return result;

        GameReducer.ApplyTransition(State, result.Transition!.Events);
        Record(State.StateVersion, result.Transition.Events);
        return result;
    }

    /// <summary>Submits and fails the test loudly if the engine refused.</summary>
    public CommandResult SubmitAccepted(GameCommand command)
    {
        var result = Submit(command);
        Assert.True(result.IsAccepted,
            $"{command.GetType().Name} was rejected: {result.Rejection?.Code} - {result.Rejection?.Message}");
        return result;
    }

    public SeatView SeatView(SeatId seat) => Projector.ProjectSeat(State, seat);

    public PublicView PublicView() => Projector.ProjectPublic(State);

    public LegalActions Legal(SeatId seat) => Rules.GetLegalActions(SeatView(seat));

    /// <summary>Every seat keeps the minimum number of opening tickets, in the order dealt.</summary>
    public void CompleteSetup()
    {
        var minimum = Manifest.RulesConstants.SetupTicketMinimumKeep;

        while (State.Lifecycle == SessionLifecycle.Setup)
        {
            var seat = State.SetupOffers.Keys.First();
            var offered = State.SetupOffers[seat];
            SubmitAccepted(new CommitTicketSelection(
                Envelope(seat), [.. offered.Take(minimum)], []));
        }
    }

    /// <summary>
    /// False once a fixture has changed state in a way no journal event describes. Replay assertions
    /// then refuse to run rather than passing vacuously.
    /// </summary>
    private bool _journalDescribesState = true;

    /// <summary>Replays the recorded journal into a fresh state (DESIGN 7.2 invariant 12).</summary>
    public GameState Replay() =>
        GameReducer.Rebuild(TestManifest.Manifest, TestManifest.Catalog, _journal);

    public void AssertReplayMatches()
    {
        if (!_journalDescribesState)
        {
            throw new InvalidOperationException(
                "This fixture changed state outside the journal, so replay equality is not meaningful here. " +
                "Whole-match replay is covered by PropertyTests.");
        }

        Assert.Equal(StateHash.Compute(State), StateHash.Compute(Replay()));
    }

    public void AssertInvariants()
    {
        var problems = InvariantChecker.Check(State);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Gives a seat cards of one kind by amending the opening deal and rebuilding from the journal,
    /// so the journal keeps describing the state exactly and replay assertions stay meaningful. The
    /// instances are taken from the bottom of the draw pile, where no recorded event refers to them.
    /// </summary>
    public void GrantCards(SeatId seat, TrainCardKind kind, int count)
    {
        if (!_journalDescribesState)
        {
            throw new InvalidOperationException(
                "Amending the opening deal rebuilds state from the journal, which would discard an " +
                "earlier direct fixture change. Call this before SeedOwnedRoutes, ReturnTicketsToDeck " +
                "or GrantTicket.");
        }

        var chosen = State.TrainDeck
            .Where(card => State.Catalog.KindOf(card) == kind)
            .TakeLast(count)
            .ToImmutableArray();

        if (chosen.Length != count)
            throw new InvalidOperationException($"The draw pile has fewer than {count} {kind} cards left.");

        var index = _journal.FindIndex(row => row.Event is InitialHandsDealt);
        var dealt = (InitialHandsDealt)_journal[index].Event;

        _journal[index] = _journal[index] with
        {
            Event = dealt with { Hands = dealt.Hands.SetItem(seat, dealt.Hands[seat].AddRange(chosen)) },
        };

        State = Replay();
    }

    /// <summary>
    /// Replaces a seat's whole hand with an exact composition, again by amending the opening deal, so
    /// a test can state precisely what the seat holds without destroying card instances.
    /// </summary>
    public void SetHand(SeatId seat, params (TrainCardKind Kind, int Count)[] cards)
    {
        if (!_journalDescribesState)
        {
            throw new InvalidOperationException(
                "Amending the opening deal rebuilds state from the journal, which would discard an " +
                "earlier direct fixture change. Call this before SeedOwnedRoutes, ReturnTicketsToDeck " +
                "or GrantTicket.");
        }

        var index = _journal.FindIndex(row => row.Event is InitialHandsDealt);
        var dealt = (InitialHandsDealt)_journal[index].Event;

        // Draw from the bottom of the pile and from this seat's own dealt cards: nothing later in
        // the journal refers to either, so the amended deal still replays cleanly.
        var pool = State.TrainDeck.Concat(dealt.Hands[seat]).ToList();
        var chosen = ImmutableArray.CreateBuilder<CardId>();

        foreach (var (kind, count) in cards)
        {
            var picked = pool
                .Where(card => State.Catalog.KindOf(card) == kind && !chosen.Contains(card))
                .TakeLast(count)
                .ToArray();

            if (picked.Length != count)
                throw new InvalidOperationException($"Only {picked.Length} {kind} cards are available.");

            chosen.AddRange(picked);
        }

        _journal[index] = _journal[index] with
        {
            Event = dealt with { Hands = dealt.Hands.SetItem(seat, chosen.ToImmutable()) },
        };

        State = Replay();
    }

    /// <summary>
    /// Puts routes straight onto the board, keeping train stock and route score consistent so the
    /// invariants still hold. No journal event describes this, so replay assertions are disabled.
    /// </summary>
    public void SeedOwnedRoutes(SeatId seat, params string[] routeIds)
    {
        foreach (var value in routeIds)
        {
            var routeId = new RouteId(value);
            var route = Manifest.Route(routeId);

            State.RouteOwnersInternal[routeId] = seat;
            State.TrainStockInternal[seat] -= route.Length;
            State.RouteScoreInternal[seat] += Manifest.RulesConstants.ScoreForLength(route.Length);
        }

        _journalDescribesState = false;
    }

    /// <summary>
    /// Returns a seat's destination tickets to the bottom of the deck. Tickets are conserved, so a
    /// fixture can hand out known ones without breaking invariant 1.
    /// </summary>
    public void ReturnTicketsToDeck(SeatId seat)
    {
        var held = State.TicketHandsInternal[seat].ToArray();
        State.TicketHandsInternal[seat].Clear();
        State.TicketDeckInternal.AddRange(held);
        _journalDescribesState = false;
    }

    /// <summary>Moves a known ticket from the deck into a seat's hand.</summary>
    public void GrantTicket(SeatId seat, string ticketId)
    {
        var id = new TicketId(ticketId);
        if (!State.TicketDeckInternal.Remove(id))
            throw new InvalidOperationException($"Ticket {ticketId} is not in the deck.");

        State.TicketHandsInternal[seat].Add(id);
        _journalDescribesState = false;
    }

    /// <summary>The seat's own card instances of one kind, for building a payment.</summary>
    public ImmutableArray<CardId> CardsOf(SeatId seat, TrainCardKind kind, int count) =>
    [
        .. State.AvailableCardsOf(seat)
            .Where(card => State.Catalog.KindOf(card) == kind)
            .OrderBy(card => card.Value)
            .Take(count)
    ];

    private void Record(long stateVersion, IEnumerable<GameEvent> events)
    {
        foreach (var domainEvent in events)
            _journal.Add(new JournaledEvent(++_sequence, stateVersion, domainEvent));
    }
}
