using System.Collections.Immutable;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Scoring;

namespace GoldenTicket.Domain.Tests;

/// <summary>
/// The longest-route calculation and final scoring. DESIGN 6.5 is specific that this is a maximum
/// weighted edge-simple trail, so the awkward shapes - loops, junctions, parallel edges and
/// disconnected components - are the cases that matter.
/// </summary>
public class ScoringTests
{
    private static BoardManifest Manifest => TestManifest.Manifest;

    private static TrailResult Trail(params string[] routeIds) =>
        LongestTrail.Compute(Manifest, routeIds.Select(id => new RouteId(id)));

    [Fact]
    public void NoRoutesScoreNothing()
    {
        Assert.Equal(0, Trail().Length);
        Assert.Empty(Trail().Witness);
    }

    [Fact]
    public void ASingleRouteIsItsOwnLength()
    {
        var result = Trail("atlanta--miami"); // length 5
        Assert.Equal(5, result.Length);
        Assert.Single(result.Witness);
    }

    [Fact]
    public void AChainAddsUp()
    {
        // Seattle-Vancouver (1) + Calgary-Vancouver (3) + Calgary-Helena (4)
        var result = Trail("seattle--vancouver--a", "calgary--vancouver", "calgary--helena");
        Assert.Equal(8, result.Length);
        Assert.Equal(3, result.Witness.Length);
    }

    /// <summary>
    /// A cycle must be traversed completely: every edge is usable once even though cities repeat.
    /// A visited-city-only search would stop short here.
    /// </summary>
    [Fact]
    public void ALoopUsesEveryEdgeExactlyOnce()
    {
        // Dallas-Houston (1), Houston-New Orleans (2), New Orleans-Little Rock (3),
        // Little Rock-Dallas (2): a four-city cycle worth 8.
        var result = Trail(
            "dallas--houston--a",
            "houston--new-orleans",
            "little-rock--new-orleans",
            "dallas--little-rock");

        Assert.Equal(8, result.Length);
        Assert.Equal(4, result.Witness.Length);
    }

    [Fact]
    public void BothLanesOfAParallelRouteCountSeparately()
    {
        var result = Trail("dallas--houston--a", "dallas--houston--b");

        // Two length-1 lanes between the same pair form a trail of 2, not 1.
        Assert.Equal(2, result.Length);
        Assert.Equal(2, result.Witness.Length);
    }

    /// <summary>
    /// At a junction the trail takes the best two branches and stops; it cannot fork.
    /// </summary>
    [Fact]
    public void AJunctionTakesTheBestTwoBranches()
    {
        // Around Denver: Helena (4), Omaha (4), Santa Fe (2). The best trail is 4 + 4 = 8.
        var result = Trail("denver--helena", "denver--omaha", "denver--santa-fe");

        Assert.Equal(8, result.Length);
        Assert.Equal(2, result.Witness.Length);
        Assert.DoesNotContain(new RouteId("denver--santa-fe"), result.Witness);
    }

    [Fact]
    public void DisconnectedComponentsAreScoredSeparatelyAndTheBestWins()
    {
        // A far-apart pair: the Miami group (6) versus the Seattle group (1).
        var result = Trail("miami--new-orleans", "seattle--vancouver--a");

        Assert.Equal(6, result.Length);
        Assert.Single(result.Witness);
        Assert.Equal(new RouteId("miami--new-orleans"), result.Witness[0]);
    }

    [Fact]
    public void TheWitnessIsAValidTrailOfTheReportedLength()
    {
        var routes = new[]
        {
            "dallas--houston--a", "dallas--houston--b", "houston--new-orleans",
            "little-rock--new-orleans", "dallas--little-rock", "dallas--el-paso",
            "el-paso--santa-fe", "denver--santa-fe", "denver--omaha",
        };

        var result = Trail(routes);

        // Every witness edge is distinct, drawn from the owned set, and consecutive edges meet.
        Assert.Equal(result.Witness.Length, result.Witness.Distinct().Count());
        Assert.All(result.Witness, routeId => Assert.Contains(routeId.Value, routes));
        Assert.Equal(result.Length, result.Witness.Sum(routeId => Manifest.Route(routeId).Length));

        // A trail is valid from one of the first edge's two endpoints; at least one must walk.
        var first = Manifest.Route(result.Witness[0]);
        Assert.True(Walks(result.Witness, first.CityA) || Walks(result.Witness, first.CityB),
            "The witness is not a continuous trail from either end of its first route.");

        bool Walks(IEnumerable<RouteId> witness, CityId start)
        {
            var city = start;
            foreach (var routeId in witness)
            {
                var route = Manifest.Route(routeId);
                if (!route.Touches(city)) return false;
                city = route.OtherEndpoint(city);
            }

            return true;
        }
    }

