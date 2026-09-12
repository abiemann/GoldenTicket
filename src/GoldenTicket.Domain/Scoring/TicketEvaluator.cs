using GoldenTicket.Domain.Manifest;

namespace GoldenTicket.Domain.Scoring;

/// <summary>
/// DESIGN 6.5: ticket connectivity uses the acting seat's claimed-route graph, recomputed from the
/// owned edges rather than patched incrementally, so an undone claim cannot leave a stale union.
/// </summary>
public sealed class SeatConnectivity
{
    private readonly Dictionary<CityId, CityId> _parent = [];

    private SeatConnectivity(BoardManifest manifest, IEnumerable<RouteId> ownedRoutes)
    {
        foreach (var routeId in ownedRoutes)
        {
            var route = manifest.Route(routeId);
            Union(route.CityA, route.CityB);
        }
    }

    public static SeatConnectivity Build(BoardManifest manifest, IEnumerable<RouteId> ownedRoutes)
        => new(manifest, ownedRoutes);

    public bool AreConnected(CityId a, CityId b) =>
        _parent.ContainsKey(a) && _parent.ContainsKey(b) && Find(a).Equals(Find(b));

    public bool Completes(TicketDefinition ticket) => AreConnected(ticket.CityA, ticket.CityB);

    private CityId Find(CityId city)
    {
        if (!_parent.TryGetValue(city, out var seed))
        {
            _parent[city] = city;
            return city;
        }

        while (!seed.Equals(_parent[seed])) seed = _parent[seed];
        _parent[city] = seed;
        return seed;
    }

    private void Union(CityId a, CityId b)
    {
        var rootA = Find(a);
        var rootB = Find(b);
        if (!rootA.Equals(rootB)) _parent[rootA] = rootB;
    }
}
