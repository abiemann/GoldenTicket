using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using GoldenTicket.Application;
using GoldenTicket.CompanionHost;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Randomness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GoldenTicket.Domain.Tests;

public class CompanionResultImageTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==");

    private static Task<GameCoordinator> CreateGame(int humans = 2, int computers = 0) => GameCoordinator.CreateAsync(
        new GameRules(TestManifest.Manifest, TestManifest.Catalog), new InMemorySessionStore(),
        new SessionSetup(SessionId.New(), [.. Enumerable.Range(1, humans + computers).Select(i =>
            new Seat(new(i), $"Seat {i}", Enum.GetValues<PlayerColor>()[i - 1],
                i <= humans ? SeatKind.Human : SeatKind.Computer, AiDifficulty.Standard))],
            new(1), VerificationMode.Manual), DeterministicRandom.SeedFrom(91), TestContext.Current.CancellationToken);

    // Boundary fixtures: no game commands or saved matches are altered. These tests concern the
    // lifecycle/version presented to the bridge, not the separately tested final scoring rules.
    private static void SetLifecycle(GameCoordinator game, SessionLifecycle lifecycle) =>
        typeof(GameCoordinator).GetField("_publicView", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(game, game.Public with { Lifecycle = lifecycle });

    private static CompanionResultImage Image(GameCoordinator game) => new(game.SessionId.Value,
        game.Public.StateVersion, new(Guid.NewGuid().ToString("N"), "golden-ticket-final-standings.png"), Png);

    [Fact]
    public async Task ImageAppearsOnlyAtTheEndAndDoesNotNeedASeatReveal()
    {
        var token = TestContext.Current.CancellationToken;
        var game = await CreateGame();
        var image = Image(game);
        var bridge = new CoordinatorCompanionBridge(() => game, canControl: () => false, resultImage: () => image);
        Assert.Null((await bridge.ReadPublicAsync(token)).ResultImage);
        Assert.Null(await bridge.ReadResultImageAsync(image.Info.Id, token));
        SetLifecycle(game, SessionLifecycle.Active);
        Assert.Null((await bridge.ReadPublicAsync(token)).ResultImage);
        Assert.Null(await bridge.ReadResultImageAsync(image.Info.Id, token));
        SetLifecycle(game, SessionLifecycle.Finished);
        var snapshot = await bridge.ReadPublicAsync(token);
        Assert.False(snapshot.CanControl);
        Assert.Null(snapshot.RevealSeatId);
        Assert.Equal(image.Info, snapshot.ResultImage);
        Assert.Same(image, await bridge.ReadResultImageAsync(image.Info.Id, token));
        Assert.Null(await bridge.ReadResultImageAsync(Guid.NewGuid().ToString("N"), token));
        Assert.DoesNotContain(Convert.ToBase64String(Png), JsonSerializer.Serialize(snapshot));
        Assert.Null(await bridge.ReadPrivateAsync(new(1), game.Public.StateVersion, token));
    }

    [Theory]
    [InlineData(0, 2, false)]
    [InlineData(1, 1, false)]
    [InlineData(2, 0, true)]
    [InlineData(2, 1, true)]
    public async Task FinishedImageIsExclusiveToGamesWithMultipleHumans(int humans, int computers, bool available)
    {
        var token = TestContext.Current.CancellationToken;
        var game = await CreateGame(humans, computers);
        SetLifecycle(game, SessionLifecycle.Finished);
        var image = Image(game);
        var bridge = new CoordinatorCompanionBridge(() => game, resultImage: () => image);
        Assert.Equal(available, (await bridge.ReadPublicAsync(token)).ResultImage is not null);
        Assert.Equal(available, await bridge.ReadResultImageAsync(image.Info.Id, token) is not null);
    }

    [Theory]
    [InlineData("other-session")]
    [InlineData("older-version")]
    [InlineData("invalid-id")]
    [InlineData("invalid-filename")]
    [InlineData("not-png")]
    [InlineData("oversize")]
    public async Task StaleOrInvalidCaptureCannotBeAdvertisedOrDownloaded(string problem)
    {
        var token = TestContext.Current.CancellationToken;
        var game = await CreateGame();
        SetLifecycle(game, SessionLifecycle.Finished);
        var image = Image(game);
        image = problem switch
        {
            "other-session" => image with { SessionId = SessionId.New().Value },
            "older-version" => image with { StateVersion = image.StateVersion - 1 },
            "invalid-id" => image with { Info = image.Info with { Id = "../results" } },
            "invalid-filename" => image with { Info = image.Info with { FileName = "../standings.png" } },
            "not-png" => image with { Png = new byte[64] },
            "oversize" => image with { Png = new byte[16 * 1024 * 1024 + 1] },
            _ => throw new ArgumentOutOfRangeException(nameof(problem))
        };
        var bridge = new CoordinatorCompanionBridge(() => game, resultImage: () => image);
        Assert.Null((await bridge.ReadPublicAsync(token)).ResultImage);
        Assert.Null(await bridge.ReadResultImageAsync(image.Info.Id, token));
    }

    [Fact]
    public async Task ReplacingOrLeavingTheGameInvalidatesThePriorCapture()
    {
        var token = TestContext.Current.CancellationToken;
        var first = await CreateGame();
        var next = await CreateGame();
        SetLifecycle(first, SessionLifecycle.Finished);
        SetLifecycle(next, SessionLifecycle.Finished);
        var image = Image(first);
        GameCoordinator? current = first;
        var bridge = new CoordinatorCompanionBridge(() => current, resultImage: () => image);
        Assert.NotNull(await bridge.ReadResultImageAsync(image.Info.Id, token));
        current = next;
        Assert.Null((await bridge.ReadPublicAsync(token)).ResultImage);
        Assert.Null(await bridge.ReadResultImageAsync(image.Info.Id, token));
        current = null;
        Assert.Null((await bridge.ReadPublicAsync(token)).ResultImage);
        Assert.Null(await bridge.ReadResultImageAsync(image.Info.Id, token));

        current = first;
        var changingBridge = new CoordinatorCompanionBridge(() => current, resultImage: () => { current = next; return image; });
        Assert.Null((await changingBridge.ReadPublicAsync(token)).ResultImage);
        current = first;
        Assert.Null(await changingBridge.ReadResultImageAsync(image.Info.Id, token));
    }

    [Fact]
    public async Task DownloadRequiresApprovedControllerAndTabButNoPrivateGrantAndNeverCaches()
    {
        var token = TestContext.Current.CancellationToken;
        var image = new CompanionResultImage("finished-fixture", 9,
            new(Guid.NewGuid().ToString("N"), "golden-ticket-final-standings.png"), Png);
        var bridge = new DownloadBridge(_ => Task.FromResult<CompanionResultImage?>(image));
        await using var host = await DownloadHost.Create(bridge, token);
        var url = "/api/result-image/" + image.Info.Id;
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(url, token)).StatusCode);
        host.AddCredentials(approve: false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(url, token)).StatusCode);
        Assert.Equal(0, bridge.Reads);
        Assert.True(host.Server.ApprovePendingController());
        host.Client.DefaultRequestHeaders.Remove("X-GoldenTicket-Tab");
        host.Client.DefaultRequestHeaders.Add("X-GoldenTicket-Tab", Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(url, token)).StatusCode);
        host.Client.DefaultRequestHeaders.Remove("X-GoldenTicket-Tab");
        host.Client.DefaultRequestHeaders.Add("X-GoldenTicket-Tab", host.Tab);
        host.Server.InvalidatePrivateGrants();
        using var result = await host.Client.GetAsync(url, token);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("image/png", result.Content.Headers.ContentType!.MediaType);
        Assert.True(result.Headers.CacheControl!.NoStore);
        Assert.Equal(image.Info.FileName, result.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Equal(Png, await result.Content.ReadAsByteArrayAsync(token));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/result-image/not-an-image-id", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/result-image/" + Guid.NewGuid().ToString("N"), token)).StatusCode);
        host.Server.RevokeController();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(url, token)).StatusCode);
    }

    [Fact]
    public async Task ControllerRevokedWhileDesktopReadWaitsDoesNotReceiveTheImage()
    {
        var token = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CompanionResultImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var image = new CompanionResultImage("finished-fixture", 9,
            new(Guid.NewGuid().ToString("N"), "golden-ticket-final-standings.png"), Png);
        var bridge = new DownloadBridge(async cancellationToken =>
        {
            entered.TrySetResult();
            return await release.Task.WaitAsync(cancellationToken);
        });
        await using var host = await DownloadHost.Create(bridge, token);
        host.AddCredentials(approve: true);
        var download = host.Client.GetAsync("/api/result-image/" + image.Info.Id, token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        host.Server.RevokeController();
        release.SetResult(image);
        using var result = await download;
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Empty(await result.Content.ReadAsByteArrayAsync(token));
    }

    private sealed class DownloadBridge(Func<CancellationToken, Task<CompanionResultImage?>> read) : ICompanionGameBridge
    {
        public int Reads { get; private set; }
        public Task<CompanionResultImage?> ReadResultImageAsync(string id, CancellationToken cancellationToken = default)
        { Reads++; return read(cancellationToken); }
        public Task<CompanionPublicSnapshot> ReadPublicAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CompanionPrivateSnapshot?> ReadPrivateAsync(SeatId seat, long expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CompanionCommandReceipt> ExecuteAsync(SeatId seat, CompanionCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // Exercise the actual endpoint over loopback HTTP; the existing HTTPS transport suite covers
    // certificates and production LAN middleware. Credentials here are issued by the real authority.
    private sealed class DownloadHost(WebApplication app, CompanionServer server, HttpClient client) : IAsyncDisposable
    {
        public CompanionServer Server => server;
        public HttpClient Client => client;
        public string Tab { get; } = Guid.NewGuid().ToString("N");
        public void AddCredentials(bool approve)
        {
            server.NewPairingCode();
            var pairing = server.Authority.RequestPair(server.Authority.PairingCode, Tab, "Standings fixture")!.Value;
            client.DefaultRequestHeaders.Add("Cookie", "GoldenTicketQuickPlayController=" + pairing.Session);
            client.DefaultRequestHeaders.Add("X-GoldenTicket-Tab", Tab);
            if (approve) Assert.True(server.ApprovePendingController());
        }
        public static async Task<DownloadHost> Create(ICompanionGameBridge bridge, CancellationToken token)
        {
            var server = new CompanionServer(bridge);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            server.MapApi(app);
            await app.StartAsync(token);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, server, new HttpClient { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(10) });
        }
        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await server.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
