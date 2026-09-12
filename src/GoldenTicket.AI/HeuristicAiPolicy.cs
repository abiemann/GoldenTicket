using System.Collections.Immutable;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Ai;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.AI;

/// <summary>
/// The deterministic heuristic opponent from DESIGN 15.1. It evaluates ticket connectivity, the cost
/// of what is still missing, usable card sets, route points, remaining trains and tempo, and it
/// always chooses a complete legal action.
///
/// It reads a <see cref="SeatView"/> and the public board data. It never sees another seat's cards,
/// the deck order, or the referee's random state, so DESIGN 5.3 holds by construction rather than by
/// convention.
/// </summary>
public sealed class HeuristicAiPolicy : IAiPolicy
{
    public ValueTask<AiDecision> ChooseAsync(
        SeatView view,
        BoardManifest manifest,
        DecisionBudget budget,
        DeterministicRandom random,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var legal = LegalActionCalculator.For(view, manifest);

        AiDecision decision =
            legal.MustCommitTicketSelection ? ChooseTickets(view, manifest, budget) :
            view.Public.TurnPhase == TurnPhase.AwaitingSecondTrainCard ? ChooseSecondCard(view, manifest, legal) :
            ChooseTurnAction(view, manifest, legal, budget, random);

        return ValueTask.FromResult(decision);
    }

    // ---- Destination tickets ------------------------------------------------------------------

    /// <summary>
    /// Keeps tickets whose value justifies the trains they still need, always respecting the minimum
    /// the profile requires.
    /// </summary>
    private static AiDecision ChooseTickets(SeatView view, BoardManifest manifest, DecisionBudget budget)
    {
        var isSetup = !view.SetupOffer.IsEmpty;
        var offered = isSetup ? view.SetupOffer : view.Offer?.Offered ?? [];
        if (offered.IsEmpty) return new AiNoDecision("No ticket offer is open.");

        var minimum = isSetup
            ? manifest.RulesConstants.SetupTicketMinimumKeep
            : view.Offer?.MinimumKeep ?? manifest.RulesConstants.InGameTicketMinimumKeep;

        var trains = view.TrainsRemaining;
        var committed = RoutePlanner.Plan(view, manifest).TotalMissingTrains;

        // A cautious seat late in the game keeps less; an early seat can afford a longer ticket.
        var appetite = budget.Difficulty switch
        {
            AiDifficulty.Relaxed => 0.75,
            AiDifficulty.Challenging => 1.15,
            _ => 1.0,
        };

        var ranked = offered
            .Select(ticketId =>
            {
                var ticket = manifest.Ticket(ticketId);
                var cost = RoutePlanner.EstimateCost(view, manifest, ticket);
                return (TicketId: ticketId, Ticket: ticket, Cost: cost, Value: Attractiveness(ticket, cost));
            })
            .OrderByDescending(entry => entry.Value)
            .ToList();

        var kept = ImmutableArray.CreateBuilder<TicketId>();
        var budgetTrains = trains - committed;

        foreach (var entry in ranked)
        {
            var affordable = entry.Cost != int.MaxValue && entry.Cost <= budgetTrains;
            var worthwhile = entry.Value >= 1.0 / appetite;

            if (kept.Count < minimum || (affordable && worthwhile))
            {
                kept.Add(entry.TicketId);
                if (entry.Cost != int.MaxValue) budgetTrains -= entry.Cost;
            }
        }

        // The ranked list is ordered by value, so the forced minimum is taken from the best of it.
        return new AiKeepTickets(kept.ToImmutable());

        static double Attractiveness(TicketDefinition ticket, int cost) =>
            cost == int.MaxValue ? -1.0 : ticket.Points / (double)Math.Max(1, cost);
    }

    // ---- Train cards --------------------------------------------------------------------------

    private static AiDecision ChooseSecondCard(SeatView view, BoardManifest manifest, LegalActions legal)
    {
        var wanted = ColorDemand(view, manifest);
        return PickCard(view, legal, wanted);
    }

    /// <summary>
    /// Prefers a face-up card the plan actually needs, then a locomotive, then a blind draw. A blind
    /// draw is worth more than a useless face-up card.
    /// </summary>
    private static AiDecision PickCard(SeatView view, LegalActions legal, IReadOnlyDictionary<TrainCardKind, double> wanted)
    {
        var best = -1;
        var bestScore = 0.6; // A blind draw is worth roughly this much.

        foreach (var slot in legal.DrawableFaceUpSlots)
        {
            if (view.Public.FaceUp[slot] is not { } kind) continue;

            var score = kind == TrainCardKind.Locomotive
                ? 2.0
                : wanted.GetValueOrDefault(kind);

            if (score <= bestScore) continue;

            bestScore = score;
            best = slot;
        }

        if (best >= 0) return new AiDrawTrainCard(best);
        if (legal.CanDrawBlindTrainCard) return new AiDrawTrainCard(null);
        if (!legal.DrawableFaceUpSlots.IsEmpty) return new AiDrawTrainCard(legal.DrawableFaceUpSlots[0]);

        return new AiNoDecision("No train card can be taken.");
    }

