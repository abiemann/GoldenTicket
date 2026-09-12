using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GoldenTicket.ConnectivitySpike;
using GoldenTicket.ConnectivitySpike.Connect;
using GoldenTicket.ConnectivitySpike.Networking;
using GoldenTicket.ConnectivitySpike.Pairing;
using GoldenTicket.ConnectivitySpike.Security;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

// DESIGN 23 M0 connectivity spike. It answers four questions on real devices, per DESIGN 22.7:
//   1. Does a per-installation local CA install and produce a trusted, secure-context origin?
//   2. Does the .local name resolve, or is the IP fallback needed?
//   3. Does the service worker register and cache the shell with the WAN disconnected?
//   4. Does a pairing round-trip work inside the finally launched context?
// It holds no game state, shows no private information and issues no game commands.

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The spike protects its private keys with Windows DPAPI and needs Windows.");
    return 2;
}

var options = SpikeOptions.Parse(args);
if (options.ShowHelp)
{
    SpikeOptions.PrintUsage();
    return 0;
}
if (options.HttpsPort is < 1 or > 65535 || options.BootstrapPort is < 1 or > 65535 ||
    options.HttpsPort == options.BootstrapPort)
{
    Console.Error.WriteLine("Choose distinct HTTPS and bootstrap ports between 1 and 65535.");
    return 2;
}

// The connection QR is drawn with block characters, which need a UTF-8 console.
try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // No console attached; the SVG file and the typed address still work.
}

// A way to see whether this console can render a scannable symbol at all, before a borrowed device
// is sitting in front of it. It touches no network.
if (options.QrProbe is not null)
{
    PrintConnectionQr(options.QrProbe, Path.Combine(LocalCertificateAuthority.DefaultDirectory, "connect"));
    return 0;
}

// ---- Choose the interface to serve on (DESIGN 18.5: private LAN only) ------------------------

var candidates = LanInterfaces.Discover();
if (candidates.Count == 0)
{
    Console.Error.WriteLine(
        "No eligible Windows Private network was found. Use trusted Wi-Fi marked Private in Windows Settings; public or unknown profiles are refused.");
    return 2;
}

var selected = SelectInterface(candidates, options.PreferredAddress);
if (selected is null) return 2;

// ---- Trust material ----------------------------------------------------------------------------

var authority = new LocalCertificateAuthority(LocalCertificateAuthority.DefaultDirectory);
LocalTrustMaterial material;

try
{
    material = authority.EnsureMaterial(selected.Address);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"The local certificate could not be prepared: {exception.Message}");
    return 2;
}

var pairing = new PairingService();
var observations = new BoundedObservationBuffer<DeviceObservation>(128);
using var serverCertificate = material.ServerCertificate;
var startedAt = DateTimeOffset.Now;
var runId = Guid.NewGuid().ToString("n")[..8];

var origins = new[]
{
    $"https://{material.Hostname}:{options.HttpsPort}",
    $"https://{selected.Address}:{options.HttpsPort}",
    $"https://localhost:{options.HttpsPort}",
};
bool NetworkIsPrivate() => WindowsNetworkProfiles.ReadPrivateAdapters().Contains(selected.AdapterId);
var requestPolicy = new SpikeRequestPolicy(selected.Address, selected.PrefixLength, origins, NetworkIsPrivate);

// ---- Name advertisement --------------------------------------------------------------------------

await using var responder = new MulticastDnsResponder(material.Hostname, selected.Address,
    selected.PrefixLength, NetworkIsPrivate);
var mdnsStarted = !options.DisableMulticastDns && responder.TryStart();

// ---- The temporary HTTP bootstrap (DESIGN 18.5) ---------------------------------------------------
// It serves only the public CA certificate and static instructions, carries no credentials, and is
// closed as soon as the certificate has been transferred.

var bootstrapOpen = true;
await using var bootstrap = BuildBootstrap(options, material, selected, NetworkIsPrivate);
await bootstrap.StartAsync();

// ---- The trusted HTTPS host ------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = SpikeRequestPolicy.MaximumRequestBytes;
    kestrel.Limits.MaxConcurrentConnections = 64;
    kestrel.Limits.Http2.MaxStreamsPerConnection = 32;

    void Listen(IPAddress address) => kestrel.Listen(address, options.HttpsPort, listen =>
        listen.UseHttps(material.ServerCertificate));

    Listen(selected.Address);
    Listen(IPAddress.Loopback);
});

