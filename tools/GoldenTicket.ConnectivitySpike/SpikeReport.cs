using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoldenTicket.ConnectivitySpike;

/// <summary>
/// What the laptop knows about itself. DESIGN 22.7 asks for exact versions and real hardware to be
/// recorded; without them a run is not evidence.
/// </summary>
public sealed record HostFacts(
    string OperatingSystem,
    string RuntimeVersion,
    string Hostname,
    string BoundAddress,
    int HttpsPort,
    string CertificateFingerprint,
    DateTimeOffset CertificateExpiry,
    IReadOnlyList<string> SubjectAlternativeNames,
    bool MulticastDnsStarted,
    string? MulticastDnsProblem,
    int MulticastDnsAnsweredQueries,
    bool BootstrapOpen);

/// <summary>
/// One device's own account of what happened, submitted from the page. These are the four questions
/// M0 exists to answer, per DESIGN 22.7: trusted HTTPS, origin resolution, offline installation and
/// a pairing round-trip in the final launched context.
/// </summary>
public sealed record DeviceObservation(
    DateTimeOffset RecordedAt,
    string Label,
    string UserAgent,
    string Origin,
    bool ReachedByName,
    bool SecureContext,
    bool ServiceWorkerSupported,
    bool ServiceWorkerRegistered,
    bool ShellCachedOffline,
    string DisplayMode,
    bool LaunchedStandalone,
    /// <summary>The in-app browser the page was opened inside, or empty for a real browser. A run
    /// from inside one explains an installation failure that is not the laptop's fault.</summary>
    string EmbeddedBrowser,
    bool Paired,
    bool SessionSurvivedReload,
    string? Notes);

/// <summary>The evidence file for one spike run (DESIGN 23.2).</summary>
public sealed record SpikeReport(
    string RunId,
    DateTimeOffset StartedAt,
    HostFacts Host,
    IReadOnlyList<DeviceObservation> Devices)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Writes the run under docs/evidence/, where the milestone reports live. Nothing here is a
    /// secret: the certificate fingerprint is meant to be compared aloud, and no private key,
    /// pairing code or device session id is recorded.
    /// </summary>
    public string Write(string repositoryRoot)
    {
        var directory = Path.Combine(repositoryRoot, "docs", "evidence", "m0-connectivity");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"run-{StartedAt:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        return path;
    }

    /// <summary>Walks up from the executable to the repository, so a run works from any directory.</summary>
    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GoldenTicket.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        return Environment.CurrentDirectory;
    }
}
