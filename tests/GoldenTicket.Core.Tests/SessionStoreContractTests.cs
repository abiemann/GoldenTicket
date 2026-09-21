using GoldenTicket.Application;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Persistence;
using Microsoft.Data.Sqlite;

namespace GoldenTicket.Domain.Tests;

public sealed class SessionStoreContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GoldenTicket.Tests", Guid.NewGuid().ToString("n"));
    private static readonly CancellationToken Token = CancellationToken.None;

    public static IEnumerable<object[]> InconsistentCommits => Cases(
        "rejected-outcome", "empty-transition", "journal-count", "outcome-version", "stale-state");

    [Theory]
    [MemberData(nameof(InconsistentCommits))]
    public async Task InconsistentTransactionsDoNotChangeStoredStateOrRecordAnOutcome(bool sqlite, string defect)
    {
        var (store, rules, state) = await CreateAsync(sqlite);
        var originalHash = StateHash.Compute(state);
        var seat = state.ActiveSeatId;
        var command = new CommitTicketSelection(
            new(state.SessionId, CommandId.New(), state.StateVersion, seat),
            [.. state.SetupOffers[seat].Take(2)], []);
        var result = rules.ValidateAndApply(state, command);
        Assert.True(result.IsAccepted);
        var transition = result.Transition!;
        var next = state.Fork();
        GameReducer.ApplyTransition(next, transition.Events);
        var outcome = new StoredCommandOutcome(command.Envelope.CommandId, true, next.StateVersion, null, null);
        switch (defect)
        {
            case "rejected-outcome": outcome = outcome with { Accepted = false }; break;
            case "empty-transition": transition = new Transition([]); break;
            case "journal-count": next.JournalSequence++; break;
            case "outcome-version": outcome = outcome with { StateVersionAfter = next.StateVersion + 1 }; break;
            case "stale-state": next.StateVersion--; break;
        }

        await Assert.ThrowsAsync<SessionIntegrityException>(() =>
            store.CommitAsync(next, outcome, transition, StateHash.Compute(next), Token));

        var restored = await RestoreAsync(store, state);
        Assert.Equal(originalHash, StateHash.Compute(restored.State));
        Assert.Null(await store.FindCommandOutcomeAsync(state.SessionId, outcome.CommandId, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectionWritesPreserveTheFirstOutcomeAndCannotRecordAcceptance(bool sqlite)
    {
        var (store, _, state) = await CreateAsync(sqlite);
        var first = new StoredCommandOutcome(CommandId.New(), false, state.StateVersion, "First", "First refusal");
        await store.RecordRejectionAsync(state.SessionId, first, Token);
        await store.RecordRejectionAsync(state.SessionId,
            first with { RejectionCode = "Later", RejectionMessage = "Later refusal" }, Token);

        Assert.Equal(first, await store.FindCommandOutcomeAsync(state.SessionId, first.CommandId, Token));
        var accepted = first with { CommandId = CommandId.New(), Accepted = true };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.RecordRejectionAsync(state.SessionId, accepted, Token));
        Assert.Null(await store.FindCommandOutcomeAsync(state.SessionId, accepted.CommandId, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConsistentHashCannotMakeAnInvalidGameRestorable(bool sqlite)
    {
        var (store, _, state) = await CreateAsync(sqlite);
        var secretTicket = state.SetupOffers[new SeatId(2)][0];
        var invalid = new Transition([new TicketSelectionCommitted(new SeatId(1), [secretTicket], [], true)]);
        GameReducer.ApplyTransition(state, invalid.Events);
        await store.CommitAsync(state,
            new StoredCommandOutcome(CommandId.New(), true, state.StateVersion, null, null),
            invalid, StateHash.Compute(state), Token);

        var error = await Assert.ThrowsAsync<SessionIntegrityException>(() => RestoreAsync(store, state));

        Assert.Contains("integrity checks", error.Message);
        Assert.DoesNotContain(secretTicket.Value, error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreRequiresThePinnedRulesPolicy(bool sqlite)
    {
        var (store, _, state) = await CreateAsync(sqlite);
        var manifest = state.Manifest;
        var changed = new BoardManifest(manifest.ProfileId, manifest.SchemaVersion,
            manifest.RulesPolicyVersion + 1, manifest.DataHash, manifest.Edition, manifest.DataAudit,
            manifest.Cities, manifest.Routes, manifest.Tickets, manifest.TrainCardDefinitions, manifest.RulesConstants);

        await Assert.ThrowsAsync<SessionIntegrityException>(() =>
            store.RestoreAsync(state.SessionId, changed, state.Catalog, Token));
    }

    public static IEnumerable<object[]> InvalidTiming => Cases(
        "missing-turns", "null-turn", "negative-time", "excessive-time", "invalid-turn", "future-turn",
        "unknown-seat", "duplicate-turn", "marker-without-unfinished", "negative-total", "excessive-total");

    [Theory]
    [MemberData(nameof(InvalidTiming))]
    public async Task InvalidTimingIsDiscardedByEitherAdapterWithoutChangingTheGame(bool sqlite, string defect)
    {
        var (store, rules, state) = await CreateAsync(sqlite);
        state = await CompleteSetupAsync(store, rules, state);
        var hash = StateHash.Compute(state);
        var entry = new TurnTimingEntry(1, state.ActiveSeatId, 100, false, false);
        var timing = defect switch
        {
            "missing-turns" => new TurnTimingSnapshot(null!),
            "null-turn" => new TurnTimingSnapshot([null!]),
            "negative-time" => new TurnTimingSnapshot([entry with { ElapsedTicks = -1 }]),
            "excessive-time" => new TurnTimingSnapshot([entry with { ElapsedTicks = long.MaxValue }]),
            "invalid-turn" => new TurnTimingSnapshot([entry with { TurnNumber = 0 }]),
            "future-turn" => new TurnTimingSnapshot([entry with { TurnNumber = state.TurnNumber + 1 }]),
            "unknown-seat" => new TurnTimingSnapshot([entry with { SeatId = new SeatId(99) }]),
            "duplicate-turn" => new TurnTimingSnapshot([entry with { Completed = true }, entry]),
            "marker-without-unfinished" => new TurnTimingSnapshot([entry with { Completed = true }], true),
            "negative-total" => new TurnTimingSnapshot([entry], GameElapsedTicks: -1),
            "excessive-total" => new TurnTimingSnapshot([entry], GameElapsedTicks: long.MaxValue),
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        await store.SaveTurnTimingAsync(state.SessionId, state.StateVersion, timing, Token);

        var restored = await RestoreAsync(store, state);

        Assert.Null(restored.Timing);
        Assert.Equal(hash, StateHash.Compute(restored.State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimingAtTheSupportedBoundaryRemainsRestorable(bool sqlite)
    {
        var (store, rules, state) = await CreateAsync(sqlite);
        state = await CompleteSetupAsync(store, rules, state);
        var limit = TimeSpan.FromDays(365).Ticks;
        var timing = new TurnTimingSnapshot([new(1, state.ActiveSeatId, limit, false, false)],
            AwaitingScoreMarker: true, GameElapsedTicks: limit);
        await store.SaveTurnTimingAsync(state.SessionId, state.StateVersion, timing, Token);

        var restored = await RestoreAsync(store, state);

        Assert.NotNull(restored.Timing);
        Assert.Equal(timing.Turns, restored.Timing.Turns);
        Assert.True(restored.Timing.AwaitingScoreMarker);
        Assert.Equal(limit, restored.Timing.GameElapsedTicks);
    }

    private static IEnumerable<object[]> Cases(params string[] cases) =>
        from sqlite in new[] { false, true }
        from defect in cases
        select new object[] { sqlite, defect };

    private async Task<(ISessionStore Store, GameRules Rules, GameState State)> CreateAsync(bool sqlite)
    {
        ISessionStore store = sqlite ? new SqliteSessionStore(_root) : new InMemorySessionStore();
        var rules = new GameRules(TestManifest.Manifest, TestManifest.Catalog);
        var setup = new SessionSetup(SessionId.New(),
            [new(new SeatId(1), "First", PlayerColor.Red, SeatKind.Human, AiDifficulty.Standard),
             new(new SeatId(2), "Second", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard)],
            new SeatId(1), VerificationMode.Manual);
        var (state, transition) = rules.CreateSession(setup, DeterministicRandom.SeedFrom(593));
        await store.CreateAsync(state, CommandId.New(), transition, StateHash.Compute(state), Token);
        return (store, rules, state);
    }

    private static async Task<GameState> CompleteSetupAsync(ISessionStore store, GameRules rules, GameState state)
    {
        foreach (var seat in state.Seats)
        {
            var command = new CommitTicketSelection(
                new(state.SessionId, CommandId.New(), state.StateVersion, seat.SeatId),
                [.. state.SetupOffers[seat.SeatId].Take(2)], []);
            var result = rules.ValidateAndApply(state, command);
            Assert.True(result.IsAccepted);
            var next = state.Fork();
            GameReducer.ApplyTransition(next, result.Transition!.Events);
            await store.CommitAsync(next,
                new StoredCommandOutcome(command.Envelope.CommandId, true, next.StateVersion, null, null),
                result.Transition, StateHash.Compute(next), Token);
            state = next;
        }
        return state;
    }

    private static Task<RestoredSession> RestoreAsync(ISessionStore store, GameState state) =>
        store.RestoreAsync(state.SessionId, TestManifest.Manifest, TestManifest.Catalog, Token);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
