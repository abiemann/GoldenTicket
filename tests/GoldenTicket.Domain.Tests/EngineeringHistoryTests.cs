using GoldenTicket.Application;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.Domain.Tests;

public sealed class EngineeringHistoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_computer_blind_draws_name_the_committed_card_in_engineering_history()
    {
        var (game, _) = await ReadyAsync();
        var updates = new List<CoordinatorUpdate>();
        game.Updated += (_, update) => updates.Add(update);
        var computerKinds = new List<TrainCardKind>();
        var expectedEngineering = game.PublicHistory
            .Select(entry => new EngineeringEventEntry(entry.Seat, entry.Text)).ToList();
        Assert.Equal(expectedEngineering, game.EngineeringHistory);

        // One complete computer turn followed by one complete human turn.
        for (var index = 0; index < 4; index++)
        {
            var seat = game.Public.ActiveSeatId;
            var computer = game.Public.SeatOf(seat).Kind == SeatKind.Computer;
            var updateCount = updates.Count;
            var outcome = await game.SubmitAsync(new SelectTrainCard(game.NewEnvelope(seat), null), Token);
            Assert.True(outcome.IsAccepted);
            var draw = Assert.Single(outcome.Result.Transition!.Events.OfType<BlindCardDrawn>());
            var kind = TestManifest.Catalog.KindOf(draw.Card);
            if (computer) computerKinds.Add(kind);
            var published = Assert.Single(updates.Skip(updateCount));
            var publicDraw = Assert.Single(published.NewEntries.Where(entry => entry.Kind == "BlindCardDrawn"));
            Assert.Equal("Drew a card from the deck.", publicDraw.Text);

            foreach (var entry in published.NewEntries)
                expectedEngineering.Add(new EngineeringEventEntry(entry.Seat,
                    computer && entry.Kind == "BlindCardDrawn"
                        ? $"Drew a {kind} card from the deck." : entry.Text));
            Assert.Equal(expectedEngineering, game.EngineeringHistory);
        }

        Assert.Equal(2, computerKinds.Count);
        Assert.Equal(TrainCardKind.Locomotive, computerKinds[0]);
        Assert.NotEqual(TrainCardKind.Locomotive, computerKinds[1]);
        Assert.Equal(2, game.EngineeringHistory.Count(entry =>
            entry.Seat == new SeatId(2) && entry.Text == "Drew a card from the deck."));
        Assert.All(game.PublicHistory.Where(entry => entry.Kind == "BlindCardDrawn"),
            entry => Assert.Equal("Drew a card from the deck.", entry.Text));
        Assert.All(updates.SelectMany(update => update.NewEntries).Where(entry => entry.Kind == "BlindCardDrawn"),
            entry => Assert.Equal("Drew a card from the deck.", entry.Text));
    }

    [Fact]
    public async Task Engineering_history_replays_identically_and_duplicate_commands_do_not_append_entries()
    {
        var (game, store) = await ReadyAsync();
        var computer = game.Public.ActiveSeatId;
        var command = new SelectTrainCard(game.NewEnvelope(computer), null);
        var outcome = await game.SubmitAsync(command, Token);
        Assert.True(outcome.IsAccepted);
        Assert.False(outcome.WasDuplicate);
        Assert.Contains(game.EngineeringHistory,
            entry => entry.Seat == computer && entry.Text == "Drew a Locomotive card from the deck.");
        var engineering = game.EngineeringHistory.ToArray();
        var publicHistory = game.PublicHistory.ToArray();
        var hash = await game.ComputeStateHashAsync(Token);
        var updateCount = 0;
        game.Updated += (_, _) => updateCount++;

        var duplicate = await game.SubmitAsync(command, Token);
        Assert.True(duplicate.IsAccepted);
        Assert.True(duplicate.WasDuplicate);
        Assert.Equal(0, updateCount);
        Assert.Equal(engineering, game.EngineeringHistory);
        Assert.Equal(publicHistory, game.PublicHistory);
        Assert.Equal(hash, await game.ComputeStateHashAsync(Token));

        var restored = await GameCoordinator.RestoreAsync(Rules(), store, game.SessionId, Token);
        Assert.Equal(engineering, restored.EngineeringHistory);
        Assert.Equal(publicHistory, restored.PublicHistory);
        Assert.Equal(hash, await restored.ComputeStateHashAsync(Token));
        var restoredUpdates = 0;
        restored.Updated += (_, _) => restoredUpdates++;
        var replayedDuplicate = await restored.SubmitAsync(command, Token);
        Assert.True(replayedDuplicate.IsAccepted);
        Assert.True(replayedDuplicate.WasDuplicate);
        Assert.Equal(0, restoredUpdates);
        Assert.Equal(engineering, restored.EngineeringHistory);
        Assert.Equal(publicHistory, restored.PublicHistory);
    }

    private static GameRules Rules() => new(TestManifest.Manifest, TestManifest.Catalog);

    private static async Task<(GameCoordinator Game, InMemorySessionStore Store)> ReadyAsync()
    {
        var setup = new SessionSetup(SessionId.New(),
        [
            new Seat(new SeatId(1), "Computer", PlayerColor.Blue, SeatKind.Computer, AiDifficulty.Relaxed),
            new Seat(new SeatId(2), "Human", PlayerColor.Red, SeatKind.Human, AiDifficulty.Relaxed)
        ], new SeatId(1), VerificationMode.Manual);
        var rules = Rules();
        var store = new InMemorySessionStore();
        var game = await GameCoordinator.CreateAsync(rules, store, setup,
            LocomotiveThenColorSeed(rules, setup), Token);
        foreach (var seat in game.Seats)
        {
            var view = await game.GetSeatViewAsync(seat.SeatId, Token);
            Assert.True((await game.SubmitAsync(new CommitTicketSelection(game.NewEnvelope(seat.SeatId),
                [.. view.SetupOffer.Take(2)], []), Token)).IsAccepted);
        }
        Assert.Equal(new SeatId(1), game.Public.ActiveSeatId);
        Assert.Equal(TurnPhase.TurnStart, game.Public.TurnPhase);
        return (game, store);
    }

    private static RandomState LocomotiveThenColorSeed(GameRules rules, SessionSetup setup)
    {
        // Pick a deterministic real shuffle instead of injecting cards or relying on a display string.
        for (ulong seed = 1; seed <= 256; seed++)
        {
            var random = DeterministicRandom.SeedFrom(seed);
            var (state, _) = rules.CreateSession(setup, random);
            if (state.Catalog.KindOf(state.TrainDeck[0]) == TrainCardKind.Locomotive &&
                state.Catalog.KindOf(state.TrainDeck[1]) != TrainCardKind.Locomotive) return random;
        }
        throw new InvalidOperationException("No deterministic locomotive-then-color shuffle was found.");
    }
}
