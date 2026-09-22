using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GoldenTicket.Domain.Tests;

public partial class CompanionHostTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstTrainDrawContinuesTheRevealedTurnAndSecondDrawCoversTheCards(bool blind)
    {
        var token = TestContext.Current.CancellationToken;
        var game = await CreateActiveContinuationGame(token);
        await using var host = await ContinuationHost.Create(game, token);
        var grant = await host.Reveal(token);
        var first = host.Command("drawTrain", blind ? null : Enumerable.Range(0, 5)
            .First(slot => game.Public.FaceUp[slot] is not (null or TrainCardKind.Locomotive)));
        var handSize = (await game.GetSeatViewAsync(new(1), token)).Hand.Length;

        var receipt = await host.Send(grant, first, token);

        Assert.True(receipt.Accepted);
        var continuation = Assert.IsType<CompanionPrivateContinuation>(receipt.Continuation);
        Assert.Equal(1, continuation.Snapshot.RevealSeatId);
        Assert.Equal(1, continuation.Snapshot.Game!.TurnNumber);
        Assert.Equal(receipt.StateVersion, continuation.Data.View.Public.StateVersion);
        Assert.Equal(receipt.StateVersion, continuation.Snapshot.Game.StateVersion);
        Assert.Equal(handSize + 1, continuation.Data.View.Hand.Length);
        Assert.NotEqual(grant, continuation.Grant);
        Assert.Equal(host.Server.Authority.Generation, continuation.HandoffGeneration);
        Assert.True(host.Server.Authority.ValidateGrant(host.Credentials, continuation.Grant, 1,
            game.SessionId.Value, receipt.StateVersion));
        Assert.False(host.Server.Authority.ValidateGrant(host.Credentials, grant, 1,
            game.SessionId.Value, first.ExpectedStateVersion));

        var repeated = await host.Send(grant, first, token);
        Assert.True(repeated.Accepted);
        Assert.Null(repeated.Continuation);
        Assert.Equal(receipt.StateVersion, game.Public.StateVersion);
        Assert.Equal(continuation.HandoffGeneration, host.Server.Authority.Generation);

        var second = await host.Send(continuation.Grant, host.Command("drawTrain"), token);
        Assert.True(second.Accepted);
        Assert.Null(second.Continuation);
        Assert.Equal(new SeatId(2), game.Public.ActiveSeatId);
        Assert.Equal(handSize + 2, (await game.GetSeatViewAsync(new(1), token)).Hand.Length);
        Assert.Empty(await game.CheckInvariantsAsync(token));
    }

    [Fact]
    public async Task VisibleLocomotiveEndsTheTurnWithoutContinuingThePrivateView()
    {
        var token = TestContext.Current.CancellationToken;
        GameCoordinator game;
        ulong seed = 91;
        do { game = await CreateActiveContinuationGame(token, seed++); }
        while (!game.Public.FaceUp.Contains(TrainCardKind.Locomotive) && seed < 120);
        var slot = game.Public.FaceUp.IndexOf(TrainCardKind.Locomotive);
        Assert.InRange(slot, 0, 4);
        await using var host = await ContinuationHost.Create(game, token);
        var receipt = await host.Send(await host.Reveal(token), host.Command("drawTrain", slot), token);
        Assert.True(receipt.Accepted);
        Assert.Null(receipt.Continuation);
        Assert.Equal(new SeatId(2), game.Public.ActiveSeatId);
    }

    [Fact]
    public async Task DestinationOfferContinuesTheSameTurnAndKeepingTicketsEndsIt()
    {
        var token = TestContext.Current.CancellationToken;
        var game = await CreateActiveContinuationGame(token);
        await using var host = await ContinuationHost.Create(game, token);
        var receipt = await host.Send(await host.Reveal(token), host.Command("drawTickets"), token);
        var continuation = Assert.IsType<CompanionPrivateContinuation>(receipt.Continuation);
        Assert.NotEmpty(continuation.Data.OfferedTickets);
        var keep = host.Command("keepTickets") with
        {
            KeptTickets = continuation.Data.OfferedTickets.Take(1).Select(ticket => ticket.Id).ToArray(),
            ReturnedTickets = continuation.Data.OfferedTickets.Skip(1).Select(ticket => ticket.Id).ToArray()
        };
        var kept = await host.Send(continuation.Grant, keep, token);
        Assert.True(kept.Accepted);
        Assert.Null(kept.Continuation);
        Assert.Equal(new SeatId(2), game.Public.ActiveSeatId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HideOrRevokeWhileRefreshingTheHandPreventsContinuation(bool revoke)
    {
        var token = TestContext.Current.CancellationToken;
        var game = await CreateActiveContinuationGame(token);
        await using var host = await ContinuationHost.Create(game, token);
        var grant = await host.Reveal(token);
        host.Bridge.AfterPrivateRead = _ =>
        {
            if (revoke) host.Server.RevokeController();
            else host.Server.InvalidatePrivateGrants();
            return Task.CompletedTask;
        };
        var receipt = await host.Send(grant, host.Command("drawTrain"), token);
        Assert.True(receipt.Accepted);
        Assert.Null(receipt.Continuation);
        Assert.Equal(1, game.Public.ActiveSeatId.Value);
    }

    [Fact]
    public async Task LaterTurnForTheSameHumanCannotReceiveTheEarlierTurnsContinuation()
    {
        var token = TestContext.Current.CancellationToken;
        var game = await CreateActiveContinuationGame(token);
        await using var host = await ContinuationHost.Create(game, token);
        var grant = await host.Reveal(token);
        host.Bridge.AfterPrivateRead = async cancellationToken =>
        {
            for (var index = 0; index < 3; index++)
                Assert.True((await game.SubmitAsync(new SelectTrainCard(
                    game.NewEnvelope(game.Public.ActiveSeatId), null), cancellationToken)).IsAccepted);
        };
        var receipt = await host.Send(grant, host.Command("drawTrain"), token);
        Assert.True(receipt.Accepted);
        Assert.Null(receipt.Continuation);
        Assert.Equal(1, game.Public.ActiveSeatId.Value);
        Assert.Equal(3, game.Public.TurnNumber);
    }

    private static async Task<GameCoordinator> CreateActiveContinuationGame(CancellationToken token, ulong seed = 91,
        InMemorySessionStore? store = null)
    {
        var game = await GameCoordinator.CreateAsync(new GameRules(TestManifest.Manifest, TestManifest.Catalog),
            store ?? new InMemorySessionStore(), new SessionSetup(SessionId.New(),
                [new Seat(new(1), "Alex", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard),
                 new Seat(new(2), "Jordan", PlayerColor.Red, SeatKind.Human, AiDifficulty.Standard)],
                new(1), VerificationMode.Manual), DeterministicRandom.SeedFrom(seed), token);
        foreach (var seat in game.Seats)
        {
            var hand = await game.GetSeatViewAsync(seat.SeatId, token);
            Assert.True((await game.SubmitAsync(new CommitTicketSelection(game.NewEnvelope(seat.SeatId),
                [.. hand.SetupOffer.Take(2)], []), token)).IsAccepted);
        }
        return game;
    }

    private sealed class ContinuationBridge(GameCoordinator game) : ICompanionGameBridge
    {
        private readonly CoordinatorCompanionBridge _inner = new(() => game);
        private int _publicReads;
        public int PublicReads => Volatile.Read(ref _publicReads);
        public CompanionGuidance? Guidance { get; set; }
        public CompanionBoardMap? BoardMap { get; set; }
        public CompanionBoardInteraction? BoardInteraction { get; set; }
        public bool RejectNextDrawForBoardCheck { get; set; }
        public Func<string, CancellationToken, Task<CompanionBoardImage?>>? BoardImageReader { get; set; }
        public Func<CancellationToken, Task>? AfterPrivateRead { get; set; }
        public async Task<CompanionPublicSnapshot> ReadPublicAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _publicReads);
            return (await _inner.ReadPublicAsync(cancellationToken)) with
            { Guidance = Guidance, BoardMap = BoardMap, BoardInteraction = BoardInteraction };
        }
        public Task<CompanionBoardImage?> ReadBoardImageAsync(string id, CancellationToken cancellationToken = default) =>
            BoardImageReader?.Invoke(id, cancellationToken) ?? Task.FromResult<CompanionBoardImage?>(null);
        public async Task<CompanionPrivateSnapshot?> ReadPrivateAsync(SeatId seat, long expectedVersion,
            CancellationToken cancellationToken = default)
        {
            var result = await _inner.ReadPrivateAsync(seat, expectedVersion, cancellationToken);
            if (AfterPrivateRead is { } after) { AfterPrivateRead = null; await after(cancellationToken); }
            return result;
        }
        public Task<CompanionCommandReceipt> ExecuteAsync(SeatId seat, CompanionCommand command,
            CancellationToken cancellationToken = default)
        {
            if (command.Kind == "drawTrain" && RejectNextDrawForBoardCheck)
            {
                RejectNextDrawForBoardCheck = false;
                return Task.FromResult(new CompanionCommandReceipt(false, false, game.Public.StateVersion,
                    "BoardCheckRequired", "Keep the board clear, then try again."));
            }
            return _inner.ExecuteAsync(seat, command, cancellationToken);
        }
    }

    private sealed class ContinuationHost(GameCoordinator game, CompanionServer server, ContinuationBridge bridge,
        WebApplication app, HttpClient client, ControllerCredentials credentials) : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };
        public CompanionServer Server => server;
        public ContinuationBridge Bridge => bridge;
        public ControllerCredentials Credentials => credentials;
        public HttpClient Client => client;
        public static async Task<ContinuationHost> Create(GameCoordinator game, CancellationToken token)
        {
            var bridge = new ContinuationBridge(game);
            var server = new CompanionServer(bridge);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
                options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            server.MapApi(app);
            await app.StartAsync(token);
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
            var tab = Guid.NewGuid().ToString("N");
            server.NewPairingCode();
            var pending = server.Authority.RequestPair(server.Authority.PairingCode, tab, "Turn continuation")!.Value;
            Assert.True(server.ApprovePendingController());
            var credentials = server.Authority.Authenticate(pending.Session, tab, true)!;
            client.DefaultRequestHeaders.Add("Cookie", "GoldenTicketQuickPlayController=" + pending.Session);
            client.DefaultRequestHeaders.Add("X-GoldenTicket-Tab", tab);
            client.DefaultRequestHeaders.Add("X-GoldenTicket-CSRF", credentials.Csrf);
            return new(game, server, bridge, app, client, credentials);
        }
        public CompanionCommand Command(string kind, int? slot = null) => new(Guid.NewGuid().ToString("N"),
            game.SessionId.Value, game.Public.StateVersion, kind, Slot: slot);
        public async Task<HttpResponseMessage> OpenEvents(CancellationToken token, string? session = null, string? tab = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
            if (session is not null) request.Headers.Add("Cookie", "GoldenTicketQuickPlayController=" + session);
            if (tab is not null) request.Headers.Add("X-GoldenTicket-Tab", tab);
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        }
        public async Task<string> Reveal(CancellationToken token)
        {
            using var response = await client.PostAsJsonAsync("/api/reveal", new
            {
                seat = 1, sessionId = game.SessionId.Value, version = game.Public.StateVersion,
                handoffGeneration = server.Authority.Generation
            }, token);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("grant").GetString()!;
        }
        public async Task<CompanionCommandReceipt> Send(string grant, CompanionCommand command, CancellationToken token)
        {
            using var response = await client.PostAsJsonAsync("/api/command", new { seat = 1, grant, command }, Json, token);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<CompanionCommandReceipt>(Json, token))!;
        }
        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
            await server.DisposeAsync();
        }
    }
}