    /// <summary>How much this seat still wants each colour, given the routes its plan needs.</summary>
    private static Dictionary<TrainCardKind, double> ColorDemand(SeatView view, BoardManifest manifest)
    {
        var plan = RoutePlanner.Plan(view, manifest);
        var demand = new Dictionary<TrainCardKind, double>();

        foreach (var (routeId, value) in plan.RouteValue)
        {
            var route = manifest.Route(routeId);

            if (route.RequiredCardKind is { } required)
            {
                var shortfall = Math.Max(0, route.Length - view.CountOf(required) - view.CountOf(TrainCardKind.Locomotive));
                if (shortfall > 0) demand[required] = demand.GetValueOrDefault(required) + value;
                continue;
            }

            // A grey route can use whichever colour the seat is closest to completing.
            foreach (var kind in Enum.GetValues<TrainCardKind>())
            {
                if (kind == TrainCardKind.Locomotive) continue;
                var held = view.CountOf(kind);
                if (held == 0 || held >= route.Length) continue;
                demand[kind] = demand.GetValueOrDefault(kind) + value * 0.35;
            }
        }

        return demand;
    }

    // ---- Turn choice --------------------------------------------------------------------------

    private static AiDecision ChooseTurnAction(
        SeatView view, BoardManifest manifest, LegalActions legal, DecisionBudget budget, DeterministicRandom random)
    {
        var plan = RoutePlanner.Plan(view, manifest);
        var trains = view.TrainsRemaining;

        var bestClaim = BestClaim(view, manifest, legal, plan, budget, random);

        if (bestClaim is { } claim)
        {
            // Late in the game, taking points now beats building a hand that will not be spent.
            var threshold = trains <= 8 ? 0.0 : ClaimThreshold(budget.Difficulty);
            if (claim.Score >= threshold) return new AiClaimRoute(claim.RouteId, claim.Payment);
        }

        // Drawing more tickets is only sensible with trains to spare and a plan that is nearly done.
        var unfinished = plan.Unfinished.Count();
        if (legal.CanRequestTicketOffer &&
            trains >= 20 &&
            unfinished == 0 &&
            view.Public.TurnNumber > view.Public.Seats.Length)
        {
            return new AiDrawTickets();
        }

        var card = PickCard(view, legal, ColorDemand(view, manifest));
        if (card is not AiNoDecision) return card;

        if (bestClaim is { } fallbackClaim) return new AiClaimRoute(fallbackClaim.RouteId, fallbackClaim.Payment);
        if (legal.CanRequestTicketOffer) return new AiDrawTickets();

        return new AiNoDecision("No legal action is available to this seat.");
    }

    private static double ClaimThreshold(AiDifficulty difficulty) => difficulty switch
    {
        AiDifficulty.Relaxed => 6.0,
        AiDifficulty.Challenging => 3.0,
        _ => 4.5,
    };

    private sealed record ScoredClaim(RouteId RouteId, PaymentOption Payment, double Score);

    /// <summary>
    /// Scores each affordable route by its immediate points, its contribution to an unfinished
    /// ticket, and what the payment costs in flexibility.
    /// </summary>
    private static ScoredClaim? BestClaim(
        SeatView view,
        BoardManifest manifest,
        LegalActions legal,
        NetworkPlan plan,
        DecisionBudget budget,
        DeterministicRandom random)
    {
        ScoredClaim? best = null;
        var constants = manifest.RulesConstants;
        var noise = budget.Difficulty == AiDifficulty.Relaxed ? 2.5 : 0.4;

        foreach (var claim in legal.Claims)
        {
            var route = manifest.Route(claim.RouteId);
            var payment = CheapestPayment(view, claim);

            double score = constants.ScoreForLength(route.Length);
            score += plan.ValueOf(claim.RouteId) * 2.5;

            // Locomotives are the scarcest card; spending them needs to be worth it.
            score -= payment.Locomotives * 1.8;

            // A contested lane that a rival could take first is worth grabbing sooner.
            if (route.ParallelGroupId is not null &&
                view.Public.Seats.Length <= constants.ParallelRouteClosedAtOrBelowPlayers)
            {
                score += 1.5;
            }

            if (budget.Difficulty == AiDifficulty.Challenging)
            {
                // Prefer routes that shorten several tickets at once, and long routes for the bonus.
                score += plan.Unfinished.Count(ticket => ticket.MissingRoutes.Contains(claim.RouteId)) * 1.2;
                score += route.Length * 0.4;
            }

            if (noise > 0) score += (random.NextInt(1000) / 1000.0 - 0.5) * noise;

            if (best is null || score > best.Score) best = new ScoredClaim(claim.RouteId, payment, score);
        }

        return best;
    }

    /// <summary>
    /// Picks the payment that keeps the most flexibility: fewest locomotives first, then the colour
    /// the seat has most spare of.
    /// </summary>
    private static PaymentOption CheapestPayment(SeatView view, LegalClaim claim) =>
        claim.Payments
            .OrderBy(option => option.Locomotives)
            .ThenByDescending(option => option.IsAllLocomotives ? -1 : view.CountOf(option.Color) - option.ColorCards)
            .ThenBy(option => option.Color)
            .First();
}