builder.Services.Configure<JsonOptions>(json =>
    json.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddRateLimiter(limits =>
{
    limits.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limits.AddFixedWindowLimiter("writes", limit =>
    {
        limit.PermitLimit = 60;
        limit.Window = TimeSpan.FromMinutes(1);
        limit.QueueLimit = 0;
        limit.AutoReplenishment = true;
    });
});

await using var app = builder.Build();

// DESIGN 18.5: validate Host and Origin against the current allowlist, and never let an API
// response be cached.
app.Use(async (context, next) =>
{
    var status = requestPolicy.Validate(context);
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
    if (status != StatusCodes.Status200OK)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsync("This request is not allowed on the selected private network and origin.");
        return;
    }

    if (!context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
            "connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    await next();
});

app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

HostFacts CurrentHostFacts() => new(
    Environment.OSVersion.VersionString,
    Environment.Version.ToString(),
    material.Hostname,
    selected.Address.ToString(),
    options.HttpsPort,
    material.AuthorityFingerprint,
    material.ServerCertificateExpiry,
    LocalCertificateAuthority.ReadSubjectAlternativeNames(material.ServerCertificate),
    mdnsStarted,
    responder.Problem,
    responder.AnsweredQueries,
    bootstrapOpen);

app.MapGet("/api/spike/host", () => Results.Ok(new
{
    hostname = material.Hostname,
    address = selected.Address.ToString(),
    port = options.HttpsPort,
    certificateExpiry = material.ServerCertificateExpiry,
    multicastDns = mdnsStarted,
    pairingCodeActive = pairing.CurrentCode is not null,
}));

app.MapGet("/api/spike/session", (HttpContext context) =>
{
    var device = pairing.Find(context.Request.Cookies["gt_spike_device"]);
    return Results.Ok(new { paired = device is not null, label = device?.Label, pairedAt = device?.PairedAt });
});

app.MapPost("/api/spike/pair", async (HttpContext context, PairRequest request) =>
{
    var result = pairing.Redeem(request.Code, request.Label ?? "");

    if (result.Outcome != PairingOutcome.Paired)
        return Results.Ok(new { paired = false, message = result.Message });

    context.Response.Cookies.Append("gt_spike_device", result.Device!.DeviceSessionId, new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        MaxAge = PairingService.SessionLifetime,
        Path = "/",
    });

    Console.WriteLine($"  paired: {result.Device.Label}");
    await Task.CompletedTask;
    return Results.Ok(new { paired = true, message = result.Message });
}).RequireRateLimiting("writes");

app.MapPost("/api/spike/report", (HttpContext context, DeviceReportRequest request) =>
{
    var reachedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
    var observation = new DeviceObservation(
        DateTimeOffset.Now,
        Truncate(request.Label, 60),
        Truncate(request.UserAgent, 300),
        reachedOrigin,
        context.Request.Host.Host.Equals(material.Hostname, StringComparison.OrdinalIgnoreCase),
        request.SecureContext,
        request.ServiceWorkerSupported,
        request.ServiceWorkerRegistered,
        request.ShellCachedOffline,
        Truncate(request.DisplayMode, 30),
        request.LaunchedStandalone,
        Truncate(request.EmbeddedBrowser, 40),
        pairing.IsPaired(context.Request.Cookies["gt_spike_device"]),
        request.SessionSurvivedReload,
        Truncate(request.Notes, 500));

    if (!observations.TryAdd(observation))
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    Console.WriteLine($"  observation from {observation.Label} ({observation.DisplayMode})");
    return Results.Ok(new { recorded = true });
}).RequireRateLimiting("writes");

await app.StartAsync();

// ---- Console ----------------------------------------------------------------------------------------

var code = pairing.IssueCode();
PrintBanner();

