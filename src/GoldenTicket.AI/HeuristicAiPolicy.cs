using System.Collections.Immutable;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Ai;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.AI;

/// <summary>
/// A deterministic, bounded opponent. Destination planning, card collection and payment share
/// one objective. Aggression uses resources that objective can spare. Only the owning SeatView
/// and public board enter the policy.
/// </summary>
public sealed class HeuristicAiPolicy : IAiPolicy
{
    public ValueTask<AiDecision> ChooseAsync(SeatView view, BoardManifest manifest,
        DecisionBudget budget, DeterministicRandom random, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var legal = LegalActionCalculator.For(view, manifest);
        if (legal.MustCommitTicketSelection)
            return ValueTask.FromResult(ChooseTickets(view, manifest, cancellationToken));

        var strategy = TicketStrategy.Build(view, manifest, cancellationToken);
        var blocking = budget.Difficulty == AiDifficulty.Aggressive
            ? AggressiveRoutePlanner.Evaluate(view, manifest, cancellationToken)
            : new Dictionary<RouteId, double>();
        var demand = ColorDemand(view, manifest, strategy, blocking);
        if (view.Public.TurnPhase == TurnPhase.AwaitingSecondTrainCard)
            return ValueTask.FromResult(PickCard(view, legal, demand));

        var claim = BestClaim(view, manifest, legal, strategy, blocking, budget, random, cancellationToken);
        if (claim is not null && (strategy.TurnsLeft <= 1 ||
            claim.Score >= (view.TrainsRemaining <= 8 ? 0 : ClaimThreshold(budget.Difficulty))))
            return ValueTask.FromResult<AiDecision>(new AiClaimRoute(claim.RouteId, claim.Payment));

        // Explore new tickets only with completed commitments, enough time and
        // a network already close to several possible destinations.
        if (legal.CanRequestTicketOffer && !strategy.Network.Unfinished.Any() &&
            view.TrainsRemaining >= 12 && strategy.TurnsLeft >= 6 &&
            manifest.Tickets.Count(ticket => !view.Tickets.Contains(ticket.TicketId) &&
                RoutePlanner.EstimateCost(view, manifest, ticket) <= 4) >= 3)
            return ValueTask.FromResult<AiDecision>(new AiDrawTickets());

        var card = PickCard(view, legal, demand);
        if (card is not AiNoDecision) return ValueTask.FromResult(card);
        if (claim is not null) return ValueTask.FromResult<AiDecision>(new AiClaimRoute(claim.RouteId, claim.Payment));
        if (legal.CanRequestTicketOffer) return ValueTask.FromResult<AiDecision>(new AiDrawTickets());
        return ValueTask.FromResult<AiDecision>(new AiNoDecision("No legal action is available to this seat."));
    }

    private static AiDecision ChooseTickets(SeatView view, BoardManifest manifest, CancellationToken token)
    {
        var setup = !view.SetupOffer.IsEmpty;
        var offered = setup ? view.SetupOffer : view.Offer?.Offered ?? [];
        if (offered.IsEmpty) return new AiNoDecision("No ticket offer is open.");
        var minimum = Math.Min(offered.Length, setup ? manifest.RulesConstants.SetupTicketMinimumKeep
            : view.Offer?.MinimumKeep ?? manifest.RulesConstants.InGameTicketMinimumKeep);
        var existing = RoutePlanner.Plan(view, manifest, token);
        var bestScore = double.NegativeInfinity;
        ImmutableArray<TicketId> kept = [];
        // The supported profile offers at most three tickets. Compare complete
        // required bundles, not independent point-per-train ratios.
        for (var mask = 1; mask < 1 << offered.Length; mask++)
        {
            token.ThrowIfCancellationRequested();
            var selected = offered.Where((_, index) => (mask & 1 << index) != 0).ToImmutableArray();
            if (selected.Length != minimum) continue;
            var plan = RoutePlanner.Plan(view, manifest, view.Tickets.Concat(selected), token);
            var missing = plan.Tickets.Count(ticket => !ticket.Reachable || ticket.MissingTrains > view.TrainsRemaining);
            // A blocked existing destination must not turn every candidate score into NaN.
            // Charge the reachable union separately; impossible commitments retain a penalty.
            var incrementalTurns = Math.Max(0, ReachableTurns(plan) - ReachableTurns(existing));
            var score = plan.Tickets.Where(ticket => selected.Contains(ticket.TicketId))
                .Sum(ticket => ticket.Reachable ? ticket.Points : -ticket.Points) - 1.75 * incrementalTurns
                - Math.Max(0, plan.TotalMissingTrains - view.TrainsRemaining) * 5 - missing * 100;
            if (score <= bestScore) continue;
            bestScore = score; kept = selected;
        }

        var current = RoutePlanner.Plan(view, manifest, view.Tickets.Concat(kept), token);
        foreach (var extra in offered.Except(kept).OrderByDescending(id => manifest.Ticket(id).Points))
        {
            var expanded = RoutePlanner.Plan(view, manifest, view.Tickets.Concat(kept).Append(extra), token);
            var additional = expanded.Tickets.First(ticket => ticket.TicketId == extra);
            // Extra commitments must be cheap extensions, leaving room for detours.
            if (!additional.Reachable || expanded.TotalMissingTrains > Math.Max(0, view.TrainsRemaining - 6) ||
                expanded.EstimatedTurns > TicketStrategy.RemainingTurns(view, manifest) * 0.8 ||
                expanded.EstimatedTurns - current.EstimatedTurns > Math.Max(1, additional.Points / 2.5)) continue;
            kept = kept.Add(extra); current = expanded;
        }
        return new AiKeepTickets(kept);

        double ReachableTurns(NetworkPlan plan) => RoutePlanner.EstimateTurns(view, manifest,
            plan.Tickets.Where(ticket => ticket.Reachable).SelectMany(ticket => ticket.MissingRoutes));
    }

