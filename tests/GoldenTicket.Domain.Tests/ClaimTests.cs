using System.Collections.Immutable;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Persistence;

namespace GoldenTicket.Domain.Tests;

/// <summary>
/// Route claims: payment validation, the reserve-then-verify protocol, parallel lanes, cancellation
/// and the final round (DESIGN 6.1, 8.3, 8.4, 22.2).
/// </summary>
public class ClaimTests
{
    private static RouteDefinition Route(string routeId) => TestManifest.Manifest.Route(new RouteId(routeId));

    private static RulesHarness Ready(int seatCount = 3, ulong seed = 42)
    {
        var harness = RulesHarness.Create(seatCount, seed);
        harness.CompleteSetup();
        return harness;
    }

    [Fact]
    public void PlanningAClaimReservesButDoesNotSpendOrScore()
    {
        var harness = Ready();
        var seat = harness.State.ActiveSeatId;
        var route = Route("atlanta--nashville"); // length 1, grey

        harness.GrantCards(seat, TrainCardKind.Red, 1);
        var payment = harness.CardsOf(seat, TrainCardKind.Red, 1);
        var handBefore = harness.State.HandOf(seat).Count;

        harness.SubmitAccepted(new PlanClaim(harness.Envelope(seat), route.RouteId, payment));

        Assert.Equal(TurnPhase.AwaitingPhysicalPlacement, harness.State.TurnPhase);
        Assert.Equal(handBefore, harness.State.HandOf(seat).Count);           // nothing spent
        Assert.Equal(0, harness.State.RouteScore[seat]);                      // nothing scored
        Assert.Equal(45, harness.State.TrainStock[seat]);                     // no trains used
        Assert.False(harness.State.RouteOwners.ContainsKey(route.RouteId));   // no owner yet
        Assert.Equal(payment, harness.State.ReservedCardsOf(seat));

        // The reserved cards cannot be spent by a second action.
        Assert.DoesNotContain(payment[0], harness.State.AvailableCardsOf(seat));

        harness.AssertInvariants();
        harness.AssertReplayMatches();
    }

    [Fact]
    public void ConfirmedPlacementSpendsScoresAndEndsTheTurnExactlyOnce()
    {
        var harness = Ready();
        var seat = harness.State.ActiveSeatId;
        var route = Route("denver--santa-fe"); // length 2, grey

        harness.GrantCards(seat, TrainCardKind.Green, 2);
        var payment = harness.CardsOf(seat, TrainCardKind.Green, 2);
        var handBefore = harness.State.HandOf(seat).Count;

        var planned = harness.SubmitAccepted(
            new PlanClaim(harness.Envelope(seat), route.RouteId, payment));

        var operationId = planned.Transition!.Events.OfType<ClaimPlanned>().Single().OperationId;

        harness.SubmitAccepted(new SubmitClaimEvidence(
            harness.Envelope(seat), operationId, EvidenceKind.ManualAttestation, "tester", "board matches"));

        Assert.Equal(seat, harness.State.RouteOwners[route.RouteId]);
        Assert.Equal(handBefore - 2, harness.State.HandOf(seat).Count);
        Assert.Equal(2, harness.State.RouteScore[seat]);
        Assert.Equal(43, harness.State.TrainStock[seat]);
        Assert.Null(harness.State.PendingClaim);
        Assert.NotEqual(seat, harness.State.ActiveSeatId);
        Assert.All(payment, card => Assert.Contains(card, harness.State.TrainDiscard));

        // A repeat of the same evidence cannot spend or score again.
        var again = harness.Submit(new SubmitClaimEvidence(
            harness.Envelope(seat), operationId, EvidenceKind.ManualAttestation, "tester", "again"));
        Assert.False(again.IsAccepted);
        Assert.Equal("NoPendingClaim", again.Rejection!.Code);

        harness.AssertInvariants();
        harness.AssertReplayMatches();
    }

