using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;
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
        public SortedDictionary<long, TurnTimingSnapshot> TimingSnapshots { get; } = [];
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
                    ? outcome with { Timing = CloneTiming(outcome.Timing) }
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
            var timing = CloneTiming(outcome.Timing);
            Append(entry, state.StateVersion, transition);

            entry.LatestStateHash = stateHash;
            entry.Outcomes[outcome.CommandId] = outcome with { Timing = timing };
            if (timing is not null) entry.TimingSnapshots[state.StateVersion] = timing;
            entry.Lifecycle = state.Lifecycle;
            entry.TurnNumber = state.TurnNumber;
            entry.StateVersion = state.StateVersion;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    public Task SaveTurnTimingAsync(
        SessionId sessionId, long stateVersion, TurnTimingSnapshot timing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timing);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = Require(sessionId);
            if (entry.StateVersion != stateVersion)
                throw new SessionIntegrityException("The saved match changed. Reload it before saving turn timing.");
            entry.TimingSnapshots[stateVersion] = CloneTiming(timing)!;
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
            if (!entry.Outcomes.TryAdd(outcome.CommandId, outcome with { Timing = CloneTiming(outcome.Timing) }))
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
        TurnTimingSnapshot? timing;

        lock (_gate)
        {
            var entry = Require(sessionId);
            journal = [.. entry.Journal];
            expected = entry.LatestStateHash;
            timing = TimingAt(entry, entry.StateVersion);
        }

        var state = GameReducer.Rebuild(manifest, catalog, journal);
        var actual = StateHash.Compute(state);

        if (expected is not null && !StateHash.Matches(state, expected))
        {
            throw new SessionIntegrityException(
                $"Replaying the journal of {sessionId} produced {actual} but the store recorded {expected}.");
        }

        return Task.FromResult(new RestoredSession(state, journal, UsableTiming(timing, state)));
    }

    public Task<PackAwayCheckpoint?> ReadCheckpointAsync(
        SessionId sessionId, CheckpointId checkpointId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // The journal is the record here too: the latest event that touched this checkpoint is
            // what a reader would see, so a readback exercises the same path the SQLite store does.
            PackAwayCheckpoint? found = null;
            foreach (var row in Require(sessionId).Journal)
            {
                switch (row.Event)
                {
                    case PackAwayCheckpointCommitted committed
                        when committed.Checkpoint.CheckpointId == checkpointId:
                        found = committed.Checkpoint;
                        break;
                    case PackAwayCheckpointVerified verified when verified.CheckpointId == checkpointId:
                        found = found is null ? null : found with { Status = CheckpointStatus.Verified };
                        break;
                    case PackAwayCheckpointFaulted faulted when faulted.CheckpointId == checkpointId:
                        found = found is null ? null : found with { Status = CheckpointStatus.Faulted };
                        break;
                }
            }

            return Task.FromResult(found);
        }
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
                    pair.Value.SeatNames,
                    LatestCheckpointName: pair.Value.Journal.Select(row => row.Event)
                        .OfType<PackAwayCheckpointCommitted>().LastOrDefault()?.Checkpoint.Name))
            ];

            return Task.FromResult(summaries);
        }
    }

    public Task DeleteSessionAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        lock (_gate) _sessions.Remove(sessionId);
        return Task.CompletedTask;
    }

    public Task<RestoredSession> RewindToVerifiedCheckpointAsync(
        SessionId sessionId, CheckpointId checkpointId, BoardManifest manifest, CardCatalog catalog,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = Require(sessionId);
            var current = GameReducer.Rebuild(manifest, catalog, entry.Journal);
            if (entry.LatestStateHash is null || !StateHash.Matches(current, entry.LatestStateHash))
                throw new SessionIntegrityException("The match changed or is damaged; it was not rewound.");

            var restored = CheckpointJournalRewind.AtVerifiedCheckpoint(
                entry.Journal, checkpointId, manifest, catalog);
            var existing = entry.Journal.Count;
            entry.Journal.RemoveRange(restored.Journal.Count, existing - restored.Journal.Count);
            foreach (var id in entry.Outcomes.Where(pair => !pair.Value.Accepted ||
                         pair.Value.StateVersionAfter > restored.State.StateVersion)
                         .Select(pair => pair.Key).ToArray())
                entry.Outcomes.Remove(id);
            foreach (var version in entry.TimingSnapshots.Keys
                         .Where(version => version > restored.State.StateVersion).ToArray())
                entry.TimingSnapshots.Remove(version);
            entry.Lifecycle = restored.State.Lifecycle;
            entry.TurnNumber = restored.State.TurnNumber;
            entry.StateVersion = restored.State.StateVersion;
            entry.LatestStateHash = StateHash.Compute(restored.State);
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(restored with
            {
                Timing = UsableTiming(TimingAt(entry, restored.State.StateVersion), restored.State)
            });
        }
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

    private static TurnTimingSnapshot? TimingAt(Entry entry, long stateVersion) =>
        CloneTiming(entry.TimingSnapshots.Where(pair => pair.Key <= stateVersion)
            .Select(pair => pair.Value).LastOrDefault());

    private static TurnTimingSnapshot? CloneTiming(TurnTimingSnapshot? timing) =>
        timing is null ? null : timing with { Turns = timing.Turns.ToArray() };

    private static TurnTimingSnapshot? UsableTiming(TurnTimingSnapshot? timing, GameState state)
    {
        if (timing?.Turns is null) return null;
        var previousTurn = 0;
        var unfinished = false;
        foreach (var turn in timing.Turns)
        {
            if (turn is null || turn.TurnNumber <= previousTurn || turn.TurnNumber > state.TurnNumber ||
                unfinished || !state.Seats.Any(seat => seat.SeatId == turn.SeatId) ||
                turn.ElapsedTicks < 0 || turn.ElapsedTicks > TimeSpan.FromDays(365).Ticks)
                return null;
            previousTurn = turn.TurnNumber;
            unfinished = !turn.Completed;
        }
        return timing.AwaitingScoreMarker && !unfinished ? null : timing;
    }

    private Entry Require(SessionId sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry)
            ? entry
            : throw new KeyNotFoundException($"No session {sessionId}.");
}
