using System.Collections.Immutable;
using GoldenTicket.Application;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Persistence;
using Microsoft.Data.Sqlite;

namespace GoldenTicket.Domain.Tests;

public sealed class SessionCheckpointRewindTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "GoldenTicket.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuitCanKeepAnEarlierVerifiedSaveAndDiscardLaterJournal(bool sqlite)
    {
        ISessionStore store = sqlite ? new SqliteSessionStore(_root) : new InMemorySessionStore();
        var rules = new GameRules(TestManifest.Manifest, TestManifest.Catalog);
        var coordinator = await GameCoordinator.CreateAsync(rules, store, Setup(),
            DeterministicRandom.SeedFrom(3927), TestContext.Current.CancellationToken);
        var driver = new ComputerSeatDriver(coordinator, new GoldenTicket.AI.HeuristicAiPolicy(), aiSeed: 3927);
        for (var turn = 0; turn < 20 && coordinator.Public.Lifecycle != SessionLifecycle.Active; turn++)
            await driver.AdvanceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SessionLifecycle.Active, coordinator.Public.Lifecycle);

        var first = await coordinator.SaveAndPackAwayAsync("Earlier save", TestContext.Current.CancellationToken);
        Assert.True(first.SafeToPack, first.Problem);
        var firstCheckpoint = first.Checkpoint!;
        var savedVersion = coordinator.Public.StateVersion;
        var savedHash = await coordinator.ComputeStateHashAsync(TestContext.Current.CancellationToken);
        string? savedSidecar = null;
        if (store is SqliteSessionStore disk)
        {
            savedSidecar = Path.Combine(disk.SessionDirectory(coordinator.SessionId),
                "checkpoint-photos", firstCheckpoint.CheckpointId.Value + ".gtphoto");
            Directory.CreateDirectory(Path.GetDirectoryName(savedSidecar)!);
            await File.WriteAllBytesAsync(savedSidecar, [1, 2, 3], TestContext.Current.CancellationToken);
        }
        await ResumeAsync(coordinator);

        // A second checkpoint represents a later, potentially incomplete Save Game attempt.
        var second = await coordinator.SaveAndPackAwayAsync("New attempt", TestContext.Current.CancellationToken);
        Assert.True(second.SafeToPack, second.Problem);
        Assert.True(coordinator.Public.StateVersion > savedVersion);
        var newestExcludingAttempt = await store.FindLatestVerifiedCheckpointAsync(
            coordinator.SessionId, TestManifest.Manifest, TestManifest.Catalog,
            second.Checkpoint!.CheckpointId, TestContext.Current.CancellationToken);
        Assert.Equal(firstCheckpoint.CheckpointId, newestExcludingAttempt?.CheckpointId);

        var rewound = await store.RewindToVerifiedCheckpointAsync(
            coordinator.SessionId, firstCheckpoint.CheckpointId, TestManifest.Manifest,
            TestManifest.Catalog, TestContext.Current.CancellationToken);
        Assert.Equal(SessionLifecycle.PackedAway, rewound.State.Lifecycle);
        Assert.Equal(savedVersion, rewound.State.StateVersion);
        Assert.Equal(firstCheckpoint.CheckpointId, rewound.State.Checkpoint?.CheckpointId);
        Assert.Equal(savedHash, StateHash.Compute(rewound.State));
        Assert.Null(await store.ReadCheckpointAsync(coordinator.SessionId,
            second.Checkpoint.CheckpointId, TestContext.Current.CancellationToken));
        if (savedSidecar is not null)
            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(savedSidecar,
                TestContext.Current.CancellationToken));

        var reopened = await GameCoordinator.RestoreAsync(rules, store, coordinator.SessionId,
            TestContext.Current.CancellationToken);
        Assert.Equal(savedVersion, reopened.Public.StateVersion);
        Assert.Equal(SessionLifecycle.PackedAway, reopened.Public.Lifecycle);
        Assert.Equal(firstCheckpoint.CheckpointId, reopened.Public.Checkpoint?.CheckpointId);
        Assert.Empty(await reopened.CheckInvariantsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SessionLifecycle.PackedAway,
            Assert.Single(await store.ListSessionsAsync(TestContext.Current.CancellationToken)).Lifecycle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownCheckpointCannotDiscardTheCurrentGame(bool sqlite)
    {
        ISessionStore store = sqlite ? new SqliteSessionStore(_root) : new InMemorySessionStore();
        var rules = new GameRules(TestManifest.Manifest, TestManifest.Catalog);
        var coordinator = await GameCoordinator.CreateAsync(rules, store, Setup(),
            DeterministicRandom.SeedFrom(99), TestContext.Current.CancellationToken);
        var before = coordinator.Public.StateVersion;
        Assert.Null(await store.FindLatestVerifiedCheckpointAsync(coordinator.SessionId,
            TestManifest.Manifest, TestManifest.Catalog, null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RewindToVerifiedCheckpointAsync(
            coordinator.SessionId, CheckpointId.New(), TestManifest.Manifest, TestManifest.Catalog,
            TestContext.Current.CancellationToken));
        var reopened = await GameCoordinator.RestoreAsync(rules, store, coordinator.SessionId,
            TestContext.Current.CancellationToken);
        Assert.Equal(before, reopened.Public.StateVersion);
    }

    private static SessionSetup Setup()
    {
        var seats = Enumerable.Range(1, 2).Select(index => new Seat(new SeatId(index),
            $"Computer {index}", (PlayerColor)index, SeatKind.Computer,
            AiDifficulty.Standard)).ToImmutableArray();
        return new SessionSetup(SessionId.New(), seats, seats[0].SeatId, VerificationMode.Manual);
    }

    private static async Task ResumeAsync(GameCoordinator coordinator)
    {
        var checkpoint = coordinator.Public.Checkpoint!;
        var token = TestContext.Current.CancellationToken;
        Assert.True((await coordinator.SubmitAsync(
            new BeginBoardRebuild(coordinator.NewEnvelope(), checkpoint.CheckpointId), token)).IsAccepted);
        Assert.True((await coordinator.SubmitAsync(new AttestBoardRebuild(
            coordinator.NewEnvelope(), checkpoint.CheckpointId, checkpoint.PhysicalTargetHash, "tester"),
            token)).IsAccepted);
        Assert.True((await coordinator.SubmitAsync(
            new ResumePackedGame(coordinator.NewEnvelope(), checkpoint.CheckpointId), token)).IsAccepted);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
