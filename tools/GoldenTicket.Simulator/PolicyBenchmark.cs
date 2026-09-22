using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoldenTicket.AI;
using GoldenTicket.Application;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Ai;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.Simulator;

public sealed record PolicyBenchmarkOptions(string BaselineAssembly, string Output, ulong FirstSeed,
    int Seeds, int[] SeatCounts, AiDifficulty[] Difficulties, bool SelfCheck, int MaximumSeconds);

/// <summary>Paired counterfactual matches: only the focal policy changes, with every seat position covered.</summary>
public static class PolicyBenchmark
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunAsync(PolicyBenchmarkOptions options)
    {
        if (options.Seeds is < 1 or > 1000 || options.MaximumSeconds is < 1 or > 3600 ||
            options.SeatCounts.Length == 0 || options.SeatCounts.Any(count => count is < 2 or > 5) ||
            options.Difficulties.Length == 0 || options.Difficulties.Any(value => !Enum.IsDefined(value)))
            throw new ArgumentException("Use 1–1000 seeds, 2–5 seats, valid difficulties, and a 1–3600 second limit.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.MaximumSeconds));
        var baseline = new PolicyAssembly(options.BaselineAssembly);
        try
        {
            var manifest = ManifestLoader.LoadClassicUs();
            var catalog = CardCatalog.FromManifest(manifest);
            var pairs = new List<BenchmarkPair>();
            var started = Stopwatch.GetTimestamp();
            var timedOut = false;
            var candidatePath = options.SelfCheck ? Path.GetFullPath(options.BaselineAssembly) : typeof(HeuristicAiPolicy).Assembly.Location;
            var candidateHash = HashFile(candidatePath);
            var baselineHash = HashFile(options.BaselineAssembly);
            var provenancePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(options.BaselineAssembly)!, "..", "baseline.json"));
            using var provenance = File.Exists(provenancePath) ? JsonDocument.Parse(File.ReadAllText(provenancePath)) : null;
            var output = Path.GetFullPath(options.Output);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var planned = options.Seeds * options.SeatCounts.Sum() * options.Difficulties.Length;
            Console.WriteLine($"Paired benchmark: {planned} pairs; rivals use frozen Standard policy; " +
                $"opponents appear Human only to each policy. Baseline SHA256 {baselineHash}.");
            void Save()
            {
                var report = new
                {
                    schemaVersion = 1, generatedUtc = DateTimeOffset.UtcNow, options,
                    baselineAssembly = Path.GetFullPath(options.BaselineAssembly), baselineSha256 = baselineHash,
                    baselineSourceProvenance = provenance?.RootElement,
                    candidateAssembly = candidatePath, candidateSha256 = candidateHash,
                    methodology = "Identical deck and per-seat policy seeds; one focal seat swapped old/new; all seat positions; " +
                        "all rivals frozen Standard policy; old/new execution order alternates; each policy sees rivals as Human so Aggressive targeting is exercised. " +
                        "No hidden opponent data is passed. Paired outcome deltas are descriptive, not a human skill rating.",
                    plannedPairs = planned, completedPairs = pairs.Count, timedOut,
                    elapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds,
                    summaries = Summarize(pairs), pairs
                };
                File.WriteAllText(output, JsonSerializer.Serialize(report, Json));
            }
            try
            {
                foreach (var difficulty in options.Difficulties)
                foreach (var seats in options.SeatCounts)
                for (var offset = 0; offset < options.Seeds; offset++)
                for (var focal = 1; focal <= seats; focal++)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var seed = checked(options.FirstSeed + (ulong)offset);
                    Task<BenchmarkGame> Old() => Play(manifest, catalog, seed, seats, new(focal), difficulty,
                        baseline.Create, baseline.Create, deadline.Token);
                    Task<BenchmarkGame> Candidate() => Play(manifest, catalog, seed, seats, new(focal), difficulty,
                        options.SelfCheck ? baseline.Create : () => new HeuristicAiPolicy(), baseline.Create, deadline.Token);
                    BenchmarkGame old, candidate;
                    if ((offset + focal) % 2 == 0) { old = await Old(); candidate = await Candidate(); }
                    else { candidate = await Candidate(); old = await Old(); }
                    var pair = new BenchmarkPair(seed, seats, focal, difficulty, old, candidate);
                    pairs.Add(pair);
                    Save();
                    Console.WriteLine($"{pairs.Count}/{planned}: {difficulty}, {seats} seats, seed {seed}, focal {focal}: " +
                        $"score {old.Score?.Total} → {candidate.Score?.Total}, completed tickets " +
                        $"{old.Score?.CompletedTickets} → {candidate.Score?.CompletedTickets}, " +
                        $"fallbacks {old.FocalDiagnostics.Fallbacks}/{candidate.FocalDiagnostics.Fallbacks}");
                    if (options.SelfCheck && old.OutcomeFingerprint != candidate.OutcomeFingerprint)
                    {
                        Console.Error.WriteLine("Old-versus-old determinism check failed. Inspect the saved pair before measuring improvements.");
                        return 1;
                    }
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                timedOut = true;
                Console.Error.WriteLine("Benchmark time bound reached; completed pairs are preserved in the report.");
            }
            Save();
            foreach (var summary in Summarize(pairs))
                Console.WriteLine($"{summary.Difficulty}/{summary.Seats} seats: {summary.Pairs} pairs, " +
                    $"mean score delta {summary.MeanScoreDelta:F2}, completed-ticket delta {summary.MeanCompletedTicketDelta:F2}, " +
                    $"focal decision p95 {summary.BaselineP95Milliseconds:F2} → {summary.CandidateP95Milliseconds:F2} ms.");
            Console.WriteLine($"Report: {output}");
            return timedOut || pairs.Any(pair => !Healthy(pair.Baseline) || !Healthy(pair.Candidate)) ? 1 : 0;
        }
        finally { baseline.Unload(); }
    }

    private static bool Healthy(BenchmarkGame game) => game.Completed && game.ReplayMatched &&
        game.InvariantProblems.Count == 0 && game.AllDiagnostics.RejectedCommands == 0;

    private static async Task<BenchmarkGame> Play(BoardManifest manifest, CardCatalog catalog, ulong seed,
        int seatCount, SeatId focal, AiDifficulty difficulty, Func<IAiPolicy> focalFactory,
        Func<IAiPolicy> rivalFactory, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        var colors = Enum.GetValues<PlayerColor>();
        var seats = Enumerable.Range(1, seatCount).Select(index => new Seat(new(index), $"Benchmark {index}",
            colors[index - 1], SeatKind.Computer, index == focal.Value ? difficulty : AiDifficulty.Standard)).ToImmutableArray();
        var store = new InMemorySessionStore();
        var coordinator = await GameCoordinator.CreateAsync(new GameRules(manifest, catalog), store,
            new(SessionId.New(), seats, new(1), VerificationMode.Manual), DeterministicRandom.SeedFrom(seed), token);
        var dispatch = new BenchmarkPolicy(focal, focalFactory(), rivalFactory());
        var driver = new ComputerSeatDriver(coordinator, dispatch, seed ^ 0xA5A5A5A5UL);
        var diagnostics = new List<AiDecisionReport>();
        AiDecisionReport? lastReport = null;
        var commands = 0;
        string? stopped = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var advanced = await driver.AdvanceAsync(token);
            commands += advanced;
            var recent = driver.Reports;
            var lastIndex = lastReport is null ? -1 : recent.ToList().FindIndex(report => ReferenceEquals(report, lastReport));
            if (lastReport is not null && lastIndex < 0)
                throw new InvalidOperationException("The bounded driver diagnostics overflowed between benchmark reads.");
            diagnostics.AddRange(recent.Skip(lastIndex + 1));
            if (recent.Count > 0) lastReport = recent[^1];
            var view = coordinator.Public;
            if (view.Lifecycle == SessionLifecycle.Finished) break;
            if (view.TurnPhase == TurnPhase.RulesDecisionRequired)
            {
                stopped = view.RulesDecision?.Code;
                break;
            }
            if (view.TurnNumber > MatchRunner.MaximumTurns || commands > 5000)
            {
                stopped = "SimulationBoundExceeded";
                break;
            }
            if (view.PendingClaim is { } pending)
            {
                var result = await coordinator.SubmitAsync(new SubmitClaimEvidence(
                    coordinator.NewEnvelope(pending.SeatId), pending.OperationId, EvidenceKind.ManualAttestation,
                    "benchmark", "synthetic operator placement"), token);
                if (!result.IsAccepted) { stopped = result.Result.Rejection?.Code; break; }
                commands++;
                continue;
            }
            if (advanced > 0) continue;
            stopped = "NoComputerAction:" + view.TurnPhase;
            break;
        }
        var problems = await coordinator.CheckInvariantsAsync(token);
        var replay = await store.RestoreAsync(coordinator.SessionId, manifest, catalog, token);
        var replayMatched = StateHash.Compute(replay.State) == await coordinator.ComputeStateHashAsync(token);
        var final = coordinator.Public.FinalResult;
        var scores = final?.Scores.Select(score => new BenchmarkScore(score.SeatId.Value, score.Total,
            score.RoutePoints, score.CompletedTicketCount, score.IncompleteTickets.Length,
            score.TicketPointsGained, score.TicketPointsLost, score.LongestTrailLength,
            final.Winners.Contains(score.SeatId))).ToArray() ?? [];
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            coordinator.Public.TurnNumber, commands, final,
            routes = coordinator.Public.RouteOwners.OrderBy(route => route.Key.Value).Select(route => new { route.Key, route.Value }),
            handCounts = coordinator.Public.Seats.Select(seat => seat.TrainCardCount)
        }, Json))));
        return new(coordinator.Public.Lifecycle == SessionLifecycle.Finished, stopped, coordinator.Public.TurnNumber,
            commands, scores.FirstOrDefault(score => score.Seat == focal.Value), scores,
            Diagnostics(diagnostics.Where(report => report.SeatId == focal)), Diagnostics(diagnostics),
            dispatch.HumanOpponentDecisions, problems, replayMatched, fingerprint,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, dispatch.AggressiveNetworkDecisions);
    }

    private sealed class BenchmarkPolicy(SeatId focal, IAiPolicy focalPolicy, IAiPolicy rivalPolicy) : IAiPolicy
    {
        internal int HumanOpponentDecisions { get; private set; }
        internal int AggressiveNetworkDecisions { get; private set; }
        public ValueTask<AiDecision> ChooseAsync(SeatView view, BoardManifest manifest, DecisionBudget budget,
            DeterministicRandom random, CancellationToken cancellationToken)
        {
            // Actual seats remain Computer so the standard driver executes them. Only public kind
            // labels change at the policy boundary; hands, decks and referee state stay inaccessible.
            var projected = view with { Public = view.Public with
            {
                Seats = view.Public.Seats.Select(seat => seat.SeatId == view.SeatId
                    ? seat : seat with { Kind = SeatKind.Human }).ToImmutableArray()
            } };
            if (budget.Difficulty == AiDifficulty.Aggressive)
            {
                HumanOpponentDecisions++;
                if (projected.Public.TurnPhase is TurnPhase.TurnStart or TurnPhase.AwaitingSecondTrainCard &&
                    projected.Public.Seats.Any(seat => seat.Kind == SeatKind.Human && seat.ClaimedRoutes.Length > 0))
                    AggressiveNetworkDecisions++;
            }
            return (view.SeatId == focal ? focalPolicy : rivalPolicy)
                .ChooseAsync(projected, manifest, budget, random, cancellationToken);
        }
    }

    private static BenchmarkDiagnostics Diagnostics(IEnumerable<AiDecisionReport> source)
    {
        var reports = source.ToArray();
        return new(reports.Length, reports.Count(report => report.UsedFallback),
            reports.Count(report => report.DecisionKind == "Rejected"),
            reports.Select(report => report.Elapsed.TotalMilliseconds).ToArray(),
            reports.Where(report => report.UsedFallback || report.DecisionKind == "Rejected")
                .Select(report => report.Note ?? report.DecisionKind).ToArray());
    }

    private static BenchmarkSummary[] Summarize(IReadOnlyList<BenchmarkPair> pairs) => pairs
        .GroupBy(pair => (pair.Seats, pair.Difficulty)).Select(group =>
        {
            var complete = group.Where(pair => pair.Baseline.Score is not null && pair.Candidate.Score is not null).ToArray();
            double Mean(Func<BenchmarkPair, double> value) => complete.Length == 0 ? 0 : complete.Average(value);
            return new BenchmarkSummary(group.Key.Seats, group.Key.Difficulty, group.Count(), complete.Length,
                Mean(pair => pair.Baseline.Score!.Total), Mean(pair => pair.Candidate.Score!.Total),
                Mean(pair => pair.Baseline.Score!.CompletedTickets), Mean(pair => pair.Candidate.Score!.CompletedTickets),
                Mean(pair => pair.Candidate.Score!.Total - pair.Baseline.Score!.Total),
                Mean(pair => pair.Candidate.Score!.CompletedTickets - pair.Baseline.Score!.CompletedTickets),
                Mean(pair => pair.Candidate.Score!.TicketPointsLost - pair.Baseline.Score!.TicketPointsLost),
                Mean(pair => pair.Baseline.Score!.Won ? 1 : 0), Mean(pair => pair.Candidate.Score!.Won ? 1 : 0),
                Percentile(group.SelectMany(pair => pair.Baseline.FocalDiagnostics.Milliseconds), .95),
                Percentile(group.SelectMany(pair => pair.Candidate.FocalDiagnostics.Milliseconds), .95),
                Percentile(group.SelectMany(pair => pair.Baseline.FocalDiagnostics.Milliseconds), 1),
                Percentile(group.SelectMany(pair => pair.Candidate.FocalDiagnostics.Milliseconds), 1),
                group.Sum(pair => pair.Baseline.AllDiagnostics.Fallbacks), group.Sum(pair => pair.Candidate.AllDiagnostics.Fallbacks));
        }).ToArray();

    private static double Percentile(IEnumerable<double> source, double fraction)
    {
        var ordered = source.Order().ToArray();
        return ordered.Length == 0 ? 0 : ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * fraction) - 1, 0, ordered.Length - 1)];
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