var running = true;
while (running)
{
    // Reading a key needs a real console. When input is piped - a scripted smoke test, or a run
    // from a wrapper - take the same single-letter commands a line at a time instead, and treat
    // end of input as quit.
    char key;
    if (Console.IsInputRedirected)
    {
        var line = Console.ReadLine();
        if (line is null) break;
        key = line.Trim().Length > 0 ? line.Trim()[0] : ' ';
    }
    else
    {
        key = Console.ReadKey(intercept: true).KeyChar;
    }

    switch (char.ToLowerInvariant(key))
    {
        case 'n':
            code = pairing.IssueCode();
            Console.WriteLine($"\n  new pairing code: {Spaced(code)}");
            break;

        case 'b':
            if (bootstrapOpen)
            {
                await bootstrap.StopAsync();
                bootstrapOpen = false;
                Console.WriteLine("\n  certificate bootstrap closed. The connection QR now points at the");
                Console.WriteLine("  trusted origin instead:");
                PrintConnectionQr(LandingAddress(), Path.Combine(LocalCertificateAuthority.DefaultDirectory, "connect"));
            }
            break;

        case 'c':
            PrintConnectionQr(LandingAddress(), Path.Combine(LocalCertificateAuthority.DefaultDirectory, "connect"));
            break;

        case 'a':
            await responder.AnnounceAsync();
            Console.WriteLine("\n  re-announced the local name.");
            break;

        case 's':
            var written = new SpikeReport(runId, startedAt, CurrentHostFacts(), observations.Snapshot())
                .Write(SpikeReport.FindRepositoryRoot());
            Console.WriteLine($"\n  report written: {written}");
            break;

        case '?':
            PrintBanner();
            break;

        case 'q':
            running = false;
            break;
    }
}

// Always leave a report behind, even if the operator forgot to press 's'.
var finalReport = new SpikeReport(runId, startedAt, CurrentHostFacts(), observations.Snapshot())
    .Write(SpikeReport.FindRepositoryRoot());

Console.WriteLine($"\nReport: {finalReport}");

if (bootstrapOpen) await bootstrap.StopAsync();
await app.StopAsync();
return 0;

// ---- Helpers ------------------------------------------------------------------------------------------

// DESIGN 18.5: the address the device should land on next. Before the certificate has been
// transferred that is the plain-HTTP bootstrap, because the device cannot yet trust anything else;
// afterwards it is the stable HTTPS origin, by name when the name is being advertised.
string LandingAddress() => bootstrapOpen
    ? $"http://{selected.Address}:{options.BootstrapPort}/"
    : mdnsStarted
        ? $"https://{material.Hostname}:{options.HttpsPort}/"
        : $"https://{selected.Address}:{options.HttpsPort}/";

void PrintBanner()
{
    Console.WriteLine();
    Console.WriteLine("GoldenTicket M0 connectivity spike");
    Console.WriteLine(new string('=', 60));
    Console.WriteLine($"  serving      https://{material.Hostname}:{options.HttpsPort}");
    Console.WriteLine($"  or by IP     https://{selected.Address}:{options.HttpsPort}");
    Console.WriteLine($"  interface    {selected.Name} ({selected.Address})");
    Console.WriteLine();
    Console.WriteLine($"  certificate  expires {material.ServerCertificateExpiry:yyyy-MM-dd}");
    Console.WriteLine("  CA SHA-256   compare this on the device before trusting it:");
    Console.WriteLine($"               {material.AuthorityFingerprint}");
    Console.WriteLine();
    Console.WriteLine(mdnsStarted
        ? "  local name   advertised over mDNS"
        : $"  local name   NOT advertised ({responder.Problem ?? "disabled"}). Use the IP address.");
    Console.WriteLine(bootstrapOpen
        ? $"  certificate  http://{selected.Address}:{options.BootstrapPort}/  (open this first, then press b)"
        : "  certificate  bootstrap closed");
    Console.WriteLine();
    if (pairing.CurrentCode is { } activeCode)
    {
        Console.WriteLine($"  pairing code {Spaced(activeCode)}   (type this on the device; it is");
        Console.WriteLine("               never in the QR, and it is single-use)");
    }
    else Console.WriteLine("  pairing code none active; press n for a new code");
    Console.WriteLine();
    Console.WriteLine("  If the phone cannot reach the laptop, allow the port through the firewall for");
    Console.WriteLine("  the private network only, in an elevated prompt:");
    Console.WriteLine($"    netsh advfirewall firewall add rule name=\"GoldenTicket spike\" dir=in action=allow protocol=TCP localport={options.HttpsPort},{options.BootstrapPort} localip={selected.Address} remoteip=localsubnet profile=private program=\"{Environment.ProcessPath}\"");
    Console.WriteLine();
    Console.WriteLine("  keys:  n new code   b close bootstrap   a re-announce   c connection QR");
    Console.WriteLine("         s save report   ? help   q quit");
    Console.WriteLine(new string('=', 60));

    PrintConnectionQr(LandingAddress(), Path.Combine(LocalCertificateAuthority.DefaultDirectory, "connect"));
}