    private static AiDecision PickCard(SeatView view, LegalActions legal, IReadOnlyDictionary<TrainCardKind, double> wanted)
    {
        var best = -1;
        var bestScore = 0.6;
        foreach (var slot in legal.DrawableFaceUpSlots)
        {
            if (view.Public.FaceUp[slot] is not { } kind) continue;
            // A visible locomotive costs both draws. A needed ordinary colour
            // is preferable, but a wild is better than unrelated colours.
            var score = kind == TrainCardKind.Locomotive ? 2.0 : wanted.GetValueOrDefault(kind);
            if (score <= bestScore) continue;
            bestScore = score; best = slot;
        }
        if (best >= 0) return new AiDrawTrainCard(best);
        if (legal.CanDrawBlindTrainCard) return new AiDrawTrainCard(null);
        if (!legal.DrawableFaceUpSlots.IsEmpty) return new AiDrawTrainCard(legal.DrawableFaceUpSlots[0]);
        return new AiNoDecision("No train card can be taken.");
    }

    private static Dictionary<TrainCardKind, double> ColorDemand(SeatView view, BoardManifest manifest,
        TicketStrategy strategy, IReadOnlyDictionary<RouteId, double> blocking)
    {
        var demand = new Dictionary<TrainCardKind, double>();
        var targets = strategy.Focus is { } focus
            ? focus.MissingRoutes.Select(manifest.Route)
            : manifest.Routes.Where(route => Available(view, manifest, route));
        var ranked = targets.Select(route =>
        {
            var color = PaymentColor(view, route);
            var held = view.CountOf(color);
            var needed = Math.Max(0, route.Length - held - view.CountOf(TrainCardKind.Locomotive));
            var benefit = strategy.HasObjective ? strategy.Priorities.GetValueOrDefault(route.RouteId)
                : manifest.RulesConstants.ScoreForLength(route.Length) + blocking.GetValueOrDefault(route.RouteId);
            return (Route: route, Color: color, Held: held, Needed: needed, Priority: benefit / (needed + 1));
        }).Where(target => target.Needed > 0).OrderByDescending(target => target.Priority)
          .ThenBy(target => target.Route.RouteId.Value, StringComparer.Ordinal).ToArray();

        // Fund one concrete next link. Summing every possible blocking route made
        // broad human networks drown out the few cards needed to complete a ticket.
        if (ranked.FirstOrDefault() is var next && next.Route is not null)
            demand[next.Color] = 3 + Math.Min(2, next.Held / (double)next.Route.Length * 2);
        if (strategy.HasObjective)
            foreach (var target in ranked.Skip(1)) demand.TryAdd(target.Color, 1.5);
        return demand;
    }

    private static TrainCardKind PaymentColor(SeatView view, RouteDefinition route) =>
        route.RequiredCardKind ?? Enum.GetValues<TrainCardKind>()
            .Where(kind => kind != TrainCardKind.Locomotive)
            .OrderByDescending(view.CountOf)
            .ThenByDescending(kind => view.Public.FaceUp.Count(card => card == kind))
            .ThenBy(kind => kind).First();

