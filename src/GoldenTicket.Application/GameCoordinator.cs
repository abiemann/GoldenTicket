using System.Collections.Immutable;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.Application;

/// <summary>What a command actually did, including whether it was a duplicate of an earlier one.</summary>
public sealed record SubmitOutcome(CommandResult Result, bool WasDuplicate, long StateVersion)
{
    public bool IsAccepted => Result.IsAccepted;
}

/// <summary>
/// The result of a save. DESIGN 19.8: only <see cref="SafeToPack"/> means the pieces may be cleared
/// away; a committed but unvalidated checkpoint reports <see cref="AwaitingValidation"/>, and a failed readback
/// leaves the match packed and faulted.
/// </summary>
public sealed record PackAwayOutcome(
    bool SafeToPack,
    bool AwaitingValidation,
    PackAwayCheckpoint? Checkpoint,
    string? Problem,
    CommandRejection? Rejection)
{
    public static PackAwayOutcome SafeToPackAway(PackAwayCheckpoint checkpoint) =>
        new(true, false, checkpoint, null, null);

    public static PackAwayOutcome StillValidating() => new(false, true, null, null, null);

    public static PackAwayOutcome Faulted(PackAwayCheckpoint checkpoint, string problem) =>
        new(false, false, checkpoint, problem, null);

    public static PackAwayOutcome Refused(CommandRejection rejection) =>
        new(false, false, null, rejection.Message, rejection);
}

/// <summary>A committed transaction, published to whatever is displaying the match.</summary>
public sealed record CoordinatorUpdate(PublicView Public, ImmutableArray<PublicEventEntry> NewEntries);

/// <summary>
/// The single authoritative writer for one match (DESIGN 18.1). Commands are serialised through one
/// queue that performs version checks and the durable transaction; the queue is never held while
/// waiting for a human, a search result, or anything else that can take arbitrary time.
/// </summary>
public sealed class GameCoordinator
{
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly GameRules _rules;
    private readonly ISessionStore _store;
    private readonly List<PublicEventEntry> _history = [];

    private GameState _state;
    private volatile PublicView _publicView;
    private volatile bool _storageFaulted;

    private GameCoordinator(GameRules rules, ISessionStore store, GameState state)
    {
        _rules = rules;
        _store = store;
        _state = state;
        _publicView = Projector.ProjectPublic(state);
    }

    /// <summary>Raised after each committed transaction. Carries public information only.</summary>
    public event EventHandler<CoordinatorUpdate>? Updated;

    public SessionId SessionId => _state.SessionId;

    /// <summary>The reviewed board data this match is pinned to. Public information.</summary>
    public Domain.Manifest.BoardManifest Manifest => _rules.Manifest;

    public ImmutableArray<Seat> Seats => _state.Seats;

    /// <summary>The latest public projection. Safe to read from any thread, including the UI.</summary>
    public PublicView Public => _publicView;

    /// <summary>A failed write may have committed. Restore from storage before issuing more commands.</summary>
    public bool StorageFaulted => _storageFaulted;

    public IReadOnlyList<PublicEventEntry> PublicHistory
    {
        get { lock (_history) return [.. _history]; }
    }

    public static async Task<GameCoordinator> CreateAsync(
        GameRules rules,
        ISessionStore store,
        SessionSetup setup,
        RandomState seed,
        CancellationToken cancellationToken = default)
    {
        var (state, transition) = rules.CreateSession(setup, seed);

        await store.CreateAsync(
            state, CommandId.New(), transition, StateHash.Compute(state), cancellationToken);

        var coordinator = new GameCoordinator(rules, store, state);
        coordinator.AppendHistory(transition.Events);
        return coordinator;
    }

    /// <summary>
    /// Reopens a saved match. DESIGN 19.4: the journal is replayed and verified before anything is
    /// displayed, and no private state is revealed by restoring.
    /// </summary>
    public static async Task<GameCoordinator> RestoreAsync(
        GameRules rules,
        ISessionStore store,
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var restored = await store.RestoreAsync(sessionId, rules.Manifest, rules.Catalog, cancellationToken);

        var coordinator = new GameCoordinator(rules, store, restored.State);
        coordinator.AppendHistory(restored.Journal.Select(row => row.Event));
        return coordinator;
    }

