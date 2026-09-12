using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace GoldenTicket.ConnectivitySpike.Pairing;

/// <summary>A device that completed the pairing exchange. No game access is implied.</summary>
public sealed record PairedDevice(string DeviceSessionId, string Label, DateTimeOffset PairedAt);

public enum PairingOutcome
{
    Paired,
    WrongCode,
    Expired,
    TooManyAttempts,
}

public sealed record PairingResult(PairingOutcome Outcome, PairedDevice? Device, string Message);

/// <summary>
/// The pairing half of the spike (DESIGN 18.5). A short-lived, single-use, rate-limited code is
/// shown on the laptop and typed into the device; a successful exchange issues a revocable device
/// session.
///
/// This proves the round-trip works in the final launched context - which is the point, because
/// DESIGN 18.5 warns that a browser tab and a home-screen web app may not share cookies or storage.
/// It grants nothing: there is no game state here, and the real host adds controller leases and
/// per-seat private-view grants in M2.
/// </summary>
public sealed class PairingService(TimeProvider? timeProvider = null)
{
    /// <summary>Long enough to type, short enough that a shoulder-surfed code is useless later.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    public const int MaximumAttempts = 5;
    private const int CodeDigits = 8;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, PairedDevice> _devices = new();
    private readonly Lock _gate = new();

    private string? _code;
    private DateTimeOffset _issuedAt;
    private int _attempts;

    /// <summary>The code currently displayed on the laptop, or null if none is outstanding.</summary>
    public string? CurrentCode
    {
        get { lock (_gate) return IsExpired() ? null : _code; }
    }

    public DateTimeOffset? CodeExpiresAt
    {
        get { lock (_gate) return _code is null ? null : _issuedAt + CodeLifetime; }
    }

    public int RemainingAttempts
    {
        get { lock (_gate) return Math.Max(0, MaximumAttempts - _attempts); }
    }

    public IReadOnlyCollection<PairedDevice> Devices => [.. _devices.Values];

    /// <summary>Issues a fresh code, invalidating any outstanding one.</summary>
    public string IssueCode()
    {
        lock (_gate)
        {
            _code = RandomNumberGenerator.GetInt32(0, (int)Math.Pow(10, CodeDigits))
                .ToString($"D{CodeDigits}");
            _issuedAt = _time.GetUtcNow();
            _attempts = 0;
            return _code;
        }
    }

    /// <summary>
    /// Consumes a submitted code. A correct code is single-use; a wrong one costs an attempt, and
    /// running out of attempts retires the code rather than allowing an unbounded guessing loop.
    /// </summary>
    public PairingResult Redeem(string? submitted, string deviceLabel)
    {
        lock (_gate)
        {
            if (_code is null) return new PairingResult(PairingOutcome.Expired, null, "No pairing code is active.");

            if (IsExpired())
            {
                _code = null;
                return new PairingResult(PairingOutcome.Expired, null, "That pairing code has expired.");
            }

            if (_attempts >= MaximumAttempts)
            {
                _code = null;
                return new PairingResult(PairingOutcome.TooManyAttempts, null,
                    "Too many attempts. Ask the laptop for a new code.");
            }

            var candidate = (submitted ?? string.Empty).Trim();

            // Constant-time comparison: the code is a short secret.
            var matches = candidate.Length == _code.Length &&
                          CryptographicOperations.FixedTimeEquals(
                              System.Text.Encoding.ASCII.GetBytes(candidate),
                              System.Text.Encoding.ASCII.GetBytes(_code));

            if (!matches)
            {
                _attempts++;
                var left = Math.Max(0, MaximumAttempts - _attempts);
                return new PairingResult(PairingOutcome.WrongCode, null,
                    left == 0
                        ? "That code is wrong, and there are no attempts left. Ask for a new code."
                        : $"That code is wrong. {left} attempt{(left == 1 ? "" : "s")} left.");
            }

            _code = null;   // single use

            var device = new PairedDevice(
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                Sanitise(deviceLabel),
                _time.GetUtcNow());

            _devices[device.DeviceSessionId] = device;
            return new PairingResult(PairingOutcome.Paired, device, "Paired.");
        }
    }

    public bool IsPaired(string? deviceSessionId) =>
        deviceSessionId is not null && _devices.ContainsKey(deviceSessionId);

    public PairedDevice? Find(string? deviceSessionId) =>
        deviceSessionId is not null && _devices.TryGetValue(deviceSessionId, out var device) ? device : null;

    public void Revoke(string deviceSessionId) => _devices.TryRemove(deviceSessionId, out _);

    private bool IsExpired() => _code is not null && _time.GetUtcNow() - _issuedAt > CodeLifetime;

    /// <summary>
    /// A device label is untrusted text from the network. It is bounded and stripped of control
    /// characters before it is ever shown on the laptop (DESIGN 18.5: serve untrusted names as text).
    /// </summary>
    private static string Sanitise(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "unnamed device";

        var cleaned = new string([.. label.Trim().Where(c => !char.IsControl(c)).Take(60)]);
        return cleaned.Length == 0 ? "unnamed device" : cleaned;
    }
}
