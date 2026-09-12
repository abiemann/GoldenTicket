using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using GoldenTicket.CompanionHost.Security;

namespace GoldenTicket.Domain.Tests;

/// <summary>
/// Guards the installed runtime's LAN-only contract independently of the online build toolchain.
/// These checks do not claim that the browser or operating system makes no background requests.
/// </summary>
public sealed class OfflineRuntimeContractTests
{
    private static readonly string ShellDirectory = Path.Combine(AppContext.BaseDirectory, "companion-web");
    private static readonly Uri LaptopOrigin = new("https://192.168.50.2:8443/");

    [Fact]
    public void ShippedPageStylesAndManifestUseOnlyBundledLaptopAssets()
    {
        var page = File.ReadAllText(Path.Combine(ShellDirectory, "index.html"));
        var references = Regex.Matches(page, "(?:src|href)\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value).ToList();
        Assert.NotEmpty(references);
        var css = File.ReadAllText(Path.Combine(ShellDirectory, "app.css"));
        Assert.DoesNotContain("@import", css, StringComparison.OrdinalIgnoreCase);
        references.AddRange(Regex.Matches(css, "url\\(\\s*[\"']?([^\\)\"']+)[\"']?\\s*\\)", RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value.Trim()));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(ShellDirectory, "manifest.webmanifest")));
        foreach (var field in new[] { "id", "start_url", "scope" })
            references.Add(manifest.RootElement.GetProperty(field).GetString()!);
        foreach (var icon in manifest.RootElement.GetProperty("icons").EnumerateArray())
            references.Add(icon.GetProperty("src").GetString()!);

        foreach (var reference in references)
        {
            var destination = new Uri(LaptopOrigin, reference);
            Assert.Equal(LaptopOrigin.GetLeftPart(UriPartial.Authority), destination.GetLeftPart(UriPartial.Authority));
            Assert.StartsWith("/companion/", destination.AbsolutePath, StringComparison.Ordinal);
            var fileName = destination.AbsolutePath["/companion/".Length..];
            var asset = Path.Combine(ShellDirectory, fileName.Length == 0 ? "index.html" : fileName);
            Assert.True(File.Exists(asset), $"The installed companion is missing {reference}.");
        }
    }

    [Fact]
    public void LocalTrustIsCreatedAndReusedWithoutOnlineCertificateEndpoints()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GoldenTicket.OfflineRuntimeTests"));
        var directory = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        try
        {
            var authority = new LocalCertificateAuthority(directory);
            var first = authority.EnsureMaterial(IPAddress.Parse("192.168.50.2"));
            using var leaf = first.ServerCertificate;
            using var root = X509CertificateLoader.LoadCertificate(first.AuthorityCertificateDer);
            // AIA (including OCSP), CRL and freshest-CRL distribution points would invite online
            // retrieval by platform certificate validators, so this private CA never supplies them.
            var onlineExtensionOids = new[] { "1.3.6.1.5.5.7.1.1", "2.5.29.31", "2.5.29.46" };
            foreach (var certificate in new[] { root, leaf })
                Assert.DoesNotContain(certificate.Extensions, extension => onlineExtensionOids.Contains(extension.Oid?.Value));
            Assert.EndsWith(".local", first.Hostname, StringComparison.Ordinal);
            Assert.Equal(new[] { first.Hostname, "localhost", "192.168.50.2", "127.0.0.1" }.Order(),
                LocalCertificateAuthority.ReadSubjectAlternativeNames(leaf).Order());
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            Assert.True(chain.Build(leaf));
            var second = authority.EnsureMaterial(IPAddress.Parse("192.168.50.2"));
            using var reused = second.ServerCertificate;
            Assert.Equal(first.AuthorityFingerprint, second.AuthorityFingerprint);
            Assert.Equal(leaf.Thumbprint, reused.Thumbprint);
        }
        finally
        {
            var resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }
}
