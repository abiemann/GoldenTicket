using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using GoldenTicket.ConnectivitySpike;
using GoldenTicket.ConnectivitySpike.Networking;
using GoldenTicket.ConnectivitySpike.Pairing;
using GoldenTicket.ConnectivitySpike.Security;
using Microsoft.AspNetCore.Http.Json;

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

// ---- Choose the interface to serve on (DESIGN 18.5: private LAN only) ------------------------

var candidates = LanInterfaces.Discover();
if (candidates.Count == 0)
{
    Console.Error.WriteLine(
        "No private network address was found. Join the laptop to the same Wi-Fi as the phone and try again.");
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
var observations = new ConcurrentBag<DeviceObservation>();
var startedAt = DateTimeOffset.Now;
var runId = Guid.NewGuid().ToString("n")[..8];

var origins = new[]
{
    $"https://{material.Hostname}:{options.HttpsPort}",
    $"https://{selected.Address}:{options.HttpsPort}",
    $"https://localhost:{options.HttpsPort}",
};

// ---- Name advertisement --------------------------------------------------------------------------

await using var responder = new MulticastDnsResponder(material.Hostname, selected.Address);
var mdnsStarted = !options.DisableMulticastDns && responder.TryStart();

// ---- The temporary HTTP bootstrap (DESIGN 18.5) ---------------------------------------------------
// It serves only the public CA certificate and static instructions, carries no credentials, and is
// closed as soon as the certificate has been transferred.

var bootstrapOpen = true;
var bootstrap = BuildBootstrap(options, material, selected.Address);
await bootstrap.StartAsync();

// ---- The trusted HTTPS host ------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;

    void Listen(IPAddress address) => kestrel.Listen(address, options.HttpsPort, listen =>
        listen.UseHttps(material.ServerCertificate));

    Listen(selected.Address);
    Listen(IPAddress.Loopback);
});

builder.Services.Configure<JsonOptions>(json =>
    json.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

var app = builder.Build();

// DESIGN 18.5: validate Host and Origin against the current allowlist, and never let an API
// response be cached.
app.Use(async (context, next) =>
{
    var host = context.Request.Host.Value ?? string.Empty;
    if (!origins.Any(origin => origin.EndsWith("//" + host, StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
        await context.Response.WriteAsync("This host name is not served here.");
        return;
    }

    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";

        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) &&
            !origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Cross-origin requests are refused.");
            return;
        }

        // A custom header cannot be sent cross-origin without a preflight this host never allows,
        // which is the CSRF guard for the spike's small API surface.
        if (HttpMethods.IsPost(context.Request.Method) &&
            context.Request.Headers["X-GoldenTicket-Spike"] != "1")
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Missing the spike request header.");
            return;
        }
    }
    else
    {
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
            "connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    await next();
});

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
    var result = pairing.Redeem(request.Code, request.Label);

    if (result.Outcome != PairingOutcome.Paired)
        return Results.Ok(new { paired = false, message = result.Message });

    context.Response.Cookies.Append("gt_spike_device", result.Device!.DeviceSessionId, new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        MaxAge = TimeSpan.FromHours(12),
    });

    Console.WriteLine($"  paired: {result.Device.Label}");
    await Task.CompletedTask;
    return Results.Ok(new { paired = true, message = result.Message });
});

app.MapPost("/api/spike/report", (HttpContext context, DeviceReportRequest request) =>
{
    var observation = new DeviceObservation(
        DateTimeOffset.Now,
        Truncate(request.Label, 60),
        Truncate(request.UserAgent, 300),
        Truncate(request.Origin, 120),
        request.Origin?.Contains(material.Hostname, StringComparison.OrdinalIgnoreCase) ?? false,
        request.SecureContext,
        request.ServiceWorkerSupported,
        request.ServiceWorkerRegistered,
        request.ShellCachedOffline,
        Truncate(request.DisplayMode, 30),
        request.LaunchedStandalone,
        pairing.IsPaired(context.Request.Cookies["gt_spike_device"]),
        request.SessionSurvivedReload,
        Truncate(request.Notes, 500));

    observations.Add(observation);
    Console.WriteLine($"  observation from {observation.Label} ({observation.DisplayMode})");
    return Results.Ok(new { recorded = true });
});

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
                Console.WriteLine("\n  certificate bootstrap closed.");
            }
            break;

        case 'a':
            await responder.AnnounceAsync();
            Console.WriteLine("\n  re-announced the local name.");
            break;

        case 's':
            var written = new SpikeReport(runId, startedAt, CurrentHostFacts(), [.. observations])
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
var finalReport = new SpikeReport(runId, startedAt, CurrentHostFacts(), [.. observations])
    .Write(SpikeReport.FindRepositoryRoot());