    [Fact]
    public void CameraEvidenceCommitsAnExistingManualModeClaimWithDistinctProvenance()
    {
        var harness = Ready();
        var seat = harness.State.ActiveSeatId;
        var route = Route("denver--santa-fe");

        harness.GrantCards(seat, TrainCardKind.Blue, 2);
        var planned = harness.SubmitAccepted(new PlanClaim(
            harness.Envelope(seat), route.RouteId, harness.CardsOf(seat, TrainCardKind.Blue, 2)));

        var operationId = planned.Transition!.Events.OfType<ClaimPlanned>().Single().OperationId;

        var result = harness.SubmitAccepted(new SubmitClaimEvidence(
            harness.Envelope(seat), operationId, EvidenceKind.CameraAutomatic,
            "local-piece-model", "two blue trains on Denver–Santa Fe in a fresh board crop"));

        var verification = Assert.Single(result.Transition!.Events.OfType<CameraVerificationRecorded>());
        Assert.Equal(operationId, verification.OperationId);
        Assert.Equal(seat, verification.SeatId);
        Assert.Equal(route.RouteId, verification.RouteId);
        Assert.Equal("local-piece-model", verification.Detector);
        Assert.Contains("two blue trains", verification.EvidenceSummary);
        Assert.IsType<CameraVerificationRecorded>(EventSerializer.Deserialize(EventSerializer.Serialize(verification)));
        Assert.DoesNotContain(result.Transition.Events, e => e is ManualVerificationRecorded);
        Assert.Equal(EvidenceKind.CameraAutomatic,
            Assert.Single(result.Transition.Events.OfType<ClaimCommitted>()).Evidence);
        Assert.Equal(seat, harness.State.RouteOwners[route.RouteId]);
        Assert.Equal(2, harness.State.RouteScore[seat]);
        Assert.Equal(43, harness.State.TrainStock[seat]);
        Assert.Null(harness.State.PendingClaim);

        var repeat = harness.Submit(new SubmitClaimEvidence(
            harness.Envelope(seat), operationId, EvidenceKind.CameraAutomatic,
            "local-piece-model", "same frame"));
        Assert.Equal("NoPendingClaim", repeat.Rejection?.Code);
        harness.AssertInvariants();
        harness.AssertReplayMatches();
    }

    [Theory]
    [InlineData("", "two matching trains")]
    [InlineData("local-piece-model", " ")]
    public void CameraEvidenceRequiresSourceAndSummary(string detector, string summary)
    {
        var harness = Ready();
        var seat = harness.State.ActiveSeatId;
        var route = Route("atlanta--nashville");
        harness.GrantCards(seat, TrainCardKind.Blue, 1);
        var planned = harness.SubmitAccepted(new PlanClaim(
            harness.Envelope(seat), route.RouteId, harness.CardsOf(seat, TrainCardKind.Blue, 1)));
        var operationId = planned.Transition!.Events.OfType<ClaimPlanned>().Single().OperationId;
        var before = StateHash.Compute(harness.State);

        var result = harness.Submit(new SubmitClaimEvidence(
            harness.Envelope(seat), operationId, EvidenceKind.CameraAutomatic, detector, summary));

        Assert.Equal("CameraEvidenceDetailsMissing", result.Rejection?.Code);
        Assert.Equal(before, StateHash.Compute(harness.State));
        Assert.NotNull(harness.State.PendingClaim);
    }

