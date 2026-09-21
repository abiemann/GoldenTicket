using System.Security.Cryptography;
using System.Text;

namespace GoldenTicket.CompanionHost;

public sealed record ControllerApproval(string Identity, string DeviceLabel, DateTimeOffset ExpiresAt);
internal sealed record ControllerCredentials(string Session, string Csrf, string Tab, long Generation);
internal sealed record PrivateGrant(string Token, int Seat, string SessionId, long Version, long Generation);
internal readonly record struct CommandAuthorization(CancellationToken Cancellation, TimeSpan ValidFor);

/// <summary>Bounded, process-local authorization. Restart requires pairing; every new tab needs
/// laptop approval. The cookie alone cannot reveal a hand or spend a card.</summary>
internal sealed class ControllerAuthority(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _sync = new();
    private string _code = "";
    private Pending? _pending;
    private ControllerCredentials? _controller;
    private DateTimeOffset _sessionExpiry;
    private DateTimeOffset _heartbeat;
    private PrivateGrant? _grant;
    private long _generation;
    private string? _deviceLabel;
    private CancellationTokenSource _grantCancellation = new();
    private sealed record Pending(string Session, string Tab, ControllerApproval Approval);

    internal string PairingCode { get { lock (_sync) return _code; } }
    internal ControllerApproval? PendingApproval { get { lock (_sync) return ValidPending()?.Approval; } }
    internal string? ControllerLabel { get { lock (_sync) return _controller is not null && _time.GetUtcNow() < _sessionExpiry ? _deviceLabel : null; } }
    internal long Generation { get { lock (_sync) return _generation; } }
    internal static string Token() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    internal void NewCode()
    {
        lock (_sync)
        {
            var previous = _code;
            do { _code = RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6"); } while (_code == previous);
            _pending = null;
        }
    }
    internal (string Session, ControllerApproval Approval)? RequestPair(string code, string tab, string label)
    {
        lock (_sync)
        {
            if (tab.Length is < 20 or > 100 || label.Length is < 1 or > 64 || string.IsNullOrWhiteSpace(label) ||
                label.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) ||
                code.Length != 6) return null;
            if (!FixedEquals(code, _code)) return null;
            // Keep the hosting code reusable. Each connection still needs laptop approval,
            // and approving a replacement revokes the previous controller.
            var session = Token();
            var approval = new ControllerApproval(RandomNumberGenerator.GetInt32(0, 10000).ToString("D4"), label, _time.GetUtcNow().AddMinutes(2));
            _pending = new(session, tab, approval);
            return (session, approval);
        }
    }
    internal bool Approve()
    {
        lock (_sync)
        {
            var pending = ValidPending();
            if (pending is null) return false;
            RevokeCore();
            _controller = new(pending.Session, Token(), pending.Tab, _generation);
            _sessionExpiry = _time.GetUtcNow().AddHours(12);
            _heartbeat = _time.GetUtcNow();
            _deviceLabel = pending.Approval.DeviceLabel;
            _pending = null;
            return true;
        }
    }
    internal bool IsPending(string? session, string? tab)
    {
        lock (_sync) { var p = ValidPending(); return p is not null && FixedEquals(session, p.Session) && FixedEquals(tab, p.Tab); }
    }
    internal ControllerCredentials? Authenticate(string? session, string? tab, bool heartbeat = false)
    {
        lock (_sync)
        {
            if (_controller is null || _time.GetUtcNow() >= _sessionExpiry ||
                !FixedEquals(session, _controller.Session) || !FixedEquals(tab, _controller.Tab)) return null;
            if (_time.GetUtcNow() - _heartbeat >= TimeSpan.FromSeconds(6)) InvalidateCore();
            if (heartbeat) _heartbeat = _time.GetUtcNow();
            return _controller;
        }
    }
    internal bool ValidateCsrf(ControllerCredentials credentials, string? csrf)
    { lock (_sync) return credentials == _controller && FixedEquals(csrf, credentials.Csrf); }
    internal PrivateGrant? Reveal(ControllerCredentials credentials, int seat, string sessionId, long version, long? expectedGeneration = null)
    {
        lock (_sync)
        {
            if (credentials != _controller || (expectedGeneration is { } generation && generation != _generation) || _time.GetUtcNow() >= _sessionExpiry ||
                _time.GetUtcNow() - _heartbeat >= TimeSpan.FromSeconds(6)) return null;
            InvalidateCore();
            _grant = new(Token(), seat, sessionId, version, _generation);
            return _grant;
        }
    }
    internal bool ValidateGrant(ControllerCredentials credentials, string? grant, int seat,
        string? sessionId, long version)
    {
        lock (_sync)
        {
            return credentials == _controller && _time.GetUtcNow() < _sessionExpiry &&
                _time.GetUtcNow() - _heartbeat < TimeSpan.FromSeconds(6) &&
                _grant is { } g && FixedEquals(grant, g.Token) && g.Seat == seat && g.SessionId == sessionId &&
                g.Version == version && g.Generation == _generation;
        }
    }
    /// <summary>Validation and cancellation capture are one atomic authorization decision. A hide
    /// immediately after this returns cancels this exact token, never a replacement grant's token.</summary>
    internal CommandAuthorization? AuthorizeCommand(ControllerCredentials credentials, string? grant,
        int seat, string? sessionId, long version)
    {
        lock (_sync)
        {
            if (!ValidateGrant(credentials, grant, seat, sessionId, version)) return null;
            var now = _time.GetUtcNow();
            var deadline = new[] { _sessionExpiry, _heartbeat.AddSeconds(6) }.Min();
            return new(_grantCancellation.Token, deadline - now);
        }
    }
    internal void InvalidatePrivateGrants() { lock (_sync) InvalidateCore(); }
    internal void Revoke() { lock (_sync) { RevokeCore(); _pending = null; } }
    private void RevokeCore() { InvalidateCore(); _controller = null; _deviceLabel = null; }
    private void InvalidateCore()
    {
        _generation++;
        _grant = null;
        _grantCancellation.Cancel();
        _grantCancellation.Dispose();
        _grantCancellation = new();
    }
    private Pending? ValidPending()
    {
        if (_pending is not null && _time.GetUtcNow() >= _pending.Approval.ExpiresAt) _pending = null;
        return _pending;
    }
    private static bool FixedEquals(string? a, string? b) => a is not null && b is not null &&
        a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
