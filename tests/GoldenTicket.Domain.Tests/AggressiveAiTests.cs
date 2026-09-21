using System.Collections.Immutable;
using GoldenTicket.AI;
using GoldenTicket.Application;
using GoldenTicket.Domain.Ai;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.Domain.Tests;

public sealed class AggressiveAiTests
{
    private static readonly SeatId Computer = new(1);
    private static readonly SeatId Human = new(2);

    [Fact]
    public async Task AggressiveClaimsCheapLinkAtHumanEndpointWhileStandardKeepsItsExistingChoice()
    {
        var (manifest, view) = Position([
            Route("human", "a", "b", 3), Route("block", "b", "c", 1), Route("elsewhere", "d", "e", 2)
        ], ["human"], [TrainCardKind.Yellow, TrainCardKind.Yellow]);

        Assert.IsType<AiDrawTrainCard>(await Choose(view, manifest, AiDifficulty.Standard));
        AssertClaim("block", await Choose(view, manifest), view, manifest);
    }

    [Fact]
    public async Task PrefersTheExtensionOfALongerHumanTrail()
    {
        var (manifest, view) = Position([
            Route("long1", "a", "b", 3), Route("long2", "b", "c", 3), Route("long3", "c", "d", 3),
            Route("short", "m", "n", 1), Route("short-block", "n", "o", 1), Route("long-block", "d", "e", 1)
        ], ["long1", "long2", "long3", "short"], [TrainCardKind.Yellow]);

        AssertClaim("long-block", await Choose(view, manifest), view, manifest);
    }

    [Fact]
    public async Task PrefersBridgeThatWouldJoinTwoLongHumanTrails()
    {
        var (manifest, view) = Position([
            Route("human1", "a", "b", 3), Route("human2", "b", "c", 3),
            Route("human3", "d", "e", 3), Route("human4", "e", "f", 3),
            Route("extension", "a", "g", 1), Route("bridge", "c", "d", 1)
        ], ["human1", "human2", "human3", "human4"], [TrainCardKind.Yellow]);

        AssertClaim("bridge", await Choose(view, manifest), view, manifest);
    }