    [Fact]
    public void CameraEvidenceCannotConfirmAnotherOperationOrAnOldStateVersion()
    {
        var harness = Ready();
        var seat = harness.State.ActiveSeatId;
        var route = Route("atlanta--nashville");
        harness.GrantCards(seat, TrainCardKind.Blue, 1);
        var planned = harness.SubmitAccepted(new PlanClaim(
            harness.Envelope(seat), route.RouteId, harness.CardsOf(seat, TrainCardKind.Blue, 1)));
        var operationId = planned.Transition!.Events.OfType<ClaimPlanned>().Single().OperationId;
        var before = StateHash.Compute(harness.State);

        var wrongOperation = harness.Submit(new SubmitClaimEvidence(
            harness.Envelope(seat), OperationId.New(), EvidenceKind.CameraAutomatic,
            "local-piece-model", "fresh matching train"));
        Assert.Equal("OperationMismatch", wrongOperation.Rejection?.Code);

        var stale = harness.Submit(new SubmitClaimEvidence(
            harness.Envelope(seat) with { ExpectedStateVersion = harness.State.StateVersion - 1 },
            operationId, EvidenceKind.CameraAutomatic,
            "local-piece-model", "fresh matching train"));
        Assert.Equal("StaleStateVersion", stale.Rejection?.Code);
        Assert.Equal(before, StateHash.Compute(harness.State));
        Assert.NotNull(harness.State.PendingClaim);
    }

    [Theory]
    [InlineData(TrainCardKind.Blue, 4, "PaymentLengthMismatch")]  // too few cards for a length-5 route
    [InlineData(TrainCardKind.Green, 5, "WrongColorPayment")]     // right count, wrong colour
    public void AnInvalidPaymentChangesNothing(TrainCardKind kind, int count, string expectedCode)
    {
        var harness = Ready();
        var seat = harness.State.ActiveSeatId;
        var route = Route("atlanta--miami"); // length 5, blue

        harness.GrantCards(seat, kind, count);
        var before = StateHash.Compute(harness.State);

        var result = harness.Submit(new PlanClaim(
            harness.Envelope(seat), route.RouteId, harness.CardsOf(seat, kind, count)));

        Assert.False(result.IsAccepted);
        Assert.Equal(expectedCode, result.Rejection!.Code);
        Assert.Equal(before, StateHash.Compute(harness.State));
    }

    [Fact]
    public void APaymentMixingTwoColoursIsRefused()
    {
        var harness = Ready();
        var seat = harness.State.ActiveSeatId;

        harness.GrantCards(seat, TrainCardKind.Red, 1);
        harness.GrantCards(seat, TrainCardKind.Green, 1);

        ImmutableArray<CardId> mixed =
        [
            harness.CardsOf(seat, TrainCardKind.Red, 1)[0],
            harness.CardsOf(seat, TrainCardKind.Green, 1)[0],
        ];

        var result = harness.Submit(new PlanClaim(
            harness.Envelope(seat), new RouteId("denver--santa-fe"), mixed));

        Assert.False(result.IsAccepted);
        Assert.Equal("MixedColorPayment", result.Rejection!.Code);
    }

    /// <summary>
    /// DESIGN 4.3/6.2: a grey route offers every legal combination so the player chooses, rather than
    /// the application silently spending locomotives.
    /// </summary>
    [Fact]
    public void AGreyRouteOffersEveryColourAndLocomotiveCombination()
    {
        var harness = Ready();
        var seat = harness.State.ActiveSeatId;

        // State the hand exactly, so the offered payments are only what this test set up.
        harness.SetHand(seat,
            (TrainCardKind.Red, 3), (TrainCardKind.White, 2), (TrainCardKind.Locomotive, 2));

        var view = harness.SeatView(seat);
        var options = LegalActionCalculator.PaymentsFor(view, requiredCardKind: null, length: 3);

        Assert.Contains(options, option => option is { Color: TrainCardKind.Red, ColorCards: 3, Locomotives: 0 });
        Assert.Contains(options, option => option is { Color: TrainCardKind.Red, ColorCards: 2, Locomotives: 1 });
        Assert.Contains(options, option => option is { Color: TrainCardKind.White, ColorCards: 2, Locomotives: 1 });
        Assert.Contains(options, option => option is { Color: TrainCardKind.Red, ColorCards: 1, Locomotives: 2 });
        Assert.DoesNotContain(options, option => option.IsAllLocomotives); // only two locomotives held

        // A colour the seat does not hold never appears.
        Assert.DoesNotContain(options, option => option.Color == TrainCardKind.Pink);

        // Every option is exactly the route length and nothing over-spends the hand.
        Assert.All(options, option =>
        {
            Assert.Equal(3, option.Total);
            Assert.True(option.ColorCards <= view.CountOf(option.Color) || option.IsAllLocomotives);
            Assert.True(option.Locomotives <= view.CountOf(TrainCardKind.Locomotive));
        });
    }

