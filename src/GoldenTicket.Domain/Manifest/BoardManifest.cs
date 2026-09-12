using System.Collections.Immutable;

namespace GoldenTicket.Domain.Manifest;

/// <summary>A city node on the supported board.</summary>
public sealed record CityDefinition(CityId StableId, string DisplayName);

/// <summary>
/// One claimable lane. <see cref="RequiredCardKind"/> is null for a grey route, which may be paid
/// with any single colour plus locomotives (DESIGN 6.1).
/// </summary>
public sealed record RouteDefinition(
    RouteId RouteId,
    CityId CityA,
    CityId CityB,
    int Length,
    TrainCardKind? RequiredCardKind,
    string? ParallelGroupId,
    string? DisplayLaneLabel)
{
    public bool IsGray => RequiredCardKind is null;

    public CityId OtherEndpoint(CityId from) =>
        from == CityA ? CityB
        : from == CityB ? CityA
        : throw new ArgumentException($"{from} is not an endpoint of {RouteId}.", nameof(from));

    public bool Touches(CityId city) => city == CityA || city == CityB;
}

/// <summary>A destination ticket definition.</summary>
public sealed record TicketDefinition(TicketId TicketId, CityId CityA, CityId CityB, int Points);

/// <summary>How many physical cards of one kind exist in the match supply.</summary>
public sealed record TrainCardDefinition(TrainCardKind CardKind, int Multiplicity);

/// <summary>
/// Numeric rules values for the pinned profile. DESIGN 6.3 keeps these in reviewed data rather
/// than scattered through executable code.
/// </summary>
public sealed record RulesConstants(
    int MinPlayers,
    int MaxPlayers,
    int StartingTrainsPerSeat,
    int StartingTrainCards,
    int SetupTicketOffer,
    int SetupTicketMinimumKeep,
    int InGameTicketOffer,
    int InGameTicketMinimumKeep,
    int FaceUpMarketSize,
    int TrainCardsPerDrawTurn,
    int LocomotiveMarketResetThreshold,
    int FinalRoundTrainThreshold,
    ImmutableDictionary<int, int> RouteScores,
    int LongestRouteBonus,
    int ParallelRouteClosedAtOrBelowPlayers)
{
    /// <summary>Points awarded for claiming a route of <paramref name="length"/> spaces.</summary>
    public int ScoreForLength(int length) =>
        RouteScores.TryGetValue(length, out var score)
            ? score
            : throw new ArgumentOutOfRangeException(
                nameof(length), length, "The pinned profile defines no score for this route length.");
}

/// <summary>Edition identity shown during onboarding (DESIGN 3.3 step 4).</summary>
public sealed record EditionDescriptor(
    string DisplayName,
    ImmutableArray<string> ProductCodes,
    string Rulebook);

