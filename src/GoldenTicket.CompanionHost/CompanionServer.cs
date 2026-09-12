using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoldenTicket.CompanionHost.Connect;
using GoldenTicket.CompanionHost.Networking;
using GoldenTicket.CompanionHost.Security;
using GoldenTicket.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GoldenTicket.CompanionHost;

public sealed record CompanionHostOptions(IPAddress Address, int HttpsPort = 8443, int BootstrapPort = 8080,
    string? CertificateDirectory = null, bool EnableBootstrap = true);
public sealed record CompanionServerStatus(bool Running, string? Address, string? PairingCode,
    string? CertificateFingerprint, string? PublicCertificatePath, string? BootstrapAddress,
    ControllerApproval? PendingApproval, string? ControllerLabel, string Message,
    string? HostnameAddress = null, string? IpAddress = null);

/// <summary>Embedded, same-origin, selected-Private-LAN game controller. Shell updates use bounded
/// two-second public snapshot polling in this milestone; the phone never queues offline actions.</summary>
public sealed class CompanionServer(ICompanionGameBridge bridge) : IAsyncDisposable
{
    public const string ApiVersion = "1";
    private const string CookieName = "__Host-GoldenTicketController";
    private readonly ControllerAuthority _authority = new();
    internal ControllerAuthority Authority => _authority;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly object _rateLock = new();
    private DateTimeOffset _rateWindow = DateTimeOffset.UtcNow;
    private int _writes;
    private WebApplication? _app;
    private WebApplication? _bootstrap;
    private MulticastDnsResponder? _mdns;
    private X509Certificate2? _serverCertificate;
    private CompanionServerStatus _status = new(false, null, null, null, null, null, null, null, "Companion is off.");
    private readonly Dictionary<string, (string Body, CompanionCommandReceipt Receipt)> _receipts = [];
    public CompanionServerStatus Status => _status with { PairingCode = _status.Running ? _authority.PairingCode : null,
        PendingApproval = _authority.PendingApproval, ControllerLabel = _authority.ControllerLabel };
    public event EventHandler? StatusChanged;
    public static IReadOnlyList<LanInterface> GetAvailableInterfaces() => LanInterfaces.Discover();
    public void InvalidatePrivateGrants() => _authority.InvalidatePrivateGrants();
    public void NewPairingCode() { _authority.NewCode(); Notify(); }
    public bool ApprovePendingController() { var approved = _authority.Approve(); Notify(); return approved; }
    public void RevokeController() { _authority.Revoke(); Notify(); }
    public string CreateConnectionQrSvg() => (_status.BootstrapAddress ?? _status.Address) is { } address
        ? QrRenderer.ToSvg(QrCode.Encode(address), "Connect to Golden Ticket") : "";
    private void Notify() => StatusChanged?.Invoke(this, EventArgs.Empty);

