using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GoldenTicket.CompanionHost.Networking;
using GoldenTicket.CompanionHost.Security;

namespace GoldenTicket.Domain.Tests;

/// <summary>Certificate lifecycle, LAN address policy and DNS wire format of the shipped host.
/// Real phone trust/install and LAN discovery still require device acceptance checks.</summary>
public sealed class CompanionNetworkingTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "GoldenTicket.CompanionNetworkingTests", Guid.NewGuid().ToString("n"));
    private readonly TestKeyVault _keys = new();

    private LocalCertificateAuthority CreateAuthority(string? directory = null) =>
        new(directory ?? _directory, _keys.Protect, _keys.Unprotect);

    public void Dispose()
    {
        _keys.Dispose();
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
        var authority = CreateAuthority();
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
        var authority = CreateAuthority();
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

        var first = CreateAuthority().EnsureMaterial(address);
        using (first.ServerCertificate)
        {
            var second = CreateAuthority().EnsureMaterial(address);
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
        var authority = CreateAuthority();

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
        var original = CreateAuthority().EnsureMaterial(address);
        using var originalServer = original.ServerCertificate;
        var replacementDirectory = Path.Combine(_directory, "replacement");
        var replacement = CreateAuthority(replacementDirectory).EnsureMaterial(address);
        using var replacementServer = replacement.ServerCertificate;
        foreach (var name in new[] { "authority.crt", "authority.key.dpapi" })
            File.Copy(Path.Combine(replacementDirectory, name), Path.Combine(_directory, name), overwrite: true);

        var renewed = CreateAuthority().EnsureMaterial(address);
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
        var authority = CreateAuthority();
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
        var authority = CreateAuthority();
        var original = authority.EnsureMaterial(address);
        using var originalServer = original.ServerCertificate;
        File.WriteAllBytes(Path.Combine(_directory, "server.pfx.dpapi"), [1]);

        var renewed = authority.EnsureMaterial(address);
        using var renewedServer = renewed.ServerCertificate;
        Assert.Equal(original.AuthorityFingerprint, renewed.AuthorityFingerprint);
        Assert.NotEqual(originalServer.Thumbprint, renewedServer.Thumbprint);
        Assert.True(renewedServer.HasPrivateKey);
    }

    [Fact]
    public void ThePrivateKeysAreNotWrittenInTheClear()
    {
        var authority = CreateAuthority();
        using var server = authority.EnsureMaterial(IPAddress.Parse("192.168.1.50")).ServerCertificate;

        foreach (var file in Directory.GetFiles(_directory))
        {
            var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(file));

            // Opaque test tokens and DER certificates: no PEM private key may appear anywhere.
            Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
        }

        // Only the public certificate is ever exported for a device (DESIGN 18.5).
        Assert.True(File.Exists(Path.Combine(_directory, "authority.crt")));
    }

    [Fact]
    public void TheFingerprintIsGroupedSoItCanBeComparedAloud()
    {
        var authority = CreateAuthority();
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

}
