using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using GoldenTicket.Application;
using GoldenTicket.CompanionHost;
using GoldenTicket.Domain;
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

public sealed class CompanionActivityTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (ControllerAuthority Authority, ControllerCredentials Credentials, PrivateGrant Grant) Paired(Clock clock)
    {
        var authority = new ControllerAuthority(clock);
        authority.NewCode();
        var tab = Guid.NewGuid().ToString("N");
        var pending = authority.RequestPair(authority.PairingCode, tab, "Activity fixture")!.Value;
        Assert.True(authority.Approve());
        var credentials = authority.Authenticate(pending.Session, tab, true)!;
        return (authority, credentials, authority.Reveal(credentials, 1, "match", 9)!);
    }

    [Fact]
    public void ExplicitActivityExtendsTheSameGrantAndHeartbeatWithoutRotatingItsCancellation()
    {
        var clock = new Clock();
        var (authority, credentials, grant) = Paired(clock);
        var authorization = authority.AuthorizeCommand(credentials, grant.Token, 1, "match", 9)!.Value;
        clock.Now += TimeSpan.FromSeconds(5);
        var renewed = authority.RenewPrivateGrant(credentials, grant.Token, 1, "match", 9, grant.Generation)!;
        Assert.Equal(grant.Token, renewed.Token);
        Assert.Equal(grant.Generation, renewed.Generation);
        Assert.Equal(clock.Now.AddSeconds(30), renewed.ExpiresAt);
        Assert.Equal(grant.ExpiresAt.AddSeconds(5), renewed.ExpiresAt);
        var afterActivity = authority.AuthorizeCommand(credentials, grant.Token, 1, "match", 9)!.Value;
        Assert.Equal(TimeSpan.FromSeconds(6), afterActivity.ValidFor);
        Assert.Equal(authorization.Cancellation, afterActivity.Cancellation);
        Assert.False(authorization.Cancellation.IsCancellationRequested);
        authority.InvalidatePrivateGrants();
        Assert.True(authorization.Cancellation.IsCancellationRequested);
    }

    [Theory]
    [InlineData("seat")]
    [InlineData("session")]
    [InlineData("version")]
    [InlineData("token")]
    [InlineData("generation")]
    public void ActivityCannotRenewAnotherView(string mismatch)
    {
        var clock = new Clock();
        var (authority, credentials, grant) = Paired(clock);
        Assert.Null(authority.RenewPrivateGrant(credentials, mismatch == "token" ? "wrong" : grant.Token,
            mismatch == "seat" ? 2 : 1, mismatch == "session" ? "another-match" : "match",
            mismatch == "version" ? 10 : 9, mismatch == "generation" ? grant.Generation - 1 : grant.Generation));
        Assert.True(authority.ValidateGrant(credentials, grant.Token, 1, "match", 9));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("heartbeat")]
    [InlineData("hide")]
    [InlineData("revoke")]
    public void ActivityNeverResurrectsAnInvalidGrant(string invalidation)
    {
        var clock = new Clock();
        var (authority, credentials, grant) = Paired(clock);
        switch (invalidation)
        {
            case "expired":
                for (var i = 0; i < 15; i++) { clock.Now += TimeSpan.FromSeconds(2); authority.Authenticate(credentials.Session, credentials.Tab, true); }
                break;
            case "heartbeat": clock.Now += TimeSpan.FromSeconds(6); break;
            case "hide": authority.InvalidatePrivateGrants(); break;
            case "revoke": authority.Revoke(); break;
        }
        Assert.Null(authority.RenewPrivateGrant(credentials, grant.Token, 1, "match", 9, grant.Generation));
        authority.Authenticate(credentials.Session, credentials.Tab, true);
        Assert.Null(authority.RenewPrivateGrant(credentials, grant.Token, 1, "match", 9, grant.Generation));
    }

    [Fact]
    public async Task ActivityEndpointRequiresCsrfAndReturnsOnlyRenewedExpiryAndGeneration()
    {
        var token = TestContext.Current.CancellationToken;
        await using var host = await ActivityHost.Create(token);
        host.Client.DefaultRequestHeaders.Remove("X-GoldenTicket-CSRF");
        using var denied = await host.Client.PostAsJsonAsync("/api/activity", host.Request, token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(0, host.Bridge.PublicReads);
        host.Client.DefaultRequestHeaders.Add("X-GoldenTicket-CSRF", host.Credentials.Csrf);
        using var response = await host.Client.PostAsJsonAsync("/api/activity", host.Request, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal(new[] { "expiresAt", "handoffGeneration" }, body.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(host.Grant.Generation, body.GetProperty("handoffGeneration").GetInt64());
        Assert.True(body.GetProperty("expiresAt").GetDateTimeOffset() >= host.Grant.ExpiresAt);
        Assert.True(host.Server.Authority.ValidateGrant(host.Credentials, host.Grant.Token,
            host.Request.Seat, host.Request.SessionId, host.Request.Version));
        Assert.Equal(1, host.Bridge.PublicReads);
        Assert.Equal(0, host.Bridge.PrivateReads);
    }

    [Theory]
    [InlineData("seat", HttpStatusCode.Conflict)]
    [InlineData("session", HttpStatusCode.Conflict)]
    [InlineData("version", HttpStatusCode.Conflict)]
    [InlineData("control", HttpStatusCode.Conflict)]
    [InlineData("grant", HttpStatusCode.Unauthorized)]
    [InlineData("generation", HttpStatusCode.Unauthorized)]
    public async Task ActivityEndpointRejectsAStaleOrDifferentView(string mismatch, HttpStatusCode expected)
    {
        var token = TestContext.Current.CancellationToken;
        await using var host = await ActivityHost.Create(token);
        var request = host.Request;
        request = mismatch switch
        {
            "seat" => request with { Seat = request.Seat + 1 },
            "session" => request with { SessionId = "another-match" },
            "version" => request with { Version = request.Version + 1 },
            "grant" => request with { Grant = "wrong" },
            "generation" => request with { HandoffGeneration = request.HandoffGeneration - 1 },
            _ => request
        };
        if (mismatch == "control") host.Bridge.Snapshot = host.Bridge.Snapshot with { CanControl = false };
        using var response = await host.Client.PostAsJsonAsync("/api/activity", request, token);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(0, host.Bridge.PrivateReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HideOrRevokeDuringThePublicReadPreventsActivityRenewal(bool revoke)
    {
        var token = TestContext.Current.CancellationToken;
        await using var host = await ActivityHost.Create(token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Bridge.BeforeRead = async cancellation => { entered.TrySetResult(); await release.Task.WaitAsync(cancellation); };
        var activity = host.Client.PostAsJsonAsync("/api/activity", host.Request, token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        if (revoke) host.Server.RevokeController();
        else
        {
            using var hidden = await host.Client.PostAsJsonAsync("/api/hide", new { }, token);
            Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);
        }
        release.TrySetResult();
        using var response = await activity;
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(host.Server.Authority.ValidateGrant(host.Credentials, host.Grant.Token,
            host.Request.Seat, host.Request.SessionId, host.Request.Version));
        Assert.Equal(0, host.Bridge.PrivateReads);
    }

    private sealed record ActivityRequest(int Seat, string SessionId, long Version, string Grant, long HandoffGeneration);

    [Fact]
    public async Task ActivityReadDoesNotBlockAChoiceAndCannotRenewAfterTheChoiceCompletes()
    {
        var token = TestContext.Current.CancellationToken;
        await using var host = await ActivityHost.Create(token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Bridge.BeforeRead = async cancellation => { entered.TrySetResult(); await release.Task.WaitAsync(cancellation); };
        host.Bridge.CommandReceipt = new(true, false, host.Request.Version + 1, null, "Choice accepted.");
        var activity = host.Client.PostAsJsonAsync("/api/activity", host.Request, token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        try
        {
            var command = new CompanionCommand(Guid.NewGuid().ToString("N"), host.Request.SessionId,
                host.Request.Version, "keepTickets", KeptTickets: []);
            using var response = await host.Client.PostAsJsonAsync("/api/command",
                new { seat = host.Request.Seat, grant = host.Request.Grant, command }, token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var receipt = await response.Content.ReadFromJsonAsync<JsonElement>(token);
            Assert.True(receipt.GetProperty("accepted").GetBoolean());
            Assert.False(host.Server.Authority.ValidateGrant(host.Credentials, host.Grant.Token,
                host.Request.Seat, host.Request.SessionId, host.Request.Version));
        }
        finally { release.TrySetResult(); }
        using var staleActivity = await activity;
        Assert.Equal(HttpStatusCode.Unauthorized, staleActivity.StatusCode);
        Assert.Equal(0, host.Bridge.PrivateReads);
    }

    private sealed class ActivityBridge(CompanionPublicSnapshot snapshot) : ICompanionGameBridge
    {
        public CompanionPublicSnapshot Snapshot { get; set; } = snapshot;
        public Func<CancellationToken, Task>? BeforeRead { get; set; }
        public CompanionCommandReceipt? CommandReceipt { get; set; }
        public int PublicReads { get; private set; }
        public int PrivateReads { get; private set; }
        public async Task<CompanionPublicSnapshot> ReadPublicAsync(CancellationToken cancellationToken = default)
        {
            PublicReads++;
            if (BeforeRead is { } beforeRead) await beforeRead(cancellationToken);
            return Snapshot;
        }
        public Task<CompanionPrivateSnapshot?> ReadPrivateAsync(SeatId seat, long expectedVersion, CancellationToken cancellationToken = default)
        { PrivateReads++; throw new InvalidOperationException("Activity must not read private cards."); }
        public Task<CompanionCommandReceipt> ExecuteAsync(SeatId seat, CompanionCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(CommandReceipt ?? throw new NotSupportedException());
    }

    private sealed class ActivityHost(WebApplication app, CompanionServer server, HttpClient client,
        ActivityBridge bridge, ControllerCredentials credentials, PrivateGrant grant, ActivityRequest request) : IAsyncDisposable
    {
        public CompanionServer Server => server;
        public HttpClient Client => client;
        public ActivityBridge Bridge => bridge;
        public ControllerCredentials Credentials => credentials;
        public PrivateGrant Grant => grant;
        public ActivityRequest Request => request;

        public static async Task<ActivityHost> Create(CancellationToken token)
        {
            var game = await GameCoordinator.CreateAsync(new GameRules(TestManifest.Manifest, TestManifest.Catalog), new InMemorySessionStore(),
                new SessionSetup(SessionId.New(), [new Seat(new(1), "Alex", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard),
                    new Seat(new(2), "Second", PlayerColor.Red, SeatKind.Human, AiDifficulty.Standard)], new(1), VerificationMode.Manual), DeterministicRandom.SeedFrom(91), token);
            var bridge = new ActivityBridge(await new CoordinatorCompanionBridge(() => game).ReadPublicAsync(token));
            var server = new CompanionServer(bridge);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.Use(async (context, next) => { context.Response.Headers.CacheControl = "no-store"; await next(context); });
            server.MapApi(app);
            await app.StartAsync(token);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var client = new HttpClient { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(10) };
            var tab = Guid.NewGuid().ToString("N");
            server.NewPairingCode();
            var pairing = server.Authority.RequestPair(server.Authority.PairingCode, tab, "Activity fixture")!.Value;
            Assert.True(server.ApprovePendingController());
            var credentials = server.Authority.Authenticate(pairing.Session, tab, true)!;
            var grant = server.Authority.Reveal(credentials, bridge.Snapshot.RevealSeatId!.Value, game.SessionId.Value, game.Public.StateVersion)!;
            client.DefaultRequestHeaders.Add("Cookie", "GoldenTicketQuickPlayController=" + pairing.Session);
            client.DefaultRequestHeaders.Add("X-GoldenTicket-Tab", tab);
            client.DefaultRequestHeaders.Add("X-GoldenTicket-CSRF", credentials.Csrf);
            return new(app, server, client, bridge, credentials, grant,
                new(grant.Seat, grant.SessionId, grant.Version, grant.Token, grant.Generation));
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