    private static bool Available(SeatView view, BoardManifest manifest, RouteDefinition route) =>
        route.Length <= view.TrainsRemaining && !view.Public.RouteOwners.ContainsKey(route.RouteId) &&
        !manifest.SiblingLanesOf(route).Any(sibling => view.Public.RouteOwners.TryGetValue(sibling.RouteId, out var owner) &&
            (owner == view.SeatId || view.Public.Seats.Length <= manifest.RulesConstants.ParallelRouteClosedAtOrBelowPlayers));

    private sealed record ScoredClaim(RouteId RouteId, PaymentOption Payment, double Score);

    private static ScoredClaim? BestClaim(SeatView view, BoardManifest manifest, LegalActions legal,
        TicketStrategy strategy, IReadOnlyDictionary<RouteId, double> blocking, DecisionBudget budget,
        DeterministicRandom random, CancellationToken token)
    {
        ScoredClaim? best = null;
        ScoredClaim? bestObjective = null;
        var noise = budget.Difficulty == AiDifficulty.Relaxed ? 2.5 : 0.4;
        foreach (var claim in legal.Claims)
        {
            token.ThrowIfCancellationRequested();
            var route = manifest.Route(claim.RouteId);
            var onObjective = strategy.Focus?.MissingRoutes.Contains(claim.RouteId) == true;
            // Consume one stable tie-break sample for every legal claim.
            var tieBreak = (random.NextInt(1000) / 1000.0 - 0.5) * noise;
            var payments = RankedPayments(view, manifest, claim, strategy);
            var payment = strategy.HasObjective && !onObjective
                ? payments.FirstOrDefault(option => strategy.CanSpendOnDetour(view, manifest, route, option, token))
                : payments.First();
            if (payment is null) continue;

            double score = manifest.RulesConstants.ScoreForLength(route.Length) - payment.Locomotives * 1.8;
            if (onObjective)
            {
                score += strategy.Priorities.GetValueOrDefault(claim.RouteId) * 2.5;
                if (strategy.Focus!.MissingRoutes.Length == 1) score += strategy.Focus.Points * 2;
                score += blocking.GetValueOrDefault(claim.RouteId) * 0.35;
            }
            else
            {
                score += blocking.GetValueOrDefault(claim.RouteId) * (strategy.HasObjective ? 0.65 : 1);
                if (strategy.HasObjective) score -= 2;
            }
            if (route.ParallelGroupId is not null &&
                view.Public.Seats.Length <= manifest.RulesConstants.ParallelRouteClosedAtOrBelowPlayers) score += 1.5;
            if (budget.Difficulty == AiDifficulty.Challenging) score += route.Length * 0.4;
            score += tieBreak;
            if (onObjective && (bestObjective is null || score > bestObjective.Score))
                bestObjective = new(claim.RouteId, payment, score);
            if (best is null || score > best.Score) best = new(claim.RouteId, payment, score);
        }
        // Build a funded destination link before spending a turn on unrelated interference.
        return bestObjective ?? best;
    }

    private static IEnumerable<PaymentOption> RankedPayments(SeatView view, BoardManifest manifest, LegalClaim claim,
        TicketStrategy strategy)
    {
        var protectedColors = strategy.Focus?.MissingRoutes.Where(id => id != claim.RouteId)
            .Select(manifest.Route).Where(route => route.RequiredCardKind.HasValue)
            .GroupBy(route => route.RequiredCardKind!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(route => route.Length))
            ?? new Dictionary<TrainCardKind, int>();
        return claim.Payments.OrderBy(option => option.Locomotives * 2.5 +
                (option.IsAllLocomotives ? 0 : Math.Max(0,
                    protectedColors.GetValueOrDefault(option.Color) - (view.CountOf(option.Color) - option.ColorCards)) -
                    Math.Max(0, protectedColors.GetValueOrDefault(option.Color) - view.CountOf(option.Color))))
            .ThenByDescending(option => option.IsAllLocomotives ? -1 : view.CountOf(option.Color) - option.ColorCards)
            .ThenBy(option => option.Color);
    }

    private static double ClaimThreshold(AiDifficulty difficulty) => difficulty switch
    {
        AiDifficulty.Relaxed => 6.0,
        AiDifficulty.Challenging => 3.0,
        _ => 4.5,
    };
}
