using GoldenTicket.Domain;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Projections;

namespace GoldenTicket.AI;

/// <summary>
/// Values opportunities to interrupt visible human networks. No opponent hand or ticket inference
/// is needed: the input is the same public board and own-seat view used by the ordinary policy.
/// </summary>
internal static class AggressiveRoutePlanner
{
    // Every trail is real (no route may be reused), but this bounded search is a lower bound, not
    // the exact exponential longest-route scorer. Deterministic caps keep live decisions small.
    private const int TrailStatesPerCity = 256;

    public static IReadOnlyDictionary<RouteId, double> Evaluate(
        SeatView view, BoardManifest manifest, CancellationToken cancellationToken)
    {
        var values = new Dictionary<RouteId, double>();
        if (!view.IsActive || view.Public.Lifecycle != SessionLifecycle.Active ||
            view.Public.TurnPhase is not (TurnPhase.TurnStart or TurnPhase.AwaitingSecondTrainCard))
            return values;

        foreach (var human in view.Public.Seats.Where(seat =>
                     seat.Kind == SeatKind.Human && seat.SeatId != view.SeatId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owned = manifest.Routes.Where(route =>
                view.Public.RouteOwners.TryGetValue(route.RouteId, out var owner) && owner == human.SeatId).ToArray();
            if (owned.Length == 0 || human.TrainsRemaining == 0) continue;

            var graph = BuildGraph(owned);
            var components = Components(graph, cancellationToken);
            var trails = new Dictionary<CityId, int>();
            foreach (var city in graph.Keys.OrderBy(city => city.Value, StringComparer.Ordinal))
                trails[city] = TrailFrom(city, graph, cancellationToken);

            var componentBest = trails.GroupBy(entry => components[entry.Key])
                .ToDictionary(group => group.Key, group => group.Max(entry => entry.Value));
            var open = manifest.Routes.Where(route => route.Length <= human.TrainsRemaining &&
                IsAvailable(view.Public, manifest, route, human.SeatId)).ToArray();
            var exits = graph.Keys.ToDictionary(city => city, city => open.Where(route => route.Touches(city))
                .Select(route => route.OtherEndpoint(city)).Distinct().Count());

            foreach (var route in open)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (route.Length > view.TrainsRemaining ||
                    !IsAvailable(view.Public, manifest, route, view.SeatId)) continue;

                // At four/five players, taking one free parallel lane leaves the other available.
                // A human-owned sibling already supplies the connection, so neither case is a block.
                if (manifest.SiblingLanesOf(route).Any(sibling =>
                        view.Public.RouteOwners.TryGetValue(sibling.RouteId, out var owner) && owner == human.SeatId ||
                        view.Public.Seats.Length > manifest.RulesConstants.ParallelRouteClosedAtOrBelowPlayers &&
                        IsAvailable(view.Public, manifest, sibling, human.SeatId))) continue;

                var touchesA = graph.ContainsKey(route.CityA);
                var touchesB = graph.ContainsKey(route.CityB);
                if (!touchesA && !touchesB) continue;

                var endA = touchesA && graph[route.CityA].Count == 1;
                var endB = touchesB && graph[route.CityB].Count == 1;
                var bridges = touchesA && touchesB && components[route.CityA] != components[route.CityB];
                var scarcity = Math.Max(touchesA ? 1.0 / Math.Max(1, exits[route.CityA]) : 0,
                    touchesB ? 1.0 / Math.Max(1, exits[route.CityB]) : 0);

                // Nearby short links are cheap ways to obstruct a visible plan. Endpoints and
                // links joining two separate sections of that human's network are more useful.
                var value = route.Length <= 3 ? 4.0 / Math.Sqrt(route.Length) : 0;
                if (route.Length <= 3 && (endA || endB)) value += 1.5;
                if (bridges) value += 3.0;

                var trailA = trails.GetValueOrDefault(route.CityA);
                var trailB = trails.GetValueOrDefault(route.CityB);
                var usefulA = touchesA && trailA >= componentBest[components[route.CityA]] * 0.8;
                var usefulB = touchesB && trailB >= componentBest[components[route.CityB]] * 0.8;
                if (bridges && usefulA && usefulB)
                {
                    // This connector joins two substantial continuous trails, rather than just
                    // adding a branch near a large network. Count the smaller trail it unlocks.
                    value += Math.Min(5, (Math.Min(trailA, trailB) + route.Length) / 2.0);
                }
                else if (usefulA && !touchesB || usefulB && !touchesA)
                {
                    // Only reward an actual extension of a long trail. A high-degree interior
                    // city that cannot finish a long trail does not get the same bonus.
                    value += Math.Min(4, Math.Max(trailA, trailB) / 3.0);
                }

                if (value <= 0) continue;
                value += scarcity * 2;
                // Multiple humans must not cause an arbitrarily large bonus to overwhelm the
                // seat's own tickets, points, train stock and locomotive conservation.
                values[route.RouteId] = Math.Max(values.GetValueOrDefault(route.RouteId), Math.Min(12, value));
            }
        }

        return values;
    }

