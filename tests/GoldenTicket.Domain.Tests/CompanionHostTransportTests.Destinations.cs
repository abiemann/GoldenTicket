using System.IO;
using GoldenTicket.CompanionHost;

namespace GoldenTicket.Domain.Tests;

public partial class CompanionHostTransportTests
{
    [Fact]
    public async Task DestinationEndpointsRemainPrivateAndSurviveTheFirstDraw()
    {
        var token = TestContext.Current.CancellationToken;
        var game = await CreateActiveContinuationGame(token);
        await using var host = await ContinuationHost.Create(game, token);
        host.Bridge.BoardMap = new(Guid.NewGuid().ToString("N"), [],
            [new("atlanta", "Atlanta", 747, 380), new("chicago", "Chicago", 653, 245)]);

        using var response = await host.OpenEvents(token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        var publicUpdate = (await ReadEvent(reader, token)).Data.GetProperty("snapshot");
        Assert.Equal(2, publicUpdate.GetProperty("boardMap").GetProperty("cities").GetArrayLength());
        Assert.DoesNotContain("fromCityId", publicUpdate.GetRawText());
        Assert.DoesNotContain("toCityId", publicUpdate.GetRawText());
        Assert.DoesNotContain("heldTickets", publicUpdate.GetRawText());

        var own = Assert.IsType<CompanionPrivateSnapshot>(
            await host.Bridge.ReadPrivateAsync(new(1), game.Public.StateVersion, token));
        Assert.NotEmpty(own.HeldTickets);
        foreach (var ticket in own.HeldTickets)
        {
            var definition = game.Manifest.Ticket(new(ticket.Id));
            Assert.Equal(definition.CityA.Value, ticket.FromCityId);
            Assert.Equal(definition.CityB.Value, ticket.ToCityId);
            Assert.Equal(game.Manifest.City(definition.CityA).DisplayName, ticket.From);
            Assert.Equal(game.Manifest.City(definition.CityB).DisplayName, ticket.To);
        }
        Assert.Null(await host.Bridge.ReadPrivateAsync(new(2), game.Public.StateVersion, token));

        var receipt = await host.Send(await host.Reveal(token), host.Command("drawTrain"), token);
        var continuation = Assert.IsType<CompanionPrivateContinuation>(receipt.Continuation);
        Assert.Equal(own.HeldTickets, continuation.Data.HeldTickets);
        var continuedMap = Assert.IsType<CompanionBoardMap>(continuation.Snapshot.BoardMap);
        Assert.Equal(host.Bridge.BoardMap.ImageId, continuedMap.ImageId);
        Assert.Equal(host.Bridge.BoardMap.Targets, continuedMap.Targets);
        Assert.Equal(host.Bridge.BoardMap.Cities, continuedMap.Cities);
    }
}