    [Fact]
    public void TicketsScorePlusWhenConnectedAndMinusWhenNot()
    {
        var harness = RulesHarness.Create(2);
        harness.CompleteSetup();

        var seat = harness.Seats[0].SeatId;
        var other = harness.Seats[1].SeatId;

        // Replace the dealt tickets with two known ones and connect only the first. Returning the
        // dealt tickets to the deck first keeps the supply conserved (invariant 1).
        harness.ReturnTicketsToDeck(seat);
        harness.ReturnTicketsToDeck(other);

        var connected = Manifest.Tickets.First(t => t.TicketId.Value == "t-denver--el-paso");
        var missed = Manifest.Tickets.First(t => t.TicketId.Value == "t-seattle--new-york");

        harness.GrantTicket(seat, connected.TicketId.Value);
        harness.GrantTicket(seat, missed.TicketId.Value);

        // Denver - Santa Fe (2) + El Paso - Santa Fe (2) connects Denver to El Paso.
        Own(harness, seat, "denver--santa-fe", "el-paso--santa-fe");

        var result = FinalScoring.Compute(harness.State);
        var score = result.ScoreOf(seat);

        Assert.Equal(connected.Points, score.TicketPointsGained);
        Assert.Equal(missed.Points, score.TicketPointsLost);
        Assert.Contains(connected.TicketId, score.CompletedTickets);
        Assert.Contains(missed.TicketId, score.IncompleteTickets);
        Assert.Equal(4, score.RoutePoints); // two length-2 routes at 2 points each
        Assert.Equal(4 + connected.Points - missed.Points + 10, score.Total); // holds the longest route
    }

    [Fact]
    public void TheLongestRouteBonusIsSharedByEveryTiedSeat()
    {
        var harness = RulesHarness.Create(2);
        harness.CompleteSetup();

        var a = harness.Seats[0].SeatId;
        var b = harness.Seats[1].SeatId;

        harness.ReturnTicketsToDeck(a);
        harness.ReturnTicketsToDeck(b);

        Own(harness, a, "atlanta--miami");             // 5
        Own(harness, b, "portland--san-francisco--a"); // 5

        var result = FinalScoring.Compute(harness.State);

        Assert.Equal(5, result.LongestTrailLength);
        Assert.Equal(2, result.LongestRouteBonusHolders.Length);
        Assert.Equal(10, result.ScoreOf(a).LongestRouteBonusPoints);
        Assert.Equal(10, result.ScoreOf(b).LongestRouteBonusPoints);
    }

    [Fact]
    public void AnIdenticalResultIsRecordedAsASharedVictory()
    {
        var harness = RulesHarness.Create(2);
        harness.CompleteSetup();

        var a = harness.Seats[0].SeatId;
        var b = harness.Seats[1].SeatId;

        harness.ReturnTicketsToDeck(a);
        harness.ReturnTicketsToDeck(b);

        Own(harness, a, "atlanta--miami");
        Own(harness, b, "portland--san-francisco--a");

        var result = FinalScoring.Compute(harness.State);

        Assert.True(result.SharedVictory);
        Assert.Equal(2, result.Winners.Length);
        Assert.Contains("shared victory", result.TieBreakExplanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MostCompletedTicketsBreaksATieOnPoints()
    {
        var harness = RulesHarness.Create(2);
        harness.CompleteSetup();

        var a = harness.Seats[0].SeatId;
        var b = harness.Seats[1].SeatId;

        harness.ReturnTicketsToDeck(a);
        harness.ReturnTicketsToDeck(b);

        // Both seats own one length-2 route, so route points and trail length tie at 2 and 2.
        Own(harness, a, "denver--santa-fe");
        Own(harness, b, "atlanta--charleston");

        // Seat A also completes a zero-risk ticket by holding one it already connects.
        harness.GrantTicket(a, "t-denver--el-paso");
        Own(harness, a, "el-paso--santa-fe");

        // Give seat B the same number of route points so only the ticket count differs.
        Own(harness, b, "charleston--raleigh");

        var result = FinalScoring.Compute(harness.State);

        Assert.Single(result.Winners);
        Assert.Equal(a, result.Winners[0]);
    }

    /// <summary>Assigns routes to a seat and keeps train stock and route score consistent.</summary>
    private static void Own(RulesHarness harness, SeatId seat, params string[] routeIds)
    {
        harness.SeedOwnedRoutes(seat, routeIds);
        harness.AssertInvariants();
    }
}