    /// <summary>Builds a fresh seat projection (DESIGN 5.3) for a private view or an AI decision.</summary>
    public async Task<SeatView> GetSeatViewAsync(SeatId seat, CancellationToken cancellationToken = default)
    {
        await _writer.WaitAsync(cancellationToken);
        try
        {
            return Projector.ProjectSeat(_state, seat);
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>
    /// Seats that have not yet committed their opening destination tickets. Returns seat ids only,
    /// never the offered tickets, so a caller can route the device without seeing a secret.
    /// </summary>
    public async Task<ImmutableArray<SeatId>> SeatsAwaitingSetupSelectionAsync(
        CancellationToken cancellationToken = default)
    {
        await _writer.WaitAsync(cancellationToken);
        try
        {
            return [.. _state.SetupOffers.Keys.OrderBy(seat => seat.Value)];
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<LegalActions> GetLegalActionsAsync(SeatId seat, CancellationToken cancellationToken = default)
        => _rules.GetLegalActions(await GetSeatViewAsync(seat, cancellationToken));

    /// <summary>An envelope addressed to the current state version, for the given seat.</summary>
    public CommandEnvelope NewEnvelope(SeatId? actor = null) =>
        new(_state.SessionId, CommandId.New(), _publicView.StateVersion, actor);

    /// <summary>
    /// Validates and, if accepted, durably commits one command. The durable write happens before the
    /// live state is swapped, so a storage failure leaves the match exactly as it was (DESIGN 21.1:
    /// "Game could not be saved" never announces success).
    /// </summary>
    public async Task<SubmitOutcome> SubmitAsync(GameCommand command, CancellationToken cancellationToken = default)
    {
        CoordinatorUpdate? update = null;
        await _writer.WaitAsync(cancellationToken);
        try
        {
            var envelope = command.Envelope;

            // Do this before looking up or recording outcomes: the store may contain other matches.
            if (envelope.SessionId != _state.SessionId)
                return new SubmitOutcome(CommandResult.Reject("WrongSession", "This command belongs to another match."),
                    WasDuplicate: false, _state.StateVersion);

            if (_storageFaulted)
                return new SubmitOutcome(CommandResult.Reject("StorageFaulted",
                    "Saving failed. Reload the saved match before continuing."), WasDuplicate: false, _state.StateVersion);

            var existing = await _store.FindCommandOutcomeAsync(
                envelope.SessionId, envelope.CommandId, cancellationToken);

            if (existing is not null)
            {
                // DESIGN 8.1: return the previous outcome rather than applying it a second time.
                var replayed = existing.Accepted
                    ? CommandResult.Accept(Array.Empty<GameEvent>())
                    : CommandResult.Reject(existing.RejectionCode ?? "Rejected", existing.RejectionMessage ?? "");

                return new SubmitOutcome(replayed, WasDuplicate: true, _state.StateVersion);
            }

            var result = _rules.ValidateAndApply(_state, command);

            if (!result.IsAccepted)
            {
                var rejection = result.Rejection!;
                try
                {
                    await _store.RecordRejectionAsync(
                        _state.SessionId,
                        new StoredCommandOutcome(
                            envelope.CommandId, false, _state.StateVersion, rejection.Code, rejection.Message),
                        cancellationToken);
                }
                catch
                {
                    _storageFaulted = true;
                    throw;
                }

                return new SubmitOutcome(result, WasDuplicate: false, _state.StateVersion);
            }

            var transition = result.Transition!;

            var next = _state.Fork();
            GameReducer.ApplyTransition(next, transition.Events);
            var hash = StateHash.Compute(next);

            try
            {
                await _store.CommitAsync(
                    next,
                    new StoredCommandOutcome(envelope.CommandId, true, next.StateVersion, null, null),
                    transition,
                    hash,
                    cancellationToken);
            }
            catch
            {
                // A lost acknowledgment is indistinguishable from a failed commit here. Continuing
                // from the old in-memory state could overwrite a draw that was already durable.
                _storageFaulted = true;
                throw;
            }

            _state = next;
            _publicView = Projector.ProjectPublic(next);
            var entries = AppendHistory(transition.Events);

            update = new CoordinatorUpdate(_publicView, entries);

            return new SubmitOutcome(result, WasDuplicate: false, next.StateVersion);
        }
        finally
        {
            _writer.Release();
            // Observers can query the coordinator without deadlocking its single writer. Consumers
            // use the included version to ignore a notification overtaken by a newer transaction.
            if (update is not null) RaiseUpdated(update);
        }
    }

    /// <summary>
    /// The canonical fingerprint of the live state, taken through the writer so it cannot race a
    /// commit. Used to prove that a replayed journal reaches the same place (invariant 12).
    /// </summary>
    public async Task<string> ComputeStateHashAsync(CancellationToken cancellationToken = default)
    {
        await _writer.WaitAsync(cancellationToken);
        try
        {
            return StateHash.Compute(_state);
        }
        finally
        {
            _writer.Release();
        }
    }

    // ---- Save, pack away and rebuild (DESIGN 19.8) -------------------------------------------

    /// <summary>
    /// The checkpoint identity of a save that has been requested but not yet written, so an
    /// interrupted preparation can be carried forward against the same request.
    /// </summary>
    public async Task<CheckpointId?> PendingCheckpointIdAsync(CancellationToken cancellationToken = default)
    {
        await _writer.WaitAsync(cancellationToken);
        try
        {
            return _state.PackAwayRequest?.CheckpointId;
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>
    /// The gameplay fingerprint, with lifecycle and transaction bookkeeping normalised away. Used to
    /// show that packing away and rebuilding left the match itself untouched (invariant 15).
    /// </summary>
    public async Task<string> ComputeLogicalStateHashAsync(CancellationToken cancellationToken = default)
    {
        await _writer.WaitAsync(cancellationToken);
        try
        {
            return StateHash.ComputeLogical(_state);
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>
    /// Runs the three durable boundaries of a save: suspend play, write the checkpoint, then read it
    /// back. A safe-to-pack result is returned only after the readback succeeds, so a committed but
    /// unvalidated checkpoint never tells anyone the pieces can be cleared away.
    /// </summary>
    public async Task<PackAwayOutcome> SaveAndPackAwayAsync(
        string name, CancellationToken cancellationToken = default)
    {
        var requested = await SubmitAsync(new SaveAndPackAway(NewEnvelope(), name), cancellationToken);
        if (!requested.IsAccepted) return PackAwayOutcome.Refused(requested.Result.Rejection!);

        return await ContinuePackAwayAsync(cancellationToken);
    }

    /// <summary>
    /// Carries an interrupted save forward. DESIGN 19.5: preparation interrupted stays paused and is
    /// retried against the preserved source state, and a restart between the checkpoint commit and
    /// its readback repeats the validation instead of reporting success. Safe to call repeatedly.
    /// </summary>
    public async Task<PackAwayOutcome> ContinuePackAwayAsync(CancellationToken cancellationToken = default)
    {
        if (_state.Lifecycle == SessionLifecycle.PreparingPackAway && _state.PackAwayRequest is { } request)
        {
            var committed = await SubmitAsync(
                new CommitPackAwayCheckpoint(NewEnvelope(), request.CheckpointId), cancellationToken);

            if (!committed.IsAccepted) return PackAwayOutcome.Refused(committed.Result.Rejection!);
        }

        if (_state.Checkpoint is { Status: CheckpointStatus.CommittedAwaitingReadback } pending)
        {
            var (succeeded, failure) = await ValidateCheckpointAsync(pending, cancellationToken);

            var recorded = await SubmitAsync(
                new RecordCheckpointReadback(NewEnvelope(), pending.CheckpointId, succeeded, failure),
                cancellationToken);

            if (!recorded.IsAccepted) return PackAwayOutcome.Refused(recorded.Result.Rejection!);
        }

        return _state.Checkpoint switch
        {
            { Status: CheckpointStatus.Verified } verified => PackAwayOutcome.SafeToPackAway(verified),
            { Status: CheckpointStatus.Faulted } faulted =>
                PackAwayOutcome.Faulted(faulted, _state.CheckpointFault ?? "readback failed"),
            _ => PackAwayOutcome.StillValidating(),
        };
    }

    /// <summary>
    /// DESIGN 19.8 step 6: read the referenced record and hashes back and validate them. The whole
    /// save is reopened from storage, the journal is replayed to the checkpoint's own source point,
    /// and both fingerprints must reproduce.
    /// </summary>
    private async Task<(bool Succeeded, string? Failure)> ValidateCheckpointAsync(
        PackAwayCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        try
        {
            var stored = await _store.ReadCheckpointAsync(
                _state.SessionId, checkpoint.CheckpointId, cancellationToken);

            if (stored is null) return (false, "the checkpoint could not be read back from storage");

            if (!string.Equals(stored.LogicalStateHash, checkpoint.LogicalStateHash, StringComparison.Ordinal) ||
                !string.Equals(stored.PhysicalTargetHash, checkpoint.PhysicalTargetHash, StringComparison.Ordinal))
            {
                return (false, "the stored checkpoint does not match the one that was committed");
            }

            var restored = await _store.RestoreAsync(
                _state.SessionId, _rules.Manifest, _rules.Catalog, cancellationToken);

            var prefix = restored.Journal
                .Where(row => row.Sequence < checkpoint.SourceJournalSequence)
                .ToList();

            if (prefix.Count == 0) return (false, "the checkpoint's source history is missing");

            var atCheckpoint = GameReducer.Rebuild(_rules.Manifest, _rules.Catalog, prefix);

            if (!string.Equals(
                    StateHash.ComputeLogical(atCheckpoint), checkpoint.LogicalStateHash, StringComparison.Ordinal))
            {
                return (false, "replaying the save did not reproduce the checkpoint's game state");
            }

            var target = PackAwayCheckpoint.HashTarget(PackAwayCheckpoint.TargetFrom(atCheckpoint));
            if (!string.Equals(target, checkpoint.PhysicalTargetHash, StringComparison.Ordinal))
                return (false, "replaying the save did not reproduce the checkpoint's board");

            return (true, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // DESIGN 21.2: report a bounded reason, never an exception payload.
            return (false, $"the save could not be validated ({exception.GetType().Name})");
        }
    }

    /// <summary>
    /// Recomputes every derived value from confirmed state and compares it with what is published.
    /// DESIGN 7.2 invariants 13 and 14 are checked here rather than assumed.
    /// </summary>
    public async Task<IReadOnlyList<string>> CheckInvariantsAsync(CancellationToken cancellationToken = default)
    {
        await _writer.WaitAsync(cancellationToken);
        try
        {
            return InvariantChecker.Check(_state);
        }
        finally
        {
            _writer.Release();
        }
    }

    private ImmutableArray<PublicEventEntry> AppendHistory(IEnumerable<GameEvent> events)
    {
        var added = ImmutableArray.CreateBuilder<PublicEventEntry>();
        foreach (var domainEvent in events)
        {
            if (domainEvent.ToPublicEntry(_rules.Manifest) is { } entry) added.Add(entry);
        }

        var entries = added.ToImmutable();
        lock (_history) _history.AddRange(entries);
        return entries;
    }

    private void RaiseUpdated(CoordinatorUpdate update)
    {
        // Observers run on the caller's thread; the UI marshals to its dispatcher itself.
        if (Updated is not { } handlers) return;
        foreach (EventHandler<CoordinatorUpdate> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, update);
            }
            catch (Exception exception)
            {
                // An observer fault cannot turn a committed action into a reported save failure,
                // nor prevent other observers being notified. Do not log potentially private text.
                System.Diagnostics.Trace.TraceError("Game update observer failed: {0}", exception.GetType().Name);
            }
        }
    }
}
