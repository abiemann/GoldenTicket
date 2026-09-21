using System.Collections.Immutable;
using GoldenTicket.Application;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Ai;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.Domain.Tests;

public class ApplicationAuditTests
{
    private static GameRules Rules() => new(TestManifest.Manifest, TestManifest.Catalog);

    private static Task<GameCoordinator> CreateAsync(ISessionStore store, bool computer = false) =>
        GameCoordinator.CreateAsync(Rules(), store, new SessionSetup(SessionId.New(),
        [
            new Seat(new SeatId(1), "First", PlayerColor.Blue,
                computer ? SeatKind.Computer : SeatKind.Human, AiDifficulty.Relaxed),
            new Seat(new SeatId(2), "Second", PlayerColor.Red, SeatKind.Human, AiDifficulty.Relaxed),
        ], new SeatId(1), VerificationMode.Manual), DeterministicRandom.SeedFrom(91));

    private static async Task<CommitTicketSelection> OpeningChoiceAsync(GameCoordinator coordinator)
    {
        var view = await coordinator.GetSeatViewAsync(new SeatId(1));
        return new CommitTicketSelection(coordinator.NewEnvelope(view.SeatId), [.. view.SetupOffer.Take(2)], []);
    }

    [Fact]
    public async Task ForeignSessionIsRejectedBeforeAnyStoreLookupOrRejectionWrite()
    {
        var store = new FaultStore();
        var current = await CreateAsync(store);
        var other = await CreateAsync(store);
        var command = await OpeningChoiceAsync(other);
        Assert.True((await other.SubmitAsync(command)).IsAccepted);
        store.Lookups = store.Rejections = 0;

        var result = await current.SubmitAsync(command);

        Assert.Equal("WrongSession", result.Result.Rejection?.Code);
        Assert.False(result.WasDuplicate);
        Assert.Equal(0, store.Lookups);
        Assert.Equal(0, store.Rejections);
        Assert.False(current.StorageFaulted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUncertainWriteBlocksFurtherCommandsUntilRestored(bool committedBeforeFailure)
    {
        var store = new FaultStore();
        var coordinator = await CreateAsync(store);
        var command = await OpeningChoiceAsync(coordinator);
        var before = await coordinator.ComputeStateHashAsync();
        store.FailCommit = true;
        store.CommitBeforeFailure = committedBeforeFailure;

        await Assert.ThrowsAsync<IOException>(() => coordinator.SubmitAsync(command));
        Assert.True(coordinator.StorageFaulted);
        Assert.Equal(before, await coordinator.ComputeStateHashAsync());
        Assert.Equal("StorageFaulted", (await coordinator.SubmitAsync(command)).Result.Rejection?.Code);
        Assert.Equal(1, store.Commits);

        store.FailCommit = false;
        var restored = await GameCoordinator.RestoreAsync(Rules(), store, coordinator.SessionId);
        Assert.False(restored.StorageFaulted);
        var retry = await restored.SubmitAsync(command);
        Assert.True(retry.IsAccepted);
        Assert.Equal(committedBeforeFailure, retry.WasDuplicate);
        Assert.Empty((await restored.GetSeatViewAsync(new SeatId(1))).SetupOffer);
        Assert.Empty(await restored.CheckInvariantsAsync());
    }

    [Fact]
    public async Task UpdateObserverCanReadTheCoordinatorWithoutDeadlocking()
    {
        var coordinator = await CreateAsync(new InMemorySessionStore());
        string? observed = null;
        coordinator.Updated += (_, _) => observed = coordinator.ComputeStateHashAsync().GetAwaiter().GetResult();
        var command = await OpeningChoiceAsync(coordinator);

        var outcome = await Task.Run(() => coordinator.SubmitAsync(command)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(outcome.IsAccepted);
        Assert.Equal(await coordinator.ComputeStateHashAsync(), observed);
    }

    [Fact]
    public async Task AFaultyObserverCannotTurnACommittedActionIntoAFailureOrSkipOtherObservers()
    {
        var coordinator = await CreateAsync(new InMemorySessionStore());
        var observed = false;
        coordinator.Updated += (_, _) => throw new InvalidOperationException("test subscriber");
        coordinator.Updated += (_, _) => observed = true;
        var command = await OpeningChoiceAsync(coordinator);

        var outcome = await coordinator.SubmitAsync(command);

        Assert.True(outcome.IsAccepted);
        Assert.True(observed);
        Assert.True((await coordinator.SubmitAsync(command)).WasDuplicate);
        Assert.False(coordinator.StorageFaulted);
    }

    [Fact]
    public async Task TwoCoordinatorsCannotOverwriteTheSameSavedVersion()
    {
        var store = new InMemorySessionStore();
        var first = await CreateAsync(store);
        var second = await GameCoordinator.RestoreAsync(Rules(), store, first.SessionId);
        var oldCommand = await OpeningChoiceAsync(second);
        Assert.True((await first.SubmitAsync(await OpeningChoiceAsync(first))).IsAccepted);

        await Assert.ThrowsAsync<SessionIntegrityException>(() => second.SubmitAsync(oldCommand));

        Assert.True(second.StorageFaulted);
        var restored = await GameCoordinator.RestoreAsync(Rules(), store, first.SessionId);
        Assert.Equal(await first.ComputeStateHashAsync(), await restored.ComputeStateHashAsync());
    }

    [Fact]
    public async Task CallerCancellationDoesNotApplyAnAiFallbackOrPublishAResult()
    {
        var coordinator = await CreateAsync(new InMemorySessionStore(), computer: true);
        var policy = new ControlledPolicy();
        var driver = new ComputerSeatDriver(coordinator, policy, 1);
        var before = await coordinator.ComputeStateHashAsync();
        using var cancellation = new CancellationTokenSource();
        var run = driver.AdvanceAsync(cancellation.Token);
        await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(before, await coordinator.ComputeStateHashAsync());
            Assert.Empty(driver.Reports);
        }
        finally
        {
            policy.Release.TrySetResult(new AiNoDecision("private policy details"));
        }
    }

    [Fact]
    public async Task APolicyIgnoringCancellationHasABoundedWaitAndItsLateResultIsDiscarded()
    {
        var coordinator = await CreateAsync(new InMemorySessionStore(), computer: true);
        var policy = new ControlledPolicy();
        var driver = new ComputerSeatDriver(coordinator, policy, 1);
        var run = driver.AdvanceAsync();
        try
        {
            Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty((await coordinator.GetSeatViewAsync(new SeatId(1))).SetupOffer);
            Assert.Contains(driver.Reports, report => report.UsedFallback);
            var version = coordinator.Public.StateVersion;
            policy.Release.TrySetResult(new AiDrawTrainCard(null));
            Assert.Equal(0, await driver.AdvanceAsync());
            Assert.Equal(version, coordinator.Public.StateVersion);
        }
        finally
        {
            policy.Release.TrySetResult(new AiNoDecision("stop"));
        }
    }

    [Fact]
    public async Task ConcurrentDriverCallsDoNotRunTheSamePolicyOrRandomStreamTwice()
    {
        var coordinator = await CreateAsync(new InMemorySessionStore(), computer: true);
        var policy = new ControlledPolicy();
        var driver = new ComputerSeatDriver(coordinator, policy, 1);
        var view = await coordinator.GetSeatViewAsync(new SeatId(1));
        var first = driver.AdvanceAsync();
        await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = driver.AdvanceAsync();
        policy.Release.TrySetResult(new AiKeepTickets([.. view.SetupOffer.Take(2)]));

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, results.Sum());
        Assert.Equal(1, policy.Calls);
    }

    [Fact]
    public async Task InvalidAiPaymentFallsBackInsteadOfThrowingOrStalling()
    {
        var coordinator = await CreateAsync(new InMemorySessionStore(), computer: true);
        var driver = new ComputerSeatDriver(coordinator, new ConstantPolicy(new AiClaimRoute(
            new RouteId("not-a-route"), new PaymentOption(TrainCardKind.Red, 999, 0))), 1);

        Assert.Equal(1, await driver.AdvanceAsync());

        Assert.Empty((await coordinator.GetSeatViewAsync(new SeatId(1))).SetupOffer);
        Assert.Contains(driver.Reports, report => report.UsedFallback);
        Assert.Empty(await coordinator.CheckInvariantsAsync());
    }

    [Fact]
    public async Task PublicAiReportsDoNotIncludeThePolicysPrivateReason()
    {
        const string secret = "sentinel-private-ticket-choice";
        var coordinator = await CreateAsync(new InMemorySessionStore(), computer: true);
        var driver = new ComputerSeatDriver(coordinator, new ConstantPolicy(new AiNoDecision(secret)), 1);

        await driver.AdvanceAsync();

        Assert.DoesNotContain(secret, string.Join(";", driver.Reports));
    }

    private sealed class ConstantPolicy(AiDecision decision) : IAiPolicy
    {
        public ValueTask<AiDecision> ChooseAsync(SeatView view, BoardManifest manifest, DecisionBudget budget,
            DeterministicRandom random, CancellationToken cancellationToken) => ValueTask.FromResult(decision);
    }

    private sealed class ControlledPolicy : IAiPolicy
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AiDecision> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public async ValueTask<AiDecision> ChooseAsync(SeatView view, BoardManifest manifest, DecisionBudget budget,
            DeterministicRandom random, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            return await Release.Task;
        }
    }

