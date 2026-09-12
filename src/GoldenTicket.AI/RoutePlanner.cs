using System.Collections.Immutable;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Projections;

namespace GoldenTicket.AI;

/// <summary>What one destination ticket still costs this seat, and which routes would deliver it.</summary>
public sealed record TicketPlan(
    TicketId TicketId,
    int Points,
    bool AlreadyComplete,
    bool Reachable,
    int MissingTrains,
    ImmutableArray<RouteId> MissingRoutes);

/// <summary>The seat's whole network plan: every ticket's cheapest remaining path.</summary>
public sealed record NetworkPlan(
    ImmutableArray<TicketPlan> Tickets,
    IReadOnlyDictionary<RouteId, double> RouteValue,
    int TotalMissingTrains)
{
    public IEnumerable<TicketPlan> Unfinished => Tickets.Where(t => !t.AlreadyComplete);

    public double ValueOf(RouteId routeId) => RouteValue.GetValueOrDefault(routeId);
}

/// <summary>
/// Estimates useful paths through owned, unclaimed and opponent-blocked edges (DESIGN 15.2). It
/// works from a <see cref="SeatView"/> and the public board data only, so it cannot consult an
/// opponent's real ticket list.
/// </summary>
public static class RoutePlanner
{
    /// <summary>Builds the plan for this seat's current tickets.</summary>
    public static NetworkPlan Plan(SeatView view, BoardManifest manifest)
    {
        var usable = UsableRoutes(view, manifest);
        var plans = ImmutableArray.CreateBuilder<TicketPlan>();
        var routeValue = new Dictionary<RouteId, double>();

        foreach (var ticketId in view.Tickets)
        {
            var ticket = manifest.Ticket(ticketId);
            var path = CheapestPath(view, manifest, usable, ticket.CityA, ticket.CityB);

            if (path is null)
            {
                plans.Add(new TicketPlan(ticketId, ticket.Points, false, false, int.MaxValue, []));
                continue;
            }

            var missing = path.Value.Routes
                .Where(routeId => !view.Public.RouteOwners.TryGetValue(routeId, out var owner) || owner != view.SeatId)
                .ToImmutableArray();

            var missingTrains = missing.Sum(routeId => manifest.Route(routeId).Length);
            var complete = missing.IsEmpty;

            plans.Add(new TicketPlan(ticketId, ticket.Points, complete, true, missingTrains, missing));

            if (complete) continue;

            // A route matters in proportion to what the ticket pays and how little is left to do.
            var share = ticket.Points / (double)Math.Max(1, missingTrains);
            foreach (var routeId in missing)
                routeValue[routeId] = routeValue.GetValueOrDefault(routeId) + share * manifest.Route(routeId).Length;
        }

        var total = plans.Where(p => !p.AlreadyComplete && p.Reachable).Sum(p => p.MissingTrains);
        return new NetworkPlan(plans.ToImmutable(), routeValue, total);
    }

    /// <summary>
    /// Estimates the trains a ticket still needs, for a seat that does not yet hold it. Used when
    /// deciding whether to keep a freshly offered ticket.
    /// </summary>
    public static int EstimateCost(SeatView view, BoardManifest manifest, TicketDefinition ticket)
    {
        var usable = UsableRoutes(view, manifest);
        var path = CheapestPath(view, manifest, usable, ticket.CityA, ticket.CityB);
        if (path is null) return int.MaxValue;

        return path.Value.Routes
            .Where(routeId => !view.Public.RouteOwners.TryGetValue(routeId, out var owner) || owner != view.SeatId)
            .Sum(routeId => manifest.Route(routeId).Length);
    }

    /// <summary>
    /// Routes this seat could still use: its own, and unclaimed ones whose parallel lane rule has not
    /// closed them. Opponent-owned routes are excluded exactly as the board excludes them.
    /// </summary>
    private static List<RouteDefinition> UsableRoutes(SeatView view, BoardManifest manifest)
    {
        var owners = view.Public.RouteOwners;
        var closesTwin = view.Public.Seats.Length <= manifest.RulesConstants.ParallelRouteClosedAtOrBelowPlayers;
        var usable = new List<RouteDefinition>(manifest.Routes.Length);

        foreach (var route in manifest.Routes)
        {
            if (owners.TryGetValue(route.RouteId, out var owner))
            {
                if (owner == view.SeatId) usable.Add(route);
                continue;
            }

            if (route.ParallelGroupId is not null)
            {
                var blocked = manifest.SiblingLanesOf(route).Any(sibling =>
                    owners.TryGetValue(sibling.RouteId, out var siblingOwner) &&
                    (closesTwin || siblingOwner == view.SeatId));

                if (blocked) continue;
            }

            usable.Add(route);
        }

        return usable;
    }

    /// <summary>
    /// Dijkstra over cities where an already-owned route is free and anything else costs its trains.
    /// Returns the routes along the cheapest path, or null when the cities cannot be connected.
    /// </summary>
    private static (int Cost, ImmutableArray<RouteId> Routes)? CheapestPath(
        SeatView view, BoardManifest manifest, List<RouteDefinition> usable, CityId from, CityId to)
    {
        var adjacency = new Dictionary<CityId, List<RouteDefinition>>();
        foreach (var route in usable)
        {
            if (!adjacency.TryGetValue(route.CityA, out var a)) adjacency[route.CityA] = a = [];
            if (!adjacency.TryGetValue(route.CityB, out var b)) adjacency[route.CityB] = b = [];
            a.Add(route);
            b.Add(route);
        }

        var distance = new Dictionary<CityId, int> { [from] = 0 };
        var cameFrom = new Dictionary<CityId, (CityId City, RouteDefinition Route)>();
        var queue = new PriorityQueue<CityId, int>();
        queue.Enqueue(from, 0);

        var settled = new HashSet<CityId>();

        while (queue.TryDequeue(out var city, out var cost))
        {
            if (!settled.Add(city)) continue;
            if (city.Equals(to)) break;
            if (!adjacency.TryGetValue(city, out var edges)) continue;

            foreach (var route in edges)
            {
                var next = route.OtherEndpoint(city);
                if (settled.Contains(next)) continue;

                var owned = view.Public.RouteOwners.TryGetValue(route.RouteId, out var owner) && owner == view.SeatId;
                var step = cost + (owned ? 0 : route.Length);

                if (distance.TryGetValue(next, out var known) && known <= step) continue;

                distance[next] = step;
                cameFrom[next] = (city, route);
                queue.Enqueue(next, step);
            }
        }

        if (!distance.TryGetValue(to, out var total)) return null;

        var routes = ImmutableArray.CreateBuilder<RouteId>();
        var walk = to;
        while (!walk.Equals(from))
        {
            var (previous, route) = cameFrom[walk];
            routes.Add(route.RouteId);
            walk = previous;
        }

        routes.Reverse();
        return (total, routes.ToImmutable());
    }
}