/// <summary>
/// DESIGN 6.3: the board data is independently auditable. Until a reviewer has checked every
/// connection, colour, lane and ticket value against the physical edition, this stays
/// <c>unaudited</c> and the application says so out loud.
/// </summary>
public sealed record DataAuditRecord(string Status, string Note, string? Reviewer, string? ReviewedOn)
{
    public bool IsAudited => string.Equals(Status, "audited", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The versioned board and card data package (DESIGN 6.3). Geometry is intentionally absent until
/// it is digitised from a controlled developer capture; no placeholder coordinates are shipped.
/// </summary>
public sealed class BoardManifest
{
    private readonly Dictionary<CityId, CityDefinition> _citiesById;
    private readonly Dictionary<RouteId, RouteDefinition> _routesById;
    private readonly Dictionary<TicketId, TicketDefinition> _ticketsById;
    private readonly Dictionary<CityId, ImmutableArray<RouteDefinition>> _routesByCity;
    private readonly Dictionary<string, ImmutableArray<RouteDefinition>> _routesByParallelGroup;

    public BoardManifest(
        string profileId,
        int schemaVersion,
        int rulesPolicyVersion,
        string dataHash,
        EditionDescriptor edition,
        DataAuditRecord dataAudit,
        ImmutableArray<CityDefinition> cities,
        ImmutableArray<RouteDefinition> routes,
        ImmutableArray<TicketDefinition> tickets,
        ImmutableArray<TrainCardDefinition> trainCardDefinitions,
        RulesConstants rulesConstants)
    {
        ProfileId = profileId;
        SchemaVersion = schemaVersion;
        RulesPolicyVersion = rulesPolicyVersion;
        DataHash = dataHash;
        Edition = edition;
        DataAudit = dataAudit;
        Cities = cities;
        Routes = routes;
        Tickets = tickets;
        TrainCardDefinitions = trainCardDefinitions;
        RulesConstants = rulesConstants;

        _citiesById = cities.ToDictionary(c => c.StableId);
        _routesById = routes.ToDictionary(r => r.RouteId);
        _ticketsById = tickets.ToDictionary(t => t.TicketId);

        _routesByCity = cities.ToDictionary(
            c => c.StableId,
            c => routes.Where(r => r.Touches(c.StableId)).ToImmutableArray());

        _routesByParallelGroup = routes
            .Where(r => r.ParallelGroupId is not null)
            .GroupBy(r => r.ParallelGroupId!)
            .ToDictionary(g => g.Key, g => g.ToImmutableArray());

        Validate();
    }

    public string ProfileId { get; }
    public int SchemaVersion { get; }
    public int RulesPolicyVersion { get; }
    public string DataHash { get; }
    public EditionDescriptor Edition { get; }
    public DataAuditRecord DataAudit { get; }
    public ImmutableArray<CityDefinition> Cities { get; }
    public ImmutableArray<RouteDefinition> Routes { get; }
    public ImmutableArray<TicketDefinition> Tickets { get; }
    public ImmutableArray<TrainCardDefinition> TrainCardDefinitions { get; }
    public RulesConstants RulesConstants { get; }

    public CityDefinition City(CityId id) => _citiesById[id];

    public RouteDefinition Route(RouteId id) => _routesById[id];

    public TicketDefinition Ticket(TicketId id) => _ticketsById[id];

    public bool TryGetRoute(RouteId id, out RouteDefinition route) => _routesById.TryGetValue(id, out route!);

    public ImmutableArray<RouteDefinition> RoutesAt(CityId city) => _routesByCity[city];

    /// <summary>Every lane of a parallel group, including <paramref name="route"/> itself.</summary>
    public ImmutableArray<RouteDefinition> ParallelLanesOf(RouteDefinition route) =>
        route.ParallelGroupId is null
            ? [route]
            : _routesByParallelGroup[route.ParallelGroupId];

    /// <summary>Lanes of the same parallel group other than <paramref name="route"/>.</summary>
    public IEnumerable<RouteDefinition> SiblingLanesOf(RouteDefinition route) =>
        ParallelLanesOf(route).Where(r => r.RouteId != route.RouteId);

    /// <summary>Total number of card instances in the match supply.</summary>
    public int TotalTrainCards => TrainCardDefinitions.Sum(d => d.Multiplicity);

    /// <summary>
    /// A route in words: both endpoint cities and, for a parallel route, the exact lane. DESIGN 4.5
    /// requires an instruction to identify the specific lane, never just the city pair.
    /// </summary>
    public string Describe(RouteId routeId)
    {
        var route = Route(routeId);
        var lane = route.DisplayLaneLabel is null ? "" : $" ({route.DisplayLaneLabel})";
        return $"{City(route.CityA).DisplayName} - {City(route.CityB).DisplayName}{lane}";
    }

    private void Validate()
    {
        if (Cities.IsDefaultOrEmpty) throw new InvalidDataException("The manifest lists no cities.");
        if (Routes.IsDefaultOrEmpty) throw new InvalidDataException("The manifest lists no routes.");
        if (Tickets.IsDefaultOrEmpty) throw new InvalidDataException("The manifest lists no tickets.");

        if (_citiesById.Count != Cities.Length) throw new InvalidDataException("Duplicate city id.");
        if (_routesById.Count != Routes.Length) throw new InvalidDataException("Duplicate route id.");
        if (_ticketsById.Count != Tickets.Length) throw new InvalidDataException("Duplicate ticket id.");

        foreach (var route in Routes)
        {
            if (!_citiesById.ContainsKey(route.CityA) || !_citiesById.ContainsKey(route.CityB))
                throw new InvalidDataException($"Route {route.RouteId} references an unknown city.");
            if (route.CityA == route.CityB)
                throw new InvalidDataException($"Route {route.RouteId} starts and ends at the same city.");
            if (!RulesConstants.RouteScores.ContainsKey(route.Length))
                throw new InvalidDataException($"Route {route.RouteId} has unscorable length {route.Length}.");
        }

        foreach (var ticket in Tickets)
        {
            if (!_citiesById.ContainsKey(ticket.CityA) || !_citiesById.ContainsKey(ticket.CityB))
                throw new InvalidDataException($"Ticket {ticket.TicketId} references an unknown city.");
            if (ticket.CityA == ticket.CityB)
                throw new InvalidDataException($"Ticket {ticket.TicketId} has equal endpoints.");
            if (ticket.Points <= 0)
                throw new InvalidDataException($"Ticket {ticket.TicketId} has a non-positive value.");
        }

        foreach (var (groupId, lanes) in _routesByParallelGroup)
        {
            if (lanes.Length < 2)
                throw new InvalidDataException($"Parallel group {groupId} has fewer than two lanes.");
            var first = lanes[0];
            foreach (var lane in lanes)
            {
                if (lane.Length != first.Length ||
                    lane.CityA != first.CityA ||
                    lane.CityB != first.CityB)
                {
                    throw new InvalidDataException(
                        $"Parallel group {groupId} mixes endpoints or lengths.");
                }
            }
        }

        foreach (var definition in TrainCardDefinitions)
        {
            if (definition.Multiplicity <= 0)
                throw new InvalidDataException($"Card kind {definition.CardKind} has no copies.");
        }

        if (TrainCardDefinitions.Select(d => d.CardKind).Distinct().Count() != TrainCardDefinitions.Length)
            throw new InvalidDataException("Duplicate train card kind in the manifest.");
    }
}