    [Fact]
    public async Task LongestTrailThreatDistinguishesAnEndpointFromAnInteriorBranch()
    {
        var (manifest, view) = Position([
            Route("human1", "a", "center", 3), Route("human2", "center", "b", 3),
            Route("human3", "center", "c", 3), Route("branch", "center", "x", 1),
            Route("extension", "a", "y", 1)
        ], ["human1", "human2", "human3"], [TrainCardKind.Yellow]);

        AssertClaim("extension", await Choose(view, manifest), view, manifest);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    public async Task ParallelLaneIsOnlyABlockWhenClaimingItClosesTheConnection(int players, bool blocks)
    {
        var (manifest, view) = Position([
            Route("human", "a", "b", 3), Route("lane-a", "b", "c", 1, parallel: "pair"),
            Route("lane-b", "b", "c", 1, parallel: "pair")
        ], ["human"], [TrainCardKind.Yellow], players);

        var decision = await Choose(view, manifest);
        if (blocks)
        {
            var claim = Assert.IsType<AiClaimRoute>(decision);
            Assert.Contains(claim.RouteId.Value, new[] { "lane-a", "lane-b" });
            AssertLegalClaim(claim, view, manifest);
        }
        else Assert.IsType<AiDrawTrainCard>(decision);
    }

    [Fact]
    public async Task RemainingParallelLaneCanBlockWhenOtherComputerOwnsItsTwin()
    {
        var (manifest, view) = Position([
            Route("human", "a", "b", 3), Route("lane-a", "b", "c", 1, parallel: "pair"),
            Route("lane-b", "b", "c", 1, parallel: "pair")
        ], ["human"], [TrainCardKind.Yellow], players: 4);
        view = view with { Public = view.Public with
        {
            RouteOwners = view.Public.RouteOwners.Add(new RouteId("lane-a"), new SeatId(3)),
        } };

        AssertClaim("lane-b", await Choose(view, manifest), view, manifest);
    }

    [Fact]
    public async Task ComputerOnlyNetworksDoNotChangeStandardChoicesOrRandomConsumption()
    {
        var (manifest, view) = Position([
            Route("opponent", "a", "b", 3), Route("nearby", "b", "c", 1), Route("elsewhere", "d", "e", 2)
        ], ["opponent"], [TrainCardKind.Yellow, TrainCardKind.Yellow]);
        view = view with { Public = view.Public with
        {
            Seats = [.. view.Public.Seats.Select(seat => seat with { Kind = SeatKind.Computer })],
        } };

        var ordinaryRandom = Random(7);
        var aggressiveRandom = Random(7);
        var ordinary = await Choose(view, manifest, AiDifficulty.Standard, ordinaryRandom);
        var aggressive = await Choose(view, manifest, AiDifficulty.Aggressive, aggressiveRandom);
        Assert.Equal(ordinary, aggressive);
        Assert.Equal(ordinaryRandom.State, aggressiveRandom.State);
        Assert.IsType<AiDrawTrainCard>(aggressive);
    }

    [Theory]
    [InlineData(TurnPhase.TurnStart)]
    [InlineData(TurnPhase.AwaitingSecondTrainCard)]
    public async Task CollectsNeededFaceUpCardsForABlockWithoutTakingAnIllegalSecondLocomotive(TurnPhase phase)
    {
        var (manifest, view) = Position([
            Route("human", "a", "b", 3), Route("block", "b", "c", 2, TrainCardKind.Red)
        ], ["human"], [TrainCardKind.Red]);
        view = view with { Public = view.Public with
        {
            TurnPhase = phase,
            FaceUp = phase == TurnPhase.TurnStart
                ? [TrainCardKind.Green, TrainCardKind.Red]
                : [TrainCardKind.Locomotive, TrainCardKind.Green, TrainCardKind.Red],
        } };

        var draw = Assert.IsType<AiDrawTrainCard>(await Choose(view, manifest));
        Assert.NotNull(draw.Slot);
        Assert.Equal(TrainCardKind.Red, view.Public.FaceUp[draw.Slot.Value]);
        Assert.Contains(draw.Slot.Value, LegalActionCalculator.For(view, manifest).DrawableFaceUpSlots);
    }

    [Fact]
    public async Task BlockingDoesNotOverrideAVeryValuableOwnTicket()
    {
        var routes = new[]
        {
            Route("human", "a", "b", 3), Route("block", "b", "c", 1),
            Route("own-ticket", "d", "e", 2, TrainCardKind.Yellow),
        };
        var (manifest, view) = Position(routes, ["human"], [TrainCardKind.Yellow, TrainCardKind.Yellow],
            ticket: new TicketDefinition(new TicketId("own"), new CityId("d"), new CityId("e"), 20));
        view = view with { Tickets = [new TicketId("own")] };

        AssertClaim("own-ticket", await Choose(view, manifest), view, manifest);
    }

    [Fact]
    public async Task DoesNotClaimAnUnaffordableBlockOrSpendReservedCards()
    {
        var (manifest, view) = Position([
            Route("human", "a", "b", 3), Route("block", "b", "c", 2, TrainCardKind.Red)
        ], ["human"], [TrainCardKind.Red, TrainCardKind.Locomotive]);
        view = view with { ReservedCards = [view.Hand[1].Id] };

        Assert.IsType<AiDrawTrainCard>(await Choose(view, manifest));
        view = view with { ReservedCards = [] };
        var claim = Assert.IsType<AiClaimRoute>(await Choose(view, manifest));
        Assert.Equal(new PaymentOption(TrainCardKind.Red, 1, 1), claim.Payment);
        AssertLegalClaim(claim, view, manifest);
    }

    [Fact]
    public async Task HiddenOpponentHandsTicketsAndDeckOrderCannotChangeAnAggressiveDecision()
    {
        var harness = RulesHarness.Create(3, 19, 1);
        harness.CompleteSetup();
        var seat = harness.State.ActiveSeatId;
        harness.SeedOwnedRoutes(harness.Seats[1].SeatId, "calgary--helena");
        var firstView = harness.SeatView(seat);
        var first = await Choose(firstView, harness.Manifest);

        var other = harness.Seats[1].SeatId;
        var oldCard = harness.State.TrainHandsInternal[other][0];
        var replacement = harness.State.TrainDeckInternal[0];
        harness.State.TrainHandsInternal[other][0] = replacement;
        harness.State.TrainDeckInternal[0] = oldCard;
        var oldTicket = harness.State.TicketHandsInternal[other][0];
        harness.State.TicketHandsInternal[other][0] = harness.State.TicketDeckInternal[0];
        harness.State.TicketDeckInternal[0] = oldTicket;
        harness.State.TrainDeckInternal.Reverse();
        harness.State.TicketDeckInternal.Reverse();
        harness.State.RandomState = DeterministicRandom.SeedFrom(888);

        Assert.Equal(first, await Choose(harness.SeatView(seat), harness.Manifest));
    }

    [Theory]
    [InlineData(AiDifficulty.Relaxed)]
    [InlineData(AiDifficulty.Standard)]
    [InlineData(AiDifficulty.Challenging)]
    public async Task EstablishedStylesIgnoreWhetherARivalsSamePublicNetworkIsHuman(AiDifficulty difficulty)
    {
        var (manifest, view) = Position([
            Route("human", "a", "b", 3), Route("nearby", "b", "c", 1), Route("elsewhere", "d", "e", 2)
        ], ["human"], [TrainCardKind.Yellow, TrainCardKind.Yellow]);
        var withHumanRandom = Random(99);
        var withComputerRandom = Random(99);
        var first = await Choose(view, manifest, difficulty, withHumanRandom);
        view = view with { Public = view.Public with
        {
            Seats = [.. view.Public.Seats.Select(seat => seat with { Kind = SeatKind.Computer })],
        } };
        Assert.Equal(first, await Choose(view, manifest, difficulty, withComputerRandom));
        Assert.Equal(withHumanRandom.State, withComputerRandom.State);
        var expectedRandom = Random(99);
        for (var i = 0; i < 2; i++) expectedRandom.NextInt(1000);
        Assert.Equal(expectedRandom.State, withHumanRandom.State);
    }

    [Fact]
    public async Task CancellationIsHonouredAndBudgetRemainsBounded()
    {
        var (manifest, view) = Position([Route("human", "a", "b", 3), Route("block", "b", "c", 1)],
            ["human"], [TrainCardKind.Yellow]);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new HeuristicAiPolicy().ChooseAsync(view, manifest,
                ComputerSeatDriver.BudgetFor(AiDifficulty.Aggressive), Random(1), source.Token));
        Assert.Equal(TimeSpan.FromSeconds(2), ComputerSeatDriver.BudgetFor(AiDifficulty.Aggressive).Deadline);
        Assert.Equal(1, (int)AiDifficulty.Standard);
        Assert.Equal(2, (int)AiDifficulty.Challenging);
        Assert.Equal(3, (int)AiDifficulty.Aggressive);
    }

