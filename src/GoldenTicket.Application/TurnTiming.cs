using GoldenTicket.Domain;
using GoldenTicket.Domain.Projections;

namespace GoldenTicket.Application;

public sealed record TurnTimingEntry(int TurnNumber, SeatId SeatId, long ElapsedTicks,
    bool Completed, bool IsPartial);

public sealed record TurnTimingSnapshot(IReadOnlyList<TurnTimingEntry> Turns,
    bool AwaitingScoreMarker = false, bool WasRunning = false, long? GameElapsedTicks = null);

/// <summary>Independent monotonic turn and game clocks, outside gameplay rules and state hashes.</summary>
public sealed class TurnTimingTracker
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly List<TurnTimingEntry> _turns;
    private readonly bool _restored;
    private PublicView? _view;
    private long _stamp;
    private bool _paused = true;
    private bool _enabled;
    private bool _holdingMarker;
    private bool _gameRunning;
    private long _gameElapsedTicks;
    private int? _current;
    private int? _initialTurn;

    public TurnTimingTracker(TimeProvider? clock = null, TurnTimingSnapshot? saved = null,
        bool restored = false, bool awaitingScoreMarker = false)
    {
        _clock = clock ?? TimeProvider.System;
        _stamp = _clock.GetTimestamp();
        _turns = saved?.Turns.ToList() ?? [];
        // Older saves know only active turn time; their unrecorded pauses cannot be rebuilt.
        // Start stopped on restore so time spent outside the running game is never added.
        _gameElapsedTicks = saved?.GameElapsedTicks ?? _turns.Sum(turn => turn.ElapsedTicks);
        _restored = restored;
        var unfinished = _turns.FindLastIndex(turn => !turn.Completed);
        if (unfinished >= 0)
        {
            _current = unfinished;
            _holdingMarker = awaitingScoreMarker;
            // An active autosave cannot recover the unrecorded interval before interruption.
            // A clean paused checkpoint can resume the same turn with exact recorded time.
            if (saved is { AwaitingScoreMarker: true } or { WasRunning: true })
                _turns[unfinished] = _turns[unfinished] with { IsPartial = true };
        }
    }

    public void Synchronize(PublicView view)
    {
        lock (_gate)
        {
            Accumulate();
            _view = view;
            _initialTurn ??= view.TurnNumber;
            Reconcile();
        }
    }

    /// <summary>
    /// Prepare the timing payload for a candidate transaction without publishing its turn change.
    /// The coordinator calls Synchronize only after storage acknowledges that transaction. Clock
    /// controls remain live during I/O; applying the view afterwards must not replace a newer pause
    /// or score-marker hold with flags copied before the write.
    /// </summary>
    internal TurnTimingSnapshot PreviewSynchronization(PublicView view)
    {
        lock (_gate)
        {
            Accumulate();
            var candidate = new TurnTimingTracker(this)
            {
                _view = view,
                _initialTurn = _initialTurn ?? view.TurnNumber,
            };
            candidate.Reconcile();
            return new(candidate._turns.ToArray(), candidate._holdingMarker,
                candidate.IsCounting, candidate._gameElapsedTicks);
        }
    }

    private TurnTimingTracker(TurnTimingTracker source)
    {
        _clock = source._clock;
        _stamp = source._stamp;
        _turns = [.. source._turns];
        _restored = source._restored;
        _view = source._view;
        _paused = source._paused;
        _enabled = source._enabled;
        _holdingMarker = source._holdingMarker;
        _gameRunning = source._gameRunning;
        _gameElapsedTicks = source._gameElapsedTicks;
        _current = source._current;
        _initialTurn = source._initialTurn;
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            Accumulate();
            _paused = paused;
            if (!paused) _enabled = true;
            Reconcile();
        }
    }

    public void HoldForScoreMarker()
    {
        lock (_gate)
        {
            Accumulate();
            if (_current is not null) _holdingMarker = true;
        }
    }

    public void SetGameRunning(bool running)
    {
        lock (_gate)
        {
            Accumulate();
            _gameRunning = running;
        }
    }

    public void ReleaseScoreMarker()
    {
        lock (_gate)
        {
            Accumulate();
            _holdingMarker = false;
            Reconcile();
        }
    }

    public TurnTimingSnapshot Snapshot()
    {
        lock (_gate)
        {
            Accumulate();
            return new(_turns.ToArray(), _holdingMarker, IsCounting, _gameElapsedTicks);
        }
    }

    private void Reconcile()
    {
        if (_view is not { } view || _holdingMarker) return;
        if (_current is { } current && (view.Lifecycle == SessionLifecycle.Finished ||
            _turns[current].TurnNumber != view.TurnNumber || _turns[current].SeatId != view.ActiveSeatId))
        {
            _turns[current] = _turns[current] with { Completed = true };
            _current = null;
        }
        if (_enabled && _current is null && view.Lifecycle == SessionLifecycle.Active)
        {
            _current = _turns.Count;
            _turns.Add(new(view.TurnNumber, view.ActiveSeatId, 0, false,
                _restored && view.TurnNumber == _initialTurn));
        }
    }

    private bool IsCounting => !_paused && _current is not null && _view is { } view &&
        (view.Lifecycle == SessionLifecycle.Active || _holdingMarker && view.Lifecycle == SessionLifecycle.Finished) &&
        view.TurnPhase != TurnPhase.RulesDecisionRequired;

    private bool IsGameCounting => _gameRunning && _view is { } view &&
        (view.Lifecycle is not (SessionLifecycle.Setup or SessionLifecycle.Finished) ||
            _holdingMarker && view.Lifecycle == SessionLifecycle.Finished);

    private void Accumulate()
    {
        var now = _clock.GetTimestamp();
        var elapsed = Math.Max(0, _clock.GetElapsedTime(_stamp, now).Ticks);
        if (IsGameCounting)
            _gameElapsedTicks = checked(_gameElapsedTicks + elapsed);
        if (IsCounting && _current is { } current)
        {
            var previous = _turns[current];
            _turns[current] = previous with { ElapsedTicks = checked(previous.ElapsedTicks + elapsed) };
        }
        _stamp = now;
    }
}
