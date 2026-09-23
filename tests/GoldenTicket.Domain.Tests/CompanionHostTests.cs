using System.Net;
using System.Text.Json;
using GoldenTicket.Application;
using GoldenTicket.CompanionHost;
using GoldenTicket.CompanionHost.Networking;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using Microsoft.AspNetCore.Http;

namespace GoldenTicket.Domain.Tests;

public class CompanionHostTests
{
    [Fact]
    public async Task PairingFloodCannotSpendTheApprovedControllersActionBudget()
    {
        await using var server = new CompanionServer(new CoordinatorCompanionBridge(() => null));
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/pair";
        for (var attempt = 0; attempt < 100; attempt++) Assert.True(server.AllowPost(context));
        Assert.False(server.AllowPost(context));

        context.Request.Path = "/api/hide";
        Assert.True(server.AllowPost(context));
        context.Request.Path = "/api/reveal";
        Assert.True(server.AllowPost(context));
        context.Request.Path = "/api/command";
        Assert.True(server.AllowPost(context));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static (ControllerAuthority Authority, ControllerCredentials Credentials) Paired(Clock? clock = null)
    {
        var authority = new ControllerAuthority(clock); authority.NewCode();
        var pending = authority.RequestPair(authority.PairingCode, new string('a', 32), "Shared Pixel")!;
        Assert.True(authority.Approve());
        return (authority, authority.Authenticate(pending.Value.Session, new string('a', 32), true)!);
    }
    [Fact]
    public void PairingRequiresExplicitLaptopApprovalAndKeepsTheCodeAvailable()
    {
        var authority = new ControllerAuthority(); authority.NewCode(); var code = authority.PairingCode;
        var pending = authority.RequestPair(code, new string('a', 32), "Pixel");
        Assert.NotNull(pending);
        Assert.Null(authority.Authenticate(pending.Value.Session, new string('a', 32)));
        Assert.Equal(code, authority.PairingCode);
        Assert.True(authority.Approve());
        Assert.NotNull(authority.Authenticate(pending.Value.Session, new string('a', 32)));
        Assert.Null(authority.Authenticate(pending.Value.Session, new string('b', 32)));
        Assert.Equal(code, authority.PairingCode);
        var replacement = authority.RequestPair(code, new string('b', 32), "Second phone");
        Assert.NotNull(replacement);
        Assert.Null(authority.Authenticate(replacement.Value.Session, new string('b', 32)));
        Assert.NotNull(authority.Authenticate(pending.Value.Session, new string('a', 32)));
    }
    [Fact]
    public void PairingCodeRemainsValidAfterLongIdle()
    {
        var clock = new Clock(); var authority = new ControllerAuthority(clock); authority.NewCode();
        var code = authority.PairingCode;
        clock.Now += TimeSpan.FromDays(2);
        Assert.Equal(code, authority.PairingCode);
        Assert.NotNull(authority.RequestPair(code, new string('a', 32), "Pixel"));
        Assert.True(authority.Approve());
    }
    [Fact]
    public void IncorrectEntriesDoNotConsumeOrPermanentlyLockThePairingCode()
    {
        var authority = new ControllerAuthority(); authority.NewCode();
        var code = authority.PairingCode;
        var wrong = code == "111111" ? "222222" : "111111";
        for (var i = 0; i < 20; i++) Assert.Null(authority.RequestPair(wrong, new string('a', 32), "Pixel"));
        Assert.Equal(code, authority.PairingCode);
        Assert.NotNull(authority.RequestPair(code, new string('a', 32), "Pixel"));
        Assert.True(authority.Approve());
    }
    [Fact]
    public void ExpiredApprovalCanBeRequestedAgainWithTheSameCode()
    {
        var clock = new Clock(); var authority = new ControllerAuthority(clock); authority.NewCode();
        var code = authority.PairingCode;
        var pending = authority.RequestPair(code, new string('a', 32), "Pixel")!.Value;
        clock.Now += TimeSpan.FromMinutes(3);
        Assert.False(authority.IsPending(pending.Session, new string('a', 32)));
        Assert.False(authority.Approve());
        Assert.Equal(code, authority.PairingCode);
        var retry = authority.RequestPair(code, new string('a', 32), "Pixel")!.Value;
        Assert.NotEqual(pending.Session, retry.Session);
        Assert.True(authority.Approve());
        Assert.Null(authority.Authenticate(pending.Session, new string('a', 32)));
        Assert.NotNull(authority.Authenticate(retry.Session, new string('a', 32)));
    }
    [Fact]
    public void NewCodeInvalidatesThePreviousCodeAndPendingApprovalButKeepsTheApprovedController()
    {
        var (authority, current) = Paired();
        var oldCode = authority.PairingCode;
        var pending = authority.RequestPair(oldCode, new string('b', 32), "Tablet")!.Value;
        authority.NewCode();
        Assert.NotEqual(oldCode, authority.PairingCode);
        Assert.Null(authority.PendingApproval);
        Assert.False(authority.IsPending(pending.Session, new string('b', 32)));
        Assert.False(authority.Approve());
        Assert.Null(authority.RequestPair(oldCode, new string('b', 32), "Tablet"));
        Assert.NotNull(authority.Authenticate(current.Session, current.Tab));
        Assert.Null(authority.Authenticate(pending.Session, new string('b', 32)));
        Assert.NotNull(authority.RequestPair(authority.PairingCode, new string('b', 32), "Tablet"));
    }
    [Fact]
    public void ReplacingControllerRevokesOldCookieGrantAndInFlightCancellation()
    {
        var (authority, old) = Paired();
        var grant = authority.Reveal(old, 1, "match", 10)!;
        var cancellation = authority.AuthorizeCommand(old, grant.Token, 1, "match", 10)!.Value.Cancellation;
        var newer = authority.RequestPair(authority.PairingCode, new string('b', 32), "Tablet")!;
        Assert.NotNull(authority.Authenticate(old.Session, old.Tab));
        Assert.True(authority.ValidateGrant(old, grant.Token, 1, "match", 10));
        Assert.False(cancellation.IsCancellationRequested);
        Assert.Null(authority.Authenticate(newer.Value.Session, new string('b', 32)));
        Assert.True(authority.Approve());
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Null(authority.Authenticate(old.Session, old.Tab));
        Assert.False(authority.ValidateGrant(old, grant.Token, 1, "match", 10));
        Assert.NotNull(authority.Authenticate(newer.Value.Session, new string('b', 32)));
    }
    [Fact]
    public void GrantIsBoundToSeatVersionMatchAndExplicitHandoff()
    {
        var (authority, device) = Paired(); var grant = authority.Reveal(device, 1, "match-a", 9)!;
        Assert.True(authority.ValidateGrant(device, grant.Token, 1, "match-a", 9));
        Assert.False(authority.ValidateGrant(device, grant.Token, 2, "match-a", 9));
        Assert.False(authority.ValidateGrant(device, grant.Token, 1, "match-a", 10));
        Assert.False(authority.ValidateGrant(device, grant.Token, 1, "match-b", 9));
        Assert.False(authority.ValidateCsrf(device, "forged"));
        Assert.True(authority.ValidateCsrf(device, device.Csrf));
        authority.InvalidatePrivateGrants();
        Assert.False(authority.ValidateGrant(device, grant.Token, 1, "match-a", 9));
    }
    [Fact]
    public void HeartbeatLossExpiresPrivateViewAndReconnectNeverRestoresIt()
    {
        var clock = new Clock(); var (authority, device) = Paired(clock); var grant = authority.Reveal(device, 1, "match", 9)!;
        clock.Now += TimeSpan.FromSeconds(6);
        Assert.False(authority.ValidateGrant(device, grant.Token, 1, "match", 9));
        Assert.NotNull(authority.Authenticate(device.Session, device.Tab, true));
        Assert.False(authority.ValidateGrant(device, grant.Token, 1, "match", 9));
        Assert.NotNull(authority.Reveal(device, 1, "match", 9));
        clock.Now += TimeSpan.FromHours(12);
        Assert.Null(authority.Authenticate(device.Session, device.Tab, true));
    }
    [Fact]
    public void PrivateGrantRemainsUsableAfterThirtySecondsWithOngoingHeartbeats()
    {
        var clock = new Clock(); var (authority, device) = Paired(clock); var grant = authority.Reveal(device, 1, "match", 9)!;
        var original = authority.AuthorizeCommand(device, grant.Token, 1, "match", 9)!.Value;
        for (var i = 0; i < 60; i++) { clock.Now += TimeSpan.FromSeconds(2); authority.Authenticate(device.Session, device.Tab, true); }
        Assert.True(authority.ValidateGrant(device, grant.Token, 1, "match", 9));
        var current = authority.AuthorizeCommand(device, grant.Token, 1, "match", 9)!.Value;
        Assert.Equal(original.Cancellation, current.Cancellation);
        Assert.False(current.Cancellation.IsCancellationRequested);
        Assert.Equal(TimeSpan.FromSeconds(6), current.ValidFor);
    }
    [Fact]
    public void ARevealRequestedBeforeHideCannotAcquireANewGrantAfterItsHandoffWasRevoked()
    {
        var (authority, device) = Paired();
        var generation = authority.Generation;
        authority.InvalidatePrivateGrants();
        Assert.Null(authority.Reveal(device, 1, "match", 4, generation));
    }
    [Fact]
    public void AuthorizingACommandAtomicallyCapturesTheRevokedGrantNotItsReplacement()
    {
        var (authority, device) = Paired();
        var grant = authority.Reveal(device, 1, "match", 4)!;
        var admitted = authority.AuthorizeCommand(device, grant.Token, 1, "match", 4)!.Value;
        Assert.False(admitted.Cancellation.IsCancellationRequested);
        authority.InvalidatePrivateGrants(); // Between admission and the desktop queue.
        var replacement = authority.Reveal(device, 1, "match", 4)!;
        var newer = authority.AuthorizeCommand(device, replacement.Token, 1, "match", 4)!.Value;
        using var linkedAfterRevocation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None, admitted.Cancellation);
        Assert.True(admitted.Cancellation.IsCancellationRequested);
        Assert.True(linkedAfterRevocation.IsCancellationRequested);
        Assert.False(newer.Cancellation.IsCancellationRequested);
        Assert.Null(authority.AuthorizeCommand(device, grant.Token, 1, "match", 4));
    }
    [Fact]
    public void QueuedCommandAuthorizationCannotOutliveTheCurrentHeartbeat()
    {
        var clock = new Clock(); var (authority, device) = Paired(clock);
        var grant = authority.Reveal(device, 1, "match", 4)!;
        clock.Now += TimeSpan.FromSeconds(5);
        var admitted = authority.AuthorizeCommand(device, grant.Token, 1, "match", 4)!.Value;
        Assert.Equal(TimeSpan.FromSeconds(1), admitted.ValidFor);
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Null(authority.AuthorizeCommand(device, grant.Token, 1, "match", 4));
    }
    [Fact]
    public void ControllerSessionStillExpiresAfterTwelveHoursEvenWithAFreshHeartbeatAndGrant()
    {
        var clock = new Clock(); var (authority, device) = Paired(clock);
        clock.Now += TimeSpan.FromHours(12) - TimeSpan.FromSeconds(1);
        Assert.NotNull(authority.Authenticate(device.Session, device.Tab, true));
        var grant = authority.Reveal(device, 1, "match", 4)!;
        Assert.Equal(TimeSpan.FromSeconds(1), authority.AuthorizeCommand(device, grant.Token, 1, "match", 4)!.Value.ValidFor);
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Null(authority.Authenticate(device.Session, device.Tab, true));
        Assert.False(authority.ValidateGrant(device, grant.Token, 1, "match", 4));
        Assert.Null(authority.AuthorizeCommand(device, grant.Token, 1, "match", 4));
    }
    [Theory]
    [InlineData(" ")]
    [InlineData("Pixel\n1234")]
    [InlineData("Pixel\u202e1234")]
    public void PairingLabelCannotSpoofLaptopApprovalWithControlOrDirectionCharacters(string label)
    {
        var authority = new ControllerAuthority(); authority.NewCode();
        Assert.Null(authority.RequestPair(authority.PairingCode, new string('a', 32), label));
    }
    [Theory]
    [InlineData("192.168.20.7", "192.168.20.2:8080", "http://192.168.20.2:8080", true, 200)]
    [InlineData("192.168.21.7", "192.168.20.2:8080", "http://192.168.20.2:8080", true, 403)]
    [InlineData("8.8.8.8", "192.168.20.2:8080", "http://192.168.20.2:8080", true, 403)]
    [InlineData("192.168.20.7", "attacker.example", "http://192.168.20.2:8080", true, 421)]
    [InlineData("192.168.20.7", "192.168.20.2:8080", "http://evil.example", true, 403)]
    [InlineData("192.168.20.7", "192.168.20.2:8080", "", true, 403)]
    [InlineData("127.0.0.1", "192.168.20.2:8080", "http://192.168.20.2:8080", false, 403)]
    public void HostOriginPrivateProfileAndSubnetAreRequired(string peer, string host, string origin, bool isPrivate, int expected)
    {
        var context = new DefaultHttpContext(); context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Host = new(host); context.Request.Method = "POST"; context.Request.Path = "/api/command";
        context.Request.Headers.Origin = origin;
        Assert.Equal(expected, CompanionServer.RequestAllowed(context,
            new LanInterface("test", "test", IPAddress.Parse("192.168.20.2"), 24, Guid.NewGuid()),
            ["http://192.168.20.2:8080"], () => isPrivate));
    }
    private static Task<GameCoordinator> CreateGame(bool secondAi = false) => GameCoordinator.CreateAsync(
        new GameRules(TestManifest.Manifest, TestManifest.Catalog), new InMemorySessionStore(),
        new SessionSetup(SessionId.New(), [new Seat(new(1), "Alex", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard),
            new Seat(new(2), "Player two", PlayerColor.Red, secondAi ? SeatKind.Computer : SeatKind.Human, AiDifficulty.Standard)], new(1), VerificationMode.Manual),
        DeterministicRandom.SeedFrom(91), TestContext.Current.CancellationToken);
    [Fact]
    public async Task BridgeRevealsOnlyNextHumanSetupSeatAndNeverAi()
    {
        var game = await CreateGame(true); var bridge = new CoordinatorCompanionBridge(() => game);
        var snapshot = await bridge.ReadPublicAsync(cancellationToken: TestContext.Current.CancellationToken); Assert.Equal(1, snapshot.RevealSeatId);
        Assert.Null(await bridge.ReadPrivateAsync(new(2), snapshot.Game!.StateVersion, cancellationToken: TestContext.Current.CancellationToken));
        var own = await bridge.ReadPrivateAsync(new(1), snapshot.Game.StateVersion, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(own); Assert.Equal(3, own.OfferedTickets.Count); Assert.Equal(4, own.View.Hand.Length);
        foreach (var ticket in own.OfferedTickets)
        {
            var destination = TestManifest.Manifest.Ticket(new TicketId(ticket.Id));
            Assert.Equal(TestManifest.Manifest.City(destination.CityA).DisplayName, ticket.From);
            Assert.Equal(TestManifest.Manifest.City(destination.CityB).DisplayName, ticket.To);
            Assert.Equal($"{ticket.From} – {ticket.To}", ticket.Label);
        }
        var publicJson = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("TrainHands", publicJson); Assert.DoesNotContain("SetupOffers", publicJson); Assert.DoesNotContain("RandomState", publicJson);
    }
    [Fact]
    public async Task BridgeUsesDurableCommandsForSetupThenHumanDrawsAndRejectsAdministrativeCommands()
    {
        var game = await CreateGame(); var bridge = new CoordinatorCompanionBridge(() => game);
        foreach (var seat in new[] { 1, 2 })
        {
            var data = (await bridge.ReadPrivateAsync(new(seat), game.Public.StateVersion, cancellationToken: TestContext.Current.CancellationToken))!;
            var selection = new CompanionCommand(Guid.NewGuid().ToString("n"), game.SessionId.Value, game.Public.StateVersion,
                "keepTickets", KeptTickets: data.OfferedTickets.Take(2).Select(t => t.Id).ToArray());
            Assert.True((await bridge.ExecuteAsync(new(seat), selection, cancellationToken: TestContext.Current.CancellationToken)).Accepted);
        }
        var count = (await game.GetSeatViewAsync(new(1), cancellationToken: TestContext.Current.CancellationToken)).Hand.Length;
        var draw = new CompanionCommand(Guid.NewGuid().ToString("n"), game.SessionId.Value, game.Public.StateVersion, "drawTrain");
        Assert.True((await bridge.ExecuteAsync(new(1), draw, cancellationToken: TestContext.Current.CancellationToken)).Accepted);
        Assert.Equal(count + 1, (await game.GetSeatViewAsync(new(1), cancellationToken: TestContext.Current.CancellationToken)).Hand.Length);
        Assert.False((await bridge.ExecuteAsync(new(1), draw, cancellationToken: TestContext.Current.CancellationToken)).Accepted); // stale version cannot redraw.
        var forbidden = draw with { CommandId = Guid.NewGuid().ToString("n"), ExpectedStateVersion = game.Public.StateVersion, Kind = "SubmitClaimEvidence" };
        Assert.False((await bridge.ExecuteAsync(new(1), forbidden, cancellationToken: TestContext.Current.CancellationToken)).Accepted);
        Assert.False((await bridge.ExecuteAsync(new(2), forbidden with { Kind = "drawTrain" }, cancellationToken: TestContext.Current.CancellationToken)).Accepted);
        Assert.Empty(await game.CheckInvariantsAsync(cancellationToken: TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task DetectedRoutePaymentsCannotBypassTheDesktopCameraAuthorization()
    {
        var token = TestContext.Current.CancellationToken;
        var game = await CreateGame();
        var bridge = new CoordinatorCompanionBridge(() => game);
        foreach (var seat in new[] { 1, 2 })
        {
            var data = (await bridge.ReadPrivateAsync(new(seat), game.Public.StateVersion, token))!;
            Assert.True((await bridge.ExecuteAsync(new(seat), new CompanionCommand(
                Guid.NewGuid().ToString("N"), game.SessionId.Value, game.Public.StateVersion,
                "keepTickets", KeptTickets: data.OfferedTickets.Take(2).Select(ticket => ticket.Id).ToArray()), token)).Accepted);
        }
        var own = (await bridge.ReadPrivateAsync(new(1), game.Public.StateVersion, token))!;
        var claim = own.Actions.Claims.First();
        var command = new CompanionCommand(Guid.NewGuid().ToString("N"), game.SessionId.Value,
            game.Public.StateVersion, "payDetectedRoute", RouteId: claim.RouteId.Value,
            Payment: claim.Payments[0], DetectedClaimId: Guid.NewGuid().ToString("N"));

        var receipt = await bridge.ExecuteAsync(new(1), command, token);

        Assert.False(receipt.Accepted);
        Assert.Equal("ActionNotAllowed", receipt.Code);
        Assert.Equal(command.ExpectedStateVersion, game.Public.StateVersion);
        Assert.Null(game.Public.PendingClaim);
        Assert.Equal(own.View.Hand, (await game.GetSeatViewAsync(new(1), token)).Hand);
    }
    [Fact]
    public async Task DesktopBusyAndReconciliationGateBlocksBothPrivateReadsAndCommands()
    {
        var game = await CreateGame(); var allowed = true;
        var bridge = new CoordinatorCompanionBridge(() => game, canControl: () => allowed);
        Assert.NotNull(await bridge.ReadPrivateAsync(new(1), game.Public.StateVersion, cancellationToken: TestContext.Current.CancellationToken));
        allowed = false;
        Assert.False((await bridge.ReadPublicAsync(cancellationToken: TestContext.Current.CancellationToken)).CanControl);
        Assert.Null(await bridge.ReadPrivateAsync(new(1), game.Public.StateVersion, cancellationToken: TestContext.Current.CancellationToken));
        var command = new CompanionCommand(Guid.NewGuid().ToString("n"), game.SessionId.Value, game.Public.StateVersion, "drawTrain");
        Assert.False((await bridge.ExecuteAsync(new(1), command, cancellationToken: TestContext.Current.CancellationToken)).Accepted);
    }
}