Console.WriteLine($"\nReport: {finalReport}");

if (bootstrapOpen) await bootstrap.StopAsync();
await app.StopAsync();
material.ServerCertificate.Dispose();
return 0;

// ---- Helpers ------------------------------------------------------------------------------------------

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
    Console.WriteLine($"  pairing code {Spaced(pairing.CurrentCode ?? code)}");
    Console.WriteLine();
    Console.WriteLine("  If the phone cannot reach the laptop, allow the port through the firewall for");
    Console.WriteLine("  the private network only, in an elevated prompt:");
    Console.WriteLine($"    netsh advfirewall firewall add rule name=\"GoldenTicket spike\" dir=in action=allow \\");
    Console.WriteLine($"      protocol=TCP localport={options.HttpsPort},{options.BootstrapPort} profile=private");
    Console.WriteLine();
    Console.WriteLine("  keys:  n new code   b close bootstrap   a re-announce   s save report   ? help   q quit");
    Console.WriteLine(new string('=', 60));
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
static WebApplication BuildBootstrap(SpikeOptions options, LocalTrustMaterial material, IPAddress address)
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.AddServerHeader = false;
        kestrel.Listen(address, options.BootstrapPort);
    });

    var bootstrap = builder.Build();

    // Raw string with $$ so the CSS braces stay literal and {{...}} marks an interpolation.
    bootstrap.MapGet("/", () => Results.Content($$"""
        <!doctype html>
        <html lang="en"><head>
        <meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>GoldenTicket certificate</title>
        <style>
          body { font: 16px/1.5 system-ui, sans-serif; margin: 0; padding: 24px;
                 background: #F1E7D2; color: #33271F; }
          h1 { color: #7A241C; font-size: 22px; }
          a.button { display: inline-block; background: #7A241C; color: #FBF6EA; padding: 14px 20px;
                     border-radius: 4px; text-decoration: none; font-weight: 600; margin: 12px 0; }
          code { background: #E4D3AF; padding: 2px 5px; border-radius: 3px; word-break: break-all; }
          ol { padding-left: 22px; } li { margin-bottom: 10px; }
        </style></head><body>
        <h1>Trust this laptop</h1>
        <p>This page is plain HTTP on purpose: your device cannot trust the certificate it has not
        installed yet. Nothing private is served here, and this page closes after setup.</p>
        <p><a class="button" href="/ca.crt">Download the certificate</a></p>
        <p>Check this fingerprint matches the laptop screen before trusting it:</p>
        <p><code>{{WebUtility.HtmlEncode(material.AuthorityFingerprint)}}</code></p>
        <ol>
          <li><b>iPhone / iPad:</b> install the downloaded profile in Settings, then go to
          Settings &rarr; General &rarr; About &rarr; Certificate Trust Settings and turn
          <b>on</b> full trust for this certificate. Installing alone is not enough.</li>
          <li><b>Android:</b> install it as a <i>CA certificate</i> in the security settings.</li>
          <li>Then open <code>https://{{WebUtility.HtmlEncode(material.Hostname)}}:{{options.HttpsPort}}</code>
          and check there is no warning. If the name does not resolve, use
          <code>https://{{address}}:{{options.HttpsPort}}</code>.</li>
        </ol>
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
    bool SessionSurvivedReload,
    string? Notes);

internal sealed record SpikeOptions(
    int HttpsPort,
    int BootstrapPort,
    string? PreferredAddress,
    bool DisableMulticastDns,
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
            args.Contains("--help") || args.Contains("-h"));
    }

    public static void PrintUsage() => Console.WriteLine("""
        GoldenTicket M0 connectivity spike

          --port <n>            HTTPS port (default 8443)
          --bootstrap-port <n>  plain-HTTP certificate transfer port (default 8080)
          --address <ip>        which private address to serve on; prompts if omitted
          --no-mdns             do not advertise the .local name, to test the IP fallback
          --help                this text

        Proves trusted local HTTPS, local-origin resolution, offline installation and pairing on a
        real device. Holds no game state and shows no private information.
        """);
}