    private sealed class FaultStore : ISessionStore
    {
        private readonly InMemorySessionStore _inner = new();
        public bool FailCommit;
        public bool CommitBeforeFailure;
        public int Commits;
        public int Lookups;
        public int Rejections;

        public Task CreateAsync(GameState state, CommandId commandId, Transition transition, string stateHash,
            CancellationToken cancellationToken) => _inner.CreateAsync(state, commandId, transition, stateHash, cancellationToken);

        public Task<PackAwayCheckpoint?> ReadCheckpointAsync(SessionId sessionId, CheckpointId checkpointId,
            CancellationToken cancellationToken) => _inner.ReadCheckpointAsync(sessionId, checkpointId, cancellationToken);

        public Task<StoredCommandOutcome?> FindCommandOutcomeAsync(SessionId sessionId, CommandId commandId,
            CancellationToken cancellationToken)
        {
            Lookups++;
            return _inner.FindCommandOutcomeAsync(sessionId, commandId, cancellationToken);
        }

        public async Task CommitAsync(GameState state, StoredCommandOutcome outcome, Transition transition,
            string stateHash, CancellationToken cancellationToken)
        {
            Commits++;
            if (!FailCommit || CommitBeforeFailure)
                await _inner.CommitAsync(state, outcome, transition, stateHash, cancellationToken);
            if (FailCommit) throw new IOException("Simulated interrupted storage acknowledgment.");
        }

        public Task RecordRejectionAsync(SessionId sessionId, StoredCommandOutcome outcome, CancellationToken cancellationToken)
        {
            Rejections++;
            return _inner.RecordRejectionAsync(sessionId, outcome, cancellationToken);
        }

        public Task<RestoredSession> RestoreAsync(SessionId sessionId, BoardManifest manifest, CardCatalog catalog,
            CancellationToken cancellationToken) => _inner.RestoreAsync(sessionId, manifest, catalog, cancellationToken);
        public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken) =>
            _inner.ListSessionsAsync(cancellationToken);
        public Task DeleteSessionAsync(SessionId sessionId, CancellationToken cancellationToken) =>
            _inner.DeleteSessionAsync(sessionId, cancellationToken);
    }
}
