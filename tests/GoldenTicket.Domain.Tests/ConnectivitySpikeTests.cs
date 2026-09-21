using System.Net;
using GoldenTicket.ConnectivitySpike;
using GoldenTicket.ConnectivitySpike.Pairing;
using GoldenTicket.ConnectivitySpike.Security;
using Microsoft.AspNetCore.Http;

namespace GoldenTicket.Domain.Tests;

/// <summary>Policy, bounded diagnostics and pairing of the standalone feasibility tool.
/// Production certificate, address and DNS tests live in CompanionNetworkingTests.</summary>
public sealed class ConnectivitySpikeTests
{
    [Theory]
    [InlineData("192.168.1.99", true, 200)]
    [InlineData("192.168.1.99", false, 403)]
    [InlineData("192.168.2.99", true, 403)]
    [InlineData("8.8.8.8", true, 403)]
    [InlineData("127.0.0.1", false, 200)]
    public void BothHostsRejectOutsidePeersAndPublicNetworkProfiles(string peer, bool isPrivate, int expected)
    {
        var policy = new SpikeRequestPolicy(IPAddress.Parse("192.168.1.50"), 24,
            ["https://gt-test.local:8443"], () => isPrivate);
        var context = NewRequest(peer);
        Assert.Equal(expected, policy.Validate(context));
        Assert.Equal(expected, policy.Validate(context, bootstrap: true));
    }

    [Theory]
    [InlineData("gt-test.local:8443", "https://gt-test.local:8443", "1", 200)]
    [InlineData("gt-test.local:8443", null, "1", 200)] // non-browser clients still need the custom header
    [InlineData("gt-test.local:8443", null, null, 403)]
    [InlineData("gt-test.local:8443", "null", "1", 403)]
    [InlineData("gt-test.local:8443", "https://evil.example", "1", 403)]
    [InlineData("gt-test.local:8443", "https://gt-test.local:8443, https://evil.example", "1", 403)]
    [InlineData("evil.example:8443", "https://gt-test.local:8443", "1", 421)]
    public void HostOriginAndCsrfChecksRejectCrossOriginWrites(string host, string? origin, string? header, int expected)
    {
        var policy = new SpikeRequestPolicy(IPAddress.Parse("192.168.1.50"), 24,
            ["https://gt-test.local:8443"], () => true);
        var context = NewRequest("192.168.1.99");
        context.Request.Method = "POST";
        context.Request.Path = "/api/spike/pair";
        context.Request.Host = new HostString(host);
        if (origin is not null) context.Request.Headers.Origin = origin;
        if (header is not null) context.Request.Headers["X-GoldenTicket-Spike"] = header;
        Assert.Equal(expected, policy.Validate(context));
    }

    [Fact]
    public void ConcurrentReportsCannotExceedTheMemoryBudget()
    {
        var buffer = new BoundedObservationBuffer<int>(128);
        Parallel.For(0, 2000, value => buffer.TryAdd(value));
        Assert.Equal(128, buffer.Snapshot().Count);
        Assert.False(buffer.TryAdd(2001));
    }

    private static DefaultHttpContext NewRequest(string peer)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Host = new HostString("gt-test.local:8443");
        context.Request.Path = "/";
        return context;
    }

    // ---- Pairing ---------------------------------------------------------------------------------

    [Fact]
    public void APairingCodeWorksExactlyOnce()
    {
        var pairing = new PairingService();
        var code = pairing.IssueCode();

        var first = pairing.Redeem(code, "Alex's phone");
        Assert.Equal(PairingOutcome.Paired, first.Outcome);
        Assert.True(pairing.IsPaired(first.Device!.DeviceSessionId));

        var second = pairing.Redeem(code, "someone else");
        Assert.Equal(PairingOutcome.Expired, second.Outcome);
    }

    [Fact]
    public void GuessingIsBoundedAndRetiresTheCode()
    {
        var pairing = new PairingService();
        var code = pairing.IssueCode();
        var incorrectCode = code == "00000000" ? "11111111" : "00000000";

        for (var attempt = 0; attempt < PairingService.MaximumAttempts; attempt++)
            Assert.Equal(PairingOutcome.WrongCode, pairing.Redeem(incorrectCode, "guesser").Outcome);

        Assert.Null(pairing.CurrentCode);

        Assert.Equal(PairingOutcome.TooManyAttempts, pairing.Redeem(code, "guesser").Outcome);
        Assert.Null(pairing.CurrentCode);
    }

    [Fact]
    public void ACodeExpires()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var pairing = new PairingService(clock);
        var code = pairing.IssueCode();

        clock.Advance(PairingService.CodeLifetime);

        Assert.Null(pairing.CurrentCode);
        Assert.Equal(PairingOutcome.Expired, pairing.Redeem(code, "late").Outcome);
    }

    [Fact]
    public void IssuingANewCodeInvalidatesTheOldOne()
    {
        var pairing = new PairingService();
        var first = pairing.IssueCode();
        var second = pairing.IssueCode();

        Assert.NotEqual(first, second);
        Assert.Equal(PairingOutcome.WrongCode, pairing.Redeem(first, "stale").Outcome);
        Assert.Equal(PairingOutcome.Paired, pairing.Redeem(second, "current").Outcome);
    }

    [Theory]
    [InlineData("", "unnamed device")]
    [InlineData("   ", "unnamed device")]
    [InlineData("Alex's phone", "Alex's phone")]
    public void DeviceLabelsAreTreatedAsUntrustedText(string submitted, string expected)
    {
        var pairing = new PairingService();
        var code = pairing.IssueCode();

        var result = pairing.Redeem(code, submitted);

        Assert.Equal(PairingOutcome.Paired, result.Outcome);
        Assert.Equal(expected, result.Device!.Label);
    }

    [Fact]
    public void ALongDeviceLabelIsBounded()
    {
        var pairing = new PairingService();
        var result = pairing.Redeem(pairing.IssueCode(), new string('x', 500));

        Assert.Equal(PairingOutcome.Paired, result.Outcome);
        Assert.True(result.Device!.Label.Length <= 60);
    }

    [Fact]
    public void AnUnknownSessionIsNotPaired()
    {
        var pairing = new PairingService();
        pairing.Redeem(pairing.IssueCode(), "phone");

        Assert.False(pairing.IsPaired("not-a-session"));
        Assert.False(pairing.IsPaired(null));
    }

    [Fact]
    public void RevokingADeviceEndsItsSession()
    {
        var pairing = new PairingService();
        var device = pairing.Redeem(pairing.IssueCode(), "phone").Device!;

        pairing.Revoke(device.DeviceSessionId);

        Assert.False(pairing.IsPaired(device.DeviceSessionId));
    }

    [Fact]
    public void RetainingACookieCannotExtendTheServerSideSessionLifetime()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var pairing = new PairingService(clock);
        var device = pairing.Redeem(pairing.IssueCode(), "phone").Device!;
        clock.Advance(PairingService.SessionLifetime - TimeSpan.FromSeconds(1));
        Assert.True(pairing.IsPaired(device.DeviceSessionId));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(pairing.Find(device.DeviceSessionId));
        Assert.False(pairing.IsPaired(device.DeviceSessionId));
        Assert.Empty(pairing.Devices);
    }

    /// <summary>A clock the test moves by hand, so no extra test-only dependency is needed.</summary>
    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
