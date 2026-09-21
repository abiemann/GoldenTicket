using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using GoldenTicket.CompanionHost;
using GoldenTicket.CompanionHost.Connect;
using GoldenTicket.CompanionHost.Networking;

namespace GoldenTicket.Domain.Tests;

public sealed class CompanionLifecycleTests
{
    [Fact]
    public void ConnectionQrPointsStraightToTheBrowserGame()
    {
        var options = new CompanionHostOptions(IPAddress.Parse("192.168.20.2"));
        var status = CompanionServer.CreateReadyStatus(options.Address, options.Port, "123456");
        const string expected = "http://192.168.20.2:8080/companion/";
        Assert.Equal(expected, status.Address);
        Assert.Equal(expected, status.ConnectionAddress);
        Assert.Equal(QrRenderer.ToSvg(QrCode.Encode(expected), "Connect to Golden Ticket"), CompanionServer.CreateConnectionQrSvg(status));
    }

    [Fact]
    public async Task HttpHostServesTheGameRejectsObsoleteSetupPathsAndRestartsWithoutRetainingControllerApproval()
    {
        var token = TestContext.Current.CancellationToken;
        var port = ReserveAvailablePort();
        var options = new CompanionHostOptions(IPAddress.Loopback, port);
        var selected = new LanInterface("fixture", "fixture", IPAddress.Loopback, 8, Guid.Empty);
        await using var server = new CompanionServer(new CoordinatorCompanionBridge(() => null));
        var privateNetwork = true;
        await server.StartOnInterfaceAsync(options, selected, () => privateNetwork, token);
        Assert.True(server.Status.Running);
        Assert.Equal($"http://127.0.0.1:{port}/companion/", server.Status.Address);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(server.Status.Address!), Timeout = TimeSpan.FromSeconds(10) };
        using var root = await client.GetAsync("/", token);
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Equal("/companion/", root.Headers.Location!.OriginalString);
        using var shell = await client.GetAsync("/companion/", token);
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.Contains("Golden Ticket", await shell.Content.ReadAsStringAsync(token));
        foreach (var obsoletePath in new[] { "/GoldenTicket-laptop-CA.crt", "/GoldenTicket-laptop.mobileconfig", "/setup.js", "/setup.css", "/companion/sw.js", "/companion/manifest.webmanifest" })
        {
            using var response = await client.GetAsync(obsoletePath, token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        var pairing = server.Authority.RequestPair(server.Authority.PairingCode, new string('a', 32), "Fixture")!.Value;
        Assert.True(server.ApprovePendingController());
        Assert.NotNull(server.Authority.Authenticate(pairing.Session, new string('a', 32)));
        privateNetwork = false;
        using var deniedNetwork = await client.GetAsync("/api/session", token);
        Assert.Equal(HttpStatusCode.Forbidden, deniedNetwork.StatusCode);
        privateNetwork = true;
        await server.StopAsync(token);
        Assert.False(server.Status.Running);
        Assert.Null(server.Status.ConnectionAddress);
        Assert.Null(server.Authority.Authenticate(pairing.Session, new string('a', 32)));
        await server.StartOnInterfaceAsync(options, selected, () => privateNetwork, token);
        using var restarted = await client.GetAsync("/api/session", token);
        Assert.Equal(HttpStatusCode.OK, restarted.StatusCode);
        Assert.Null(server.Authority.Authenticate(pairing.Session, new string('a', 32)));
    }

    private static int ReserveAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
