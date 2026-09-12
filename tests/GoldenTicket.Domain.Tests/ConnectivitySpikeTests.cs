using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GoldenTicket.ConnectivitySpike;
using GoldenTicket.ConnectivitySpike.Networking;
using GoldenTicket.ConnectivitySpike.Pairing;
using GoldenTicket.ConnectivitySpike.Security;
using Microsoft.AspNetCore.Http;

namespace GoldenTicket.Domain.Tests;

/// <summary>
/// The parts of the M0 connectivity spike that can be settled on this machine. The four questions
/// the spike actually exists to answer need real devices (DESIGN 22.7) and cannot be tested here;
/// these cover the certificate shape, the wire format and the pairing rules so a borrowed-device
/// session is not spent debugging those.
/// </summary>
public class ConnectivitySpikeTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "GoldenTicket.Spike", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A held handle on a temporary directory must not fail the run.
        }
    }

    // ---- Certificates -------------------------------------------------------------------------

    [Fact]
    public void TheServerCertificateCoversTheNameAndTheAddressActuallyUsed()
    {
        var authority = new LocalCertificateAuthority(_directory);
        var address = IPAddress.Parse("192.168.1.50");

        var material = authority.EnsureMaterial(address);
        using var server = material.ServerCertificate;

        var names = LocalCertificateAuthority.ReadSubjectAlternativeNames(server);

        // DESIGN 18.5: the names must match the hostname or IP the device actually types, because
        // a mismatch must never be worked around by disabling validation.
        Assert.Contains(material.Hostname, names);
        Assert.Contains("192.168.1.50", names);
        Assert.Contains("localhost", names);
        Assert.True(server.HasPrivateKey);
    }

    [Fact]
    public void TheServerCertificateStaysInsideApplesTrustWindowAndSaysItIsForServerAuthentication()
    {
        var authority = new LocalCertificateAuthority(_directory);
        var material = authority.EnsureMaterial(IPAddress.Parse("10.0.0.5"));
        using var server = material.ServerCertificate;

        // Apple rejects manually trusted leaves valid for more than 825 days.
        var lifetime = server.NotAfter - server.NotBefore;
        Assert.True(lifetime.TotalDays < 825, $"The leaf is valid for {lifetime.TotalDays:F0} days.");
        Assert.Equal(LocalCertificateAuthority.ServerCertificateDays, Math.Round(lifetime.TotalDays));

        var usages = server.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains(usages.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(),
            oid => oid.Value == "1.3.6.1.5.5.7.3.1");

        var basic = server.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.False(basic.CertificateAuthority);
    }

    [Fact]
    public void TheAuthorityIsReusedAcrossRunsSoTrustedDevicesKeepWorking()
    {
        var address = IPAddress.Parse("192.168.1.50");

        var first = new LocalCertificateAuthority(_directory).EnsureMaterial(address);
        using (first.ServerCertificate)
        {
            var second = new LocalCertificateAuthority(_directory).EnsureMaterial(address);
            using (second.ServerCertificate)
            {
                Assert.Equal(first.AuthorityFingerprint, second.AuthorityFingerprint);
                Assert.Equal(first.Hostname, second.Hostname);
            }
        }
    }

    [Fact]
    public void MovingToADifferentNetworkReissuesTheLeafButKeepsTheAuthority()
    {
        var authority = new LocalCertificateAuthority(_directory);

        var home = authority.EnsureMaterial(IPAddress.Parse("192.168.1.50"));
        var homeFingerprint = home.AuthorityFingerprint;
        home.ServerCertificate.Dispose();

        var elsewhere = authority.EnsureMaterial(IPAddress.Parse("10.20.30.40"));
        using var server = elsewhere.ServerCertificate;

        Assert.Equal(homeFingerprint, elsewhere.AuthorityFingerprint);
        Assert.Contains("10.20.30.40", LocalCertificateAuthority.ReadSubjectAlternativeNames(server));
    }

    [Fact]
    public void ReplacingTheAuthorityCannotLeaveALeafSignedByTheOldAuthority()
    {
        var address = IPAddress.Parse("192.168.1.50");
        var original = new LocalCertificateAuthority(_directory).EnsureMaterial(address);
        using var originalServer = original.ServerCertificate;
        var replacementDirectory = Path.Combine(_directory, "replacement");
        var replacement = new LocalCertificateAuthority(replacementDirectory).EnsureMaterial(address);
        using var replacementServer = replacement.ServerCertificate;
        foreach (var name in new[] { "authority.crt", "authority.key.dpapi" })
            File.Copy(Path.Combine(replacementDirectory, name), Path.Combine(_directory, name), overwrite: true);

        var renewed = new LocalCertificateAuthority(_directory).EnsureMaterial(address);
        using var renewedServer = renewed.ServerCertificate;
        using var root = X509CertificateLoader.LoadCertificate(renewed.AuthorityCertificateDer);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;

        Assert.Equal(replacement.AuthorityFingerprint, renewed.AuthorityFingerprint);
        Assert.NotEqual(originalServer.Thumbprint, renewedServer.Thumbprint);
        Assert.True(chain.Build(renewedServer));
    }

    [Fact]
    public void MissingAuthorityKeyFailsWithoutSilentlyReplacingDeviceTrust()
    {
        var authority = new LocalCertificateAuthority(_directory);
        using var server = authority.EnsureMaterial(IPAddress.Parse("10.0.0.5")).ServerCertificate;
        var originalCa = File.ReadAllBytes(Path.Combine(_directory, "authority.crt"));
        File.Delete(Path.Combine(_directory, "authority.key.dpapi"));

        Assert.Throws<CryptographicException>(() => authority.EnsureMaterial(IPAddress.Parse("10.0.0.5")));
        Assert.Equal(originalCa, File.ReadAllBytes(Path.Combine(_directory, "authority.crt")));
    }

    [Fact]
    public void MalformedProtectedLeafIsReissuedWithoutChangingTheAuthority()
    {
        var address = IPAddress.Parse("10.0.0.5");
        var authority = new LocalCertificateAuthority(_directory);
        var original = authority.EnsureMaterial(address);
        using var originalServer = original.ServerCertificate;
        var malformed = ProtectedData.Protect([1],
            System.Text.Encoding.UTF8.GetBytes("GoldenTicket.CompanionHost.v1"), DataProtectionScope.CurrentUser);
        File.WriteAllBytes(Path.Combine(_directory, "server.pfx.dpapi"), malformed);

        var renewed = authority.EnsureMaterial(address);
        using var renewedServer = renewed.ServerCertificate;
        Assert.Equal(original.AuthorityFingerprint, renewed.AuthorityFingerprint);
        Assert.NotEqual(originalServer.Thumbprint, renewedServer.Thumbprint);
        Assert.True(renewedServer.HasPrivateKey);
    }

    [Fact]
    public void ThePrivateKeysAreNotWrittenInTheClear()
    {
        var authority = new LocalCertificateAuthority(_directory);
        using var server = authority.EnsureMaterial(IPAddress.Parse("192.168.1.50")).ServerCertificate;

        foreach (var file in Directory.GetFiles(_directory))
        {
            var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(file));

            // DPAPI-protected blobs and DER certificates: no PEM private key may appear anywhere.
            Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
        }

        // Only the public certificate is ever exported for a device (DESIGN 18.5).
        Assert.True(File.Exists(Path.Combine(_directory, "authority.crt")));
    }

    [Fact]
    public void TheFingerprintIsGroupedSoItCanBeComparedAloud()
    {
        var authority = new LocalCertificateAuthority(_directory);
        var material = authority.EnsureMaterial(IPAddress.Parse("192.168.1.50"));
        material.ServerCertificate.Dispose();

        // 32 bytes as 8 groups of 4 hex pairs.
        var groups = material.AuthorityFingerprint.Split(' ');
        Assert.Equal(8, groups.Length);
        Assert.All(groups, group => Assert.Equal(8, group.Length));
    }

    // ---- Private addresses --------------------------------------------------------------------

    [Theory]
    [InlineData("192.168.1.10", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("169.254.1.1", false)]   // link-local means DHCP failed
    [InlineData("127.0.0.1", false)]
    public void OnlyPrivateAddressesMayBeServedOn(string address, bool expected)
    {
        Assert.Equal(expected, LanInterfaces.IsPrivate(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("192.168.1.99", 24, true)]
    [InlineData("::ffff:192.168.1.99", 24, true)]
    [InlineData("192.168.2.99", 24, false)]
    [InlineData("10.0.0.1", 24, false)]
    [InlineData("8.8.8.8", 8, false)]
    [InlineData("192.168.1.49", 30, true)]
    [InlineData("192.168.1.54", 30, false)]
    [InlineData("192.168.1.50", 0, false)]
    public void PeersMustBelongToTheSelectedSubnet(string peer, int prefixLength, bool allowed)
    {
        Assert.Equal(allowed, LanInterfaces.IsInSubnet(IPAddress.Parse(peer), IPAddress.Parse("192.168.1.50"), prefixLength));
    }

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

    // ---- Multicast DNS wire format ---------------------------------------------------------------

    [Fact]
    public void TheResponderAnswersAQueryForItsOwnName()
    {
        var responder = new MulticastDnsResponder("gt-1a2b3c4d.local", IPAddress.Parse("192.168.1.50"));

        var query = BuildQuery("gt-1a2b3c4d.local", type: 1);
        Assert.True(responder.AsksForThisName(query, out _));
    }

    [Theory]
    [InlineData("gt-99999999.local")]   // another installation
    [InlineData("printer.local")]
    [InlineData("gt-1a2b3c4d.lan")]
    public void TheResponderIgnoresQueriesForOtherNames(string name)
    {
        var responder = new MulticastDnsResponder("gt-1a2b3c4d.local", IPAddress.Parse("192.168.1.50"));
        Assert.False(responder.AsksForThisName(BuildQuery(name, type: 1), out _));
    }

    [Fact]
    public void TheResponderIgnoresResponsesSoTwoRespondersCannotLoop()
    {
        var responder = new MulticastDnsResponder("gt-1a2b3c4d.local", IPAddress.Parse("192.168.1.50"));

        var message = BuildQuery("gt-1a2b3c4d.local", type: 1);
        message[2] = 0x84;   // set QR and AA: this is an answer, not a question

        Assert.False(responder.AsksForThisName(message, out _));
    }

    [Fact]
    public void TheAnswerCarriesTheAddressAsAnAuthoritativeARecord()
    {
        var responder = new MulticastDnsResponder("gt-1a2b3c4d.local", IPAddress.Parse("192.168.1.50"));
        var response = responder.BuildResponse(queryId: 0x1234);

        Assert.Equal(0x12, response[0]);
        Assert.Equal(0x34, response[1]);
        Assert.Equal(0x84, response[2]);            // response, authoritative
        Assert.Equal(0, response[5]);               // no questions echoed
        Assert.Equal(1, response[7]);               // one answer

        // The last four bytes are the A record's address.
        Assert.Equal(new byte[] { 192, 168, 1, 50 }, response[^4..]);
    }

    [Fact]
    public void NamesAreEncodedAsLengthPrefixedLabels()
    {
        var encoded = MulticastDnsResponder.EncodeName("gt-1a2b3c4d.local");

        Assert.Equal(11, encoded[0]);
        Assert.Equal("gt-1a2b3c4d", System.Text.Encoding.ASCII.GetString(encoded, 1, 11));
        Assert.Equal(5, encoded[12]);
        Assert.Equal("local", System.Text.Encoding.ASCII.GetString(encoded, 13, 5));
        Assert.Equal(0, encoded[^1]);
    }

    private static byte[] BuildQuery(string name, ushort type)
    {
        var encoded = MulticastDnsResponder.EncodeName(name);
        var message = new byte[12 + encoded.Length + 4];

        message[5] = 1;   // one question
        encoded.CopyTo(message, 12);

        var offset = 12 + encoded.Length;
        message[offset + 1] = (byte)type;
        message[offset + 3] = 1;   // class IN
        return message;
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
