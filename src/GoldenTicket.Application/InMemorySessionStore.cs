using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Application;

/// <summary>
/// A volatile store with the same transaction and deduplication semantics as the SQLite one, used by
/// the headless simulator and by tests that are exercising rules rather than durability. It is not a
/// save: closing the process loses the match.
/// </summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<SessionId, Entry> _sessions = [];

    private sealed class Entry
    {
        public required string ProfileId { get; init; }
        public required string ManifestHash { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required IReadOnlyList<string> SeatNames { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
        public SessionLifecycle Lifecycle { get; set; }
        public int TurnNumber { get; set; }
        public long StateVersion { get; set; }
        public List<JournaledEvent> Journal { get; } = [];
        public Dictionary<CommandId, StoredCommandOutcome> Outcomes { get; } = [];
        public string? LatestStateHash { get; set; }
    }

    public Task CreateAsync(
        GameState state,
        CommandId commandId,
        Transition transition,
        string stateHash,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_sessions.ContainsKey(state.SessionId))
                throw new InvalidOperationException($"Session {state.SessionId} already exists.");

            var entry = new Entry
            {
                ProfileId = state.Manifest.ProfileId,
                ManifestHash = state.Manifest.DataHash,
                CreatedAt = DateTimeOffset.UtcNow,
                SeatNames = [.. state.Seats.Select(seat => seat.DisplayName)],
                UpdatedAt = DateTimeOffset.UtcNow,
                Lifecycle = state.Lifecycle,
                TurnNumber = state.TurnNumber,
                StateVersion = state.StateVersion,
            };

            _sessions[state.SessionId] = entry;
            Append(entry, state.StateVersion, transition);
            entry.LatestStateHash = stateHash;
            entry.Outcomes[commandId] = new StoredCommandOutcome(commandId, true, state.StateVersion, null, null);
        }

        return Task.CompletedTask;
    }

    public Task<StoredCommandOutcome?> FindCommandOutcomeAsync(
        SessionId sessionId, CommandId commandId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(
                _sessions.TryGetValue(sessionId, out var entry) && entry.Outcomes.TryGetValue(commandId, out var outcome)
                    ? outcome
                    : null);
        }
    }

    public Task CommitAsync(
        GameState state,
        StoredCommandOutcome outcome,
        Transition transition,
        string stateHash,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var entry = Require(state.SessionId);
            cancellationToken.ThrowIfCancellationRequested();
            if (state.StateVersion != entry.StateVersion + 1 || outcome.StateVersionAfter != state.StateVersion)
                throw new SessionIntegrityException("The saved match changed. Reload it before committing another action.");
            if (entry.Outcomes.ContainsKey(outcome.CommandId))
                throw new SessionIntegrityException("This command already has a durable outcome.");
            Append(entry, state.StateVersion, transition);

            entry.LatestStateHash = stateHash;
            entry.Outcomes[outcome.CommandId] = outcome;
            entry.Lifecycle = state.Lifecycle;
            entry.TurnNumber = state.TurnNumber;
            entry.StateVersion = state.StateVersion;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    public Task RecordRejectionAsync(
        SessionId sessionId, StoredCommandOutcome outcome, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = Require(sessionId);
            if (!entry.Outcomes.TryAdd(outcome.CommandId, outcome))
                throw new SessionIntegrityException("This command already has a durable outcome.");
        }
        return Task.CompletedTask;
    }

    public Task<RestoredSession> RestoreAsync(
        SessionId sessionId,
        BoardManifest manifest,
        CardCatalog catalog,
        CancellationToken cancellationToken)
    {
        List<JournaledEvent> journal;
        string? expected;

        lock (_gate)
        {
            var entry = Require(sessionId);
            journal = [.. entry.Journal];
            expected = entry.LatestStateHash;
        }

        var state = GameReducer.Rebuild(manifest, catalog, journal);
        var actual = StateHash.Compute(state);

        if (expected is not null && !StateHash.Matches(state, expected))
        {
            throw new SessionIntegrityException(
                $"Replaying the journal of {sessionId} produced {actual} but the store recorded {expected}.");
        }

        return Task.FromResult(new RestoredSession(state, journal));
    }

    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<SessionSummary> summaries =
            [
                .. _sessions.Select(pair => new SessionSummary(
                    pair.Key,
                    pair.Value.ProfileId,
                    pair.Value.CreatedAt,
                    pair.Value.UpdatedAt,
                    pair.Value.Lifecycle,
                    pair.Value.TurnNumber,
                    pair.Value.SeatNames))
            ];

            return Task.FromResult(summaries);
        }
    }

    public Task DeleteSessionAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        lock (_gate) _sessions.Remove(sessionId);
        return Task.CompletedTask;
    }

    /// <summary>The journal as stored, for replay and privacy tests.</summary>
    public IReadOnlyList<JournaledEvent> JournalOf(SessionId sessionId)
    {
        lock (_gate) return [.. Require(sessionId).Journal];
    }

    private static void Append(Entry entry, long stateVersion, Transition transition)
    {
        var sequence = entry.Journal.Count == 0 ? -1 : entry.Journal[^1].Sequence;
        foreach (var domainEvent in transition.Events)
            entry.Journal.Add(new JournaledEvent(++sequence, stateVersion, domainEvent));
    }

    private Entry Require(SessionId sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry)
            ? entry
            : throw new KeyNotFoundException($"No session {sessionId}.");
}
