using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoldenTicket.Domain.Manifest;

/// <summary>
/// Reads the reviewed data package from disk. DESIGN 6.3 keeps board data out of executable code;
/// DESIGN 14.5 applies the same principle to model bundles. No code is loaded from this file.
/// </summary>
public static class ManifestLoader
{
    public const string ClassicUsFileName = "classic-us-v1.json";
    public const string ClassicUsProfileId = "ttr-us-classic-en-v1";
    public const int SupportedSchemaVersion = 1;
    public const int SupportedRulesPolicyVersion = 1;

    /// <summary>Loads the pinned classic North America profile from the default data location.</summary>
    public static BoardManifest LoadClassicUs() => LoadClassicUs(LocateClassicUs());

    /// <summary>
    /// The runtime entry point accepts only a hashed profile this rules engine implements.
    /// Generic Load remains available to developer tools preparing a new data checksum.
    /// </summary>
    public static BoardManifest LoadClassicUs(string path)
    {
        var manifest = Load(path);
        if (!string.Equals(manifest.ProfileId, ClassicUsProfileId, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported board profile '{manifest.ProfileId}'. Expected {ClassicUsProfileId}.");

        if (manifest.SchemaVersion != SupportedSchemaVersion)
            throw new InvalidDataException($"Unsupported board schema version {manifest.SchemaVersion}.");

        if (manifest.RulesPolicyVersion != SupportedRulesPolicyVersion)
            throw new InvalidDataException($"Unsupported rules policy version {manifest.RulesPolicyVersion}.");

        if (string.IsNullOrWhiteSpace(manifest.DataHash))
            throw new InvalidDataException("The runtime board manifest must record its validated dataHash.");

        return manifest;
    }

    public static BoardManifest Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var manifest = new BoardManifest(
            profileId: root.GetProperty("profileId").GetString()!,
            schemaVersion: root.GetProperty("schemaVersion").GetInt32(),
            rulesPolicyVersion: root.GetProperty("rulesPolicyVersion").GetInt32(),
            dataHash: root.TryGetProperty("dataHash", out var hash) ? hash.GetString() ?? "" : "",
            edition: ReadEdition(root.GetProperty("edition")),
            dataAudit: ReadAudit(root.GetProperty("dataAudit")),
            cities: ReadCities(root.GetProperty("cities")),
            routes: ReadRoutes(root.GetProperty("routes")),
            tickets: ReadTickets(root.GetProperty("tickets")),
            trainCardDefinitions: ReadCardDefinitions(root.GetProperty("trainCardDefinitions")),
            rulesConstants: ReadConstants(root.GetProperty("rulesConstants")));

        var computed = ComputeDataHash(manifest);
        if (!string.IsNullOrEmpty(manifest.DataHash) &&
            !string.Equals(manifest.DataHash, computed, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The data manifest at '{path}' does not match its recorded dataHash. " +
                $"Recorded {manifest.DataHash}, computed {computed}. " +
                "Board data must be reviewed and re-hashed deliberately, never edited in place.");
        }

        return manifest;
    }

    /// <summary>
    /// Hashes the reviewed content (not the file's formatting) so the checksum survives
    /// reformatting but not a changed connection, colour, lane, ticket value, or constant.
    /// </summary>
    public static string ComputeDataHash(BoardManifest manifest)
    {
        var builder = new StringBuilder();
        var invariant = CultureInfo.InvariantCulture;

        builder.Append("profile:").Append(manifest.ProfileId)
            .Append('|').Append(manifest.SchemaVersion.ToString(invariant))
            .Append('|').Append(manifest.RulesPolicyVersion.ToString(invariant)).Append('\n');

        foreach (var city in manifest.Cities.OrderBy(c => c.StableId.Value, StringComparer.Ordinal))
            builder.Append("city:").Append(city.StableId.Value).Append('\n');

        foreach (var route in manifest.Routes.OrderBy(r => r.RouteId.Value, StringComparer.Ordinal))
        {
            builder.Append("route:").Append(route.RouteId.Value)
                .Append('|').Append(route.CityA.Value)
                .Append('|').Append(route.CityB.Value)
                .Append('|').Append(route.Length.ToString(invariant))
                .Append('|').Append(route.RequiredCardKind?.ToString() ?? "-")
                .Append('|').Append(route.ParallelGroupId ?? "-")
                .Append('\n');
        }

        foreach (var ticket in manifest.Tickets.OrderBy(t => t.TicketId.Value, StringComparer.Ordinal))
        {
            builder.Append("ticket:").Append(ticket.TicketId.Value)
                .Append('|').Append(ticket.CityA.Value)
                .Append('|').Append(ticket.CityB.Value)
                .Append('|').Append(ticket.Points.ToString(invariant))
                .Append('\n');
        }

        foreach (var card in manifest.TrainCardDefinitions.OrderBy(c => (int)c.CardKind))
        {
            builder.Append("card:").Append(card.CardKind.ToString())
                .Append('|').Append(card.Multiplicity.ToString(invariant)).Append('\n');
        }

        var constants = manifest.RulesConstants;
        builder.Append("constants:")
            .Append(constants.MinPlayers).Append('|').Append(constants.MaxPlayers)
            .Append('|').Append(constants.StartingTrainsPerSeat)
            .Append('|').Append(constants.StartingTrainCards)
            .Append('|').Append(constants.SetupTicketOffer)
            .Append('|').Append(constants.SetupTicketMinimumKeep)
            .Append('|').Append(constants.InGameTicketOffer)
            .Append('|').Append(constants.InGameTicketMinimumKeep)
            .Append('|').Append(constants.FaceUpMarketSize)
            .Append('|').Append(constants.TrainCardsPerDrawTurn)
            .Append('|').Append(constants.LocomotiveMarketResetThreshold)
            .Append('|').Append(constants.FinalRoundTrainThreshold)
            .Append('|').Append(constants.LongestRouteBonus)
            .Append('|').Append(constants.ParallelRouteClosedAtOrBelowPlayers)
            .Append('\n');

        foreach (var (length, score) in constants.RouteScores.OrderBy(p => p.Key))
            builder.Append("score:").Append(length).Append('=').Append(score).Append('\n');

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return "sha256:" + Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// Finds the shipped data file beside the binaries, falling back to the repository layout so
    /// tests and tools run from a source checkout without a copy step.
    /// </summary>
    public static string LocateClassicUs()
    {
        var relative = Path.Combine("data", "classic-us", ClassicUsFileName);

        var beside = Path.Combine(AppContext.BaseDirectory, relative);
        if (File.Exists(beside)) return beside;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find '{relative}'. The reviewed board data package must ship with the application.");
    }

    private static EditionDescriptor ReadEdition(JsonElement element) => new(
        element.GetProperty("displayName").GetString()!,
        [.. element.GetProperty("productCodes").EnumerateArray().Select(e => e.GetString()!)],
        element.GetProperty("rulebook").GetString()!);

    private static DataAuditRecord ReadAudit(JsonElement element) => new(
        element.GetProperty("status").GetString()!,
        element.GetProperty("note").GetString()!,
        element.GetProperty("reviewer").GetString(),
        element.GetProperty("reviewedOn").GetString());

    private static ImmutableArray<CityDefinition> ReadCities(JsonElement element) =>
    [
        .. element.EnumerateArray().Select(e => new CityDefinition(
            new CityId(e.GetProperty("stableId").GetString()!),
            e.GetProperty("displayName").GetString()!))
    ];

    private static ImmutableArray<RouteDefinition> ReadRoutes(JsonElement element) =>
    [
        .. element.EnumerateArray().Select(e => new RouteDefinition(
            new RouteId(e.GetProperty("routeId").GetString()!),
            new CityId(e.GetProperty("cityA").GetString()!),
            new CityId(e.GetProperty("cityB").GetString()!),
            e.GetProperty("length").GetInt32(),
            ReadCardKind(e.GetProperty("requiredCardKind")),
            e.GetProperty("parallelGroupId").GetString(),
            e.GetProperty("displayLaneLabel").GetString()))
    ];

    private static ImmutableArray<TicketDefinition> ReadTickets(JsonElement element) =>
    [
        .. element.EnumerateArray().Select(e => new TicketDefinition(
            new TicketId(e.GetProperty("ticketId").GetString()!),
            new CityId(e.GetProperty("cityA").GetString()!),
            new CityId(e.GetProperty("cityB").GetString()!),
            e.GetProperty("points").GetInt32()))
    ];

    private static ImmutableArray<TrainCardDefinition> ReadCardDefinitions(JsonElement element) =>
    [
        .. element.EnumerateArray().Select(e => new TrainCardDefinition(
            Enum.Parse<TrainCardKind>(e.GetProperty("cardKind").GetString()!, ignoreCase: false),
            e.GetProperty("multiplicity").GetInt32()))
    ];

    private static RulesConstants ReadConstants(JsonElement element)
    {
        var scores = element.GetProperty("routeScores")
            .EnumerateObject()
            .ToImmutableDictionary(
                p => int.Parse(p.Name, CultureInfo.InvariantCulture),
                p => p.Value.GetInt32());

        return new RulesConstants(
            element.GetProperty("minPlayers").GetInt32(),
            element.GetProperty("maxPlayers").GetInt32(),
            element.GetProperty("startingTrainsPerSeat").GetInt32(),
            element.GetProperty("startingTrainCards").GetInt32(),
            element.GetProperty("setupTicketOffer").GetInt32(),
            element.GetProperty("setupTicketMinimumKeep").GetInt32(),
            element.GetProperty("inGameTicketOffer").GetInt32(),
            element.GetProperty("inGameTicketMinimumKeep").GetInt32(),
            element.GetProperty("faceUpMarketSize").GetInt32(),
            element.GetProperty("trainCardsPerDrawTurn").GetInt32(),
            element.GetProperty("locomotiveMarketResetThreshold").GetInt32(),
            element.GetProperty("finalRoundTrainThreshold").GetInt32(),
            scores,
            element.GetProperty("longestRouteBonus").GetInt32(),
            element.GetProperty("parallelRouteClosedAtOrBelowPlayers").GetInt32());
    }

    private static TrainCardKind? ReadCardKind(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null
            ? null
            : Enum.Parse<TrainCardKind>(element.GetString()!, ignoreCase: false);
}
