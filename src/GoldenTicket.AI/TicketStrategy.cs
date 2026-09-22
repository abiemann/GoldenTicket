using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Projections;

namespace GoldenTicket.AI;

/// <summary>A feasible destination objective and the cards/trains it protects from side trips.</summary>
internal sealed record TicketStrategy(NetworkPlan Network, TicketPlan? Focus,
    IReadOnlyDictionary<RouteId, double> Priorities, double TurnsLeft)
{
    public bool HasObjective => Focus is not null;

    public static TicketStrategy Build(SeatView view, BoardManifest manifest, CancellationToken token)
    {
        var all = RoutePlanner.Plan(view, manifest, token);
        var turnsLeft = RemainingTurns(view, manifest);
        // Rank each destination on its own before asking it to share another one's backbone.
        // Only a known final round is a hard deadline; a rough clock must not abandon every goal.
        var candidates = all.Unfinished.Select(ticket =>
                RoutePlanner.Plan(view, manifest, [ticket.TicketId], token).Tickets[0])
            .Where(ticket => ticket.Reachable && ticket.MissingTrains <= view.TrainsRemaining &&
                (view.Public.FinalRound is null || ticket.EstimatedTurns <= turnsLeft))
            .OrderByDescending(ticket => 2.0 * ticket.Points / Math.Max(1, ticket.EstimatedTurns) /
                Math.Max(1, ticket.EstimatedTurns / Math.Max(1, turnsLeft)))
            .ThenBy(ticket => ticket.MissingTrains).ThenBy(ticket => ticket.TicketId.Value, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0) return new(all, null, new Dictionary<RouteId, double>(), turnsLeft);

        var focus = candidates[0];
        var tickets = new List<TicketId> { focus.TicketId };
        var network = RoutePlanner.Plan(view, manifest, tickets, token);
        foreach (var ticket in candidates.Skip(1))
        {
            var combined = RoutePlanner.Plan(view, manifest, tickets.Append(ticket.TicketId), token);
            if (combined.TotalMissingTrains > view.TrainsRemaining || combined.EstimatedTurns > turnsLeft) continue;
            tickets.Add(ticket.TicketId);
            network = combined;
        }

        // Completing the primary ticket comes first. Shared links still help every ticket
        // in the feasible bundle, but a cheap unrelated claim cannot starve this objective.
        focus = network.Tickets.First(ticket => ticket.TicketId == focus.TicketId);
        var priorities = new Dictionary<RouteId, double>();
        foreach (var ticket in network.Unfinished)
        foreach (var route in ticket.MissingRoutes)
        {
            var value = 2.0 * ticket.Points / Math.Max(1, ticket.MissingRoutes.Length);
            priorities[route] = priorities.GetValueOrDefault(route) + value *
                (ticket.TicketId == focus.TicketId ? 1.0 : 0.3);
        }
        return new(network, focus, priorities, turnsLeft);
    }

    public static double RemainingTurns(SeatView view, BoardManifest manifest)
    {
        if (view.Public.FinalRound is { } final)
            return final.RemainingTurnsBySeat.GetValueOrDefault(view.SeatId);

        // Public train stock and hand sizes provide a rough clock, not knowledge of
        // anyone's cards: claims cost a turn, and replacing cards costs draw turns.
        return view.Public.Seats.Min(seat =>
        {
            var trainsToFinish = Math.Max(0, seat.TrainsRemaining - manifest.RulesConstants.FinalRoundTrainThreshold);
            return Math.Max(1, trainsToFinish / 3.5 + Math.Max(0, trainsToFinish - seat.TrainCardCount) / 2.0 + 1);
        });
    }

    public bool CanSpendOnDetour(SeatView view, BoardManifest manifest, RouteDefinition route,
        PaymentOption payment, CancellationToken token)
    {
        if (!HasObjective) return true;
        // Never take the trains reserved for an unfinished destination. Re-evaluate
        // after payment so a grey blocking link cannot consume its needed colour set.
        var spent = LegalActionCalculator.ResolveCards(view, payment).ToHashSet();
        var after = view with
        {
            Hand = [.. view.Hand.Where(card => !spent.Contains(card.Id))],
            Public = view.Public with
            {
                RouteOwners = view.Public.RouteOwners.Add(route.RouteId, view.SeatId),
                Seats = [.. view.Public.Seats.Select(seat => seat.SeatId != view.SeatId ? seat : seat with
                {
                    TrainsRemaining = seat.TrainsRemaining - route.Length,
                    TrainCardCount = seat.TrainCardCount - spent.Count,
                    ClaimedRoutes = seat.ClaimedRoutes.Add(route.RouteId),
                })],
            },
        };
        var remaining = RoutePlanner.Plan(after, manifest, Network.Tickets.Select(ticket => ticket.TicketId), token);
        return remaining.TotalMissingTrains <= after.TrainsRemaining &&
            remaining.EstimatedTurns <= Network.EstimatedTurns + 0.01 &&
            remaining.EstimatedTurns + 2 <= TurnsLeft;
    }
}
