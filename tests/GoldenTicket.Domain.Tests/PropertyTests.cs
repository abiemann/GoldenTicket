using GoldenTicket.AI;
using GoldenTicket.Application;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Simulator;

namespace GoldenTicket.Domain.Tests;

/// <summary>
/// Seeded whole-match properties (DESIGN 22.1 layer 2): resource conservation, valid ownership,
/// replay equality and phase invariants, checked over complete games rather than single commands.
/// </summary>
public class PropertyTests
{
    [Theory]
    [InlineData(2, 101UL)]
    [InlineData(3, 202UL)]
    [InlineData(4, 303UL)]
    [InlineData(5, 404UL)]
    public async Task ACompleteMatchHoldsItsInvariantsAndReplaysToTheSameState(int seatCount, ulong seed)
    {
        var runner = new MatchRunner(TestManifest.Manifest, TestManifest.Catalog);
        var report = await runner.RunAsync(seed, seatCount, AiDifficulty.Standard, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Completed, $"The match stopped early: {report.StoppedBecause}");
        Assert.Empty(report.InvariantProblems);
        Assert.True(report.ReplayMatched, "Replaying the journal did not reproduce the live state.");
        Assert.NotNull(report.Result);
    }

    [Fact]
    public async Task TheSameSeedProducesTheSameMatchTwice()
    {
        var runner = new MatchRunner(TestManifest.Manifest, TestManifest.Catalog);

        var first = await Play(runner, 555UL);
        var second = await Play(runner, 555UL);

        Assert.Equal(first, second);

        // A different seed must produce a different match, or the shuffle is not doing its job.
        var other = await Play(runner, 556UL);
        Assert.NotEqual(first, other);
    }

    private static async Task<string> Play(MatchRunner runner, ulong seed)
    {
        var report = await runner.RunAsync(seed, 3, AiDifficulty.Standard,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(report.Completed, report.StoppedBecause);

        // Compare the outcome, not the session id, which is new for every run.
        return string.Join("|", report.Result!.Scores
            .OrderBy(score => score.SeatId.Value)
            .Select(score => $"{score.SeatId.Value}:{score.Total}:{score.LongestTrailLength}:" +
                             $"{score.CompletedTicketCount}")) + $"|turns={report.Turns}";
    }

    /// <summary>
    /// DESIGN 7.2 invariant 1: the card supply is conserved through every reshuffle. This drives a
    /// match far enough that the draw pile is rebuilt from the discards at least once.
    /// </summary>
    [Fact]
    public async Task TheCardSupplyIsConservedAcrossReshuffles()
    {
        var store = new InMemorySessionStore();
        var rules = new GameRules(TestManifest.Manifest, TestManifest.Catalog);

        var seats = Enumerable.Range(0, 4)
            .Select(index => new Seat(
                new SeatId(index + 1), $"Seat {index + 1}", Enum.GetValues<PlayerColor>()[index],
                SeatKind.Computer, AiDifficulty.Standard))
            .ToArray();

        var setup = new SessionSetup(
            SessionId.New(), [.. seats], seats[0].SeatId, VerificationMode.Manual);

        var coordinator = await GameCoordinator.CreateAsync(
            rules, store, setup, DeterministicRandom.SeedFrom(88), cancellationToken: TestContext.Current.CancellationToken);

        var driver = new ComputerSeatDriver(coordinator, new HeuristicAiPolicy(), aiSeed: 88);

        var guard = 0;
        while (coordinator.Public.Lifecycle != SessionLifecycle.Finished && guard++ < 500)
        {
            await driver.AdvanceAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Empty(await coordinator.CheckInvariantsAsync(cancellationToken: TestContext.Current.CancellationToken));

            if (coordinator.Public.PendingClaim is not { } pending) continue;

            await coordinator.SubmitAsync(new SubmitClaimEvidence(
                coordinator.NewEnvelope(pending.SeatId), pending.OperationId,
                EvidenceKind.ManualAttestation, "test", "board matches"), cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Equal(SessionLifecycle.Finished, coordinator.Public.Lifecycle);

        var reshuffles = store.JournalOf(coordinator.SessionId)
            .Count(row => row.Event is Events.DeckReshuffled);

        Assert.True(reshuffles > 0, "The match never exhausted the draw pile, so the reshuffle path was untested.");
    }

    /// <summary>
    /// DESIGN 7.2 invariant 14: published route scores always equal a fresh calculation. The
    /// invariant checker runs after every command in the loop above; this asserts the final figures
    /// against an independent recomputation as well.
    /// </summary>
    [Theory]
    [InlineData(3, 707UL)]
    [InlineData(4, 808UL)]
    public async Task FinalRoutePointsMatchAnIndependentRecomputation(int seatCount, ulong seed)
    {
        var runner = new MatchRunner(TestManifest.Manifest, TestManifest.Catalog);
        var store = new InMemorySessionStore();

        var report = await runner.RunAsync(seed, seatCount, AiDifficulty.Standard, store, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(report.Completed, report.StoppedBecause);

        var restored = await store.RestoreAsync(
            report.SessionId, TestManifest.Manifest, TestManifest.Catalog, TestContext.Current.CancellationToken);

        foreach (var score in report.Result!.Scores)
        {
            var recomputed = restored.State.RoutesOwnedBy(score.SeatId)
                .Sum(routeId => TestManifest.Manifest.RulesConstants.ScoreForLength(
                    TestManifest.Manifest.Route(routeId).Length));

            Assert.Equal(recomputed, score.RoutePoints);

            // Invariant 13: remaining stock plus track on the board equals the starting stock.
            var used = restored.State.TotalTrackLengthOwnedBy(score.SeatId);
            Assert.Equal(
                TestManifest.Manifest.RulesConstants.StartingTrainsPerSeat,
                restored.State.TrainStock[score.SeatId] + used);
        }
    }
}
