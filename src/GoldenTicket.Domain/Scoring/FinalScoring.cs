using System.Collections.Immutable;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Domain.Scoring;

/// <summary>
/// Exact final scoring from confirmed domain state (DESIGN 7.2 invariant 11). Route points are
/// recomputed here rather than trusted from the running total, which is also what invariant 14
/// asks for.
/// </summary>
public static class FinalScoring
{
    public static FinalResult Compute(GameState state)
    {
        var manifest = state.Manifest;
        var constants = manifest.RulesConstants;

        var trails = state.Seats.ToDictionary(
            seat => seat.SeatId,
            seat => LongestTrail.Compute(manifest, state.RoutesOwnedBy(seat.SeatId)));

        var longestTrailLength = trails.Values.Max(trail => trail.Length);

        // DESIGN 6.1: the bonus is awarded to all tied holders. A zero-length trail wins nothing.
        var bonusHolders = longestTrailLength > 0
            ? trails.Where(pair => pair.Value.Length == longestTrailLength)
                .Select(pair => pair.Key)
                .ToImmutableArray()
            : ImmutableArray<SeatId>.Empty;

        var scores = ImmutableArray.CreateBuilder<SeatScore>(state.Seats.Length);
        foreach (var seat in state.Seats)
        {
            var owned = state.RoutesOwnedBy(seat.SeatId).ToArray();
            var routePoints = owned.Sum(routeId => constants.ScoreForLength(manifest.Route(routeId).Length));

            var connectivity = SeatConnectivity.Build(manifest, owned);
            var completed = ImmutableArray.CreateBuilder<TicketId>();
            var incomplete = ImmutableArray.CreateBuilder<TicketId>();
            var gained = 0;
            var lost = 0;

            foreach (var ticketId in state.TicketsOf(seat.SeatId))
            {
                var ticket = manifest.Ticket(ticketId);
                if (connectivity.Completes(ticket))
                {
                    completed.Add(ticketId);
                    gained += ticket.Points;
                }
                else
                {
                    incomplete.Add(ticketId);
                    lost += ticket.Points;
                }
            }

            var trail = trails[seat.SeatId];
            var holdsBonus = bonusHolders.Contains(seat.SeatId);

            scores.Add(new SeatScore(
                seat.SeatId,
                routePoints,
                completed.ToImmutable(),
                incomplete.ToImmutable(),
                gained,
                lost,
                trail.Length,
                trail.Witness,
                holdsBonus,
                holdsBonus ? constants.LongestRouteBonus : 0));
        }

        var final = scores.ToImmutable();
        var (winners, explanation) = ResolveWinners(final);

        return new FinalResult(
            final,
            winners,
            longestTrailLength,
            bonusHolders,
            SharedVictory: winners.Length > 1,
            explanation);
    }

    /// <summary>
    /// DESIGN 6.1 tie-breaks: highest total, then most completed tickets, then the longest-path
    /// bonus holder. DESIGN 6.4: if that still leaves several seats, the result is a shared
    /// victory, recorded as an explicit product clarification rather than an arbitrary pick.
    /// </summary>
    private static (ImmutableArray<SeatId> Winners, string Explanation) ResolveWinners(
        ImmutableArray<SeatScore> scores)
    {
        var topTotal = scores.Max(score => score.Total);
        var candidates = scores.Where(score => score.Total == topTotal).ToImmutableArray();
        if (candidates.Length == 1)
            return ([candidates[0].SeatId], "Highest final score.");

        var topTickets = candidates.Max(score => score.CompletedTicketCount);
        var byTickets = candidates.Where(score => score.CompletedTicketCount == topTickets).ToImmutableArray();
        if (byTickets.Length == 1)
            return ([byTickets[0].SeatId], "Tied on points; decided by the most completed destination tickets.");

        var withBonus = byTickets.Where(score => score.HoldsLongestRouteBonus).ToImmutableArray();
        if (withBonus.Length == 1)
            return ([withBonus[0].SeatId], "Tied on points and tickets; decided by the longest continuous route.");

        var shared = (withBonus.Length > 1 ? withBonus : byTickets)
            .Select(score => score.SeatId)
            .ToImmutableArray();

        return (shared,
            "Tied on points, completed tickets and the longest continuous route. " +
            "GoldenTicket records this as a shared victory (DESIGN 6.4 product clarification).");
    }
}