public sealed record BenchmarkScore(int Seat, int Total, int RoutePoints, int CompletedTickets, int IncompleteTickets,
    int TicketPointsGained, int TicketPointsLost, int LongestTrail, bool Won);
public sealed record BenchmarkDiagnostics(int Decisions, int Fallbacks, int RejectedCommands,
    IReadOnlyList<double> Milliseconds, IReadOnlyList<string> Problems);
public sealed record BenchmarkGame(bool Completed, string? StoppedBecause, int Turns, int Commands,
    BenchmarkScore? Score, IReadOnlyList<BenchmarkScore> AllScores, BenchmarkDiagnostics FocalDiagnostics,
    BenchmarkDiagnostics AllDiagnostics, int HumanOpponentDecisions, IReadOnlyList<string> InvariantProblems,
    bool ReplayMatched, string OutcomeFingerprint, double ElapsedMilliseconds, int AggressiveNetworkDecisions);
public sealed record BenchmarkPair(ulong Seed, int Seats, int FocalSeat, AiDifficulty Difficulty,
    BenchmarkGame Baseline, BenchmarkGame Candidate);
public sealed record BenchmarkSummary(int Seats, AiDifficulty Difficulty, int Pairs, int CompletedPairs,
    double BaselineMeanScore, double CandidateMeanScore, double BaselineMeanCompletedTickets, double CandidateMeanCompletedTickets,
    double MeanScoreDelta, double MeanCompletedTicketDelta, double MeanTicketPenaltyDelta,
    double BaselineWinRate, double CandidateWinRate, double BaselineP95Milliseconds, double CandidateP95Milliseconds,
    double BaselineMaxMilliseconds, double CandidateMaxMilliseconds,
    int BaselineFallbacks, int CandidateFallbacks);
