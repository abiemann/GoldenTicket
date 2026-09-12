using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Application;

/// <summary>
/// The durable outcome of one command, kept so a repeated click, a retry after a lost
/// acknowledgement, or a post-crash resend returns the previous result instead of applying it
/// again (DESIGN 8.1).
/// </summary>
public sealed record StoredCommandOutcome(
    CommandId CommandId,
    bool Accepted,
    long StateVersionAfter,
    string? RejectionCode,
    string? RejectionMessage);

/// <summary>A restored match and the journal it was rebuilt from.</summary>
public sealed record RestoredSession(GameState State, IReadOnlyList<JournaledEvent> Journal);

/// <summary>Enough to list saved matches without opening private state (DESIGN 19.4 step 1).</summary>
public sealed record SessionSummary(
    SessionId SessionId,
    string ProfileId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    SessionLifecycle Lifecycle,
    int TurnNumber,
    IReadOnlyList<string> SeatNames,
    string? UnavailableReason = null);

/// <summary>
/// Durable storage for one match. DESIGN 19.3: the domain events, the command deduplication result
/// and the current version/state hash are committed in a single transaction.
/// </summary>
public interface ISessionStore
{
    /// <summary>Creates the match and writes its first transaction.</summary>
    Task CreateAsync(
        GameState state,
        CommandId commandId,
        Transition transition,
        string stateHash,
        CancellationToken cancellationToken);

    /// <summary>The stored outcome for a command id, or null if it has never been applied.</summary>
    Task<StoredCommandOutcome?> FindCommandOutcomeAsync(
        SessionId sessionId,
        CommandId commandId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Commits one transaction: its events, the command outcome and the resulting version and state
    /// hash, atomically.
    /// </summary>
    Task CommitAsync(
        GameState state,
        StoredCommandOutcome outcome,
        Transition transition,
        string stateHash,
        CancellationToken cancellationToken);

    /// <summary>Records a refusal so retrying the same command id returns the same refusal.</summary>
    Task RecordRejectionAsync(
        SessionId sessionId,
        StoredCommandOutcome outcome,
        CancellationToken cancellationToken);

    /// <summary>
    /// Rebuilds a match from its journal and verifies it against the stored state hash
    /// (DESIGN 19.4 steps 2-3).
    /// </summary>
    Task<RestoredSession> RestoreAsync(
        SessionId sessionId,
        BoardManifest manifest,
        CardCatalog catalog,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken);

    Task DeleteSessionAsync(SessionId sessionId, CancellationToken cancellationToken);
}

/// <summary>
/// Raised when a restored journal does not reproduce the stored hash. DESIGN 19.8: an unrecoverable
/// state checksum failure stops restoration rather than inventing missing state.
/// </summary>
public sealed class SessionIntegrityException(string message) : Exception(message);
