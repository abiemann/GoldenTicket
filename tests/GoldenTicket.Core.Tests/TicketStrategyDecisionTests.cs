using System.Collections.Immutable;
using GoldenTicket.AI;
using GoldenTicket.Application;
using GoldenTicket.Domain.Ai;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.Domain.Tests;

public sealed class TicketStrategyDecisionTests
{
    private static readonly SeatId Computer = new(1);
    private static readonly SeatId Human = new(2);

    [Fact]
    public async Task CompletesAnOrdinaryDestinationBeforeATemptingHumanNetworkBlock()
    {
        var (manifest, view) = Position(
            [Route("human-west", "a", "b", 6), Route("human-east", "c", "d", 6),
             Route("block", "b", "c", 1, TrainCardKind.Blue),
             Route("finish", "u", "v", 2, TrainCardKind.Pink)],
            [Ticket("ordinary", "u", "v", 4)],
            [TrainCardKind.Pink, TrainCardKind.Pink, TrainCardKind.Blue],
            ["human-west", "human-east"]);

        AssertClaim("finish", await Choose(view, manifest), view, manifest);
    }

    [Fact]
    public async Task DoesNotSpendTheOnlyNeededPinkOnAnUnrelatedOneTrainBlock()
    {
        var (manifest, view) = Position(
            [Route("human-west", "a", "b", 6), Route("human-east", "c", "d", 6),
             Route("block", "b", "c", 1), Route("finish", "u", "v", 4, TrainCardKind.Pink)],
            [Ticket("nearby-destination", "u", "v", 12)], [TrainCardKind.Pink],
            ["human-west", "human-east"], faceUp: [TrainCardKind.Green, TrainCardKind.Locomotive]);

        AssertDraw(TrainCardKind.Locomotive, await Choose(view, manifest), view, manifest);
    }

    [Fact]
    public async Task DoesNotSpendTrainStockNeededForAnOtherwiseFinishableDestination()
    {
        var (manifest, view) = Position(
            [Route("human-west", "a", "b", 6), Route("human-east", "c", "d", 6),
             Route("block", "b", "c", 1, TrainCardKind.Blue),
             Route("finish", "u", "v", 4, TrainCardKind.Pink)],
            [Ticket("destination", "u", "v", 12)],
            [TrainCardKind.Pink, TrainCardKind.Pink, TrainCardKind.Pink, TrainCardKind.Blue],
            ["human-west", "human-east"], trains: 4, faceUp: [TrainCardKind.Pink, TrainCardKind.Green]);

        AssertDraw(TrainCardKind.Pink, await Choose(view, manifest), view, manifest);
    }