    private static ValueTask<AiDecision> Choose(SeatView view, BoardManifest manifest,
        AiDifficulty difficulty = AiDifficulty.Aggressive, DeterministicRandom? random = null) =>
        new HeuristicAiPolicy().ChooseAsync(view, manifest, ComputerSeatDriver.BudgetFor(difficulty),
            random ?? Random(7), CancellationToken.None);

    private static DeterministicRandom Random(ulong seed) => new(DeterministicRandom.SeedFrom(seed));

    private static void AssertClaim(string routeId, AiDecision decision, SeatView view, BoardManifest manifest)
    {
        var claim = Assert.IsType<AiClaimRoute>(decision);
        Assert.Equal(routeId, claim.RouteId.Value);
        AssertLegalClaim(claim, view, manifest);
    }

    private static void AssertLegalClaim(AiClaimRoute claim, SeatView view, BoardManifest manifest) =>
        Assert.Contains(LegalActionCalculator.For(view, manifest).Claims,
            legal => legal.RouteId == claim.RouteId && legal.Payments.Contains(claim.Payment));

    private static RouteDefinition Route(string id, string a, string b, int length,
        TrainCardKind? color = null, string? parallel = null) =>
        new(new RouteId(id), new CityId(a), new CityId(b), length, color, parallel, parallel is null ? null : id);

    private static (BoardManifest Manifest, SeatView View) Position(
        IEnumerable<RouteDefinition> routes, string[] owned, TrainCardKind[] hand, int players = 2,
        TicketDefinition? ticket = null)
    {
        var allRoutes = routes.ToImmutableArray();
        var cities = allRoutes.SelectMany(route => new[] { route.CityA, route.CityB }).Distinct()
            .Select(city => new CityDefinition(city, city.Value)).ToImmutableArray();
        var original = TestManifest.Manifest;
        var manifest = new BoardManifest("aggressive-test", 1, 1, "synthetic", original.Edition,
            original.DataAudit, cities, allRoutes,
            [ticket ?? new TicketDefinition(new TicketId("unused"), cities[0].StableId, cities[1].StableId, 1)],
            original.TrainCardDefinitions, original.RulesConstants);
        var owners = owned.ToImmutableDictionary(id => new RouteId(id), _ => Human);
        var seats = Enumerable.Range(1, players).Select(index => new PublicSeatSummary(
            new SeatId(index), $"Seat {index}", (PlayerColor)(index - 1), "", index == 2 ? SeatKind.Human : SeatKind.Computer,
            45 - (index == 2 ? owned.Sum(id => manifest.Route(new RouteId(id)).Length) : 0), 0,
            index == 1 ? hand.Length : 4, 0, 0, index == 2 ? [.. owners.Keys] : [])).ToImmutableArray();
        var publicView = new PublicView(SessionId.New(), 1, 1, SessionLifecycle.Active, TurnPhase.TurnStart,
            TurnAction.None, VerificationMode.Manual, Computer, 1, seats, [TrainCardKind.Green],
            50, 0, 0, owners, null, null, null, null, null, false, null);
        var view = new SeatView(publicView, Computer,
            [.. hand.Select((kind, index) => new HeldCard(new CardId(index), kind))], [], [], null, []);
        return (manifest, view);
    }
}
