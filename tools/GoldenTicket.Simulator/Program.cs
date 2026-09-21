using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Simulator;

// Headless tooling for the deterministic parts of GoldenTicket: it audits the reviewed board data
// and plays reproducible matches (DESIGN 15.6, 18.3 tools/GoldenTicket.Simulator).

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

return command switch
{
    "verify-data" => VerifyData(args),
    "simulate" => await SimulateAsync(args),
    _ => Help(),
};

static int Help()
{
    Console.WriteLine("""
        GoldenTicket simulator

          verify-data [--write-hash]
              Loads data/classic-us/classic-us-v1.json, reports its contents, and checks the
              recorded dataHash. --write-hash records the computed hash into the file.

          simulate [--games N] [--seed S] [--seats N] [--difficulty Relaxed|Standard|Challenging|Aggressive]
                   [--verbose]
              Plays reproducible all-computer matches and checks invariants and replay equality.
        """);

    return 0;
}

static int VerifyData(string[] args)
{
    var path = ManifestLoader.LocateClassicUs();
    Console.WriteLine($"Data package: {path}");

    BoardManifest manifest;
    try
    {
        manifest = ManifestLoader.Load(path);
    }
    catch (InvalidDataException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }

    var computed = ManifestLoader.ComputeDataHash(manifest);
    var parallelGroups = manifest.Routes.Where(r => r.ParallelGroupId is not null)
        .Select(r => r.ParallelGroupId!).Distinct().Count();

    Console.WriteLine($"  profile            {manifest.ProfileId} (schema {manifest.SchemaVersion}, " +
                      $"rules policy {manifest.RulesPolicyVersion})");
    Console.WriteLine($"  edition            {manifest.Edition.DisplayName} " +
                      $"[{string.Join(", ", manifest.Edition.ProductCodes)}]");
    Console.WriteLine($"  cities             {manifest.Cities.Length}");
    Console.WriteLine($"  routes             {manifest.Routes.Length} " +
                      $"({parallelGroups} parallel groups, " +
                      $"{manifest.Routes.Count(r => r.IsGray)} grey lanes)");
    Console.WriteLine($"  total track length {manifest.Routes.Sum(r => r.Length)} train spaces");
    Console.WriteLine($"  tickets            {manifest.Tickets.Length} " +
                      $"({manifest.Tickets.Min(t => t.Points)}-{manifest.Tickets.Max(t => t.Points)} points)");
    Console.WriteLine($"  train cards        {manifest.TotalTrainCards} " +
                      $"({manifest.TrainCardDefinitions.Length} kinds)");
    Console.WriteLine($"  computed dataHash  {computed}");
    Console.WriteLine($"  recorded dataHash  {(string.IsNullOrEmpty(manifest.DataHash) ? "(none)" : manifest.DataHash)}");

    foreach (var problem in AuditRoutes(manifest)) Console.WriteLine($"  ! {problem}");

    Console.WriteLine();
    Console.WriteLine($"  audit status       {manifest.DataAudit.Status.ToUpperInvariant()}" +
                      (manifest.DataAudit.IsAudited
                          ? $" by {manifest.DataAudit.Reviewer} on {manifest.DataAudit.ReviewedOn}"
                          : " - not yet checked against the physical board (DESIGN 6.3)"));

    if (!args.Contains("--write-hash")) return 0;

    // Write to the reviewed source file, not the copy that sits beside the binaries.
    var source = LocateSourceDataFile() ?? path;
    var document = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
    document["dataHash"] = computed;
    File.WriteAllText(source, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    Console.WriteLine($"\nRecorded dataHash {computed} into {source}");

    return 0;
}

/// <summary>Finds data/classic-us/ in the repository, by walking up from the working directory.</summary>
static string? LocateSourceDataFile()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "GoldenTicket.sln")))
        {
            var candidate = Path.Combine(
                directory.FullName, "data", "classic-us", ManifestLoader.ClassicUsFileName);

            return File.Exists(candidate) ? candidate : null;
        }

        directory = directory.Parent;
    }

    return null;
}

