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
    public void PairingRequiresExplicitLaptopApprovalAndCodeIsSingleUse()
    {
        var authority = new ControllerAuthority(); authority.NewCode(); var code = authority.PairingCode;
        var pending = authority.RequestPair(code, new string('a', 32), "Pixel");
        Assert.NotNull(pending);
        Assert.Null(authority.Authenticate(pending.Value.Session, new string('a', 32)));
        Assert.Null(authority.RequestPair(code, new string('b', 32), "Second phone"));
        Assert.True(authority.Approve());
        Assert.NotNull(authority.Authenticate(pending.Value.Session, new string('a', 32)));
        Assert.Null(authority.Authenticate(pending.Value.Session, new string('b', 32)));
    }
    [Fact]
    public void PairingCodesAttemptLimitAndExpiryAreEnforced()
    {
        var clock = new Clock(); var authority = new ControllerAuthority(clock); authority.NewCode();
        var code = authority.PairingCode;
        var wrong = code == "111111" ? "222222" : "111111";
        for (var i = 0; i < 6; i++) Assert.Null(authority.RequestPair(wrong, new string('a', 32), "Pixel"));
        Assert.Null(authority.RequestPair(code, new string('a', 32), "Pixel"));
        authority.NewCode(); code = authority.PairingCode; clock.Now += TimeSpan.FromMinutes(5);
        Assert.Null(authority.RequestPair(code, new string('a', 32), "Pixel"));
    }
    [Fact]
    public void ReplacingControllerRevokesOldCookieGrantAndInFlightCancellation()
    {
        var (authority, old) = Paired();
        var grant = authority.Reveal(old, 1, "match", 10)!;
        var cancellation = authority.AuthorizeCommand(old, grant.Token, 1, "match", 10)!.Value.Cancellation;
        authority.NewCode(); var newer = authority.RequestPair(authority.PairingCode, new string('b', 32), "Tablet")!;
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
    public void PrivateGrantExpiresAfterThirtySecondsEvenWithHeartbeats()
    {
        var clock = new Clock(); var (authority, device) = Paired(clock); var grant = authority.Reveal(device, 1, "match", 9)!;
        for (var i = 0; i < 15; i++) { clock.Now += TimeSpan.FromSeconds(2); authority.Authenticate(device.Session, device.Tab, true); }
        Assert.False(authority.ValidateGrant(device, grant.Token, 1, "match", 9));
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
    public void QueuedCommandAuthorizationCannotOutliveTheCurrentHeartbeatOrPrivateGrant()
    {
        var clock = new Clock(); var (authority, device) = Paired(clock);
        var grant = authority.Reveal(device, 1, "match", 4)!;
        clock.Now += TimeSpan.FromSeconds(5);
        var admitted = authority.AuthorizeCommand(device, grant.Token, 1, "match", 4)!.Value;
        Assert.Equal(TimeSpan.FromSeconds(1), admitted.ValidFor);
        clock.Now += TimeSpan.FromSeconds(1);
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
            new Seat(new(2), "Player two", PlayerColor.Red, secondAi ? SeatKind.Computer : SeatKind.Human, AiDifficulty.Standard)], new(1), VerificationMode.Manual), DeterministicRandom.SeedFrom(91));
    [Fact]
    public async Task BridgeRevealsOnlyNextHumanSetupSeatAndNeverAi()
    {
        var game = await CreateGame(true); var bridge = new CoordinatorCompanionBridge(() => game);
        var snapshot = await bridge.ReadPublicAsync(); Assert.Equal(1, snapshot.RevealSeatId);
        Assert.Null(await bridge.ReadPrivateAsync(new(2), snapshot.Game!.StateVersion));
        var own = await bridge.ReadPrivateAsync(new(1), snapshot.Game.StateVersion);
        Assert.NotNull(own); Assert.Equal(3, own.OfferedTickets.Count); Assert.Equal(4, own.View.Hand.Length);
        var publicJson = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("TrainHands", publicJson); Assert.DoesNotContain("SetupOffers", publicJson); Assert.DoesNotContain("RandomState", publicJson);
    }
    [Fact]
    public async Task BridgeUsesDurableCommandsForSetupThenHumanDrawsAndRejectsAdministrativeCommands()
    {
        var game = await CreateGame(); var bridge = new CoordinatorCompanionBridge(() => game);
        foreach (var seat in new[] { 1, 2 })
        {
            var data = (await bridge.ReadPrivateAsync(new(seat), game.Public.StateVersion))!;
            var selection = new CompanionCommand(Guid.NewGuid().ToString("n"), game.SessionId.Value, game.Public.StateVersion,
                "keepTickets", KeptTickets: data.OfferedTickets.Take(2).Select(t => t.Id).ToArray());
            Assert.True((await bridge.ExecuteAsync(new(seat), selection)).Accepted);
        }
        var count = (await game.GetSeatViewAsync(new(1))).Hand.Length;
        var draw = new CompanionCommand(Guid.NewGuid().ToString("n"), game.SessionId.Value, game.Public.StateVersion, "drawTrain");
        Assert.True((await bridge.ExecuteAsync(new(1), draw)).Accepted);
        Assert.Equal(count + 1, (await game.GetSeatViewAsync(new(1))).Hand.Length);
        Assert.False((await bridge.ExecuteAsync(new(1), draw)).Accepted); // stale version cannot redraw.
        var forbidden = draw with { CommandId = Guid.NewGuid().ToString("n"), ExpectedStateVersion = game.Public.StateVersion, Kind = "SubmitClaimEvidence" };
        Assert.False((await bridge.ExecuteAsync(new(1), forbidden)).Accepted);
        Assert.False((await bridge.ExecuteAsync(new(2), forbidden with { Kind = "drawTrain" })).Accepted);
        Assert.Empty(await game.CheckInvariantsAsync());
    }
    [Fact]
    public async Task DesktopBusyAndReconciliationGateBlocksBothPrivateReadsAndCommands()
    {
        var game = await CreateGame(); var allowed = true;
        var bridge = new CoordinatorCompanionBridge(() => game, canControl: () => allowed);
        Assert.NotNull(await bridge.ReadPrivateAsync(new(1), game.Public.StateVersion));
        allowed = false;
        Assert.False((await bridge.ReadPublicAsync()).CanControl);
        Assert.Null(await bridge.ReadPrivateAsync(new(1), game.Public.StateVersion));
        var command = new CompanionCommand(Guid.NewGuid().ToString("n"), game.SessionId.Value, game.Public.StateVersion, "drawTrain");
        Assert.False((await bridge.ExecuteAsync(new(1), command)).Accepted);
    }
}
