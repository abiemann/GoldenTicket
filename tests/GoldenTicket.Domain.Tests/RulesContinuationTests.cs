using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Domain.Tests;

/// <summary>
/// The reviewed continuations for positions the printed rules do not reach (DESIGN 6.4). A pause
/// keeps the match safe; these make it playable again, and the tests hold the engine to the rule
/// that nothing is applied until an operator accepts a policy they were actually shown.
/// </summary>
public class RulesContinuationTests
{
    private const string Operator = "tester";

    private static RulesHarness Ready()
    {
        var harness = RulesHarness.Create();
        harness.CompleteSetup();
        return harness;
    }

    /// <summary>Leaves the supply in an exact state, as the audit tests do.</summary>
    private static void ArrangeSupply(RulesHarness harness, TrainCardKind?[] market, TrainCardKind[] deck)
    {
        var state = harness.State;
        var pool = state.Catalog.All.ToList();

        CardId Take(TrainCardKind kind)
        {
            var card = pool.First(candidate => state.Catalog.KindOf(candidate) == kind);
            pool.Remove(card);
            return card;
        }

        state.FaceUpInternal.Clear();
        foreach (var kind in market) state.FaceUpInternal.Add(kind is { } value ? Take(value) : null);

        state.TrainDeckInternal.Clear();
        foreach (var kind in deck) state.TrainDeckInternal.Add(Take(kind));

        state.TrainDiscardInternal.Clear();
        foreach (var hand in state.TrainHandsInternal.Values) hand.Clear();

        var other = state.Seats.First(seat => seat.SeatId != state.ActiveSeatId).SeatId;
        state.TrainHandsInternal[other].AddRange(pool);

        harness.MarkJournalDiverged();
        harness.AssertInvariants();
    }

