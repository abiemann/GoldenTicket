using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoldenTicket.CompanionHost.Connect;
using GoldenTicket.CompanionHost.Networking;
using GoldenTicket.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GoldenTicket.CompanionHost;

public sealed record CompanionHostOptions(IPAddress Address, int Port = 8080);
public sealed record CompanionServerStatus(bool Running, string? Address, string? PairingCode,
    ControllerApproval? PendingApproval, string? ControllerLabel, string Message)
{
    public string? ConnectionAddress => Address;
}

/// <summary>Embedded, same-origin, selected-Private-LAN game controller with event-driven
/// public SSE updates. The phone never queues offline actions.</summary>
public sealed partial class CompanionServer(ICompanionGameBridge bridge, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    public const string ApiVersion = "1";
    private const string CookieName = "GoldenTicketQuickPlayController";
    private readonly ControllerAuthority _authority = new(timeProvider);
    internal ControllerAuthority Authority => _authority;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly object _rateLock = new();
    private DateTimeOffset _rateWindow = DateTimeOffset.UtcNow;
    private int _writes;
    private WebApplication? _app;
    private CompanionServerStatus _status = new(false, null, null, null, null, "Companion is off.");
    private readonly Dictionary<string, (string Body, CompanionCommandReceipt Receipt)> _receipts = [];
    public CompanionServerStatus Status => _status with { PairingCode = _status.Running ? _authority.PairingCode : null,
        PendingApproval = _authority.PendingApproval, ControllerLabel = _authority.ControllerLabel };
    public event EventHandler? StatusChanged;
    public static IReadOnlyList<LanInterface> GetAvailableInterfaces() => LanInterfaces.Discover();
    public void InvalidatePrivateGrants() { _authority.InvalidatePrivateGrants(); NotifyGameChanged(); }
    public void NewPairingCode() { _authority.NewCode(); Notify(); }
    public bool ApprovePendingController() { var approved = _authority.Approve(); Notify(); return approved; }
    public void RevokeController() { _authority.Revoke(); Notify(); }
    public string CreateConnectionQrSvg() => CreateConnectionQrSvg(_status);
    internal static string CreateConnectionQrSvg(CompanionServerStatus status) => status.ConnectionAddress is { } address
        ? QrRenderer.ToSvg(QrCode.Encode(address), "Connect to Golden Ticket") : "";
    private void Notify() { NotifyGameChanged(); StatusChanged?.Invoke(this, EventArgs.Empty); }

    public Task StartAsync(CompanionHostOptions options, CancellationToken cancellationToken = default) =>
        StartOnInterfaceAsync(options, null, null, cancellationToken);

    // Allows transport tests to exercise actual startup and shutdown without depending on a Windows adapter profile.
    internal async Task StartOnInterfaceAsync(CompanionHostOptions options, LanInterface? selected,
        Func<bool>? networkPrivate, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_app is not null) return;
            if (options.Port is < 1024 or > 65535)
                throw new ArgumentException("Choose an unprivileged port between 1024 and 65535.");
            selected ??= GetAvailableInterfaces().FirstOrDefault(i => i.Address.Equals(options.Address))
                ?? throw new InvalidOperationException("Choose an active Windows Private LAN connection. Public or unknown profiles are blocked.");
            networkPrivate ??= () => WindowsNetworkProfiles.ReadPrivateAdapters().Contains(selected.AdapterId);
            if (!selected.Address.Equals(options.Address) || !networkPrivate())
                throw new InvalidOperationException("Choose an active Windows Private LAN connection. Public or unknown profiles are blocked.");
            var origins = new[] { $"http://{selected.Address}:{options.Port}", $"http://localhost:{options.Port}" };
            _streamAllowed = context => RequestAllowed(context, selected, origins, networkPrivate) == 200;
            var app = CreateApplication();
            ConfigureKestrel(app, selected.Address, options.Port);
            var host = app.Build();
            _app = host;
            host.Use(async (context, next) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; connect-src 'self'; worker-src 'none'; manifest-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
                var policy = RequestAllowed(context, selected, origins, networkPrivate);
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
            _authority.NewCode();
            _status = CreateReadyStatus(selected.Address, options.Port, _authority.PairingCode);
            Notify();
        }
        catch { await StopCoreAsync(); throw; }
        finally { _lifecycle.Release(); }
    }

    internal static CompanionServerStatus CreateReadyStatus(IPAddress address, int httpPort, string pairingCode)
    {
        return new(true, CompanionAddresses.Game(address, httpPort), pairingCode, null, null,
            "Quick play is ready. Scan the QR code, join, and play. No certificate or installation needed.");
    }

    private static WebApplicationBuilder CreateApplication()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, Args = [] });
        builder.Logging.ClearProviders();
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        return builder;
    }
    private static void ConfigureKestrel(WebApplicationBuilder builder, IPAddress address, int port)
    {
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 16384;
            k.Limits.MaxConcurrentConnections = 32;
            k.Limits.Http2.MaxStreamsPerConnection = 16;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(20);
            k.Listen(address, port);
            if (!address.Equals(IPAddress.Loopback)) k.Listen(IPAddress.Loopback, port);
        });
    }
    internal static int RequestAllowed(HttpContext context, LanInterface selected, IReadOnlyList<string> origins,
        Func<bool> isPrivate)
    {
        var peer = context.Connection.RemoteIpAddress;
        if (!isPrivate() || peer is null || (!IPAddress.IsLoopback(peer) && !LanInterfaces.IsInSubnet(peer, selected.Address, selected.PrefixLength))) return 403;
        if (!origins.Any(o => string.Equals(new Uri(o).Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase))) return 421;
        if (context.Request.ContentLength > 16384) return 413;
        if (context.Request.Path.StartsWithSegments("/api"))
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
            ["icon.svg"] = "image/svg+xml" };
        foreach (var (asset, contentType) in assets)
        {
            var name = asset.Length == 0 ? "index.html" : asset;
            app.MapGet("/companion/" + asset, () => Results.File(Path.Combine(AppContext.BaseDirectory, "companion-web", name), contentType));
        }
    }
    private bool Csrf(HttpContext context, ControllerCredentials credentials) =>
        _authority.ValidateCsrf(credentials, context.Request.Headers["X-GoldenTicket-CSRF"]);

    internal void MapApi(WebApplication app)
    {
        app.MapGet("/api/events", StreamEventsAsync);
        ControllerCredentials? Credentials(HttpContext context, bool heartbeat = false) =>
            _authority.Authenticate(context.Request.Cookies[CookieName], context.Request.Headers["X-GoldenTicket-Tab"], heartbeat);
        app.MapPost("/api/pair", async (HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync<PairRequest>(context.RequestAborted);
            if (request is null || request.Code is null || request.Tab is null || request.Label is null) return Results.BadRequest();
            var pending = _authority.RequestPair(request.Code, request.Tab, request.Label);
            if (pending is null) return Results.Json(new { message = "Could not join. Check the pairing code shown on the laptop and try again." }, statusCode: 403);
            context.Response.Cookies.Append(CookieName, pending.Value.Session, new CookieOptions { HttpOnly = true, Secure = false, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromHours(12), IsEssential = true });
            Notify();
            return Results.Ok(new { pending = true, identity = pending.Value.Approval.Identity, message = "Confirm this identity on the laptop." });
        });
        app.MapGet("/api/session", async (HttpContext context) =>
        {
            // Retained for diagnostics/compatibility; the shipped browser uses /api/events.
            return Results.Ok(await ReadSessionAsync(context, context.RequestAborted));
        });
        app.MapGet("/api/result-image/{id}", async (HttpContext context, string id) =>
        {
            var credentials = Credentials(context);
            if (credentials is null) return Results.Unauthorized();
            if (!Guid.TryParseExact(id, "N", out _)) return Results.NotFound();
            var image = await bridge.ReadResultImageAsync(id, context.RequestAborted);
            // A controller can be revoked or replaced while the desktop dispatcher is busy.
            if (Credentials(context) != credentials) return Results.Unauthorized();
            if (image is null || !CompanionResultImage.IsValid(image) || image.Info.Id != id) return Results.NotFound();
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(image.Png, "image/png", image.Info.FileName, enableRangeProcessing: false);
        });
        app.MapPost("/api/hide", (HttpContext context) =>
        {
            var credentials = Credentials(context);
            if (credentials is null || !Csrf(context, credentials)) return Results.Unauthorized();
            InvalidatePrivateGrants();
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
                NotifyGameChanged();
                return Results.Ok(new { grant = grant.Token, handoffGeneration = grant.Generation, data = privateSnapshot });
            }
            finally { _requests.Release(); }
        });
        app.MapPost("/api/command", async (HttpContext context) =>
        {
            if (!await _requests.WaitAsync(0, context.RequestAborted)) return Results.StatusCode(429);
            try
            {
                // A current command is also proof that the controller is connected. This
                // gives the desktop's bounded camera check a full heartbeat interval;
                // Authenticate still invalidates any grant after a prior connection gap.
                var credentials = Credentials(context, heartbeat: true);
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
                var before = await bridge.ReadPublicAsync(cancel.Token);
                var receipt = await bridge.ExecuteAsync(new SeatId(request.Seat), command, cancel.Token);
                var continuation = receipt.Accepted
                    ? await ContinuePrivateViewAsync(credentials, request, before, receipt.StateVersion, context.RequestAborted)
                    : null;
                if (continuation is null) _authority.InvalidatePrivateGrants();
                if (_receipts.Count >= 64) _receipts.Remove(_receipts.Keys.First());
                // Retries may acknowledge a saved command, but never replay a private view.
                _receipts[key] = (body, receipt);
                NotifyGameChanged();
                return Results.Ok(receipt with { Continuation = continuation });
            }
            finally { _requests.Release(); }
        });
    }
    private sealed record PairRequest(string Code, string Tab, string Label);
    private sealed record RevealRequest(int Seat, string SessionId, long Version, long HandoffGeneration);
    private sealed record ActionRequest(int Seat, string Grant, CompanionCommand Command);

    private async Task<CompanionPrivateContinuation?> ContinuePrivateViewAsync(ControllerCredentials credentials,
        ActionRequest request, CompanionPublicSnapshot before, long nextVersion, CancellationToken cancellationToken)
    {
        var command = request.Command;
        bool SameTurn(CompanionPublicSnapshot snapshot) => before.Game is { } previous &&
            before.CanControl && before.RevealSeatId == request.Seat &&
            previous.SessionId.Value == command.SessionId && previous.StateVersion == command.ExpectedStateVersion &&
            previous.Lifecycle == SessionLifecycle.Active && previous.ActiveSeatId.Value == request.Seat &&
            snapshot.Game is { Lifecycle: SessionLifecycle.Active } current &&
            snapshot.CanControl && snapshot.RevealSeatId == request.Seat &&
            current.SessionId == previous.SessionId && current.TurnNumber == previous.TurnNumber &&
            current.ActiveSeatId == previous.ActiveSeatId && current.StateVersion == nextVersion &&
            current.SeatOf(current.ActiveSeatId).Kind == SeatKind.Human;

        var after = await bridge.ReadPublicAsync(cancellationToken);
        if (!SameTurn(after)) return null;
        var data = await bridge.ReadPrivateAsync(new SeatId(request.Seat), nextVersion, cancellationToken);
        if (data is null || data.View.SeatId.Value != request.Seat ||
            data.View.Public.SessionId != after.Game!.SessionId || data.View.Public.StateVersion != nextVersion ||
            data.View.Public.TurnNumber != after.Game.TurnNumber ||
            data.View.Public.ActiveSeatId != after.Game.ActiveSeatId) return null;
        // A hide, pause, turn change or load may race either awaited read.
        after = await bridge.ReadPublicAsync(cancellationToken);
        if (!SameTurn(after)) return null;
        var grant = _authority.ContinueGrant(credentials, request.Grant, request.Seat, command.SessionId,
            command.ExpectedStateVersion, nextVersion);
        return grant is null ? null : new(grant.Token, grant.Generation, after, data);
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
        _events.StopAll();
        if (_app is { } app) { _app = null; await ShutdownAsync(app); }
        _receipts.Clear();
        _status = new(false, null, null, null, null, "Companion is off."); Notify();
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