// The "Connect phone or tablet" view of DESIGN 18.5: a locally generated QR, the same address as
// readable text, and nothing else. The QR carries no pairing code and no game state.
static void PrintConnectionQr(string address, string directory)
{
    QrCode symbol;
    try
    {
        symbol = QrCode.Encode(address);
    }
    catch (ArgumentException exception)
    {
        Console.WriteLine($"  No QR for {address}: {exception.Message}");
        return;
    }

    Console.WriteLine();
    Console.WriteLine("  Point the phone's camera at this. It only opens the page below.");
    Console.WriteLine();

    var printed = QrRenderer.TryWriteToConsole(symbol, Console.Out);
    var file = QrRenderer.TryWriteSvg(symbol, address, directory);

    Console.WriteLine();
    Console.WriteLine($"  or type it:  {address}");

    if (!printed)
        Console.WriteLine("  This console cannot draw the symbol; open the file below instead.");

    if (file is not null)
        Console.WriteLine($"  larger:      {file}");
}

static string Spaced(string value) =>
    value.Length == 8 ? $"{value[..4]} {value[4..]}" : value;

static string Truncate(string? value, int length) =>
    string.IsNullOrWhiteSpace(value)
        ? ""
        : new string([.. value.Trim().Where(c => !char.IsControl(c)).Take(length)]);

static LanInterface? SelectInterface(IReadOnlyList<LanInterface> candidates, string? preferred)
{
    if (preferred is not null)
    {
        var match = candidates.FirstOrDefault(c => c.Address.ToString() == preferred);
        if (match is not null) return match;

        Console.Error.WriteLine($"{preferred} is not one of this machine's private addresses:");
        foreach (var candidate in candidates) Console.Error.WriteLine($"  {candidate}");
        return null;
    }

    if (candidates.Count == 1) return candidates[0];

    Console.WriteLine("Which network is the phone on?");
    for (var index = 0; index < candidates.Count; index++)
        Console.WriteLine($"  {index + 1}. {candidates[index]}");

    Console.Write("Choose a number: ");
    var answer = Console.ReadLine();

    return int.TryParse(answer, out var choice) && choice >= 1 && choice <= candidates.Count
        ? candidates[choice - 1]
        : null;
}

