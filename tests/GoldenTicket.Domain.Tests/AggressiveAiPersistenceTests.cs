using System.Text.Json;
using GoldenTicket.Application;
using GoldenTicket.Domain.Ai;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Persistence;
using Microsoft.Data.Sqlite;

namespace GoldenTicket.Domain.Tests;

public sealed class AggressiveAiPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "GoldenTicket.Tests", Guid.NewGuid().ToString("n"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SavedComputerStylesSurviveReloadAndReachEachSeatsDecisionPolicy()
    {
        var rules = new GameRules(TestManifest.Manifest, TestManifest.Catalog);
        var store = new SqliteSessionStore(_root);
        var setup = new SessionSetup(SessionId.New(),
            [new(new(1), "Human", PlayerColor.Black, SeatKind.Human, AiDifficulty.Standard),
             new(new(2), "Aggressive", PlayerColor.Red, SeatKind.Computer, AiDifficulty.Aggressive),
             new(new(3), "Standard", PlayerColor.Blue, SeatKind.Computer, AiDifficulty.Standard)],
            new(1), VerificationMode.Manual);
        var original = await GameCoordinator.CreateAsync(
            rules, store, setup, DeterministicRandom.SeedFrom(719), Token);
        var humanView = await original.GetSeatViewAsync(new(1), Token);
        var keep = await original.SubmitAsync(new CommitTicketSelection(
            original.NewEnvelope(new(1)), [.. humanView.SetupOffer.Take(2)], []), Token);
        Assert.True(keep.IsAccepted);
        var savedHash = await original.ComputeStateHashAsync(Token);

        var restored = await GameCoordinator.RestoreAsync(
            rules, new SqliteSessionStore(_root), original.SessionId, Token);

        Assert.Equal(setup.Seats.ToArray(), restored.Seats.ToArray());
        Assert.Equal(savedHash, await restored.ComputeStateHashAsync(Token));

        // A restore that preserved the displayed roster but silently changed the actual AI mode
        // would still be wrong: exercise the real driver and inspect its per-seat policy budgets.
        var policy = new RecordingSetupPolicy();
        var driver = new ComputerSeatDriver(restored, policy, aiSeed: 81);
        Assert.Equal(2, await driver.AdvanceAsync(Token));
        Assert.Collection(policy.Decisions,
            first => Assert.Equal((new SeatId(2), AiDifficulty.Aggressive), first),
            second => Assert.Equal((new SeatId(3), AiDifficulty.Standard), second));
        Assert.All(driver.Reports, report => Assert.False(report.UsedFallback));
        Assert.Equal(SessionLifecycle.Active, restored.Public.Lifecycle);
        Assert.Equal(new SeatId(1), restored.Public.ActiveSeatId);
        Assert.Empty(await restored.CheckInvariantsAsync(Token));

        // The next durable transaction must also replay with the same identities and mode.
        var reopened = await GameCoordinator.RestoreAsync(
            rules, new SqliteSessionStore(_root), original.SessionId, Token);
        Assert.Equal(setup.Seats.ToArray(), reopened.Seats.ToArray());
        Assert.Equal(await restored.ComputeStateHashAsync(Token),
            await reopened.ComputeStateHashAsync(Token));
    }

    [Theory]
    [InlineData("Relaxed", 0)]
    [InlineData("Standard", 1)]
    [InlineData("Challenging", 2)]
    public void ExistingSeatWireShapeAndHashEncodingRemainCompatible(string difficulty, int encodedValue)
    {
        // This is the existing saved Seat payload, not generated from the current record.
        // Journal events use named enums, while the canonical state hash uses numeric enums.
        var savedSeat = "{\"SeatId\":2,\"DisplayName\":\"Computer\",\"Color\":\"Red\",\"Kind\":\"Computer\",\"Difficulty\":\""
            + difficulty + "\"}";
        var restored = JsonSerializer.Deserialize<Seat>(savedSeat, EventSerializer.Options);

        Assert.NotNull(restored);
        Assert.Equal(encodedValue, (int)restored.Difficulty);
        Assert.Equal(savedSeat, JsonSerializer.Serialize(restored, EventSerializer.Options));
        Assert.Equal("{\"SeatId\":2,\"DisplayName\":\"Computer\",\"Color\":1,\"Kind\":1,\"Difficulty\":"
            + encodedValue + "}", JsonSerializer.Serialize(restored));
    }

    private sealed class RecordingSetupPolicy : IAiPolicy
    {
        public List<(SeatId SeatId, AiDifficulty Difficulty)> Decisions { get; } = [];

        public ValueTask<AiDecision> ChooseAsync(SeatView view, BoardManifest manifest,
            DecisionBudget budget, DeterministicRandom random, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Decisions.Add((view.SeatId, budget.Difficulty));
            return ValueTask.FromResult<AiDecision>(new AiKeepTickets([.. view.SetupOffer.Take(2)]));
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
