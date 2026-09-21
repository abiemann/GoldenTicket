using GoldenTicket.Application;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.Domain.Tests;

public sealed class CoordinatorTimingTransactionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnacknowledgedTurnDoesNotAdvancePublishedTiming(bool committedBeforeFailure)
    {
        var store = new ControlledStore();
        var clock = new TestClock();
        var coordinator = await ReadyForSecondCard(store, clock);
        var version = coordinator.Public.StateVersion;
        store.Delay = true;
        store.Fail = true;
        store.CommitBeforeFailure = committedBeforeFailure;
        var submit = coordinator.SubmitAsync(new SelectTrainCard(coordinator.NewEnvelope(new(1)), null), Token);
        try
        {
            await store.Entered.Task.WaitAsync(Token);
            Assert.Equal(version, coordinator.Public.StateVersion);
            var pendingTurn = Assert.Single(coordinator.TurnTiming.Turns);
            Assert.False(pendingTurn.Completed);
            Assert.Equal(1, pendingTurn.TurnNumber);
            Assert.Equal(TimeSpan.FromSeconds(20).Ticks, pendingTurn.ElapsedTicks);
        }
        finally { store.Release.TrySetResult(); }
        await Assert.ThrowsAsync<IOException>(() => submit);

        Assert.True(coordinator.StorageFaulted);
        Assert.Equal(version, coordinator.Public.StateVersion);
        Assert.False(Assert.Single(coordinator.TurnTiming.Turns).Completed);

        var restored = await GameCoordinator.RestoreAsync(Rules(), store, coordinator.SessionId, Token, clock);
        Assert.Equal(committedBeforeFailure ? 2 : 1, restored.Public.TurnNumber);
        Assert.Equal(committedBeforeFailure ? 2 : 1, restored.TurnTiming.Turns.Count);
        Assert.Equal(committedBeforeFailure, restored.TurnTiming.Turns[0].Completed);
        Assert.Empty(await restored.CheckInvariantsAsync(Token));
    }

    [Fact]
    public async Task SuccessfulCommitPreservesPauseChangesMadeWhileStorageWasBusy()
    {
        var store = new ControlledStore();
        var clock = new TestClock();
        var coordinator = await ReadyForSecondCard(store, clock);
        store.Delay = true;
        var submit = coordinator.SubmitAsync(new SelectTrainCard(coordinator.NewEnvelope(new(1)), null), Token);
        try
        {
            await store.Entered.Task.WaitAsync(Token);
            clock.Advance(TimeSpan.FromSeconds(3));
            coordinator.SetTurnTimingPaused(true);
            clock.Advance(TimeSpan.FromSeconds(5));
        }
        finally { store.Release.TrySetResult(); }
        Assert.True((await submit).IsAccepted);

        Assert.Equal(2, coordinator.Public.TurnNumber);
        var timing = coordinator.TurnTiming;
        Assert.False(timing.WasRunning);
        Assert.Equal(TimeSpan.FromSeconds(23).Ticks, timing.Turns[0].ElapsedTicks);
        Assert.True(timing.Turns[0].Completed);
        Assert.Equal(0, timing.Turns[1].ElapsedTicks);
        Assert.Equal(TimeSpan.FromSeconds(28).Ticks, timing.GameElapsedTicks);

        coordinator.SetTurnTimingPaused(false);
        clock.Advance(TimeSpan.FromSeconds(2));
        await coordinator.FlushTurnTimingAsync(Token);
        var restored = await GameCoordinator.RestoreAsync(Rules(), store, coordinator.SessionId, Token, clock);
        Assert.Equal(TimeSpan.FromSeconds(23).Ticks, restored.TurnTiming.Turns[0].ElapsedTicks);
        Assert.Equal(TimeSpan.FromSeconds(2).Ticks, restored.TurnTiming.Turns[1].ElapsedTicks);
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, restored.TurnTiming.GameElapsedTicks);
    }

    private static GameRules Rules() => new(TestManifest.Manifest, TestManifest.Catalog);

    private static async Task<GameCoordinator> ReadyForSecondCard(ControlledStore store, TestClock clock)
    {
        var coordinator = await GameCoordinator.CreateAsync(Rules(), store,
            new(SessionId.New(), [new(new(1), "One", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard),
                new(new(2), "Two", PlayerColor.Yellow, SeatKind.Human, AiDifficulty.Standard)],
                new(1), VerificationMode.Manual), DeterministicRandom.SeedFrom(91), Token, clock);
        coordinator.SetTurnTimingPaused(false);
        coordinator.SetGameTimingRunning(true);
        foreach (var seat in coordinator.Seats)
        {
            var view = await coordinator.GetSeatViewAsync(seat.SeatId, Token);
            Assert.True((await coordinator.SubmitAsync(new CommitTicketSelection(
                coordinator.NewEnvelope(seat.SeatId), [.. view.SetupOffer.Take(2)], []), Token)).IsAccepted);
        }
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True((await coordinator.SubmitAsync(new SelectTrainCard(coordinator.NewEnvelope(new(1)), null), Token)).IsAccepted);
        clock.Advance(TimeSpan.FromSeconds(10));
        return coordinator;
    }

    private sealed class TestClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }

    private sealed class ControlledStore : ISessionStore
    {
        private readonly InMemorySessionStore _inner = new();
        public bool Delay;
        public bool Fail;
        public bool CommitBeforeFailure;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CreateAsync(GameState state, CommandId id, Transition transition, string hash, CancellationToken token) =>
            _inner.CreateAsync(state, id, transition, hash, token);
        public Task<StoredCommandOutcome?> FindCommandOutcomeAsync(SessionId session, CommandId id, CancellationToken token) =>
            _inner.FindCommandOutcomeAsync(session, id, token);
        public async Task CommitAsync(GameState state, StoredCommandOutcome outcome, Transition transition,
            string hash, CancellationToken token)
        {
            if (Delay)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            if (!Fail || CommitBeforeFailure) await _inner.CommitAsync(state, outcome, transition, hash, token);
            if (Fail) throw new IOException("Simulated lost storage acknowledgement.");
        }
        public Task RecordRejectionAsync(SessionId session, StoredCommandOutcome outcome, CancellationToken token) =>
            _inner.RecordRejectionAsync(session, outcome, token);
        public Task SaveTurnTimingAsync(SessionId session, long version, TurnTimingSnapshot timing, CancellationToken token) =>
            _inner.SaveTurnTimingAsync(session, version, timing, token);
        public Task<RestoredSession> RestoreAsync(SessionId session, BoardManifest manifest, CardCatalog catalog, CancellationToken token) =>
            _inner.RestoreAsync(session, manifest, catalog, token);
        public Task<PackAwayCheckpoint?> ReadCheckpointAsync(SessionId session, CheckpointId checkpoint, CancellationToken token) =>
            _inner.ReadCheckpointAsync(session, checkpoint, token);
        public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken token) => _inner.ListSessionsAsync(token);
        public Task DeleteSessionAsync(SessionId session, CancellationToken token) => _inner.DeleteSessionAsync(session, token);
    }
}