    public async Task StartAsync(CompanionHostOptions options, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_app is not null) return;
            if (options.HttpsPort is < 1024 or > 65535 || options.BootstrapPort is < 1024 or > 65535 ||
                options.HttpsPort == options.BootstrapPort) throw new ArgumentException("Choose different unprivileged ports.");
            var selected = GetAvailableInterfaces().FirstOrDefault(i => i.Address.Equals(options.Address))
                ?? throw new InvalidOperationException("Choose an active Windows Private LAN connection. Public or unknown profiles are blocked.");
            bool NetworkPrivate() => WindowsNetworkProfiles.ReadPrivateAdapters().Contains(selected.AdapterId);
            var directory = options.CertificateDirectory ?? LocalCertificateAuthority.DefaultDirectory;
            var material = new LocalCertificateAuthority(directory).EnsureMaterial(options.Address);
            _serverCertificate = material.ServerCertificate;
            var origins = new[] { $"https://{selected.Address}:{options.HttpsPort}", $"https://{material.Hostname}:{options.HttpsPort}", $"https://localhost:{options.HttpsPort}" };
            var app = CreateApplication();
            ConfigureKestrel(app, selected.Address, options.HttpsPort, material.ServerCertificate);
            var host = app.Build();
            _app = host;
            host.Use(async (context, next) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; worker-src 'self'; manifest-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
                var policy = RequestAllowed(context, selected, origins, NetworkPrivate);
                if (policy != 200) { context.Response.StatusCode = policy; return; }
                if (HttpMethods.IsPost(context.Request.Method) && !AllowWrite()) { context.Response.StatusCode = 429; return; }
                try { await next(context); }
                catch (OperationCanceledException) { if (!context.Response.HasStarted) context.Response.StatusCode = 409; }
                catch (Exception ex) when (ex is JsonException or BadHttpRequestException or ArgumentException)
                { if (!context.Response.HasStarted) context.Response.StatusCode = 400; }
                catch (Exception)
                {
                    // Private choices and exception objects never become HTTP/log output.
                    _authority.InvalidatePrivateGrants();
                    if (!context.Response.HasStarted) { context.Response.StatusCode = 503; await context.Response.WriteAsync("The laptop could not complete this request. Check it before continuing."); }
                }
            });
            MapShell(host);
            MapApi(host);
            await host.StartAsync(cancellationToken);
            _mdns = new(material.Hostname, selected.Address, selected.PrefixLength, NetworkPrivate);
            var mdnsStarted = _mdns.TryStart();
            _authority.NewCode();
            _status = new(true, origins[mdnsStarted ? 1 : 0] + "/companion/", _authority.PairingCode,
                material.AuthorityFingerprint, Path.Combine(directory, "authority.crt"), null, null, null,
                "Companion ready on this Private LAN. If the local name cannot resolve, use the IP fallback; that is a separate browser origin.",
                origins[1] + "/companion/", origins[0] + "/companion/");
            if (options.EnableBootstrap)
            {
                var builder = CreateApplication();
                ConfigureKestrel(builder, selected.Address, options.BootstrapPort, null);
                _bootstrap = builder.Build();
                var bootstrapOrigins = new[] { $"http://{selected.Address}:{options.BootstrapPort}", $"http://{material.Hostname}:{options.BootstrapPort}", $"http://localhost:{options.BootstrapPort}" };
                _bootstrap.Use(async (context, next) =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    context.Response.Headers["Referrer-Policy"] = "no-referrer";
                    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
                    var status = RequestAllowed(context, selected, bootstrapOrigins, NetworkPrivate, bootstrap: true);
                    if (status != 200 || !HttpMethods.IsGet(context.Request.Method)) { context.Response.StatusCode = status == 200 ? 405 : status; return; }
                    await next(context);
                });
                _bootstrap.MapGet("/GoldenTicket-laptop-CA.crt", () => Results.File(material.AuthorityCertificateDer, "application/x-x509-ca-cert", "GoldenTicket-laptop-CA.crt"));
                _bootstrap.MapGet("/", () => Results.Content("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>Golden Ticket certificate setup</title><h1>Trust this laptop</h1><p>This page transfers only the public certificate. Compare its SHA-256 fingerprint against the laptop display before installing it. Never transfer a private key.</p><p><a href=\"/GoldenTicket-laptop-CA.crt\">Download the public laptop CA certificate</a></p><p>Android: Settings → Security and privacy → More security and privacy → Encryption and credentials → Install a certificate → CA certificate. Authenticate and choose the downloaded file. Labels vary by version.</p><p>If Chrome warns about this public certificate download over HTTP, use USB file transfer instead. Copy only the public .crt file shown on the laptop and verify its fingerprint. This is distinct from bypassing an HTTPS browser error, which is never an accepted setup step.</p><p>iPhone/iPad: install the downloaded profile in Settings, then enable its full trust in General → About → Certificate Trust Settings.</p><p>Remove the dedicated Golden Ticket certificate in the same settings when you stop using this laptop. Never bypass an HTTPS browser warning.</p><p>After trust, open the HTTPS address shown on the laptop. Close certificate sharing on the laptop when finished.</p></html>", "text/html"));
                await _bootstrap.StartAsync(cancellationToken);
                _status = _status with { BootstrapAddress = bootstrapOrigins[0] + "/" };
            }
            Notify();
        }
        catch { await StopCoreAsync(); throw; }
        finally { _lifecycle.Release(); }
    }

    private static WebApplicationBuilder CreateApplication()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, Args = [] });
        builder.Logging.ClearProviders();
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        return builder;
    }
    private static void ConfigureKestrel(WebApplicationBuilder builder, IPAddress address, int port, X509Certificate2? certificate)
    {
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 16384;
            k.Limits.MaxConcurrentConnections = 32;
            k.Limits.Http2.MaxStreamsPerConnection = 16;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(20);
            void Listen(IPAddress ip) => k.Listen(ip, port, o => { if (certificate is not null) o.UseHttps(certificate); });
            Listen(address); Listen(IPAddress.Loopback);
        });
    }
    internal static int RequestAllowed(HttpContext context, LanInterface selected, IReadOnlyList<string> origins,
        Func<bool> isPrivate, bool bootstrap = false)
    {
        var peer = context.Connection.RemoteIpAddress;
        if (!isPrivate() || peer is null || (!IPAddress.IsLoopback(peer) && !LanInterfaces.IsInSubnet(peer, selected.Address, selected.PrefixLength))) return 403;
        if (!origins.Any(o => string.Equals(new Uri(o).Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase))) return 421;
        if (context.Request.ContentLength > 16384) return 413;
        if (!bootstrap && context.Request.Path.StartsWithSegments("/api"))
        {
            var origin = context.Request.Headers.Origin.ToString();
            if ((!string.IsNullOrEmpty(origin) && !origins.Contains(origin, StringComparer.OrdinalIgnoreCase)) ||
                (HttpMethods.IsPost(context.Request.Method) && string.IsNullOrEmpty(origin))) return 403;
            if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site") return 403;
        }
        return 200;
    }
    private bool AllowWrite()
    {
        lock (_rateLock)
        {
            if (DateTimeOffset.UtcNow - _rateWindow >= TimeSpan.FromMinutes(1)) { _rateWindow = DateTimeOffset.UtcNow; _writes = 0; }
            return ++_writes <= 100;
        }
    }
    internal static void MapShell(WebApplication app)
    {
        app.MapGet("/", () => Results.Redirect("/companion/"));
        var assets = new Dictionary<string, string> { [""] = "text/html", ["app.js"] = "text/javascript", ["app.css"] = "text/css",
            ["sw.js"] = "text/javascript", ["manifest.webmanifest"] = "application/manifest+json", ["icon.svg"] = "image/svg+xml",
            ["icon-192.png"] = "image/png", ["icon-512.png"] = "image/png" };
        foreach (var (asset, contentType) in assets)
        {
            var name = asset.Length == 0 ? "index.html" : asset;
            app.MapGet("/companion/" + asset, (HttpContext context) =>
            {
                if (asset == "sw.js") context.Response.Headers["Service-Worker-Allowed"] = "/companion/";
                return Results.File(Path.Combine(AppContext.BaseDirectory, "companion-web", name), contentType);
            });
        }
    }
    private ControllerCredentials? Credentials(HttpContext context, bool heartbeat = false) =>
        _authority.Authenticate(context.Request.Cookies[CookieName], context.Request.Headers["X-GoldenTicket-Tab"], heartbeat);
    private bool Csrf(HttpContext context, ControllerCredentials credentials) =>
        _authority.ValidateCsrf(credentials, context.Request.Headers["X-GoldenTicket-CSRF"]);

    internal void MapApi(WebApplication app)
    {
        app.MapPost("/api/pair", async (HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync<PairRequest>(context.RequestAborted);
            if (request is null || request.Code is null || request.Tab is null || request.Label is null) return Results.BadRequest();
            var pending = _authority.RequestPair(request.Code, request.Tab, request.Label);
            if (pending is null) return Results.Json(new { message = "Code expired or incorrect. Request a new code on the laptop." }, statusCode: 403);
            context.Response.Cookies.Append(CookieName, pending.Value.Session, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromHours(12), IsEssential = true });
            Notify();
            return Results.Ok(new { pending = true, identity = pending.Value.Approval.Identity, message = "Confirm this identity on the laptop." });
        });
        app.MapGet("/api/session", async (HttpContext context) =>
        {
            var credentials = Credentials(context, heartbeat: true);
            if (credentials is null)
                return Results.Json(new { pending = _authority.IsPending(context.Request.Cookies[CookieName], context.Request.Headers["X-GoldenTicket-Tab"]), paired = false }, statusCode: 200);
            var snapshot = await bridge.ReadPublicAsync(context.RequestAborted);
            if (!snapshot.CanControl) _authority.InvalidatePrivateGrants();
            if (Credentials(context) != credentials) return Results.Unauthorized();
            return Results.Ok(new { paired = true, csrf = credentials.Csrf, controllerGeneration = credentials.Generation,
                handoffGeneration = _authority.Generation, apiVersion = ApiVersion, assetsVersion = "2", snapshot });
        });
        app.MapPost("/api/hide", (HttpContext context) =>
        {
            var credentials = Credentials(context);
            if (credentials is null || !Csrf(context, credentials)) return Results.Unauthorized();
            _authority.InvalidatePrivateGrants();
            return Results.Ok(new { hidden = true });
        });
        app.MapPost("/api/reveal", async (HttpContext context) =>
        {
            if (!await _requests.WaitAsync(0, context.RequestAborted)) return Results.StatusCode(429);
            try
            {
                var credentials = Credentials(context);
                if (credentials is null || !Csrf(context, credentials)) return Results.Unauthorized();
                var request = await context.Request.ReadFromJsonAsync<RevealRequest>(context.RequestAborted);
                if (request is null) return Results.BadRequest();
                var snapshot = await bridge.ReadPublicAsync(context.RequestAborted);
                if (!snapshot.CanControl || snapshot.RevealSeatId != request.Seat || snapshot.Game?.StateVersion != request.Version || snapshot.Game.SessionId.Value != request.SessionId) return Results.Conflict();
                var grant = _authority.Reveal(credentials, request.Seat, request.SessionId, request.Version, request.HandoffGeneration);
                if (grant is null) return Results.Unauthorized();
                var privateSnapshot = await bridge.ReadPrivateAsync(new SeatId(request.Seat), request.Version, context.RequestAborted);
                if (privateSnapshot is null || !_authority.ValidateGrant(credentials, grant.Token, request.Seat, request.SessionId, request.Version)) return Results.Conflict();
                return Results.Ok(new { grant = grant.Token, expiresAt = grant.ExpiresAt, handoffGeneration = grant.Generation, data = privateSnapshot });
            }
            finally { _requests.Release(); }
        });
        app.MapPost("/api/command", async (HttpContext context) =>
        {
            if (!await _requests.WaitAsync(0, context.RequestAborted)) return Results.StatusCode(429);
            try
            {
                var credentials = Credentials(context);
                if (credentials is null || !Csrf(context, credentials)) return Results.Unauthorized();
                var request = await context.Request.ReadFromJsonAsync<ActionRequest>(context.RequestAborted);
                if (request?.Command is null) return Results.BadRequest();
                var command = request.Command;
                var key = credentials.Session + ":" + command.CommandId;
                var body = JsonSerializer.Serialize(command);
                if (_receipts.TryGetValue(key, out var saved)) return saved.Body == body ? Results.Ok(saved.Receipt) : Results.Conflict();
                var authorization = _authority.AuthorizeCommand(credentials, request.Grant, request.Seat, command.SessionId, command.ExpectedStateVersion);
                if (authorization is null) return Results.Unauthorized();
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, authorization.Value.Cancellation);
                cancel.CancelAfter(authorization.Value.ValidFor);
                var receipt = await bridge.ExecuteAsync(new SeatId(request.Seat), command, cancel.Token);
                _authority.InvalidatePrivateGrants();
                if (_receipts.Count >= 64) _receipts.Remove(_receipts.Keys.First());
                _receipts[key] = (body, receipt);
                return Results.Ok(receipt);
            }
            finally { _requests.Release(); }
        });
    }
    private sealed record PairRequest(string Code, string Tab, string Label);
    private sealed record RevealRequest(int Seat, string SessionId, long Version, long HandoffGeneration);
    private sealed record ActionRequest(int Seat, string Grant, CompanionCommand Command);

    public async Task CloseBootstrapAsync()
    {
        var app = Interlocked.Exchange(ref _bootstrap, null);
        if (app is not null) await ShutdownAsync(app);
        _status = _status with { BootstrapAddress = null }; Notify();
    }
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try { await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
    }
    private async Task StopCoreAsync()
    {
        _authority.Revoke();
        if (_app is { } app) { _app = null; await ShutdownAsync(app); }
        await CloseBootstrapAsync();
        if (_mdns is not null) { await _mdns.DisposeAsync(); _mdns = null; }
        _serverCertificate?.Dispose(); _serverCertificate = null;
        _receipts.Clear();
        _status = new(false, null, null, null, null, null, null, null, "Companion is off."); Notify();
    }
    public async ValueTask DisposeAsync() => await StopAsync();
    private static async Task ShutdownAsync(WebApplication app)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await app.StopAsync(timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        await app.DisposeAsync();
    }
}
