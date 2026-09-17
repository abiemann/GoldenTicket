namespace GoldenTicket.Vision;

public enum ScoreMarkerMoveState
{
    WaitingForFreshFrame,
    Missing,
    Ambiguous,
    WrongPosition,
    Stabilizing,
    Confirmed
}

public sealed record ScoreMarkerMoveObservation(ScoreMarkerMoveState State)
{
    public bool Confirmed => State == ScoreMarkerMoveState.Confirmed;
}

/// <summary>
/// Requires a chosen physical score marker to be read at the requested printed track number
/// in two separate fresh, stable observations. It does not infer completed laps; the caller
/// supplies the printed 1–100 score for this pending move.
/// </summary>
public sealed class ScoreMarkerMoveVerifier
{
    private static readonly TimeSpan MaximumFrameAge = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumStableInterval = TimeSpan.FromSeconds(1);
    private readonly record struct Identity(string OperationKey, MarkerColor Color, int PrintedScore,
        long CropRevision, long ModelRevision, long CameraEpoch);

    private Identity? _identity;
    private long _lastSequence;
    private DateTimeOffset? _firstMatchingAt;
    private bool _confirmed;

    public void Reset()
    {
        _identity = null;
        _lastSequence = 0;
        _firstMatchingAt = null;
        _confirmed = false;
    }

    public ScoreMarkerMoveObservation Observe(CameraFrame uprightRectifiedBoard,
        IReadOnlyList<ScoreMarkerReading> readings, MarkerColor color, int targetPrintedScore,
        string operationKey, long cropRevision, long modelRevision)
    {
        ArgumentNullException.ThrowIfNull(uprightRectifiedBoard);
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetPrintedScore, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetPrintedScore, 100);

        var identity = new Identity(operationKey, color, targetPrintedScore, cropRevision,
            modelRevision, uprightRectifiedBoard.Epoch);
        if (_identity != identity)
        {
            Reset();
            _identity = identity;
        }

        if (uprightRectifiedBoard.Age > MaximumFrameAge ||
            Math.Abs((double)uprightRectifiedBoard.Width / uprightRectifiedBoard.Height - 1.6) > .02 ||
            uprightRectifiedBoard.Sequence <= _lastSequence)
        {
            _firstMatchingAt = null;
            return new(ScoreMarkerMoveState.WaitingForFreshFrame);
        }
        _lastSequence = uprightRectifiedBoard.Sequence;
        if (_confirmed) return new(ScoreMarkerMoveState.Stabilizing);

        var matchingColor = readings.Where(reading => reading.Color == color).ToArray();
        ScoreMarkerMoveState? failure = matchingColor.Length switch
        {
            0 => ScoreMarkerMoveState.Missing,
            > 1 => ScoreMarkerMoveState.Ambiguous,
            _ => null
        };
        if (failure is null && readings.Any(reading => reading.Color is null))
            failure = ScoreMarkerMoveState.Ambiguous;
        if (failure is null && matchingColor[0].Status != ScoreMarkerReadingStatus.Read)
            failure = ScoreMarkerMoveState.Ambiguous;
        if (failure is null && matchingColor[0].Score != targetPrintedScore)
            failure = ScoreMarkerMoveState.WrongPosition;

        if (failure is { } rejected)
        {
            _firstMatchingAt = null;
            return new(rejected);
        }

        if (_firstMatchingAt is null || uprightRectifiedBoard.CapturedAt < _firstMatchingAt)
        {
            _firstMatchingAt = uprightRectifiedBoard.CapturedAt;
            return new(ScoreMarkerMoveState.Stabilizing);
        }
        if (uprightRectifiedBoard.CapturedAt - _firstMatchingAt < MinimumStableInterval)
            return new(ScoreMarkerMoveState.Stabilizing);

        _confirmed = true;
        return new(ScoreMarkerMoveState.Confirmed);
    }
}
