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
    public IReadOnlyList<int> ProblemCandidateIndices { get; init; } = [];
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
        string operationKey, long cropRevision, long modelRevision,
        IReadOnlyList<PieceCandidate>? candidates = null)
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

        if (ReadingFailure(readings, color, targetPrintedScore, candidates) is { } rejected)
        {
            _firstMatchingAt = null;
            return new(rejected)
            {
                ProblemCandidateIndices = FindProblemCandidateIndices(readings, color, candidates)
            };
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

    internal static ScoreMarkerMoveState? ReadingFailure(IReadOnlyList<ScoreMarkerReading> readings,
        MarkerColor color, int targetPrintedScore, IReadOnlyList<PieceCandidate>? candidates)
    {
        var relevant = RelevantReadings(readings, color, candidates);
        var matchingColor = relevant.Where(reading => reading.Color == color).ToArray();
        if (matchingColor.Length == 0) return ScoreMarkerMoveState.Missing;
        if (matchingColor.Length > 1) return ScoreMarkerMoveState.Ambiguous;
        var target = matchingColor[0];
        if (target.Status != ScoreMarkerReadingStatus.Read) return ScoreMarkerMoveState.Ambiguous;
        // An unrelated coin or uncertain marker elsewhere must not veto a clear target.
        // Keep uncertainty local when another outline could describe the same physical body.
        if (relevant.Any(reading => reading.Color is null))
            return ScoreMarkerMoveState.Ambiguous;
        return target.Score != targetPrintedScore ? ScoreMarkerMoveState.WrongPosition : null;
    }

    internal static IReadOnlyList<int> FindProblemCandidateIndices(IReadOnlyList<ScoreMarkerReading> readings,
        MarkerColor color, IReadOnlyList<PieceCandidate>? candidates) =>
        RelevantReadings(readings, color, candidates)
            .Select(reading => reading.CandidateIndex)
            .Where(index => index >= 0 && (candidates is null || index < candidates.Count))
            .Distinct().ToArray();

    private static IReadOnlyList<ScoreMarkerReading> RelevantReadings(IReadOnlyList<ScoreMarkerReading> readings,
        MarkerColor color, IReadOnlyList<PieceCandidate>? candidates)
    {
        var targets = readings.Where(reading => reading.Color == color).ToArray();
        if (candidates is null || targets.Length == 0) return targets;
        var targetBoxes = targets.Select(reading => MarkerBounds(candidates, reading.CandidateIndex))
            .OfType<Box>().ToArray();
        return [.. targets, .. readings.Where(reading => reading.Color is null &&
            MarkerBounds(candidates, reading.CandidateIndex) is { } other &&
            targetBoxes.Any(target => Nearby(target, other)))];
    }

    private static bool Nearby(Box target, Box other)
    {
        if (Math.Min(target.Right, other.Right) > Math.Max(target.Left, other.Left) &&
            Math.Min(target.Bottom, other.Bottom) > Math.Max(target.Top, other.Top)) return true;
        var dx = target.CenterX - other.CenterX;
        var dy = target.CenterY - other.CenterY;
        var tolerance = Math.Max(Math.Max(target.Width, target.Height), Math.Max(other.Width, other.Height)) * .8;
        return dx * dx + dy * dy <= tolerance * tolerance;
    }

    private static Box? MarkerBounds(IReadOnlyList<PieceCandidate> candidates, int index)
    {
        if (index < 0 || index >= candidates.Count) return null;
        var candidate = candidates[index];
        if (candidate.Kind != PieceCandidateKind.PlayerMarker || candidate.Outline.Count < 3 ||
            candidate.Outline.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                point.X is < 0 or > 1 || point.Y is < 0 or > 1)) return null;
        var box = new Box(candidate.Outline.Min(point => point.X) * 1996,
            candidate.Outline.Min(point => point.Y) * 1248,
            candidate.Outline.Max(point => point.X) * 1996,
            candidate.Outline.Max(point => point.Y) * 1248);
        return box.Width > 0 && box.Height > 0 ? box : null;
    }

    private readonly record struct Box(double Left, double Top, double Right, double Bottom)
    {
        public double Width => Right - Left;
        public double Height => Bottom - Top;
        public double CenterX => (Left + Right) / 2;
        public double CenterY => (Top + Bottom) / 2;
    }
}
