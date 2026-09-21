using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;

namespace GoldenTicket.Domain.Tests;

public sealed class GameStateEncapsulationTests
{
    [Fact]
    public void PublicCollectionsCannotBeCastBackAndMutated()
    {
        var harness = RulesHarness.Create();
        var state = harness.State;
        var before = StateHash.Compute(state);
        var seat = state.ActiveSeatId;

        CannotClear(state.RouteOwners);
        CannotClear(state.TrainStock);
        CannotClear(state.RouteScore);
        CannotClear(state.SetupOffers);
        CannotClear(state.PendingTicketReturns);
        CannotClear(state.TrainDeck);
        CannotClear(state.TrainDiscard);
        CannotClear(state.FaceUp);
        CannotClear(state.TicketDeck);
        CannotClear(state.HandOf(seat));
        CannotClear(state.TicketsOf(seat));
        var available = Assert.IsAssignableFrom<IReadOnlyList<CardId>>(state.AvailableCardsOf(seat));
        CannotClear(available);

        Assert.Equal(before, StateHash.Compute(state));
        harness.AssertInvariants();
        harness.AssertReplayMatches();
    }

    [Fact]
    public void PublicCollectionsAndDictionaryEnumerationsDoNotExposeBackingCollectionsThroughSyncRoot()
    {
        var harness = RulesHarness.Create();
        var state = harness.State;
        var before = StateHash.Compute(state);
        var seat = state.ActiveSeatId;

        HasNoSyncRoot(state.RouteOwners);
        HasNoSyncRoot(state.TrainStock);
        HasNoSyncRoot(state.RouteScore);
        HasNoSyncRoot(state.SetupOffers);
        HasNoSyncRoot(state.PendingTicketReturns);
        HasNoSyncRoot(state.TrainDeck);
        HasNoSyncRoot(state.TrainDiscard);
        HasNoSyncRoot(state.FaceUp);
        HasNoSyncRoot(state.TicketDeck);
        HasNoSyncRoot(state.HandOf(seat));
        HasNoSyncRoot(state.TicketsOf(seat));
        HasNoSyncRoot(state.AvailableCardsOf(seat));

        foreach (var enumeration in new object[]
        {
            state.RouteOwners.Keys, state.RouteOwners.Values,
            state.TrainStock.Keys, state.TrainStock.Values,
            state.RouteScore.Keys, state.RouteScore.Values,
            state.SetupOffers.Keys, state.SetupOffers.Values,
            state.PendingTicketReturns.Keys, state.PendingTicketReturns.Values,
        })
            HasNoSyncRoot(enumeration);

        Assert.Equal(before, StateHash.Compute(state));
        harness.AssertInvariants();
        harness.AssertReplayMatches();
    }

    [Fact]
    public void CollectionViewsStillReflectReducerChangesAndForksStayDetached()
    {
        var harness = RulesHarness.Create();
        harness.CompleteSetup();
        var state = harness.State;
        var seat = state.ActiveSeatId;
        var hand = state.HandOf(seat);
        var deck = state.TrainDeck;
        var originalHash = StateHash.Compute(state);
        var card = deck[0];
        var handCount = hand.Count;
        var deckCount = deck.Count;
        var fork = state.Fork();
        var forkHand = fork.HandOf(seat);

        GameReducer.ApplyTransition(fork, [new BlindCardDrawn(seat, card)]);

        Assert.Equal(handCount + 1, forkHand.Count);
        Assert.Contains(card, forkHand);
        Assert.Equal(deckCount - 1, fork.TrainDeck.Count);
        Assert.Equal(handCount, hand.Count);
        Assert.Equal(deckCount, deck.Count);
        Assert.Equal(originalHash, StateHash.Compute(state));

        harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(seat), null));

        Assert.Equal(handCount + 1, hand.Count);
        Assert.Contains(card, hand);
        Assert.Equal(deckCount - 1, deck.Count);
        harness.AssertInvariants();
        harness.AssertReplayMatches();
    }

    private static void CannotClear<T>(IReadOnlyList<T> values)
    {
        Assert.False(values is ICollection<T>);
        Assert.False(values is System.Collections.IList);
    }

    private static void CannotClear<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> values)
        where TKey : notnull
    {
        Assert.False(values is IDictionary<TKey, TValue>);
        Assert.False(values is ICollection<KeyValuePair<TKey, TValue>>);
        Assert.False(values is System.Collections.IDictionary);
    }

    private static void HasNoSyncRoot(object values) =>
        Assert.False(values is System.Collections.ICollection);
}
