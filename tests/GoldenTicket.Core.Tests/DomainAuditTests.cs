using System.Collections.Immutable;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Scoring;

namespace GoldenTicket.Domain.Tests;

public class DomainAuditTests
{
    [Fact]
    public void NoSelectableSecondDrawPreservesTheFirstCardAndPausesTheSameTurn()
    {
        var harness = Ready();
        ArrangeSupply(harness,
            [TrainCardKind.Locomotive, TrainCardKind.Locomotive, null, null, null],
            [TrainCardKind.Red]);
        var seat = harness.State.ActiveSeatId;
        var card = harness.State.TrainDeck[0];
        var turn = harness.State.TurnNumber;

        var result = harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(seat), null));

        Assert.Equal(TurnPhase.RulesDecisionRequired, harness.State.TurnPhase);
        Assert.Equal("NoSelectableSecondDraw", harness.State.RulesDecision!.Code);
        Assert.Contains(card, harness.State.HandOf(seat));
        Assert.Equal(seat, harness.State.ActiveSeatId);
        Assert.Equal(turn, harness.State.TurnNumber);
        Assert.Equal(1, harness.State.TrainCardsTakenThisTurn);
        Assert.DoesNotContain(result.Transition!.Events, e => e is TurnCompleted or TurnStarted);
        Assert.False(harness.Submit(new RequestTicketOffer(harness.Envelope(seat))).IsAccepted);
        harness.AssertInvariants();
    }

    [Fact]
    public void PartialMarketSupplyPausesWithoutDiscardingTheSelectedCard()
    {
        var harness = Ready();
        ArrangeSupply(harness,
            [TrainCardKind.Red, TrainCardKind.Green, TrainCardKind.Blue, TrainCardKind.White, TrainCardKind.Pink], []);
        var seat = harness.State.ActiveSeatId;
        var card = harness.State.FaceUp[0]!.Value;

        harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(seat), 0));

        Assert.Equal("PartialMarketSupply", harness.State.RulesDecision!.Code);
        Assert.Equal(TurnPhase.RulesDecisionRequired, harness.State.TurnPhase);
        Assert.Contains(card, harness.State.HandOf(seat));
        Assert.Equal(seat, harness.State.ActiveSeatId);
        harness.AssertInvariants();
    }

    [Fact]
    public void ImpossibleMarketAfterSecondPickCannotHaveItsPauseOverwrittenByEndTurn()
    {
        var harness = Ready();
        var seat = harness.State.ActiveSeatId;
        harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(seat), null));
        ArrangeSupply(harness,
            [TrainCardKind.Red, TrainCardKind.Green, TrainCardKind.Blue, TrainCardKind.Locomotive, TrainCardKind.Locomotive],
            [TrainCardKind.Locomotive]);

        var result = harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(seat), 0));

        Assert.Equal("MarketResetImpossible", harness.State.RulesDecision!.Code);
        Assert.Equal(TurnPhase.RulesDecisionRequired, harness.State.TurnPhase);
        Assert.Equal(seat, harness.State.ActiveSeatId);
        Assert.DoesNotContain(result.Transition!.Events, e => e is TurnCompleted or TurnStarted);
        Assert.DoesNotContain(result.Transition.Events, e => e is MarketReset);
        harness.AssertInvariants();
    }

    [Fact]
    public void CameraModeCannotSilentlyAcceptManualPlacement()
    {
        var harness = PendingClaim();
        harness.State.VerificationMode = VerificationMode.CameraVerified;
        var before = StateHash.Compute(harness.State);

        var result = harness.Submit(Evidence(harness));

        Assert.Equal("ManualVerificationNotSelected", result.Rejection!.Code);
        Assert.Equal(before, StateHash.Compute(harness.State));
        Assert.NotNull(harness.State.PendingClaim);
    }

    [Theory]
    [InlineData("", "whole board matches")]
    [InlineData("operator", " ")]
    public void ManualPlacementRequiresRecordedAttestationDetails(string actor, string reason)
    {
        var harness = PendingClaim();
        var result = harness.Submit(Evidence(harness) with { Operator = actor, Reason = reason });

        Assert.Equal("AttestationDetailsMissing", result.Rejection!.Code);
        Assert.NotNull(harness.State.PendingClaim);
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("cancel")]
    [InlineData("restore")]
    public void AnotherSeatCannotControlAPendingClaim(string action)
    {
        var harness = PendingClaim();
        var claim = harness.State.PendingClaim!;
        var other = harness.Seats.First(seat => seat.SeatId != claim.SeatId).SeatId;
        if (action == "restore")
            harness.SubmitAccepted(new CancelPendingClaim(harness.Envelope(claim.SeatId), claim.OperationId, true));

        GameCommand command = action switch
        {
            "confirm" => Evidence(harness) with { Envelope = harness.Envelope(other) },
            "cancel" => new CancelPendingClaim(harness.Envelope(other), claim.OperationId, false),
            _ => new ConfirmBeforeStateRestored(harness.Envelope(other), claim.OperationId),
        };
        var before = StateHash.Compute(harness.State);
        var result = harness.Submit(command);

        Assert.Equal("WrongSeat", result.Rejection!.Code);
        Assert.Equal(before, StateHash.Compute(harness.State));
    }

    [Fact]
    public void AClaimMustStillReferToTheCurrentBoardRevisionAtCommit()
    {
        var harness = PendingClaim();
        harness.State.BoardRevision++;

        var result = harness.Submit(Evidence(harness));

        Assert.Equal("StaleBoardRevision", result.Rejection!.Code);
        Assert.NotNull(harness.State.PendingClaim);
    }

    [Fact]
    public void ConservationRejectsUnknownReplacementCardsAndTickets()
    {
        var harness = Ready();
        harness.State.TrainDeckInternal[0] = new CardId(9999);
        harness.State.TicketDeckInternal[0] = new TicketId("unknown-ticket");

        var problems = InvariantChecker.Check(harness.State);

        Assert.Contains(problems, problem => problem.StartsWith("Unknown card", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.StartsWith("Unknown ticket", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownOwnedRouteProducesDiagnosticsInsteadOfThrowing()
    {
        var harness = Ready();
        harness.State.RouteOwnersInternal[new RouteId("unknown-route")] = harness.State.ActiveSeatId;

        var problems = InvariantChecker.Check(harness.State);

        Assert.Contains(problems, problem => problem.Contains("not on the supported board", StringComparison.Ordinal));
    }

    [Fact]
    public void EquivalentManifestCardRowOrderingPreservesSavedCardIdentities()
    {
        var source = TestManifest.Manifest;
        var reordered = new BoardManifest(source.ProfileId, source.SchemaVersion, source.RulesPolicyVersion,
            source.DataHash, source.Edition, source.DataAudit, source.Cities, source.Routes, source.Tickets,
            [.. source.TrainCardDefinitions.Reverse()], source.RulesConstants);
        var catalog = CardCatalog.FromManifest(reordered);

        Assert.Equal(source.DataHash, ManifestLoader.ComputeDataHash(reordered));
        Assert.All(TestManifest.Catalog.All,
            card => Assert.Equal(TestManifest.Catalog.KindOf(card), catalog.KindOf(card)));
    }

    [Theory]
    [InlineData("witness")]
    [InlineData("completed")]
    [InlineData("incomplete")]
    [InlineData("holdsBonus")]
    [InlineData("bonusHolders")]
    [InlineData("longest")]
    [InlineData("shared")]
    [InlineData("explanation")]
    public void VersionTwoHashCoversAllFinalScoringEvidence(string changedField)
    {
        var harness = Ready();
        var state = harness.State;
        state.FinalResult = FinalScoring.Compute(state);
        var original = StateHash.Compute(state);
        var result = state.FinalResult;
        var score = result.Scores[0];

        state.FinalResult = changedField switch
        {
            "witness" => result with { Scores = result.Scores.SetItem(0, score with { LongestTrailWitness = [state.Manifest.Routes[0].RouteId] }) },
            "completed" => result with { Scores = result.Scores.SetItem(0, score with { CompletedTickets = [state.Manifest.Tickets[0].TicketId] }) },
            "incomplete" => result with { Scores = result.Scores.SetItem(0, score with { IncompleteTickets = [] }) },
            "holdsBonus" => result with { Scores = result.Scores.SetItem(0, score with { HoldsLongestRouteBonus = !score.HoldsLongestRouteBonus }) },
            "bonusHolders" => result with { LongestRouteBonusHolders = [state.ActiveSeatId] },
            "longest" => result with { LongestTrailLength = result.LongestTrailLength + 1 },
            "shared" => result with { SharedVictory = !result.SharedVictory },
            _ => result with { TieBreakExplanation = "Changed result explanation" },
        };

        Assert.False(StateHash.Matches(state, original));
    }

    [Fact]
    public void HashValidationSupportsExistingSavesAndRejectsUnknownVersions()
    {
        var state = Ready().State;

        Assert.StartsWith("sha256-v2:", StateHash.Compute(state));
        Assert.True(StateHash.Matches(state, StateHash.Compute(state)));
        Assert.True(StateHash.Matches(state, StateHash.ComputeLegacy(state)));
        Assert.False(StateHash.Matches(state, "sha256-v3:unknown"));
    }

    [Fact]
    public void HashCoversRulesDecisionExplanationAndTicketOfferSetupFlag()
    {
        var state = Ready().State;
        state.RulesDecision = new RulesDecision("supply", "original");
        var original = StateHash.Compute(state);
        state.RulesDecision = state.RulesDecision with { Explanation = "changed" };
        Assert.False(StateHash.Matches(state, original));

        state.CurrentTicketOffer = new TicketOffer(state.ActiveSeatId, [state.TicketDeck[0]], 1, false);
        original = StateHash.Compute(state);
        state.CurrentTicketOffer = state.CurrentTicketOffer with { IsSetupOffer = true };
        Assert.False(StateHash.Matches(state, original));
    }

    private static RulesHarness Ready()
    {
        var harness = RulesHarness.Create();
        harness.CompleteSetup();
        return harness;
    }

    private static RulesHarness PendingClaim()
    {
        var harness = Ready();
        var seat = harness.State.ActiveSeatId;
        harness.GrantCards(seat, TrainCardKind.Red, 1);
        harness.SubmitAccepted(new PlanClaim(harness.Envelope(seat), new RouteId("atlanta--nashville"),
            harness.CardsOf(seat, TrainCardKind.Red, 1)));
        return harness;
    }

    private static SubmitClaimEvidence Evidence(RulesHarness harness) => new(
        harness.Envelope(harness.State.PendingClaim!.SeatId), harness.State.PendingClaim.OperationId,
        EvidenceKind.ManualAttestation, "tester", "whole board matches");

    // Establish a depleted but conserved supply independently of the engine. The opponent owns
    // every other card, so no shuffle can unexpectedly make additional cards available.
    private static void ArrangeSupply(RulesHarness harness, TrainCardKind?[] market, TrainCardKind[] deck)
    {
        var state = harness.State;
        var pool = state.Catalog.All.ToList();
        CardId Take(TrainCardKind kind)
        {
            var card = pool.First(card => state.Catalog.KindOf(card) == kind);
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
        harness.AssertInvariants();
    }
}
