using GoldenTicket.Domain.Model;

namespace GoldenTicket.Domain;

/// <summary>
/// Recomputes the DESIGN 7.2 invariants from confirmed state. These are checked in tests, in the
/// headless simulator, and after restore - not assumed because the engine "should" maintain them.
/// </summary>
public static class InvariantChecker
{
    /// <summary>Returns one message per violation. An empty list means the state is consistent.</summary>
    public static IReadOnlyList<string> Check(GameState state)
    {
        var problems = new List<string>();

        CheckCardConservation(state, problems);
        CheckTicketConservation(state, problems);
        CheckTrainStock(state, problems);
        CheckRouteScores(state, problems);
        CheckOwnership(state, problems);
        CheckReservation(state, problems);
        CheckRulesPolicies(state, problems);
        CheckPackAway(state, problems);

        return problems;
    }

    public static void AssertConsistent(GameState state)
    {
        var problems = Check(state);
        if (problems.Count == 0) return;

        throw new InvalidOperationException(
            "The match violated its invariants:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    private static void CheckRulesPolicies(GameState state, List<string> problems)
    {
        foreach (var (code, policyId) in state.AcceptedRulesPolicies)
        {
            if (RulesContinuations.For(code) is not { } policy ||
                !string.Equals(policy.PolicyId, policyId, StringComparison.Ordinal))
                problems.Add("The match contains an unsupported accepted rules policy.");
        }

        if (state.ConsecutivePasses < 0 || state.ConsecutivePasses > state.Seats.Length)
            problems.Add("The consecutive-pass count is outside the supported table size.");
    }

    private static void CheckPackAway(GameState state, List<string> problems)
    {
        if (state.IsGameplaySuspended != (state.PackAwayRequest is not null))
            problems.Add("The pack-away lifecycle does not match its saved request.");

        if ((state.Lifecycle is SessionLifecycle.PackedAway or SessionLifecycle.Rebuilding) !=
            (state.Checkpoint is not null))
            problems.Add("The pack-away lifecycle does not match its checkpoint.");

        if (state.RebuildAttested && state.Lifecycle != SessionLifecycle.Rebuilding)
            problems.Add("A rebuild attestation exists outside reconstruction.");

        if (state.Checkpoint is not { } checkpoint) return;

        if (checkpoint.SessionId != state.SessionId || checkpoint.FormatVersion != PackAwayCheckpoint.CurrentFormatVersion ||
            checkpoint.ProfileId != state.Manifest.ProfileId || checkpoint.ManifestHash != state.Manifest.DataHash ||
            checkpoint.BoardRevision != state.BoardRevision || checkpoint.SourceStateVersion >= state.StateVersion ||
            checkpoint.SourceJournalSequence >= state.JournalSequence ||
            checkpoint.CheckpointId != state.PackAwayRequest?.CheckpointId ||
            checkpoint.SuspendedTurnPhase != state.TurnPhase || checkpoint.PendingOperationId != state.PendingClaim?.OperationId ||
            !StateHash.MatchesLogical(state, checkpoint.LogicalStateHash))
            problems.Add("The checkpoint does not match its frozen source game.");

        if (!Enum.IsDefined(checkpoint.Status) ||
            (state.Lifecycle == SessionLifecycle.Rebuilding && checkpoint.Status != CheckpointStatus.Verified))
            problems.Add("The checkpoint has an invalid validation status for this lifecycle.");

        if (checkpoint.PhysicalTarget.IsDefault || checkpoint.TargetProvenance != TargetProvenance.LogicalStateOnly ||
            checkpoint.PhotoHash is not null)
        {
            problems.Add("The checkpoint has an unsupported physical target format.");
            return;
        }

        if (checkpoint.PhysicalTarget.Length != state.RouteOwners.Count ||
            checkpoint.PhysicalTarget.Select(route => route.RouteId).Distinct().Count() != checkpoint.PhysicalTarget.Length ||
            checkpoint.PhysicalTarget.Any(route =>
                !state.Manifest.TryGetRoute(route.RouteId, out var definition) || definition.Length != route.Length ||
                !state.RouteOwners.TryGetValue(route.RouteId, out var owner) || owner != route.SeatId) ||
            checkpoint.PhysicalTargetHash != PackAwayCheckpoint.HashTarget(checkpoint.PhysicalTarget))
            problems.Add("The checkpoint's physical target does not match the committed board.");
    }

    /// <summary>Invariant 1: card instances exist in exactly one legal location and supply is conserved.</summary>
    private static void CheckCardConservation(GameState state, List<string> problems)
    {
        var seen = new Dictionary<CardId, string>();

        void Record(CardId card, string location)
        {
            if ((uint)card.Value >= (uint)state.Catalog.Count)
                problems.Add($"Unknown card {card} is in {location}.");

            if (seen.TryGetValue(card, out var previous))
                problems.Add($"Card {card} is in both {previous} and {location}.");
            else
                seen[card] = location;
        }

        foreach (var card in state.TrainDeck) Record(card, "the draw pile");
        foreach (var card in state.TrainDiscard) Record(card, "the discards");

        for (var slot = 0; slot < state.FaceUp.Count; slot++)
        {
            if (state.FaceUp[slot] is { } card) Record(card, $"market slot {slot}");
        }

        foreach (var seat in state.Seats)
        {
            foreach (var card in state.HandOf(seat.SeatId)) Record(card, $"seat {seat.SeatId}'s hand");
        }

        if (seen.Count != state.Catalog.Count)
            problems.Add($"{seen.Count} of {state.Catalog.Count} train cards are accounted for.");
    }

    /// <summary>The same conservation rule for destination tickets, including temporary offers.</summary>
    private static void CheckTicketConservation(GameState state, List<string> problems)
    {
        var seen = new Dictionary<TicketId, string>();
        var known = state.Manifest.Tickets.Select(ticket => ticket.TicketId).ToHashSet();

        void Record(TicketId ticket, string location)
        {
            if (!known.Contains(ticket))
                problems.Add($"Unknown ticket {ticket} is in {location}.");

            if (seen.TryGetValue(ticket, out var previous))
                problems.Add($"Ticket {ticket} is in both {previous} and {location}.");
            else
                seen[ticket] = location;
        }

        foreach (var ticket in state.TicketDeck) Record(ticket, "the ticket deck");

        foreach (var seat in state.Seats)
        {
            foreach (var ticket in state.TicketsOf(seat.SeatId)) Record(ticket, $"seat {seat.SeatId}'s tickets");
        }

        foreach (var (seatId, offered) in state.SetupOffers)
        {
            foreach (var ticket in offered) Record(ticket, $"seat {seatId}'s setup offer");
        }

        foreach (var (seatId, returned) in state.PendingTicketReturns)
        {
            foreach (var ticket in returned) Record(ticket, $"seat {seatId}'s held-aside returns");
        }

        if (state.CurrentTicketOffer is { } offer)
        {
            foreach (var ticket in offer.Offered) Record(ticket, "the open ticket offer");
        }

        if (seen.Count != state.Manifest.Tickets.Length)
            problems.Add($"{seen.Count} of {state.Manifest.Tickets.Length} destination tickets are accounted for.");
    }

    /// <summary>
    /// Invariant 13: each seat's remaining stock plus the total length of its owned routes equals its
    /// starting stock.
    /// </summary>
    private static void CheckTrainStock(GameState state, List<string> problems)
    {
        var starting = state.Manifest.RulesConstants.StartingTrainsPerSeat;

        foreach (var seat in state.Seats)
        {
            var used = state.RoutesOwnedBy(seat.SeatId)
                .Where(routeId => state.Manifest.TryGetRoute(routeId, out _))
                .Sum(routeId => state.Manifest.Route(routeId).Length);
            var remaining = state.TrainStock[seat.SeatId];

            if (remaining + used != starting)
            {
                problems.Add(
                    $"Seat {seat.SeatId} has {remaining} trains left and {used} on the board, " +
                    $"which is not the starting stock of {starting}.");
            }

            if (remaining < 0) problems.Add($"Seat {seat.SeatId} has a negative train stock.");
        }
    }

    /// <summary>Invariant 14: each public route score equals a fresh calculation from owned routes.</summary>
    private static void CheckRouteScores(GameState state, List<string> problems)
    {
        var constants = state.Manifest.RulesConstants;

        foreach (var seat in state.Seats)
        {
            var recomputed = state.RoutesOwnedBy(seat.SeatId)
                .Where(routeId => state.Manifest.TryGetRoute(routeId, out _))
                .Sum(routeId => constants.ScoreForLength(state.Manifest.Route(routeId).Length));

            if (state.RouteScore[seat.SeatId] != recomputed)
            {
                problems.Add(
                    $"Seat {seat.SeatId} shows {state.RouteScore[seat.SeatId]} route points " +
                    $"but its owned routes are worth {recomputed}.");
            }
        }
    }

    /// <summary>Invariant 4 and the parallel-lane rule from the pinned profile.</summary>
    private static void CheckOwnership(GameState state, List<string> problems)
    {
        var seatIds = state.Seats.Select(seat => seat.SeatId).ToHashSet();

        foreach (var (routeId, seatId) in state.RouteOwners)
        {
            if (!state.Manifest.TryGetRoute(routeId, out var route))
            {
                problems.Add($"Route {routeId} is owned but is not on the supported board.");
                continue;
            }

            if (!seatIds.Contains(seatId))
            {
                problems.Add($"Route {routeId} is owned by unknown seat {seatId}.");
                continue;
            }

            if (route.ParallelGroupId is null) continue;

            foreach (var sibling in state.Manifest.SiblingLanesOf(route))
            {
                if (!state.RouteOwners.TryGetValue(sibling.RouteId, out var siblingOwner)) continue;

                if (siblingOwner == seatId)
                    problems.Add($"Seat {seatId} owns both lanes of parallel group {route.ParallelGroupId}.");

                if (state.Seats.Length <= state.Manifest.RulesConstants.ParallelRouteClosedAtOrBelowPlayers)
                {
                    problems.Add(
                        $"Parallel group {route.ParallelGroupId} has two owners at a table of " +
                        $"{state.Seats.Length}, where one claim closes its twin.");
                }
            }
        }
    }

    /// <summary>
    /// Invariant 2 in its pre-commit form: reserved cards are still a normal part of the owning hand
    /// and cannot be spent twice.
    /// </summary>
    private static void CheckReservation(GameState state, List<string> problems)
    {
        if (state.PendingClaim is not { } claim) return;

        if (state.Seats.All(seat => seat.SeatId != claim.SeatId))
        {
            problems.Add($"Pending claim belongs to unknown seat {claim.SeatId}.");
            return;
        }

        if (claim.ReservedCards.Distinct().Count() != claim.ReservedCards.Length)
            problems.Add("Pending claim reserves the same card more than once.");

        if (claim.BaseBoardRevision != state.BoardRevision)
            problems.Add("Pending claim refers to a stale board revision.");

        var hand = state.HandOf(claim.SeatId).ToHashSet();
        foreach (var card in claim.ReservedCards)
        {
            if (!hand.Contains(card))
                problems.Add($"Reserved card {card} is not in seat {claim.SeatId}'s hand.");
        }

        if (!state.Manifest.TryGetRoute(claim.RouteId, out var route))
        {
            problems.Add($"Pending claim names unknown route {claim.RouteId}.");
            return;
        }

        if (claim.ReservedCards.Length != route.Length)
        {
            problems.Add(
                $"Pending claim on {claim.RouteId} reserves {claim.ReservedCards.Length} cards " +
                $"for a route of {route.Length}.");
        }
    }
}