// The bootstrap is plain HTTP by necessity - the device cannot trust the CA it has not yet
// installed. DESIGN 18.5 permits exactly this, provided it serves only the public certificate and
// static instructions and is closed afterwards.
static WebApplication BuildBootstrap(SpikeOptions options, LocalTrustMaterial material, LanInterface selected,
    Func<bool> networkIsPrivate)
{
    var address = selected.Address;
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.AddServerHeader = false;
        kestrel.Limits.MaxRequestBodySize = SpikeRequestPolicy.MaximumRequestBytes;
        kestrel.Limits.MaxConcurrentConnections = 32;
        kestrel.Listen(address, options.BootstrapPort);
    });

    var bootstrap = builder.Build();
    var policy = new SpikeRequestPolicy(address, selected.PrefixLength,
        [$"http://{address}:{options.BootstrapPort}"], networkIsPrivate);
    bootstrap.Use(async (context, next) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        var status = policy.Validate(context, bootstrap: true);
        if (status != StatusCodes.Status200OK) { context.Response.StatusCode = status; return; }
        await next();
    });

    // Raw string with $$ so the CSS braces stay literal and {{...}} marks an interpolation.
    bootstrap.MapGet("/", () => Results.Content($$"""
        <!doctype html>
        <html lang="en"><head>
        <meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>GoldenTicket certificate</title>
        <style>
          body { font: 16px/1.5 system-ui, sans-serif; margin: 0; padding: 24px;
                 background: #F1E7D2; color: #33271F; max-width: 640px; }
          h1 { color: #7A241C; font-size: 22px; }
          h2 { color: #7A241C; font-size: 17px; margin-top: 26px; }
          a.button { display: inline-block; background: #7A241C; color: #FBF6EA; padding: 14px 20px;
                     border-radius: 4px; text-decoration: none; font-weight: 600; margin: 12px 0; }
          code { background: #E4D3AF; padding: 2px 5px; border-radius: 3px; word-break: break-all;
                 -webkit-user-select: all; user-select: all; }
          .warn { padding: 10px 12px; background: #E4D3AF; border-left: 4px solid #7A241C;
                  border-radius: 3px; }
          ol { padding-left: 22px; } li { margin-bottom: 10px; }
        </style></head><body>
        <h1>Trust this laptop</h1>
        <p>This page is plain HTTP on purpose: your device cannot trust the certificate it has not
        installed yet. Nothing private is served here, and this page closes after setup.</p>

        <h2>1 &middot; Install the certificate</h2>
        <p><a class="button" href="/ca.crt">Download the certificate</a></p>
        <p>Before trusting it, inspect the <b>downloaded certificate's SHA-256 fingerprint</b> in the
        device certificate viewer and compare it with the <b>laptop screen</b>. Matching text on
        this unencrypted page alone does not verify the downloaded file.</p>
        <p><code>{{WebUtility.HtmlEncode(material.AuthorityFingerprint)}}</code></p>
        <ol>
          <li><b>iPhone / iPad:</b> install the downloaded profile in Settings, then go to
          Settings &rarr; General &rarr; About &rarr; Certificate Trust Settings and turn
          <b>on</b> full trust for this certificate. Installing alone is not enough.</li>
          <li><b>Android:</b> install it as a <i>CA certificate</i> in the security settings.</li>
        </ol>

        <h2>2 &middot; Open the trusted address</h2>
        <p><code>https://{{WebUtility.HtmlEncode(material.Hostname)}}:{{options.HttpsPort}}/</code></p>
        <p>If that name does not resolve, use
        <code>https://{{address}}:{{options.HttpsPort}}/</code> instead.</p>
        <p class="warn"><b>Do not tap through a warning.</b> If the browser offers
        &ldquo;Advanced&rdquo;, &ldquo;Proceed anyway&rdquo; or &ldquo;Visit this website&rdquo;,
        stop. The certificate is not trusted yet, and continuing past the warning hides that rather
        than fixing it. Go back to step 1 instead.</p>
        <p>Open that address in <b>Chrome or Safari</b>, not inside a messaging app's browser: an
        in-app browser usually cannot install a home-screen app.</p>

        <h2>3 &middot; Install to the home screen, then pair</h2>
        <p>Add the page to the home screen and open it from there <i>before</i> typing the pairing
        code. A browser tab and the installed app keep separate cookies, so a code spent in a tab can
        leave the installed app unpaired. The pairing code is shown on the laptop and is never part
        of the QR code.</p>
        </body></html>
        """, "text/html; charset=utf-8"));

    bootstrap.MapGet("/ca.crt", () => Results.File(
        material.AuthorityCertificateDer, "application/x-x509-ca-cert", "goldenticket-authority.crt"));

    return bootstrap;
}

internal sealed record PairRequest(string? Code, string? Label);

internal sealed record DeviceReportRequest(
    string? Label,
    string? UserAgent,
    string? Origin,
    bool SecureContext,
    bool ServiceWorkerSupported,
    bool ServiceWorkerRegistered,
    bool ShellCachedOffline,
    string? DisplayMode,
    bool LaunchedStandalone,
    string? EmbeddedBrowser,
    bool SessionSurvivedReload,
    string? Notes);

internal sealed record SpikeOptions(
    int HttpsPort,
    int BootstrapPort,
    string? PreferredAddress,
    bool DisableMulticastDns,
    string? QrProbe,
    bool ShowHelp)
{
    public static SpikeOptions Parse(string[] args)
    {
        int Read(string name, int fallback)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value)
                ? value
                : fallback;
        }

        string? ReadText(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        return new SpikeOptions(
            Read("--port", 8443),
            Read("--bootstrap-port", 8080),
            ReadText("--address"),
            args.Contains("--no-mdns"),
            ReadText("--qr"),
            args.Contains("--help") || args.Contains("-h"));
    }

    public static void PrintUsage() => Console.WriteLine("""
        GoldenTicket M0 connectivity spike

          --port <n>            HTTPS port (default 8443)
          --bootstrap-port <n>  plain-HTTP certificate transfer port (default 8080)
          --address <ip>        which private address to serve on; prompts if omitted
          --no-mdns             do not advertise the .local name, to test the IP fallback
          --qr <text>           draw one QR symbol and exit, to check this console can render a
                                scannable one; touches no network
          --help                this text

        Proves trusted local HTTPS, local-origin resolution, offline installation and pairing on a
        real device. Holds no game state and shows no private information.
        """);
}
