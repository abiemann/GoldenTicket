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
    ImmutableArray<RouteId> MissingRoutes,
    double EstimatedTurns = 0);

/// <summary>A bounded working plan for the seat's tickets, with shared route and card costs.</summary>
public sealed record NetworkPlan(
    ImmutableArray<TicketPlan> Tickets,
    IReadOnlyDictionary<RouteId, double> RouteValue,
    int TotalMissingTrains,
    double EstimatedTurns = 0)
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
    public static NetworkPlan Plan(SeatView view, BoardManifest manifest) =>
        Plan(view, manifest, view.Tickets);

    public static NetworkPlan Plan(SeatView view, BoardManifest manifest, CancellationToken cancellationToken) =>
        Plan(view, manifest, view.Tickets, cancellationToken);

    /// <summary>
    /// Plans a small portfolio using only this seat's cards and the visible board. Independent
    /// paths and a bounded set of shared-network construction orders compete on completion time.
    /// Shared routes, cards and locomotives are charged once for the whole portfolio.
    /// </summary>
    public static NetworkPlan Plan(SeatView view, BoardManifest manifest,
        IEnumerable<TicketId> tickets, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var usable = UsableRoutes(view, manifest);
        var ids = tickets.Distinct().ToArray();
        if (ids.Length == 0) return new NetworkPlan([], new Dictionary<RouteId, double>(), 0, 0);
        var independent = Build(ids, shareRoutes: false);
        if (ids.Length == 1) return independent;
        var best = independent;
        var bestTurns = ComparableTurns(best);
        // At most four further passes over the tiny board, regardless of ticket count. These
        // orders cover valuable backbones and cheap connectors without factorial search.
        var orders = new[]
        {
            ids,
            ids.Reverse().ToArray(),
            ids.OrderByDescending(id => manifest.Ticket(id).Points).ThenBy(id => id.Value, StringComparer.Ordinal).ToArray(),
            independent.Tickets.OrderBy(ticket => ticket.EstimatedTurns)
                .ThenBy(ticket => ticket.TicketId.Value, StringComparer.Ordinal).Select(ticket => ticket.TicketId).ToArray(),
        };
        var seenOrders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var order in orders)
        {
            if (!seenOrders.Add(string.Join("\0", order.Select(id => id.Value)))) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = Build(order, shareRoutes: true);
            var candidateTurns = ComparableTurns(candidate);
            var candidateFeasible = candidate.TotalMissingTrains <= view.TrainsRemaining;
            var bestFeasible = best.TotalMissingTrains <= view.TrainsRemaining;
            if (candidateFeasible && !bestFeasible || candidateFeasible == bestFeasible &&
                (candidateTurns < bestTurns ||
                 candidateTurns == bestTurns && candidate.TotalMissingTrains < best.TotalMissingTrains))
            {
                best = candidate;
                bestTurns = candidateTurns;
            }
        }
        return best;

        // A blocked commitment makes the whole portfolio impossible, but must not erase the
        // timing differences between ways of completing its remaining reachable tickets.
        double ComparableTurns(NetworkPlan plan) => double.IsFinite(plan.EstimatedTurns) ? plan.EstimatedTurns :
            EstimateTurns(view, manifest, plan.Tickets.Where(ticket => ticket.Reachable).SelectMany(ticket => ticket.MissingRoutes));

        NetworkPlan Build(IEnumerable<TicketId> order, bool shareRoutes)
        {
            var byId = new Dictionary<TicketId, TicketPlan>();
            var planned = new HashSet<RouteId>();
            foreach (var ticketId in order)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ticket = manifest.Ticket(ticketId);
                var path = CheapestPath(view, manifest, usable, ticket.CityA, ticket.CityB,
                    preferTurns: true, shareRoutes ? planned : null, cancellationToken);
                var missing = MissingRoutes(path);
                var missingTrains = missing.Sum(routeId => manifest.Route(routeId).Length);
                if (path is not null && missingTrains > view.TrainsRemaining)
                {
                    // A hand-friendly detour must not hide a shorter path this seat can still
                    // finish with its actual plastic trains.
                    var shortest = CheapestPath(view, manifest, usable, ticket.CityA, ticket.CityB,
                        preferTurns: false, null, cancellationToken);
                    var shorterMissing = MissingRoutes(shortest);
                    if (shorterMissing.Sum(routeId => manifest.Route(routeId).Length) <= view.TrainsRemaining)
                    {
                        path = shortest;
                        missing = shorterMissing;
                        missingTrains = missing.Sum(routeId => manifest.Route(routeId).Length);
                    }
                }

                if (path is null)
                {
                    byId[ticketId] = new TicketPlan(ticketId, ticket.Points, false, false, int.MaxValue, [], double.PositiveInfinity);
                    continue;
                }
                byId[ticketId] = new TicketPlan(ticketId, ticket.Points, missing.IsEmpty, true,
                    missingTrains, missing, EstimateTurns(view, manifest, missing));
                planned.UnionWith(missing);
            }

            var plans = ids.Select(id => byId[id]).ToImmutableArray();
            var routeValue = new Dictionary<RouteId, double>();
            foreach (var ticket in plans.Where(ticket => ticket.Reachable && !ticket.AlreadyComplete))
            {
                var share = ticket.Points / (double)Math.Max(1, ticket.MissingTrains);
                foreach (var routeId in ticket.MissingRoutes)
                    routeValue[routeId] = routeValue.GetValueOrDefault(routeId) + share * manifest.Route(routeId).Length;
            }
            return new NetworkPlan(plans, routeValue, planned.Sum(id => manifest.Route(id).Length),
                plans.Any(ticket => !ticket.Reachable) ? double.PositiveInfinity : EstimateTurns(view, manifest, planned));
        }

        ImmutableArray<RouteId> MissingRoutes((int Cost, ImmutableArray<RouteId> Routes)? path) => path is null ? [] :
            [.. path.Value.Routes.Where(id => !view.Public.RouteOwners.TryGetValue(id, out var owner) || owner != view.SeatId)];
    }

    /// <summary>
    /// One action per claim plus half a turn per missing card. A card may contribute to only one
    /// route, including grey routes; locomotives cover only the residual deficit once. This is a
    /// transparent optimistic draw estimate, not knowledge of the deck's future cards.
    /// </summary>
    public static double EstimateTurns(SeatView view, BoardManifest manifest, IEnumerable<RouteId> routeIds)
    {
        var routes = routeIds.Distinct().Select(manifest.Route).ToArray();
        var held = Enum.GetValues<TrainCardKind>().Where(kind => kind != TrainCardKind.Locomotive)
            .ToDictionary(kind => kind, view.CountOf);
        var missing = 0;
        foreach (var route in routes.Where(route => route.RequiredCardKind is not null))
        {
            var color = route.RequiredCardKind!.Value;
            var available = Math.Min(route.Length, held[color]);
            held[color] -= available;
            missing += route.Length - available;
        }
        foreach (var route in routes.Where(route => route.RequiredCardKind is null).OrderByDescending(route => route.Length))
        {
            var color = held.OrderByDescending(entry => Math.Min(entry.Value, route.Length))
                .ThenByDescending(entry => entry.Value).ThenBy(entry => entry.Key).First().Key;
            var available = Math.Min(route.Length, held[color]);
            held[color] -= available;
            missing += route.Length - available;
        }
        missing = Math.Max(0, missing - view.CountOf(TrainCardKind.Locomotive));
        return routes.Length + missing / 2.0;
    }

    /// <summary>
    /// Estimates the trains a ticket still needs, for a seat that does not yet hold it. Used when
    /// deciding whether to keep a freshly offered ticket.
    /// </summary>
    public static int EstimateCost(SeatView view, BoardManifest manifest, TicketDefinition ticket)
    {
        var usable = UsableRoutes(view, manifest);
        var path = CheapestPath(view, manifest, usable, ticket.CityA, ticket.CityB, preferTurns: false);
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
    /// Dijkstra over cities. Owned/shared planned edges are free; new routes cost either trains
    /// (the human-facing minimum-train estimate) or claim/draw time (the computer's working plan).
    /// Returns the routes along the cheapest path, or null when the cities cannot be connected.
    /// </summary>
    private static (int Cost, ImmutableArray<RouteId> Routes)? CheapestPath(
        SeatView view, BoardManifest manifest, List<RouteDefinition> usable, CityId from, CityId to,
        bool preferTurns, ISet<RouteId>? planned = null, CancellationToken cancellationToken = default)
    {
        var hand = Enum.GetValues<TrainCardKind>().ToDictionary(kind => kind, view.CountOf);
        var mostColorCards = hand.Where(entry => entry.Key != TrainCardKind.Locomotive).Max(entry => entry.Value);
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
            cancellationToken.ThrowIfCancellationRequested();
            if (!settled.Add(city)) continue;
            if (city.Equals(to)) break;
            if (!adjacency.TryGetValue(city, out var edges)) continue;

            foreach (var route in edges)
            {
                var next = route.OtherEndpoint(city);
                if (settled.Contains(next)) continue;

                var owned = view.Public.RouteOwners.TryGetValue(route.RouteId, out var owner) && owner == view.SeatId;
                var held = route.RequiredCardKind is { } required ? hand[required] : mostColorCards;
                var deficit = Math.Max(0, route.Length - held - hand[TrainCardKind.Locomotive]);
                var step = cost + (owned || planned?.Contains(route.RouteId) == true ? 0 :
                    preferTurns ? 100 + deficit * 50 + route.Length * 2 : route.Length);

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