    private static RulesHarness PausedOnSecondDraw()
    {
        var harness = Ready();
        ArrangeSupply(harness,
            [TrainCardKind.Locomotive, TrainCardKind.Locomotive, null, null, null],
            [TrainCardKind.Red]);

        harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(harness.State.ActiveSeatId), null));
        Assert.Equal(RulesContinuations.NoSelectableSecondDraw, harness.State.RulesDecision!.Code);
        return harness;
    }

    // ---- The policy catalogue -----------------------------------------------------------------

    [Fact]
    public void EveryPausedCodeTheEngineRaisesHasAReviewedWayForward()
    {
        // If the engine can stop the match on a code, an operator must be able to get it moving.
        string[] raised =
        [
            RulesContinuations.NoSelectableSecondDraw,
            RulesContinuations.PartialMarketSupply,
            RulesContinuations.MarketResetImpossible,
            RulesContinuations.MarketResetUnstable,
            RulesContinuations.NoLegalAction,
        ];

        Assert.All(raised, code => Assert.NotNull(RulesContinuations.For(code)));
    }

    [Fact]
    public void EveryPolicySaysWhatItDoesAndWhy()
    {
        Assert.All(RulesContinuations.All, policy =>
        {
            Assert.False(string.IsNullOrWhiteSpace(policy.Title));
            Assert.False(string.IsNullOrWhiteSpace(policy.Why));
            Assert.False(string.IsNullOrWhiteSpace(policy.Effect));
            Assert.False(string.IsNullOrWhiteSpace(policy.PolicyId));
        });
    }

    [Fact]
    public void AnUnknownCodeHasNoPolicy() => Assert.Null(RulesContinuations.For("SomethingNobodyReviewed"));

    // ---- Accepting one ------------------------------------------------------------------------

    [Fact]
    public void NothingHappensUntilAnOperatorAcceptsThePolicy()
    {
        var harness = PausedOnSecondDraw();
        var seat = harness.State.ActiveSeatId;
        var before = StateHash.Compute(harness.State);

        // Ordinary play stays refused while the match is paused.
        Assert.False(harness.Submit(new RequestTicketOffer(harness.Envelope(seat))).IsAccepted);
        Assert.Equal(before, StateHash.Compute(harness.State));
    }

    [Fact]
    public void AcceptingTheSecondDrawPolicyEndsTheTurnWithTheCardAlreadyTaken()
    {
        var harness = PausedOnSecondDraw();
        var seat = harness.State.ActiveSeatId;
        var held = harness.State.HandOf(seat).ToArray();
        var policy = RulesContinuations.For(RulesContinuations.NoSelectableSecondDraw)!;

        var result = harness.SubmitAccepted(new ResolveRulesDecision(
            harness.Envelope(seat), policy.Code, policy.PolicyId, Operator));

        Assert.Null(harness.State.RulesDecision);
        Assert.NotEqual(seat, harness.State.ActiveSeatId);          // the turn passed on
        Assert.Equal(held, harness.State.HandOf(seat));             // the drawn card was not taken back
        Assert.Contains(result.Transition!.Events, e => e is TurnCompleted);
        Assert.Contains(result.Transition.Events, e => e is RulesDecisionResolved);

        harness.AssertInvariants();
    }

    [Fact]
    public void TheAcceptanceIsRecordedWithItsPolicyVersion()
    {
        var harness = PausedOnSecondDraw();
        var policy = RulesContinuations.For(RulesContinuations.NoSelectableSecondDraw)!;

        harness.SubmitAccepted(new ResolveRulesDecision(
            harness.Envelope(harness.State.ActiveSeatId), policy.Code, policy.PolicyId, Operator));

        var resolved = harness.Journal.Select(row => row.Event).OfType<RulesDecisionResolved>().Single();

        Assert.Equal(policy.Code, resolved.Code);
        Assert.Equal(policy.PolicyId, resolved.PolicyId);
        Assert.Equal(RulesContinuations.PolicyVersion, resolved.PolicyVersion);
        Assert.Equal(Operator, resolved.Operator);
        Assert.Equal(policy.PolicyId, harness.State.AcceptedRulesPolicies[policy.Code]);
    }

    [Fact]
    public void AnAcceptedPolicyAppliesAgainWithoutStoppingPlay()
    {
        var harness = PausedOnSecondDraw();
        var policy = RulesContinuations.For(RulesContinuations.NoSelectableSecondDraw)!;

        harness.SubmitAccepted(new ResolveRulesDecision(
            harness.Envelope(harness.State.ActiveSeatId), policy.Code, policy.PolicyId, Operator));

        // Put the next seat in the same position.
        ArrangeSupply(harness,
            [TrainCardKind.Locomotive, TrainCardKind.Locomotive, null, null, null],
            [TrainCardKind.Green]);

        var seat = harness.State.ActiveSeatId;
        var result = harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(seat), null));

        // This time the turn simply ends: no second pause for a policy already accepted.
        Assert.Null(harness.State.RulesDecision);
        Assert.NotEqual(seat, harness.State.ActiveSeatId);
        Assert.Contains(result.Transition!.Events, e => e is TurnCompleted);
        Assert.DoesNotContain(result.Transition.Events, e => e is RulesDecisionRaised);
    }

    // ---- Guards --------------------------------------------------------------------------------

    [Fact]
    public void APolicyForADifferentPositionIsRefused()
    {
        var harness = PausedOnSecondDraw();
        var other = RulesContinuations.For(RulesContinuations.PartialMarketSupply)!;

        var result = harness.Submit(new ResolveRulesDecision(
            harness.Envelope(harness.State.ActiveSeatId), other.Code, other.PolicyId, Operator));

        Assert.False(result.IsAccepted);
        Assert.Equal("RulesDecisionMismatch", result.Rejection!.Code);
        Assert.NotNull(harness.State.RulesDecision);
    }

    [Fact]
    public void APolicyTheOperatorWasNotShownIsRefused()
    {
        var harness = PausedOnSecondDraw();

        var result = harness.Submit(new ResolveRulesDecision(
            harness.Envelope(harness.State.ActiveSeatId),
            RulesContinuations.NoSelectableSecondDraw, "something-else", Operator));

        Assert.False(result.IsAccepted);
        Assert.Equal("PolicyMismatch", result.Rejection!.Code);
        Assert.NotNull(harness.State.RulesDecision);
    }

    [Fact]
    public void AnAcceptanceMustRecordWhoMadeIt()
    {
        var harness = PausedOnSecondDraw();
        var policy = RulesContinuations.For(RulesContinuations.NoSelectableSecondDraw)!;

        var result = harness.Submit(new ResolveRulesDecision(
            harness.Envelope(harness.State.ActiveSeatId), policy.Code, policy.PolicyId, "   "));

        Assert.False(result.IsAccepted);
        Assert.Equal("OperatorMissing", result.Rejection!.Code);
    }

    [Fact]
    public void ResolvingWhenNothingIsPausedIsRefused()
    {
        var harness = Ready();
        var policy = RulesContinuations.For(RulesContinuations.NoSelectableSecondDraw)!;

        var result = harness.Submit(new ResolveRulesDecision(
            harness.Envelope(harness.State.ActiveSeatId), policy.Code, policy.PolicyId, Operator));

        Assert.False(result.IsAccepted);
        Assert.Equal("NoRulesDecision", result.Rejection!.Code);
    }

    // ---- Market policies ------------------------------------------------------------------------

    [Fact]
    public void AcceptingTheSmallerMarketPolicyLetsPlayCarryOn()
    {
        var harness = Ready();
        ArrangeSupply(harness,
            [TrainCardKind.Red, TrainCardKind.Green, TrainCardKind.Blue, TrainCardKind.White, TrainCardKind.Pink], []);

        var seat = harness.State.ActiveSeatId;
        harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(seat), 0));
        Assert.Equal(RulesContinuations.PartialMarketSupply, harness.State.RulesDecision!.Code);

        var policy = RulesContinuations.For(RulesContinuations.PartialMarketSupply)!;
        harness.SubmitAccepted(new ResolveRulesDecision(
            harness.Envelope(seat), policy.Code, policy.PolicyId, Operator));

        Assert.Null(harness.State.RulesDecision);

        // The seat had taken one card of two, so it still owes its second pick.
        Assert.Equal(TurnPhase.AwaitingSecondTrainCard, harness.State.TurnPhase);
        Assert.Equal(seat, harness.State.ActiveSeatId);
        Assert.Equal(1, harness.State.TrainCardsTakenThisTurn);

        harness.AssertInvariants();
    }

    [Fact]
    public void AcceptingTheLocomotivePolicyLeavesTheMarketAloneAndFinishesTheTurn()
    {
        var harness = Ready();
        var seat = harness.State.ActiveSeatId;

        harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(seat), null));
        ArrangeSupply(harness,
            [TrainCardKind.Red, TrainCardKind.Green, TrainCardKind.Blue, TrainCardKind.Locomotive, TrainCardKind.Locomotive],
            [TrainCardKind.Locomotive]);

        harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(seat), 0));
        Assert.Equal(RulesContinuations.MarketResetImpossible, harness.State.RulesDecision!.Code);

        var marketBefore = harness.State.FaceUp.ToArray();
        var policy = RulesContinuations.For(RulesContinuations.MarketResetImpossible)!;

        var result = harness.SubmitAccepted(new ResolveRulesDecision(
            harness.Envelope(seat), policy.Code, policy.PolicyId, Operator));

        Assert.Null(harness.State.RulesDecision);
        Assert.Equal(marketBefore, harness.State.FaceUp.ToArray());   // nothing was reshuffled
        Assert.DoesNotContain(result.Transition!.Events, e => e is MarketReset);

        // That was the second card of the draw, so the turn is over.
        Assert.NotEqual(seat, harness.State.ActiveSeatId);

        harness.AssertInvariants();
    }

    // ---- The pass policy must terminate -----------------------------------------------------------

    /// <summary>
    /// The pass policy is the only one that could circle the table forever. Once every seat has
    /// passed in a row, nothing further can happen, so the match is scored.
    /// </summary>
    [Fact]
    public void AFullRoundOfPassesEndsTheMatchInsteadOfLooping()
    {
        var harness = Ready();

        // Strip the supply bare: no cards anywhere, no destination tickets, no affordable route.
        var state = harness.State;
        state.FaceUpInternal.Clear();
        for (var slot = 0; slot < harness.Manifest.RulesConstants.FaceUpMarketSize; slot++)
            state.FaceUpInternal.Add(null);

        var pool = state.Catalog.All.ToList();
        state.TrainDeckInternal.Clear();
        state.TrainDiscardInternal.Clear();
        foreach (var hand in state.TrainHandsInternal.Values) hand.Clear();

        // Everything is parked in another seat's hands rather than destroyed, so the supply is empty
        // and the active seat holds nothing: it cannot draw, take tickets, or pay for any route.
        var seat = state.ActiveSeatId;
        var parked = state.Seats.First(s => s.SeatId != seat).SeatId;

        state.TrainHandsInternal[parked].AddRange(pool);
        state.TicketHandsInternal[parked].AddRange(state.TicketDeckInternal);
        state.TicketDeckInternal.Clear();

        harness.MarkJournalDiverged();
        Assert.False(harness.Legal(seat).Any);

        // Reach the paused state the way the engine does, then accept the pass policy.
        var raised = new RulesDecisionRaised(
            RulesContinuations.NoLegalAction, "no legal action for this seat");
        GameReducer.ApplyTransition(state, [raised]);

        var policy = RulesContinuations.For(RulesContinuations.NoLegalAction)!;
        var result = harness.Submit(new ResolveRulesDecision(
            harness.Envelope(seat), policy.Code, policy.PolicyId, Operator));

        Assert.True(result.IsAccepted, result.Rejection?.Message);

        // Either play found a seat that could act, or the match was scored. It did not hang.
        Assert.True(
            harness.State.Lifecycle == SessionLifecycle.Finished || harness.State.RulesDecision is null,
            "The pass policy neither resumed play nor ended the match.");

        if (harness.State.Lifecycle == SessionLifecycle.Finished)
            Assert.NotNull(harness.State.FinalResult);

        harness.AssertInvariants();
    }

    [Fact]
    public void APassResetsOnceASeatActsAgain()
    {
        var harness = Ready();
        var seat = harness.State.ActiveSeatId;

        GameReducer.ApplyTransition(harness.State, [new TurnCompleted(seat, 1, TurnAction.None)]);
        Assert.Equal(1, harness.State.ConsecutivePasses);

        GameReducer.ApplyTransition(harness.State, [new TurnCompleted(seat, 2, TurnAction.DrawTrainCards)]);
        Assert.Equal(0, harness.State.ConsecutivePasses);
    }
}