    [Theory]
    [InlineData(TurnPhase.TurnStart)]
    [InlineData(TurnPhase.AwaitingSecondTrainCard)]
    public async Task FundsTheFinalDestinationColorInsteadOfSummingSeveralSpeculativeBlocks(TurnPhase phase)
    {
        var (manifest, view) = Position(
            [Route("human-west", "a", "b", 6), Route("human-east", "c", "d", 6),
             Route("blue-block", "b", "c", 2, TrainCardKind.Blue),
             Route("red-block", "b", "e", 2, TrainCardKind.Red),
             Route("green-block", "c", "f", 2, TrainCardKind.Green),
             Route("finish", "u", "v", 4, TrainCardKind.Pink)],
            [Ticket("destination", "u", "v", 12)], [TrainCardKind.Pink],
            ["human-west", "human-east"],
            faceUp: [TrainCardKind.Blue, TrainCardKind.Red, TrainCardKind.Green,
                     TrainCardKind.Pink, TrainCardKind.Locomotive]);
        view = view with { Public = view.Public with { TurnPhase = phase } };

        AssertDraw(TrainCardKind.Pink, await Choose(view, manifest), view, manifest);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnInfeasibleHighValueTicketDoesNotStarveAReachableDestination(bool blocked)
    {
        var (manifest, view) = Position(
            [Route("unfinishable", "a", "b", 6, TrainCardKind.Pink),
             Route("finish", "u", "v", 2, TrainCardKind.Red)],
            [Ticket("high-value", "a", "b", 21), Ticket("reachable", "u", "v", 4)],
            [TrainCardKind.Red, TrainCardKind.Red], blocked ? ["unfinishable"] : [], trains: 4);

        // In one case the only connection is opponent-owned; in the other it needs
        // six trains when only four remain. Neither should erase the reachable goal.
        AssertClaim("finish", await Choose(view, manifest), view, manifest);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UnreachableHeldAndOfferedTicketsStillProduceTheRequiredLegalKeep(int minimum)
    {
        var (manifest, view) = Position(
            [Route("held-block", "a", "b", 2), Route("first-block", "c", "d", 2),
             Route("second-block", "e", "f", 2), Route("third-block", "g", "h", 2)],
            [Ticket("held", "a", "b", 21), Ticket("low", "c", "d", 4),
             Ticket("medium", "e", "f", 8), Ticket("high", "g", "h", 12)], [],
            ["held-block", "first-block", "second-block", "third-block"]);
        var offered = ImmutableArray.Create(new TicketId("high"), new TicketId("low"), new TicketId("medium"));
        view = view with
        {
            Tickets = [new TicketId("held")],
            Offer = new TicketOffer(Computer, offered, minimum, false),
            Public = view.Public with { TurnPhase = TurnPhase.AwaitingTicketKeep },
        };

        Assert.True(LegalActionCalculator.For(view, manifest).MustCommitTicketSelection);
        var decision = Assert.IsType<AiKeepTickets>(await Choose(view, manifest));
        Assert.Equal(minimum, decision.Kept.Length);
        Assert.Equal(minimum, decision.Kept.Distinct().Count());
        Assert.All(decision.Kept, ticket => Assert.Contains(ticket, offered));
        Assert.Contains(new TicketId("low"), decision.Kept);
        Assert.DoesNotContain(new TicketId("high"), decision.Kept);
    }

    [Fact]
    public async Task LastTurnClaimsOneAvailablePointInsteadOfDrawingUnusableCards()
    {
        var (manifest, view) = Position([Route("one-point", "u", "v", 1)], [], [TrainCardKind.Blue]);
        view = view with { Public = view.Public with
        {
            FinalRound = new FinalRoundSummary(Human,
                new Dictionary<SeatId, int> { [Computer] = 1, [Human] = 0 }.ToImmutableDictionary()),
        } };

        Assert.True(LegalActionCalculator.For(view, manifest).CanDrawBlindTrainCard);
        AssertClaim("one-point", await Choose(view, manifest), view, manifest);
    }

    [Fact]
    public async Task AggressiveCanSpendSpareBlueOnAUsefulBlockWithoutDelayingItsPinkDestination()
    {
        var (manifest, view) = Position(
            [Route("human-west", "a", "b", 6), Route("human-east", "c", "d", 6),
             Route("block", "b", "c", 1, TrainCardKind.Blue),
             Route("finish", "u", "v", 4, TrainCardKind.Pink)],
            [Ticket("destination", "u", "v", 12)],
            [TrainCardKind.Pink, TrainCardKind.Pink, TrainCardKind.Pink, TrainCardKind.Blue],
            ["human-west", "human-east"], faceUp: [TrainCardKind.Pink, TrainCardKind.Green]);

        var aggressive = Assert.IsType<AiClaimRoute>(await Choose(view, manifest));
        AssertClaim("block", aggressive, view, manifest);
        Assert.Equal(new PaymentOption(TrainCardKind.Blue, 1, 0), aggressive.Payment);
        AssertDraw(TrainCardKind.Pink, await Choose(view, manifest, AiDifficulty.Standard), view, manifest);
    }

    private static ValueTask<AiDecision> Choose(SeatView view, BoardManifest manifest,
        AiDifficulty difficulty = AiDifficulty.Aggressive) =>
        new HeuristicAiPolicy().ChooseAsync(view, manifest, ComputerSeatDriver.BudgetFor(difficulty),
            new DeterministicRandom(DeterministicRandom.SeedFrom(47)), TestContext.Current.CancellationToken);

    private static void AssertClaim(string id, AiDecision decision, SeatView view, BoardManifest manifest)
    {
        var claim = Assert.IsType<AiClaimRoute>(decision);
        Assert.Equal(new RouteId(id), claim.RouteId);
        Assert.Contains(LegalActionCalculator.For(view, manifest).Claims,
            legal => legal.RouteId == claim.RouteId && legal.Payments.Contains(claim.Payment));
    }

    private static void AssertDraw(TrainCardKind color, AiDecision decision, SeatView view, BoardManifest manifest)
    {
        var draw = Assert.IsType<AiDrawTrainCard>(decision);
        Assert.NotNull(draw.Slot);
        Assert.Equal(color, view.Public.FaceUp[draw.Slot.Value]);
        Assert.Contains(draw.Slot.Value, LegalActionCalculator.For(view, manifest).DrawableFaceUpSlots);
    }

    private static RouteDefinition Route(string id, string a, string b, int length, TrainCardKind? color = null) =>
        new(new RouteId(id), new CityId(a), new CityId(b), length, color, null, null);

    private static TicketDefinition Ticket(string id, string a, string b, int points) =>
        new(new TicketId(id), new CityId(a), new CityId(b), points);

    private static (BoardManifest Manifest, SeatView View) Position(RouteDefinition[] routes,
        TicketDefinition[] tickets, TrainCardKind[] hand, string[]? humanRoutes = null,
        int trains = 45, TrainCardKind?[]? faceUp = null)
    {
        humanRoutes ??= [];
        var cities = routes.SelectMany(route => new[] { route.CityA, route.CityB }).Distinct()
            .Select(id => new CityDefinition(id, id.Value)).ToImmutableArray();
        var original = TestManifest.Manifest;
        var manifest = new BoardManifest("ticket-strategy-test", 1, 1, "synthetic", original.Edition,
            original.DataAudit, cities, [.. routes],
            tickets.Length == 0 ? [Ticket("unused", routes[0].CityA.Value, routes[0].CityB.Value, 1)] : [.. tickets],
            original.TrainCardDefinitions, original.RulesConstants);
        var owners = humanRoutes.ToImmutableDictionary(id => new RouteId(id), _ => Human);
        var seats = ImmutableArray.Create(
            new PublicSeatSummary(Computer, "Computer", PlayerColor.Green, "", SeatKind.Computer,
                trains, 0, hand.Length, tickets.Length, 0, []),
            new PublicSeatSummary(Human, "Human", PlayerColor.Red, "", SeatKind.Human,
                45 - humanRoutes.Sum(id => manifest.Route(new RouteId(id)).Length), 0, 4, 2, 0, [.. owners.Keys]));
        var publicView = new PublicView(SessionId.New(), 1, 1, SessionLifecycle.Active, TurnPhase.TurnStart,
            TurnAction.None, VerificationMode.Manual, Computer, 30, seats,
            faceUp is null ? [TrainCardKind.Green] : [.. faceUp], 50, 0, 0, owners,
            null, null, null, null, null, false, null);
        return (manifest, new SeatView(publicView, Computer,
            [.. hand.Select((kind, index) => new HeldCard(new CardId(index), kind))], [],
            [.. tickets.Select(ticket => ticket.TicketId)], null, []));
    }
}