    [Fact]
    public void AColouredRouteAcceptsLocomotivesButNotAnotherColour()
    {
        var harness = Ready();
        var seat = harness.State.ActiveSeatId;

        harness.SetHand(seat, (TrainCardKind.Blue, 3), (TrainCardKind.Locomotive, 3));

        var options = LegalActionCalculator.PaymentsFor(
            harness.SeatView(seat), TrainCardKind.Blue, length: 3);

        Assert.Contains(options, option => option is { ColorCards: 3, Locomotives: 0 });
        Assert.Contains(options, option => option.IsAllLocomotives && option.Locomotives == 3);
        Assert.All(options, option => Assert.True(option.IsAllLocomotives || option.Color == TrainCardKind.Blue));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    public void AParallelLaneClosesItsTwinOnlyAtSmallTables(int seatCount, bool closesTwin)
    {
        var harness = Ready(seatCount);
        var first = harness.State.ActiveSeatId;
        var laneA = Route("dallas--houston--a"); // length 1, grey, parallel
        var laneB = Route("dallas--houston--b");

        harness.GrantCards(first, TrainCardKind.Red, 1);
        var planned = harness.SubmitAccepted(new PlanClaim(
            harness.Envelope(first), laneA.RouteId, harness.CardsOf(first, TrainCardKind.Red, 1)));

        var operationId = planned.Transition!.Events.OfType<ClaimPlanned>().Single().OperationId;
        harness.SubmitAccepted(new SubmitClaimEvidence(
            harness.Envelope(first), operationId, EvidenceKind.ManualAttestation, "tester", "ok"));

        var second = harness.State.ActiveSeatId;
        Assert.NotEqual(first, second);

        harness.GrantCards(second, TrainCardKind.Red, 1);
        var result = harness.Submit(new PlanClaim(
            harness.Envelope(second), laneB.RouteId, harness.CardsOf(second, TrainCardKind.Red, 1)));

        if (closesTwin)
        {
            Assert.False(result.IsAccepted);
            Assert.Equal("ParallelLaneClosed", result.Rejection!.Code);
            Assert.DoesNotContain(harness.Legal(second).Claims, claim => claim.RouteId == laneB.RouteId);
        }
        else
        {
            Assert.True(result.IsAccepted);
        }

        harness.AssertInvariants();
    }

    [Fact]
    public void Kansas_city_oklahoma_city_lane_a_cannot_be_claimed_after_lane_b_at_a_two_seat_table()
    {
        var harness = Ready(2);
        var first = harness.State.ActiveSeatId;
        var laneB = Route("kansas-city--oklahoma-city--b");
        var laneA = Route("kansas-city--oklahoma-city--a");
        harness.GrantCards(first, TrainCardKind.Yellow, 2);
        var planned = harness.SubmitAccepted(new PlanClaim(
            harness.Envelope(first), laneB.RouteId, harness.CardsOf(first, TrainCardKind.Yellow, 2)));
        var operationId = planned.Transition!.Events.OfType<ClaimPlanned>().Single().OperationId;
        harness.SubmitAccepted(new SubmitClaimEvidence(
            harness.Envelope(first), operationId, EvidenceKind.ManualAttestation, "tester", "ok"));

        var second = harness.State.ActiveSeatId;
        harness.GrantCards(second, TrainCardKind.Blue, 2);
        Assert.DoesNotContain(harness.Legal(second).Claims, claim => claim.RouteId == laneA.RouteId);
        var attempt = harness.Submit(new PlanClaim(
            harness.Envelope(second), laneA.RouteId, harness.CardsOf(second, TrainCardKind.Blue, 2)));
        Assert.Equal("ParallelLaneClosed", attempt.Rejection?.Code);
    }

    [Fact]
    public void NoSeatMayOwnBothLanesOfAParallelRoute()
    {
        var harness = Ready(5);
        var seat = harness.State.ActiveSeatId;
        var laneA = Route("dallas--houston--a");

        harness.GrantCards(seat, TrainCardKind.Red, 1);
        var planned = harness.SubmitAccepted(new PlanClaim(
            harness.Envelope(seat), laneA.RouteId, harness.CardsOf(seat, TrainCardKind.Red, 1)));

        var operationId = planned.Transition!.Events.OfType<ClaimPlanned>().Single().OperationId;
        harness.SubmitAccepted(new SubmitClaimEvidence(
            harness.Envelope(seat), operationId, EvidenceKind.ManualAttestation, "tester", "ok"));

        // Bring the turn back round to the same seat.
        while (harness.State.ActiveSeatId != seat)
        {
            var other = harness.State.ActiveSeatId;
            harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(other), null));
            if (harness.State.TurnPhase == TurnPhase.AwaitingSecondTrainCard)
                harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(other), null));
        }

        Assert.DoesNotContain(harness.Legal(seat).Claims,
            claim => claim.RouteId == new RouteId("dallas--houston--b"));
    }

    [Fact]
    public void CancellingAfterPlacementRestoresTheBoardBeforeReleasingTheReservation()
    {
        var harness = Ready();
        var seat = harness.State.ActiveSeatId;

        harness.GrantCards(seat, TrainCardKind.Red, 2);
        var payment = harness.CardsOf(seat, TrainCardKind.Red, 2);
        var planned = harness.SubmitAccepted(new PlanClaim(
            harness.Envelope(seat), new RouteId("denver--santa-fe"), payment));

        var operationId = planned.Transition!.Events.OfType<ClaimPlanned>().Single().OperationId;

        harness.SubmitAccepted(new CancelPendingClaim(harness.Envelope(seat), operationId, TrainsWerePlaced: true));

        Assert.Equal(TurnPhase.RestoreBeforeState, harness.State.TurnPhase);
        Assert.NotNull(harness.State.PendingClaim);
        Assert.Equal(payment, harness.State.ReservedCardsOf(seat));

        // The claim cannot be completed once it is being restored.
        var forced = harness.Submit(new SubmitClaimEvidence(
            harness.Envelope(seat), operationId, EvidenceKind.ManualAttestation, "tester", "sneaky"));
        Assert.False(forced.IsAccepted);
        Assert.Equal("WrongPhase", forced.Rejection!.Code);

        harness.SubmitAccepted(new ConfirmBeforeStateRestored(harness.Envelope(seat), operationId));

        Assert.Null(harness.State.PendingClaim);
        Assert.Equal(TurnPhase.TurnStart, harness.State.TurnPhase);
        Assert.Equal(seat, harness.State.ActiveSeatId);      // the turn was never spent
        Assert.Equal(0, harness.State.RouteScore[seat]);
        Assert.All(payment, card => Assert.Contains(card, harness.State.AvailableCardsOf(seat)));

        harness.AssertInvariants();
        harness.AssertReplayMatches();
    }

    [Fact]
    public void CancellingWithNothingPlacedReleasesTheReservationImmediately()
    {
        var harness = Ready();
        var seat = harness.State.ActiveSeatId;

        harness.GrantCards(seat, TrainCardKind.Red, 2);
        var planned = harness.SubmitAccepted(new PlanClaim(
            harness.Envelope(seat), new RouteId("denver--santa-fe"),
            harness.CardsOf(seat, TrainCardKind.Red, 2)));

        var operationId = planned.Transition!.Events.OfType<ClaimPlanned>().Single().OperationId;
        harness.SubmitAccepted(new CancelPendingClaim(harness.Envelope(seat), operationId, TrainsWerePlaced: false));

        Assert.Null(harness.State.PendingClaim);
        Assert.Equal(TurnPhase.TurnStart, harness.State.TurnPhase);
    }

    [Fact]
    public void NoOtherActionIsAllowedWhileAClaimIsPending()
    {
        var harness = Ready();
        var seat = harness.State.ActiveSeatId;

        harness.GrantCards(seat, TrainCardKind.Red, 2);
        harness.SubmitAccepted(new PlanClaim(
            harness.Envelope(seat), new RouteId("denver--santa-fe"),
            harness.CardsOf(seat, TrainCardKind.Red, 2)));

        Assert.False(harness.Submit(new SelectTrainCard(harness.Envelope(seat), null)).IsAccepted);
        Assert.False(harness.Submit(new RequestTicketOffer(harness.Envelope(seat))).IsAccepted);

        var legal = harness.Legal(seat);
        Assert.True(legal.MustResolvePendingClaim);
        Assert.False(legal.Any);
    }

    /// <summary>
    /// DESIGN 6.1: after a seat finishes with at most two trains, every seat - the trigger included -
    /// takes exactly one more turn.
    /// </summary>
    [Fact]
    public void TheFinalRoundGivesEverySeatIncludingTheTriggerOneMoreTurn()
    {
        var harness = Ready(3);
        var trigger = harness.State.ActiveSeatId;

        // Deal the payment first: amending the deal rebuilds from the journal, which would undo the
        // board seeding below.
        harness.GrantCards(trigger, TrainCardKind.Red, 1);

        // Put 42 trains on the board so the seat has three left; the next one-train claim then
        // finishes its turn at two and starts the final round.
        harness.SeedOwnedRoutes(trigger,
            "calgary--winnipeg", "duluth--helena", "duluth--toronto", "el-paso--houston",
            "el-paso--los-angeles", "helena--seattle", "miami--new-orleans");

        Assert.Equal(3, harness.State.TrainStock[trigger]);

        var planned = harness.SubmitAccepted(new PlanClaim(
            harness.Envelope(trigger), new RouteId("atlanta--nashville"),
            harness.CardsOf(trigger, TrainCardKind.Red, 1)));

        var operationId = planned.Transition!.Events.OfType<ClaimPlanned>().Single().OperationId;
        harness.SubmitAccepted(new SubmitClaimEvidence(
            harness.Envelope(trigger), operationId, EvidenceKind.ManualAttestation, "tester", "ok"));

        var finalRound = harness.State.FinalRound;
        Assert.NotNull(finalRound);
        Assert.Equal(trigger, finalRound!.TriggeringSeatId);
        Assert.All(harness.Seats, seat => Assert.Equal(1, finalRound.RemainingTurnsBySeat[seat.SeatId]));

        // Play out the remaining turns with plain draws.
        var guard = 0;
        while (harness.State.Lifecycle == SessionLifecycle.Active && guard++ < 20)
        {
            var seat = harness.State.ActiveSeatId;
            harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(seat), null));
            if (harness.State.TurnPhase == TurnPhase.AwaitingSecondTrainCard)
                harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(seat), null));
        }

        Assert.Equal(SessionLifecycle.Finished, harness.State.Lifecycle);
        Assert.NotNull(harness.State.FinalResult);

        // Exactly one extra turn each, the trigger included, and no seat gets two.
        var extraTurns = harness.Journal
            .Select(row => row.Event)
            .OfType<TurnStarted>()
            .Where(start => start.TurnNumber > finalRound.TriggeringTurnNumber)
            .ToList();

        Assert.Equal(3, extraTurns.Count);
        Assert.Equal(3, extraTurns.Select(start => start.SeatId).Distinct().Count());
        Assert.Contains(trigger, extraTurns.Select(start => start.SeatId));

        harness.AssertInvariants();
    }
}
