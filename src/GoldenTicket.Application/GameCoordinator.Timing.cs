namespace GoldenTicket.Application;

public sealed partial class GameCoordinator
{
    private readonly TurnTimingTracker _turnTiming;

    public TurnTimingSnapshot TurnTiming => _turnTiming.Snapshot();

    public void SetTurnTimingPaused(bool paused) => _turnTiming.SetPaused(paused);

    /// <summary>Keep physical scoring-marker time with the player who claimed the route.</summary>
    public void HoldTurnTimingForScoreMarker() => _turnTiming.HoldForScoreMarker();

    public void ReleaseTurnTimingForScoreMarker() => _turnTiming.ReleaseScoreMarker();

    public async Task FlushTurnTimingAsync(CancellationToken cancellationToken = default)
    {
        await _writer.WaitAsync(cancellationToken);
        try
        {
            if (!_storageFaulted)
                await _store.SaveTurnTimingAsync(SessionId, _publicView.StateVersion,
                    _turnTiming.Snapshot(), cancellationToken);
        }
        finally { _writer.Release(); }
    }
}
