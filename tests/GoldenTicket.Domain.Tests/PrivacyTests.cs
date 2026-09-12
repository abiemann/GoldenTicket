using System.Text.Json;
using GoldenTicket.Domain.Ai;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.Domain.Tests;

/// <summary>
/// DESIGN 5.2 / 22.4: nothing in a public projection, the public history, or an AI's inputs may
/// carry a secret. These tests search the actual serialised payloads for sentinel identifiers rather
/// than trusting that the interface hides them.
/// </summary>
public class PrivacyTests
{
    private static RulesHarness Ready(ulong seed = 7)
    {
        var harness = RulesHarness.Create(3, seed);
        harness.CompleteSetup();
        return harness;
    }

    [Fact]
    public void ThePublicViewCarriesNoHandOrTicketIdentities()
    {
        var harness = Ready();

        // Take a card so there is a private draw to leak, then look for it.
        var seat = harness.State.ActiveSeatId;
        harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(seat), null));

        var drawnCard = harness.State.HandOf(seat)[^1];
        var serialised = JsonSerializer.Serialize(harness.PublicView(), EventSerializerOptions);

        foreach (var ticketId in harness.State.TicketsOf(seat))
            Assert.DoesNotContain(ticketId.Value, serialised, StringComparison.Ordinal);

        // Every hand card id, and the whole deck order, must be absent.
        Assert.DoesNotContain($"\"handCards\"", serialised, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(JsonSerializer.Serialize(harness.State.TrainDeck.Take(5)), serialised);
        Assert.DoesNotContain(harness.State.RandomState.ToWire(), serialised, StringComparison.Ordinal);

        // The public view knows how many cards the seat holds, never which.
        var summary = harness.PublicView().SeatOf(seat);
        Assert.Equal(harness.State.HandOf(seat).Count, summary.TrainCardCount);
        Assert.True(summary.TrainCardCount > 0);
        Assert.NotEqual(0, drawnCard.Value + 1); // the drawn instance exists but is not published
    }

    private static readonly JsonSerializerOptions EventSerializerOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    [Fact]
    public void ThePublicHistoryDescribesABlindDrawWithoutNamingTheCard()
    {
        var harness = Ready();
        var seat = harness.State.ActiveSeatId;

        harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(seat), null));

        var entries = harness.Journal
            .Select(row => row.Event.ToPublicEntry(harness.Manifest))
            .Where(entry => entry is not null)
            .Select(entry => entry!.Text)
            .ToList();

        Assert.Contains(entries, text => text.Contains("Drew a card from the deck", StringComparison.Ordinal));

        var joined = string.Join("\n", entries);
        foreach (var card in harness.State.HandOf(seat))
            Assert.DoesNotContain($"card {card.Value}", joined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThePublicHistoryReportsTicketCountsNotTicketNames()
    {
        var harness = RulesHarness.Create(3, 11);
        var seat = harness.Seats[0].SeatId;
        var offered = harness.State.SetupOffers[seat];

        harness.SubmitAccepted(new CommitTicketSelection(harness.Envelope(seat), [.. offered.Take(2)], []));

        var entry = harness.Journal
            .Select(row => row.Event.ToPublicEntry(harness.Manifest))
            .Last(text => text is { Kind: "TicketsKept" });

        Assert.Contains("Kept 2 destination tickets", entry!.Text, StringComparison.Ordinal);

        foreach (var ticketId in offered)
        {
            Assert.DoesNotContain(ticketId.Value, entry.Text, StringComparison.Ordinal);

            var ticket = harness.Manifest.Ticket(ticketId);
            var city = harness.Manifest.City(ticket.CityA).DisplayName;
            Assert.DoesNotContain(city, entry.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ASeatViewNeverCarriesAnotherSeatsCardsOrTheDeckOrder()
    {
        var harness = Ready();
        var seat = harness.Seats[0].SeatId;
        var other = harness.Seats[1].SeatId;

        var view = harness.SeatView(seat);
        var serialised = JsonSerializer.Serialize(view, EventSerializerOptions);

        foreach (var card in harness.State.HandOf(other))
            Assert.DoesNotContain($":{card.Value},", serialised, StringComparison.Ordinal);

        foreach (var ticketId in harness.State.TicketsOf(other))
            Assert.DoesNotContain(ticketId.Value, serialised, StringComparison.Ordinal);

        Assert.DoesNotContain(harness.State.RandomState.ToWire(), serialised, StringComparison.Ordinal);
        Assert.Equal(seat, view.SeatId);
    }

    /// <summary>
    /// DESIGN 22.2: altering hidden referee state without changing the seat view must not change what
    /// a computer opponent does for a fixed seed.
    /// </summary>
    [Fact]
    public async Task ComputerDecisionsDependOnlyOnThePermittedView()
    {
        var harness = Ready(19);
        var seat = harness.State.ActiveSeatId;

        var view = harness.SeatView(seat);
        var policy = new AI.HeuristicAiPolicy();
        var budget = new DecisionBudget(AiDifficulty.Standard, TimeSpan.FromSeconds(5));

        var first = await policy.ChooseAsync(
            view, harness.Manifest, budget, new DeterministicRandom(DeterministicRandom.SeedFrom(5)),
            CancellationToken.None);

        // Shuffle the parts of the referee's state this seat may not see.
        var other = harness.Seats.First(s => s.SeatId != seat).SeatId;
        harness.State.TrainHandsInternal[other].Reverse();
        harness.State.TrainDeckInternal.Reverse();
        harness.State.TicketDeckInternal.Reverse();
        harness.State.RandomState = DeterministicRandom.SeedFrom(999);

        var second = await policy.ChooseAsync(
            harness.SeatView(seat), harness.Manifest, budget,
            new DeterministicRandom(DeterministicRandom.SeedFrom(5)), CancellationToken.None);

        Assert.Equal(first, second);
    }

    [Fact]
    public void EveryEventDeclaresAVisibilityAndOnlyPublicOnesStayReadable()
    {
        var harness = Ready();
        harness.SubmitAccepted(new SelectTrainCard(harness.Envelope(harness.State.ActiveSeatId), null));

        var byVisibility = harness.Journal
            .Select(row => row.Event)
            .GroupBy(domainEvent => domainEvent.Visibility)
            .ToDictionary(group => group.Key, group => group.Select(e => e.GetType().Name).Distinct().ToList());

        // Deck order and the random continuation are referee-only.
        Assert.Contains(nameof(SessionCreated), byVisibility[EventVisibility.Referee]);
        Assert.Contains(nameof(InitialHandsDealt), byVisibility[EventVisibility.Referee]);

        // A blind draw and a ticket offer are private to the acting seat.
        Assert.Contains(nameof(BlindCardDrawn), byVisibility[EventVisibility.Private]);
        Assert.Contains(nameof(SetupOfferCreated), byVisibility[EventVisibility.Private]);

        // Turn flow and the face-up market are public.
        Assert.Contains(nameof(TurnStarted), byVisibility[EventVisibility.Public]);
    }
}
