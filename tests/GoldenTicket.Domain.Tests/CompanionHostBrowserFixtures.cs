using System.Text.Json;
using System.Text.Json.Serialization;
using GoldenTicket.Application;
using GoldenTicket.CompanionHost;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.Domain.Tests;

/// <summary>Reproducible synthetic data for the browser UI test. These are actual bridge payloads,
/// never captures of a person's saved match. Set GOLDENTICKET_COMPANION_FIXTURE_DIRECTORY to export.</summary>
public class CompanionHostBrowserFixtures
{
    [Fact]
    public async Task ExportSyntheticBridgePayloadsWhenRequested()
    {
        var token = TestContext.Current.CancellationToken;
        async Task<GameCoordinator> Create(bool setupComplete)
        {
            var game = await GameCoordinator.CreateAsync(new GameRules(TestManifest.Manifest, TestManifest.Catalog), new InMemorySessionStore(),
                new SessionSetup(SessionId.New(), [new Seat(new(1), "Alex", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard),
                    new Seat(new(2), "Jordan", PlayerColor.Red, SeatKind.Human, AiDifficulty.Standard)], new(1), VerificationMode.Manual), DeterministicRandom.SeedFrom(91), token);
            if (setupComplete)
                foreach (var seat in game.Seats)
                {
                    var hand = await game.GetSeatViewAsync(seat.SeatId, token);
                    Assert.True((await game.SubmitAsync(new CommitTicketSelection(game.NewEnvelope(seat.SeatId), [.. hand.SetupOffer.Take(2)], []), token)).IsAccepted);
                }
            return game;
        }
        var fixtures = new Dictionary<string, object>();
        async Task Capture(string name, GameCoordinator game)
        {
            var bridge = new CoordinatorCompanionBridge(() => game);
            var publicView = await bridge.ReadPublicAsync(token);
            var privateView = await bridge.ReadPrivateAsync(new(publicView.RevealSeatId!.Value), publicView.Game!.StateVersion, token);
            Assert.NotNull(privateView);
            fixtures[name] = new { snapshot = publicView, data = privateView };
        }
        var setup = await Create(false); await Capture("setup", setup);
        var first = await setup.GetSeatViewAsync(new(1), token);
        await setup.SubmitAsync(new CommitTicketSelection(setup.NewEnvelope(new(1)), [.. first.SetupOffer.Take(2)], []), token);
        await Capture("secondHumanSetup", setup);
        var game = await Create(true); await Capture("turnStart", game);
        await game.SubmitAsync(new SelectTrainCard(game.NewEnvelope(new(1)), null), token);
        await Capture("secondDraw", game);
        await game.SubmitAsync(new SelectTrainCard(game.NewEnvelope(new(1)), null), token);
        await Capture("nextHuman", game);
        var ticketGame = await Create(true);
        await ticketGame.SubmitAsync(new RequestTicketOffer(ticketGame.NewEnvelope(new(1))), token);
        await Capture("ticketOffer", ticketGame);
        var routeGame = await Create(true);
        var legal = await routeGame.GetLegalActionsAsync(new(1), token); Assert.NotEmpty(legal.Claims);
        var handForClaim = await routeGame.GetSeatViewAsync(new(1), token);
        var claim = legal.Claims[0];
        await routeGame.SubmitAsync(new PlanClaim(routeGame.NewEnvelope(new(1)), claim.RouteId,
            LegalActionCalculator.ResolveCards(handForClaim, claim.Payments[0])), token);
        await Capture("physicalPlacement", routeGame);
        var directory = Environment.GetEnvironmentVariable("GOLDENTICKET_COMPANION_FIXTURE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
            options.Converters.Add(new JsonStringEnumConverter());
            await File.WriteAllTextAsync(Path.Combine(directory, "fixtures.json"), JsonSerializer.Serialize(fixtures, options), token);
        }
    }
}
