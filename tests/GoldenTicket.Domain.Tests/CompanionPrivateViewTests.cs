using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoldenTicket.Application;
using GoldenTicket.CompanionHost;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GoldenTicket.Domain.Tests;

public sealed class CompanionPrivateViewTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task RevealedCardsRemainUsableWithoutInteractionWhileSessionPollingContinues()
    {
        var token = TestContext.Current.CancellationToken;
        await using var host = await PrivateViewHost.Create(token);
        Assert.False(host.Revealed.TryGetProperty("expiresAt", out _));
        using var removedActivity = await host.Client.PostAsJsonAsync("/api/activity", new { }, token);
        Assert.Equal(HttpStatusCode.NotFound, removedActivity.StatusCode);

        for (var i = 0; i < 20; i++)
        {
            host.Time.Now += TimeSpan.FromSeconds(2);
            var session = await host.Client.GetFromJsonAsync<JsonElement>("/api/session", token);
            Assert.True(session.GetProperty("paired").GetBoolean());
            Assert.Equal(host.Revealed.GetProperty("handoffGeneration").GetInt64(),
                session.GetProperty("handoffGeneration").GetInt64());
        }

        var request = host.Choice();
        using var response = await host.Client.PostAsJsonAsync("/api/command", request, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("accepted").GetBoolean());
        Assert.True(host.Game.Public.StateVersion > request.Command.ExpectedStateVersion);
        Assert.False(host.Server.Authority.ValidateGrant(host.Credentials, request.Grant, request.Seat,
            request.Command.SessionId, request.Command.ExpectedStateVersion));
        Assert.Empty(await host.Game.CheckInvariantsAsync(token));
    }

    [Theory]
    [InlineData("hide")]
    [InlineData("handoff")]
    [InlineData("another-reveal")]
    [InlineData("revoke")]
    [InlineData("disconnect")]
    [InlineData("session-expiry")]
    public async Task PollingNeverRestoresAViewAfterItHasBeenInvalidated(string invalidation)
    {
        var token = TestContext.Current.CancellationToken;
        await using var host = await PrivateViewHost.Create(token);
        var request = host.Choice();
        switch (invalidation)
        {
            case "hide":
                using (var hidden = await host.Client.PostAsJsonAsync("/api/hide", new { }, token))
                    Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);
                break;
            case "handoff": host.Server.InvalidatePrivateGrants(); break;
            case "another-reveal":
                using (var revealed = await host.Client.PostAsJsonAsync("/api/reveal", host.RevealRequest(), token))
                    Assert.Equal(HttpStatusCode.OK, revealed.StatusCode);
                break;
            case "revoke": host.Server.RevokeController(); break;
            case "disconnect": host.Time.Now += TimeSpan.FromSeconds(6); break;
            case "session-expiry": host.Time.Now += TimeSpan.FromHours(12); break;
        }

        // Reconnection or an ordinary heartbeat cannot restore the old private view.
        using var polled = await host.Client.GetAsync("/api/session", token);
        Assert.Equal(HttpStatusCode.OK, polled.StatusCode);
        using var response = await host.Client.PostAsJsonAsync("/api/command", request, token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(request.Command.ExpectedStateVersion, host.Game.Public.StateVersion);
        Assert.False(host.Server.Authority.ValidateGrant(host.Credentials, request.Grant, request.Seat,
            request.Command.SessionId, request.Command.ExpectedStateVersion));
    }

    private sealed record CommandRequest(int Seat, string Grant, CompanionCommand Command);

    private sealed class PrivateViewHost(WebApplication app, CompanionServer server, HttpClient client,
        GameCoordinator game, Clock time, ControllerCredentials credentials, JsonElement revealed) : IAsyncDisposable
    {
        public CompanionServer Server => server;
        public HttpClient Client => client;
        public GameCoordinator Game => game;
        public Clock Time => time;
        public ControllerCredentials Credentials => credentials;
        public JsonElement Revealed => revealed;

        public object RevealRequest() => new
        {
            seat = 1, sessionId = game.SessionId.Value, version = game.Public.StateVersion,
            handoffGeneration = server.Authority.Generation
        };

        public CommandRequest Choice() => new(1, revealed.GetProperty("grant").GetString()!,
            new(Guid.NewGuid().ToString("N"), game.SessionId.Value, game.Public.StateVersion, "keepTickets",
                KeptTickets: revealed.GetProperty("data").GetProperty("offeredTickets").EnumerateArray()
                    .Take(2).Select(ticket => ticket.GetProperty("id").GetString()!).ToArray()));

        public static async Task<PrivateViewHost> Create(CancellationToken token)
        {
            var game = await GameCoordinator.CreateAsync(new GameRules(TestManifest.Manifest, TestManifest.Catalog), new InMemorySessionStore(),
                new SessionSetup(SessionId.New(), [new Seat(new(1), "Alex", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard),
                    new Seat(new(2), "Second", PlayerColor.Red, SeatKind.Human, AiDifficulty.Standard)], new(1), VerificationMode.Manual), DeterministicRandom.SeedFrom(91), token);
            var time = new Clock();
            var server = new CompanionServer(new CoordinatorCompanionBridge(() => game), time);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
                options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.Use(async (context, next) => { context.Response.Headers.CacheControl = "no-store"; await next(context); });
            server.MapApi(app);
            await app.StartAsync(token);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var client = new HttpClient { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(10) };
            var tab = Guid.NewGuid().ToString("N");
            server.NewPairingCode();
            var pairing = server.Authority.RequestPair(server.Authority.PairingCode, tab, "Private view fixture")!.Value;
            Assert.True(server.ApprovePendingController());
            var credentials = server.Authority.Authenticate(pairing.Session, tab, true)!;
            client.DefaultRequestHeaders.Add("Cookie", "GoldenTicketQuickPlayController=" + pairing.Session);
            client.DefaultRequestHeaders.Add("X-GoldenTicket-Tab", tab);
            client.DefaultRequestHeaders.Add("X-GoldenTicket-CSRF", credentials.Csrf);
            using var response = await client.PostAsJsonAsync("/api/reveal", new
            {
                seat = 1, sessionId = game.SessionId.Value, version = game.Public.StateVersion,
                handoffGeneration = server.Authority.Generation
            }, token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var revealed = await response.Content.ReadFromJsonAsync<JsonElement>(token);
            return new(app, server, client, game, time, credentials, revealed);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await server.DisposeAsync();
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }
}
