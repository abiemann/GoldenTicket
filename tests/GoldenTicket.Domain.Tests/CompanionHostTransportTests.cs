using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoldenTicket.Application;
using GoldenTicket.CompanionHost;
using GoldenTicket.CompanionHost.Networking;
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

public class CompanionHostTransportTests
{
    [Fact]
    public async Task RealHttpsTransportRequiresApprovalCsrfAndCurrentSeatGrantAndCommitsOnce()
    {
        var token = TestContext.Current.CancellationToken;
        var game = await GameCoordinator.CreateAsync(new GameRules(TestManifest.Manifest, TestManifest.Catalog), new InMemorySessionStore(),
            new SessionSetup(SessionId.New(), [new Seat(new(1), "Alex", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard),
                new Seat(new(2), "Second", PlayerColor.Red, SeatKind.Human, AiDifficulty.Standard)], new(1), VerificationMode.Manual), DeterministicRandom.SeedFrom(91), token);
        await using var server = new CompanionServer(new CoordinatorCompanionBridge(() => game));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Golden Ticket transport test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx, "test-only"), "test-only", X509KeyStorageFlags.Exportable);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.WebHost.ConfigureKestrel(k => { k.Limits.MaxRequestBodySize = 16384; k.Listen(IPAddress.Loopback, 0, o => o.UseHttps(certificate)); });
        await using var app = builder.Build();
        string[] origins = [];
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var status = CompanionServer.RequestAllowed(context, new LanInterface("test", "test", IPAddress.Parse("192.168.20.2"), 24, Guid.Empty), origins, () => true);
            if (status != 200) { context.Response.StatusCode = status; return; }
            await next(context);
        });
        server.MapApi(app); CompanionServer.MapShell(app);
        await app.StartAsync(token);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single(); origins = [address];
        using var handler = new HttpClientHandler { CookieContainer = new() };
        handler.ServerCertificateCustomValidationCallback = (_, presented, _, errors) =>
        {
            if (presented is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) return false;
            using var chain = new X509Chain(); chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(certificate); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            return chain.Build(presented);
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
        var tab = Guid.NewGuid().ToString("n"); client.DefaultRequestHeaders.Add("X-GoldenTicket-Tab", tab); client.DefaultRequestHeaders.Add("Origin", address);
        var shell = await client.GetAsync("/companion/", token); Assert.True(shell.IsSuccessStatusCode);
        Assert.Contains("Golden Ticket", await shell.Content.ReadAsStringAsync(token));
        foreach (var asset in new[] { "app.js", "app.css", "manifest.webmanifest", "sw.js", "icon.svg", "icon-192.png", "icon-512.png" })
            Assert.True((await client.GetAsync("/companion/" + asset, token)).IsSuccessStatusCode, asset);
        server.NewPairingCode();
        var pairing = await client.PostAsJsonAsync("/api/pair", new { code = server.Authority.PairingCode, tab, label = "Transport fixture" }, token);
        Assert.True(pairing.IsSuccessStatusCode);
        var cookie = string.Join(";", pairing.Headers.GetValues("Set-Cookie"));
        Assert.Contains("secure", cookie); Assert.Contains("httponly", cookie); Assert.Contains("samesite=strict", cookie);
        Assert.Contains("no-store", pairing.Headers.CacheControl!.ToString());
        var status = (await client.GetFromJsonAsync<JsonElement>("/api/session", token));
        Assert.False(status.GetProperty("paired").GetBoolean()); Assert.True(status.GetProperty("pending").GetBoolean());
        Assert.True(server.ApprovePendingController());
        status = await client.GetFromJsonAsync<JsonElement>("/api/session", token);
        Assert.Equal("2", status.GetProperty("assetsVersion").GetString());
        var csrf = status.GetProperty("csrf").GetString()!;
        var generation = status.GetProperty("handoffGeneration").GetInt64();
        var reveal = new { seat = 1, sessionId = game.SessionId.Value, version = game.Public.StateVersion, handoffGeneration = generation };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/reveal", reveal, token)).StatusCode);
        client.DefaultRequestHeaders.Add("X-GoldenTicket-CSRF", csrf);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/reveal", reveal with { seat = 2 }, token)).StatusCode);
        var response = await client.PostAsJsonAsync("/api/reveal", reveal, token); Assert.True(response.IsSuccessStatusCode);
        var own = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        var grant = own.GetProperty("grant").GetString()!;
        var offered = own.GetProperty("data").GetProperty("offeredTickets").EnumerateArray().Take(2).Select(t => t.GetProperty("id").GetString()).ToArray();
        var command = new { commandId = Guid.NewGuid().ToString("n"), sessionId = game.SessionId.Value, expectedStateVersion = game.Public.StateVersion, kind = "keepTickets", keptTickets = offered };
        var body = new { seat = 1, grant, command };
        var accepted = await client.PostAsJsonAsync("/api/command", body, token); Assert.True(accepted.IsSuccessStatusCode);
        Assert.True((await accepted.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("accepted").GetBoolean());
        var version = game.Public.StateVersion;
        var retry = await client.PostAsJsonAsync("/api/command", body, token); Assert.True(retry.IsSuccessStatusCode); Assert.Equal(version, game.Public.StateVersion);
        var publicState = await client.GetFromJsonAsync<JsonElement>("/api/session", token);
        Assert.Equal(2, publicState.GetProperty("snapshot").GetProperty("revealSeatId").GetInt32());
        Assert.DoesNotContain("offeredTickets", publicState.GetRawText());
        client.DefaultRequestHeaders.Remove("Origin"); client.DefaultRequestHeaders.Add("Origin", "https://attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/hide", new { }, token)).StatusCode);
        Assert.Empty(await game.CheckInvariantsAsync(token));
        await app.StopAsync(token);
    }
}