    private static bool IsAvailable(PublicView view, BoardManifest manifest, RouteDefinition route, SeatId seat)
    {
        if (view.RouteOwners.ContainsKey(route.RouteId)) return false;
        var closesTwin = view.Seats.Length <= manifest.RulesConstants.ParallelRouteClosedAtOrBelowPlayers;
        return !manifest.SiblingLanesOf(route).Any(sibling =>
            view.RouteOwners.TryGetValue(sibling.RouteId, out var owner) && (closesTwin || owner == seat));
    }

    private static Dictionary<CityId, List<RouteDefinition>> BuildGraph(IEnumerable<RouteDefinition> routes)
    {
        var graph = new Dictionary<CityId, List<RouteDefinition>>();
        foreach (var route in routes)
        {
            if (!graph.TryGetValue(route.CityA, out var a)) graph[route.CityA] = a = [];
            if (!graph.TryGetValue(route.CityB, out var b)) graph[route.CityB] = b = [];
            a.Add(route);
            b.Add(route);
        }
        // Stable order also favours finding long lower bounds early when a dense graph hits its cap.
        foreach (var edges in graph.Values)
            edges.Sort((a, b) => a.Length != b.Length ? b.Length.CompareTo(a.Length) :
                StringComparer.Ordinal.Compare(a.RouteId.Value, b.RouteId.Value));
        return graph;
    }

    private static Dictionary<CityId, int> Components(
        Dictionary<CityId, List<RouteDefinition>> graph, CancellationToken cancellationToken)
    {
        var components = new Dictionary<CityId, int>();
        var nextComponent = 0;
        foreach (var city in graph.Keys)
        {
            if (components.ContainsKey(city)) continue;
            var component = nextComponent++;
            var pending = new Stack<CityId>();
            components[city] = component;
            pending.Push(city);
            while (pending.TryPop(out var current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var route in graph[current])
                {
                    var next = route.OtherEndpoint(current);
                    if (components.TryAdd(next, component)) pending.Push(next);
                }
            }
        }
        return components;
    }

    private static int TrailFrom(
        CityId start, Dictionary<CityId, List<RouteDefinition>> graph, CancellationToken cancellationToken)
    {
        var remaining = TrailStatesPerCity;
        var best = 0;
        var visited = new HashSet<RouteId>();
        Walk(start, 0);
        return best;

        void Walk(CityId city, int length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            best = Math.Max(best, length);
            if (--remaining <= 0) return;
            foreach (var route in graph[city])
            {
                if (remaining <= 0) break;
                if (!visited.Add(route.RouteId)) continue;
                Walk(route.OtherEndpoint(city), length + route.Length);
                visited.Remove(route.RouteId);
            }
        }
    }
}
