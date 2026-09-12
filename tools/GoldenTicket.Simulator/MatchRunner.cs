using System.Collections.Immutable;
using GoldenTicket.AI;
using GoldenTicket.Application;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.Simulator;

/// <summary>The outcome of one headless match, with the checks that ran against it.</summary>
public sealed record MatchReport(
    SessionId SessionId,
    ulong Seed,
    bool Completed,
    int Turns,
    int Commands,
    string? StoppedBecause,
    FinalResult? Result,
    IReadOnlyList<string> InvariantProblems,
    bool ReplayMatched,
    TimeSpan Elapsed);

/// <summary>
/// Plays a complete match with no interface. DESIGN 15.6 asks for reproducible simulated matches;
/// DESIGN 23.1 wants the manual confirmation path exercised before camera evidence replaces it, so
/// the runner acts as the operator and attests to each placement.
/// </summary>
public sealed class MatchRunner(BoardManifest manifest, CardCatalog catalog)
{
    /// <summary>Guards against a rule bug producing an endless match.</summary>
    public const int MaximumTurns = 400;

    public async Task<MatchReport> RunAsync(
        ulong seed,
        int seatCount,
        AiDifficulty difficulty,
        ISessionStore? store = null,
        CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        store ??= new InMemorySessionStore();
        var rules = new GameRules(manifest, catalog);
        var setup = BuildSetup(seatCount, difficulty);

        var coordinator = await GameCoordinator.CreateAsync(
            rules, store, setup, DeterministicRandom.SeedFrom(seed), cancellationToken);

        var driver = new ComputerSeatDriver(coordinator, new HeuristicAiPolicy(), aiSeed: seed ^ 0xA5A5A5A5UL);

        var commands = 0;
        string? stopped = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            commands += await driver.AdvanceAsync(cancellationToken);

            var view = coordinator.Public;

            if (view.Lifecycle == SessionLifecycle.Finished) break;

            if (view.TurnPhase == TurnPhase.RulesDecisionRequired)
            {
                stopped = $"{view.RulesDecision?.Code}: {view.RulesDecision?.Explanation}";
                break;
            }

            if (view.TurnNumber > MaximumTurns)
            {
                stopped = $"the match passed {MaximumTurns} turns without finishing";
                break;
            }

            if (view.PendingClaim is { } pending)
            {
                // Stand in for the human operator: place the trains, then attest to the whole board.
                var evidence = new SubmitClaimEvidence(
                    coordinator.NewEnvelope(pending.SeatId),
                    pending.OperationId,
                    EvidenceKind.ManualAttestation,
                    "simulator",
                    "headless simulation");

                var outcome = await coordinator.SubmitAsync(evidence, cancellationToken);
                if (!outcome.IsAccepted)
                {
                    stopped = $"placement evidence refused: {outcome.Result.Rejection?.Code}";
                    break;
                }

                commands++;
                continue;
            }

            // Nothing moved and nothing is pending: a human seat would be needed, which a headless
            // all-computer match should never reach.
            stopped = $"no computer action was available in phase {view.TurnPhase}";
            break;
        }

        var problems = await coordinator.CheckInvariantsAsync(cancellationToken);
        var replayed = await store.RestoreAsync(coordinator.SessionId, manifest, catalog, cancellationToken);
        var liveHash = await coordinator.ComputeStateHashAsync(cancellationToken);
        var replayMatched = StateHash.Compute(replayed.State) == liveHash;

        return new MatchReport(
            coordinator.SessionId,
            seed,
            coordinator.Public.Lifecycle == SessionLifecycle.Finished,
            coordinator.Public.TurnNumber,
            commands,
            stopped,
            coordinator.Public.FinalResult,
            problems,
            replayMatched,
            System.Diagnostics.Stopwatch.GetElapsedTime(started));
    }

    private static SessionSetup BuildSetup(int seatCount, AiDifficulty difficulty)
    {
        var colors = Enum.GetValues<PlayerColor>();
        var seats = ImmutableArray.CreateBuilder<Seat>(seatCount);

        for (var index = 0; index < seatCount; index++)
        {
            seats.Add(new Seat(
                new SeatId(index + 1),
                $"{colors[index]} computer",
                colors[index],
                SeatKind.Computer,
                difficulty));
        }

        var built = seats.ToImmutable();
        return new SessionSetup(SessionId.New(), built, built[0].SeatId, VerificationMode.Manual);
    }
}
