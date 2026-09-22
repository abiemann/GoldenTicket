using System.Collections.Immutable;
using GoldenTicket.AI;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Projections;

namespace GoldenTicket.Domain.Tests;

public sealed class TicketPortfolioPlannerTests
{
    [Fact]
    public void WorkingPlanUsesReadyCardsAndFewerClaimsWhileHumanEstimateStillReportsMinimumTrains()
    {
        var ticket = Ticket("ticket", "a", "c", 8);
        var (manifest, view) = Position([
            Route("ready", "a", "c", 6, TrainCardKind.Red),
            Route("short1", "a", "b", 2, TrainCardKind.Red),
            Route("short2", "b", "c", 2, TrainCardKind.Red),
        ], [ticket], [.. Enumerable.Repeat(TrainCardKind.Red, 6)]);

        var plan = RoutePlanner.Plan(view, manifest, TestContext.Current.CancellationToken);
        Assert.Equal([new RouteId("ready")], plan.Tickets[0].MissingRoutes);
        Assert.Equal(1, plan.EstimatedTurns);
        Assert.Equal(6, plan.TotalMissingTrains);
        Assert.Equal(4, RoutePlanner.EstimateCost(view, manifest, ticket));
    }

    [Fact]
    public void SlightlyLongerRouteCanFinishSoonerWithTheCardsAlreadyHeld()
    {
        var (manifest, view) = Position([
            Route("short-unready", "a", "c", 3, TrainCardKind.Blue),
            Route("ready1", "a", "b", 2, TrainCardKind.Red),
            Route("ready2", "b", "c", 2, TrainCardKind.Red),
        ], [Ticket("ticket", "a", "c", 7)], [.. Enumerable.Repeat(TrainCardKind.Red, 4)]);

        var plan = RoutePlanner.Plan(view, manifest, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "ready1", "ready2" }, plan.Tickets[0].MissingRoutes.Select(id => id.Value));
        Assert.Equal(2, plan.EstimatedTurns);
        Assert.Equal(4, plan.TotalMissingTrains);
    }

    [Fact]
    public void ReadyButOverlongDetourDoesNotHideAStockFeasibleTicket()
    {
        var (manifest, view) = Position([
            Route("ready", "a", "c", 6, TrainCardKind.Red),
            Route("short1", "a", "b", 2, TrainCardKind.Blue),
            Route("short2", "b", "c", 2, TrainCardKind.Blue),
        ], [Ticket("ticket", "a", "c", 7)], [.. Enumerable.Repeat(TrainCardKind.Red, 6)]);
        view = view with { Public = view.Public with
        {
            Seats = [view.Public.Seats[0] with { TrainsRemaining = 4 }, view.Public.Seats[1]],
        } };

        var plan = RoutePlanner.Plan(view, manifest, TestContext.Current.CancellationToken);
        Assert.Equal(4, plan.TotalMissingTrains);
        Assert.Equal(new[] { "short1", "short2" }, plan.Tickets[0].MissingRoutes.Select(id => id.Value));
    }

    [Fact]
    public void SharedTicketRoutesUseOneTrainAndCardBudgetForThePortfolio()
    {
        var (manifest, view) = Position([
            Route("shared", "a", "b", 2, TrainCardKind.Red),
            Route("left", "b", "c", 2, TrainCardKind.Blue),
            Route("right", "b", "d", 2, TrainCardKind.Yellow),
        ], [Ticket("left-ticket", "a", "c", 6), Ticket("right-ticket", "a", "d", 6)],
            [TrainCardKind.Red, TrainCardKind.Red, TrainCardKind.Blue, TrainCardKind.Blue]);

        var plan = RoutePlanner.Plan(view, manifest, TestContext.Current.CancellationToken);
        Assert.Equal(8, plan.Tickets.Sum(ticket => ticket.MissingTrains));
        Assert.Equal(6, plan.TotalMissingTrains);
        // Three claims, with the two yellow cards still to collect. Shared red is spent once.
        Assert.Equal(4, plan.EstimatedTurns);
        Assert.Equal(2, plan.Tickets[0].EstimatedTurns);
        Assert.Equal(3, plan.Tickets[1].EstimatedTurns);
    }

    [Fact]
    public void RepeatedRouteColorsCannotReuseTheSameHandOrLocomotive()
    {
        var (manifest, view) = Position([
            Route("one", "a", "b", 3, TrainCardKind.Red),
            Route("two", "b", "c", 3, TrainCardKind.Red),
        ], [Ticket("ticket", "a", "c", 8)],
            [TrainCardKind.Red, TrainCardKind.Red, TrainCardKind.Locomotive]);

        var plan = RoutePlanner.Plan(view, manifest, TestContext.Current.CancellationToken);
        // Six payment cards, three held: three still missing plus two claim actions.
        Assert.Equal(3.5, plan.EstimatedTurns);
        Assert.Equal(3.5, plan.Tickets[0].EstimatedTurns);
    }

    [Fact]
    public void JointPlanningReusesATicketBackboneInsteadOfBuyingTwoSeparatePaths()
    {
        var (manifest, view) = Position([
            Route("left-start", "a", "b", 3), Route("left-end", "b", "d", 3),
            Route("right-start", "a", "c", 3), Route("shared-end", "c", "d", 3),
            Route("connector", "e", "c", 1),
        ], [Ticket("across", "a", "d", 8), Ticket("joining", "e", "d", 5)], []);

        var plan = RoutePlanner.Plan(view, manifest, TestContext.Current.CancellationToken);
        Assert.Equal(7, plan.TotalMissingTrains);
        Assert.Contains(new RouteId("shared-end"), plan.Tickets[0].MissingRoutes);
        Assert.Contains(new RouteId("shared-end"), plan.Tickets[1].MissingRoutes);
        Assert.Equal(6.5, plan.EstimatedTurns);
    }

    [Fact]
    public void GreyRoutesUseSingleColorSetsAndDoNotReuseReservedCards()
    {
        var (manifest, view) = Position([
            Route("one", "a", "b", 2), Route("two", "b", "c", 2),
        ], [Ticket("ticket", "a", "c", 6)],
            [TrainCardKind.Red, TrainCardKind.Blue, TrainCardKind.Locomotive]);
        view = view with { ReservedCards = [view.Hand[2].Id] };

        Assert.Equal(3, RoutePlanner.Plan(view, manifest, TestContext.Current.CancellationToken).EstimatedTurns);
        view = view with { ReservedCards = [] };
        Assert.Equal(2.5, RoutePlanner.Plan(view, manifest, TestContext.Current.CancellationToken).EstimatedTurns);
    }

    [Fact]
    public void OfferedSubsetCanBeComparedWithoutChangingTheSeatsActualTickets()
    {
        var (manifest, view) = Position([
            Route("shared", "a", "b", 2), Route("left", "b", "c", 2), Route("right", "b", "d", 2),
        ], [Ticket("left-ticket", "a", "c", 6), Ticket("right-ticket", "a", "d", 6)], []);
        view = view with { Tickets = [] };

        var single = RoutePlanner.Plan(view, manifest, [new TicketId("left-ticket")], TestContext.Current.CancellationToken);
        var both = RoutePlanner.Plan(view, manifest, [new TicketId("left-ticket"), new TicketId("right-ticket")], TestContext.Current.CancellationToken);
        Assert.Empty(view.Tickets);
        Assert.Equal(4, single.TotalMissingTrains);
        Assert.Equal(6, both.TotalMissingTrains);
        Assert.Equal(6, both.EstimatedTurns);
    }

    [Fact]
    public void CompletedTicketNeedsNoTrainsCardsOrActions()
    {
        var (manifest, view) = Position([Route("owned", "a", "b", 2)], [Ticket("ticket", "a", "b", 4)], []);
        view = view with { Public = view.Public with
        {
            RouteOwners = view.Public.RouteOwners.Add(new RouteId("owned"), view.SeatId),
        } };

        var plan = RoutePlanner.Plan(view, manifest, TestContext.Current.CancellationToken);
        Assert.True(plan.Tickets[0].AlreadyComplete);
        Assert.Equal(0, plan.TotalMissingTrains);
        Assert.Equal(0, plan.EstimatedTurns);
        Assert.Empty(plan.RouteValue);
    }

    [Fact]
    public void BlockedTicketIsUnreachableRatherThanAZeroCostObjective()
    {
        var (manifest, view) = Position([Route("blocked", "a", "b", 2)], [Ticket("ticket", "a", "b", 4)], []);
        view = view with { Public = view.Public with
        {
            RouteOwners = view.Public.RouteOwners.Add(new RouteId("blocked"), new SeatId(2)),
        } };

        var plan = RoutePlanner.Plan(view, manifest, TestContext.Current.CancellationToken);
        Assert.False(plan.Tickets[0].Reachable);
        Assert.False(plan.Tickets[0].AlreadyComplete);
        Assert.Equal(double.PositiveInfinity, plan.Tickets[0].EstimatedTurns);
        Assert.Equal(double.PositiveInfinity, plan.EstimatedTurns);
    }

    [Fact]
    public void PlannerHonoursCancellationBeforePortfolioSearch()
    {
        var (manifest, view) = Position([Route("route", "a", "b", 2)], [Ticket("ticket", "a", "b", 4)], []);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => RoutePlanner.Plan(view, manifest, cancellation.Token));
    }

    [Fact]
    public void AnImpossibleTicketDoesNotEraseTimingDifferencesForTheRemainingObjectives()
    {
        var (manifest, view) = Position([
            Route("ready", "a", "d", 6, TrainCardKind.Red),
            Route("small1", "a", "b", 1), Route("small2", "b", "c", 1), Route("small3", "c", "d", 1),
            Route("blocked", "x", "y", 2),
        ], [Ticket("main", "a", "d", 10), Ticket("joining", "a", "c", 6), Ticket("impossible", "x", "y", 4)],
            [.. Enumerable.Repeat(TrainCardKind.Red, 6)]);
        view = view with { Public = view.Public with
        {
            RouteOwners = view.Public.RouteOwners.Add(new RouteId("blocked"), new SeatId(2)),
        } };

        var plan = RoutePlanner.Plan(view, manifest, TestContext.Current.CancellationToken);
        Assert.Equal(double.PositiveInfinity, plan.EstimatedTurns);
        Assert.False(plan.Tickets[2].Reachable);
        Assert.Contains(new RouteId("ready"), plan.Tickets[0].MissingRoutes);
        Assert.Equal(7, plan.TotalMissingTrains);
        Assert.Equal(2.5, RoutePlanner.EstimateTurns(view, manifest,
            plan.Tickets.SelectMany(ticket => ticket.MissingRoutes)));
    }

    private static RouteDefinition Route(string id, string a, string b, int length, TrainCardKind? color = null) =>
        new(new RouteId(id), new CityId(a), new CityId(b), length, color, null, null);

    private static TicketDefinition Ticket(string id, string a, string b, int points) =>
        new(new TicketId(id), new CityId(a), new CityId(b), points);

    private static (BoardManifest Manifest, SeatView View) Position(
        IEnumerable<RouteDefinition> routes, TicketDefinition[] tickets, TrainCardKind[] hand)
    {
        var allRoutes = routes.ToImmutableArray();
        var cities = allRoutes.SelectMany(route => new[] { route.CityA, route.CityB }).Distinct()
            .Select(city => new CityDefinition(city, city.Value)).ToImmutableArray();
        var original = TestManifest.Manifest;
        var manifest = new BoardManifest("portfolio-test", 1, 1, "synthetic", original.Edition, original.DataAudit,
            cities, allRoutes, [.. tickets], original.TrainCardDefinitions, original.RulesConstants);
        var self = new SeatId(1);
        var seats = new[]
        {
            new PublicSeatSummary(self, "Computer", PlayerColor.Red, "", SeatKind.Computer, 45, 0, hand.Length, tickets.Length, 0, []),
            new PublicSeatSummary(new SeatId(2), "Human", PlayerColor.Blue, "", SeatKind.Human, 45, 0, 4, 2, 0, []),
        };
        var publicView = new PublicView(SessionId.New(), 1, 1, SessionLifecycle.Active, TurnPhase.TurnStart,
            TurnAction.None, VerificationMode.Manual, self, 1, [.. seats], [], 50, 0, 0,
            ImmutableDictionary<RouteId, SeatId>.Empty, null, null, null, null, null, false, null);
        return (manifest, new SeatView(publicView, self,
            [.. hand.Select((kind, index) => new HeldCard(new CardId(index), kind))], [],
            [.. tickets.Select(ticket => ticket.TicketId)], null, []));
    }
}
