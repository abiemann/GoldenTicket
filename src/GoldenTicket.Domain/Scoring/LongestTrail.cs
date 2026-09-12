using System.Collections.Immutable;
using GoldenTicket.Domain.Manifest;

namespace GoldenTicket.Domain.Scoring;

/// <summary>The exact longest continuous trail for one seat, with the trail that achieves it.</summary>
public sealed record TrailResult(int Length, ImmutableArray<RouteId> Witness)
{
    public static readonly TrailResult Empty = new(0, []);
}

/// <summary>
/// Raised instead of silently returning an approximation. DESIGN 6.5: a timeout approximation must
/// never determine the winner.
/// </summary>
public sealed class LongestTrailBudgetExceededException(int budget)
    : Exception($"The exact longest-trail search exceeded its budget of {budget:N0} expansions. " +
                "Official scoring must not fall back to an approximation.")
{
    public int Budget { get; } = budget;
}

/// <summary>
/// DESIGN 6.5: the longest route is a maximum weighted <em>edge-simple trail</em>. Cities may be
/// revisited; a route edge may not be reused. A shortest-path algorithm or a visited-city-only DFS
/// is incorrect, so this is an explicit search with exact memoisation over used-edge sets.
/// </summary>
public static class LongestTrail
{
    /// <summary>Expansion budget guarding against a pathological component. Never silently exceeded.</summary>
    public const int DefaultBudget = 20_000_000;

    public static TrailResult Compute(
        BoardManifest manifest,
        IEnumerable<RouteId> ownedRoutes,
        int budget = DefaultBudget)
    {
        var routes = ownedRoutes.Select(manifest.Route).ToArray();
        if (routes.Length == 0) return TrailResult.Empty;

        var best = TrailResult.Empty;
        foreach (var component in SplitIntoComponents(routes))
        {
            var candidate = SolveComponent(component, budget);
            if (candidate.Length > best.Length) best = candidate;
        }

        return best;
    }

    /// <summary>
    /// Connected components are solved separately: a trail cannot cross between them, and keeping
    /// them apart keeps each used-edge set small.
    /// </summary>
    private static List<RouteDefinition[]> SplitIntoComponents(RouteDefinition[] routes)
    {
        var parent = new Dictionary<CityId, CityId>();

        CityId Find(CityId city)
        {
            if (!parent.TryGetValue(city, out var seed))
            {
                parent[city] = city;
                return city;
            }

            while (!seed.Equals(parent[seed])) seed = parent[seed];
            parent[city] = seed;
            return seed;
        }

        foreach (var route in routes)
        {
            var a = Find(route.CityA);
            var b = Find(route.CityB);
            if (!a.Equals(b)) parent[a] = b;
        }

        return [.. routes.GroupBy(route => Find(route.CityA)).Select(group => group.ToArray())];
    }

    private static TrailResult SolveComponent(RouteDefinition[] routes, int budget)
    {
        if (routes.Length > 64)
        {
            throw new LongestTrailBudgetExceededException(budget);
        }

        var cities = routes
            .SelectMany(route => new[] { route.CityA, route.CityB })
            .Distinct()
            .ToArray();

        var cityIndex = cities
            .Select((city, index) => (city, index))
            .ToDictionary(pair => pair.city, pair => pair.index);

        // incident[c] = indices into routes touching city c
        var incident = new List<int>[cities.Length];
        for (var i = 0; i < incident.Length; i++) incident[i] = [];
        for (var edge = 0; edge < routes.Length; edge++)
        {
            incident[cityIndex[routes[edge].CityA]].Add(edge);
            incident[cityIndex[routes[edge].CityB]].Add(edge);
        }

        var memo = new Dictionary<(int City, ulong Used), int>();
        var expansions = 0;

        int Best(int city, ulong used)
        {
            if (memo.TryGetValue((city, used), out var cached)) return cached;

            if (++expansions > budget) throw new LongestTrailBudgetExceededException(budget);

            var best = 0;
            foreach (var edge in incident[city])
            {
                var bit = 1UL << edge;
                if ((used & bit) != 0) continue;

                var route = routes[edge];
                var next = cityIndex[route.OtherEndpoint(cities[city])];
                var candidate = route.Length + Best(next, used | bit);
                if (candidate > best) best = candidate;
            }

            // Written only after the state is fully explored, so no pruned partial result is
            // ever cached as an exact value (DESIGN 6.5).
            memo[(city, used)] = best;
            return best;
        }

        var bestLength = 0;
        var bestStart = 0;
        for (var city = 0; city < cities.Length; city++)
        {
            var length = Best(city, 0UL);
            if (length > bestLength)
            {
                bestLength = length;
                bestStart = city;
            }
        }

        return new TrailResult(bestLength, Reconstruct(bestStart, bestLength));

        ImmutableArray<RouteId> Reconstruct(int start, int target)
        {
            var witness = ImmutableArray.CreateBuilder<RouteId>();
            var city = start;
            var used = 0UL;
            var remaining = target;

            while (remaining > 0)
            {
                var advanced = false;
                foreach (var edge in incident[city])
                {
                    var bit = 1UL << edge;
                    if ((used & bit) != 0) continue;

                    var route = routes[edge];
                    var next = cityIndex[route.OtherEndpoint(cities[city])];
                    if (route.Length + Best(next, used | bit) != remaining) continue;

                    witness.Add(route.RouteId);
                    used |= bit;
                    remaining -= route.Length;
                    city = next;
                    advanced = true;
                    break;
                }

                if (!advanced)
                {
                    throw new InvalidOperationException(
                        "The longest-trail witness could not be reconstructed from exact values.");
                }
            }

            return witness.ToImmutable();
        }
    }
}