/// <summary>
/// Structural checks a reviewer can rely on. They cannot replace the physical audit DESIGN 6.3
/// requires, and they are not claimed to.
/// </summary>
static IEnumerable<string> AuditRoutes(BoardManifest manifest)
{
    foreach (var city in manifest.Cities)
    {
        var degree = manifest.RoutesAt(city.StableId).Length;
        if (degree == 0) yield return $"{city.DisplayName} has no routes.";
        if (degree == 1) yield return $"{city.DisplayName} has only one route; check the board.";
    }

    var duplicates = manifest.Routes
        .Where(route => route.ParallelGroupId is null)
        .GroupBy(route => route.CityA.Value + "|" + route.CityB.Value)
        .Where(group => group.Count() > 1);

    foreach (var group in duplicates)
        yield return $"{group.Key} appears {group.Count()} times without a parallel group id.";

    var unreachable = manifest.Tickets.Where(ticket =>
        manifest.RoutesAt(ticket.CityA).IsEmpty || manifest.RoutesAt(ticket.CityB).IsEmpty);

    foreach (var ticket in unreachable)
        yield return $"Ticket {ticket.TicketId} names a city with no routes.";
}

static async Task<int> SimulateAsync(string[] args)
{
    var games = ReadInt(args, "--games", 20);
    var seed = (ulong)ReadInt(args, "--seed", 1);
    var seats = ReadInt(args, "--seats", 4);
    var verbose = args.Contains("--verbose");

    var difficulty = Enum.TryParse<AiDifficulty>(ReadString(args, "--difficulty", "Standard"), true, out var parsed)
        ? parsed
        : AiDifficulty.Standard;

    var manifest = ManifestLoader.LoadClassicUs();
    var catalog = CardCatalog.FromManifest(manifest);
    var runner = new MatchRunner(manifest, catalog);

    Console.WriteLine($"{games} matches, {seats} computer seats, {difficulty}, seeds {seed}..{seed + (ulong)games - 1}");
    Console.WriteLine();

    var completed = 0;
    var failures = new List<string>();
    var totalTurns = 0;
    var elapsed = TimeSpan.Zero;

    for (var index = 0; index < games; index++)
    {
        var report = await runner.RunAsync(seed + (ulong)index, seats, difficulty);
        elapsed += report.Elapsed;
        totalTurns += report.Turns;

        if (report.Completed) completed++;
        else failures.Add($"seed {report.Seed}: {report.StoppedBecause}");

        if (report.InvariantProblems.Count > 0)
            failures.Add($"seed {report.Seed}: {string.Join("; ", report.InvariantProblems)}");

        if (!report.ReplayMatched)
            failures.Add($"seed {report.Seed}: replaying the journal did not reproduce the live state");

        if (!verbose || report.Result is not { } result) continue;

        Console.WriteLine($"seed {report.Seed,4}  turns {report.Turns,3}  commands {report.Commands,4}  " +
                          $"{report.Elapsed.TotalMilliseconds,7:F0} ms");

        foreach (var score in result.Scores.OrderByDescending(s => s.Total))
        {
            Console.WriteLine(
                $"    seat {score.SeatId.Value}  total {score.Total,4}  routes {score.RoutePoints,4}  " +
                $"tickets +{score.TicketPointsGained}/-{score.TicketPointsLost}  " +
                $"longest {score.LongestTrailLength}{(score.HoldsLongestRouteBonus ? "*" : " ")}  " +
                $"completed {score.CompletedTicketCount}/{score.CompletedTicketCount + score.IncompleteTickets.Length}");
        }

        Console.WriteLine($"    winner(s): {string.Join(", ", result.Winners.Select(w => w.Value))} - {result.TieBreakExplanation}");
        Console.WriteLine();
    }

    Console.WriteLine($"completed {completed}/{games}   mean turns {(games == 0 ? 0 : totalTurns / (double)games):F1}   " +
                      $"mean {(games == 0 ? 0 : elapsed.TotalMilliseconds / games):F0} ms/match");

    if (failures.Count == 0)
    {
        Console.WriteLine("invariants held and every journal replayed to the same state.");
        return 0;
    }

    Console.WriteLine();
    Console.WriteLine($"{failures.Count} problem(s):");
    foreach (var failure in failures.Take(40)) Console.WriteLine($"  {failure}");
    return 1;
}

static int ReadInt(string[] args, string name, int fallback) =>
    int.TryParse(ReadString(args, name, null), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value
        : fallback;

static string? ReadString(string[] args, string name, string? fallback)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}
