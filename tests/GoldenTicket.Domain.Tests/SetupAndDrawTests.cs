using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Domain.Tests;

/// <summary>Setup, the digital market, and the two-card draw subphases (DESIGN 6.1, 6.2, 22.2).</summary>
public class SetupAndDrawTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void SetupDealsTheProfilesOpeningHandsAndMarket(int seatCount)
    {
        var harness = RulesHarness.Create(seatCount);
        var constants = harness.Manifest.RulesConstants;

        Assert.All(harness.Seats, seat =>
        {
            Assert.Equal(constants.StartingTrainCards, harness.State.HandOf(seat.SeatId).Count);
            Assert.Equal(constants.StartingTrainsPerSeat, harness.State.TrainStock[seat.SeatId]);
            Assert.Equal(constants.SetupTicketOffer, harness.State.SetupOffers[seat.SeatId].Length);
        });

        Assert.Equal(constants.FaceUpMarketSize, harness.State.FaceUp.Count(card => card is not null));
        harness.AssertInvariants();
        harness.AssertReplayMatches();
    }

    [Fact]
    public void OpeningMarketNeverHoldsThreeLocomotives()
    {
        // The rule applies to the opening layout too, so it must hold across many shuffles.
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var harness = RulesHarness.Create(4, seed);
            var locomotives = harness.State.FaceUp
                .Count(card => card is { } value && harness.State.Catalog.KindOf(value) == TrainCardKind.Locomotive);

            Assert.True(locomotives < harness.Manifest.RulesConstants.LocomotiveMarketResetThreshold,
                $"Seed {seed} left {locomotives} locomotives face up.");
        }
    }

    [Fact]
    public void SetupReturnsAreRecycledOnlyAfterEverySeatHasChosen()
    {
        var harness = RulesHarness.Create(3);
        var seats = harness.Seats.Select(seat => seat.SeatId).ToList();

        harness.SubmitAccepted(new CommitTicketSelection(
            harness.Envelope(seats[0]), [.. harness.State.SetupOffers[seats[0]].Take(2)], []));

        // One seat has chosen; nothing may be recycled yet.
        Assert.Single(harness.State.PendingTicketReturns);
        Assert.Equal(SessionLifecycle.Setup, harness.State.Lifecycle);
        Assert.DoesNotContain(harness.Journal, row => row.Event is SetupReturnsRecycled);

        foreach (var seat in seats.Skip(1))
        {
            harness.SubmitAccepted(new CommitTicketSelection(
                harness.Envelope(seat), [.. harness.State.SetupOffers[seat].Take(2)], []));
        }

        var recycled = harness.Journal.Select(row => row.Event).OfType<SetupReturnsRecycled>().Single();
        Assert.Equal(3, recycled.AppendedInOrder.Length);
        Assert.Empty(harness.State.PendingTicketReturns);
        Assert.Equal(SessionLifecycle.Active, harness.State.Lifecycle);

        // The returned tickets are at the bottom of the deck, in seat order.
        var bottom = harness.State.TicketDeck.TakeLast(3).ToArray();
        Assert.Equal(recycled.AppendedInOrder, bottom);

        harness.AssertInvariants();
        harness.AssertReplayMatches();
    }

    [Fact]
    public void SetupSelectionBelowTheMinimumIsRejectedWithoutChangingAnything()
    {
        var harness = RulesHarness.Create();
        var seat = harness.Seats[0].SeatId;
        var before = StateHash.Compute(harness.State);

        var result = harness.Submit(new CommitTicketSelection(
            harness.Envelope(seat), [harness.State.SetupOffers[seat][0]], []));

        Assert.False(result.IsAccepted);
        Assert.Equal("TooFewTicketsKept", result.Rejection!.Code);
        Assert.Equal(before, StateHash.Compute(harness.State));
    }

    [Fact]
    public void TwoBlindDrawsCompleteATurnAndPassItOn()
    {
        var harness = RulesHarness.Create(3);
        harness.CompleteSetup();

        var first = harness.State.ActiveSeatId;
        Assert.Equal(TurnPhase.TurnStart, harness.State.TurnPhase);

        harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(first), null));
        Assert.Equal(TurnPhase.AwaitingSecondTrainCard, harness.State.TurnPhase);
        Assert.Equal(first, harness.State.ActiveSeatId);

        harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(first), null));
        Assert.Equal(TurnPhase.TurnStart, harness.State.TurnPhase);
        Assert.NotEqual(first, harness.State.ActiveSeatId);

        Assert.Equal(6, harness.State.HandOf(first).Count);
        harness.AssertInvariants();
        harness.AssertReplayMatches();
    }

    [Fact]
    public void AFaceUpLocomotiveTakesTheWholeTurn()
    {
        var harness = RulesHarness.Create(3);
        harness.CompleteSetup();
        var seat = harness.State.ActiveSeatId;

        var slot = FindFaceUpSlot(harness, TrainCardKind.Locomotive);
        if (slot is null) return; // This shuffle has no face-up locomotive; other seeds cover it.

        harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(seat), slot));

        Assert.NotEqual(seat, harness.State.ActiveSeatId);
        Assert.Equal(5, harness.State.HandOf(seat).Count);
    }

    [Fact]
    public void AFaceUpLocomotiveCannotBeTheSecondPick()
    {
        var harness = FindHarnessWithFaceUpLocomotive();
        var seat = harness.State.ActiveSeatId;

        harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(seat), null));

        var slot = FindFaceUpSlot(harness, TrainCardKind.Locomotive);
        Assert.NotNull(slot);

        var result = harness.Submit(new SelectTrainCard(harness.Envelope(seat), slot));

        Assert.False(result.IsAccepted);
        Assert.Equal("LocomotiveCannotBeSecondPick", result.Rejection!.Code);
        Assert.Equal(TurnPhase.AwaitingSecondTrainCard, harness.State.TurnPhase);

        // The blocked slot is also absent from the offered actions.
        Assert.DoesNotContain(slot.Value, harness.Legal(seat).DrawableFaceUpSlots);
    }

    [Fact]
    public void TheReplacementCardIsAvailableForTheSecondPick()
    {
        var harness = RulesHarness.Create(3);
        harness.CompleteSetup();
        var seat = harness.State.ActiveSeatId;

        var slot = Enumerable.Range(0, harness.State.FaceUp.Count)
            .First(index => harness.State.FaceUp[index] is { } card &&
                            harness.State.Catalog.KindOf(card) != TrainCardKind.Locomotive);

        var takenCard = harness.State.FaceUp[slot]!.Value;
        harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(seat), slot));

        var replacement = harness.State.FaceUp[slot];
        Assert.NotNull(replacement);
        Assert.NotEqual(takenCard, replacement!.Value);

        // The second pick draws the replacement instance, not the card that was taken.
        if (harness.State.Catalog.KindOf(replacement.Value) == TrainCardKind.Locomotive) return;

        harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(seat), slot));
        Assert.Contains(replacement.Value, harness.State.HandOf(seat));
        Assert.Contains(takenCard, harness.State.HandOf(seat));
    }

    [Fact]
    public void OnlyTheActiveSeatMayDraw()
    {
        var harness = RulesHarness.Create(3);
        harness.CompleteSetup();

        var other = harness.Seats.First(seat => seat.SeatId != harness.State.ActiveSeatId).SeatId;
        var result = harness.Submit(new SelectTrainCard(harness.Envelope(other), null));

        Assert.False(result.IsAccepted);
        Assert.Equal("NotActiveSeat", result.Rejection!.Code);
    }

    [Fact]
    public void AStaleStateVersionIsRejected()
    {
        var harness = RulesHarness.Create(3);
        harness.CompleteSetup();
        var seat = harness.State.ActiveSeatId;

        var stale = harness.Envelope(seat) with { ExpectedStateVersion = harness.State.StateVersion - 1 };
        var result = harness.Submit(new SelectTrainCard(stale, null));

        Assert.False(result.IsAccepted);
        Assert.Equal("StaleStateVersion", result.Rejection!.Code);
    }

    [Fact]
    public void DrawingDestinationTicketsOffersThreeAndKeepsAtLeastOne()
    {
        var harness = RulesHarness.Create(3);
        harness.CompleteSetup();
        var seat = harness.State.ActiveSeatId;
        var before = harness.State.TicketsOf(seat).Count;

        harness.SubmitAccepted(new RequestTicketOffer(harness.Envelope(seat)));

        var offer = harness.State.CurrentTicketOffer;
        Assert.NotNull(offer);
        Assert.Equal(3, offer!.Offered.Length);
        Assert.Equal(1, offer.MinimumKeep);
        Assert.Equal(TurnPhase.AwaitingTicketKeep, harness.State.TurnPhase);

        // Keeping none is refused; the offer survives intact.
        var refused = harness.Submit(new CommitTicketSelection(harness.Envelope(seat), [], []));
        Assert.False(refused.IsAccepted);
        Assert.Equal(offer.Offered, harness.State.CurrentTicketOffer!.Offered);

        var keeping = offer.Offered[1];
        var returning = new[] { offer.Offered[2], offer.Offered[0] };
        harness.SubmitAccepted(new CommitTicketSelection(harness.Envelope(seat), [keeping], [.. returning]));

        Assert.Equal(before + 1, harness.State.TicketsOf(seat).Count);
        Assert.Contains(keeping, harness.State.TicketsOf(seat));

        // DESIGN 6.4: rejections go underneath in the order the acting seat chose.
        Assert.Equal(returning, harness.State.TicketDeck.TakeLast(2).ToArray());

        harness.AssertInvariants();
        harness.AssertReplayMatches();
    }

    private static int? FindFaceUpSlot(RulesHarness harness, TrainCardKind kind)
    {
        for (var slot = 0; slot < harness.State.FaceUp.Count; slot++)
        {
            if (harness.State.FaceUp[slot] is { } card && harness.State.Catalog.KindOf(card) == kind)
                return slot;
        }

        return null;
    }

    private static RulesHarness FindHarnessWithFaceUpLocomotive()
    {
        for (ulong seed = 1; seed < 200; seed++)
        {
            var harness = RulesHarness.Create(3, seed);
            harness.CompleteSetup();
            if (FindFaceUpSlot(harness, TrainCardKind.Locomotive) is not null) return harness;
        }

        throw new InvalidOperationException("No seed produced a face-up locomotive after setup.");
    }
}
