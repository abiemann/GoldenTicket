namespace GoldenTicket.Vision;

public sealed record SavedScoreMarker(MarkerColor Color, int PrintedScore);

public enum SavedBoardRestoreStage
{
    WaitingForCamera,
    CheckingMarker,
    CheckingTrains,
    Confirmed
}

public sealed record SavedBoardRestoreObservation(SavedBoardRestoreStage Stage,
    SavedScoreMarker? Marker = null, ScoreMarkerMoveState? MarkerState = null,
    BoardInventoryObservation? Inventory = null);

/// <summary>
/// Checks the saved scoring markers in a predictable order, then every saved train route.
/// Previously checked markers must remain in place; transient missed detections pause the
/// inventory check rather than making the prompt jump between colors.
/// </summary>
public sealed class SavedBoardRestoreVerifier
{
    private readonly SavedScoreMarker[] _markers;
    private readonly ScoreMarkerMoveVerifier _markerVerifier = new();
    private readonly BoardInventoryVerifier _inventoryVerifier;
    private readonly string _operationKey = Guid.NewGuid().ToString("N");
    private int _markerIndex;
    private int[] _mismatchCounts;
    private long _epoch = -1;
    private long _cropRevision = -1;
    private long _modelRevision = -1;
    private long _lastSequence;

    public SavedBoardRestoreVerifier(IReadOnlyList<SavedScoreMarker> markers,
        IReadOnlyList<BoardInventoryRoute> routes, BoardInventoryRoute? pendingRoute = null,
        int? pendingSlotMask = null)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(routes);
        _markers = markers.ToArray();
        if (_markers.Select(marker => marker.Color).Distinct().Count() != _markers.Length)
            throw new ArgumentException("Each scoring marker color must appear once.", nameof(markers));
        _mismatchCounts = new int[_markers.Length];
        _inventoryVerifier = new BoardInventoryVerifier(routes, pendingRoute, pendingSlotMask);
    }

    public SavedBoardRestoreObservation Observe(CameraFrame board,
        IReadOnlyList<ScoreMarkerReading> scores, IReadOnlyList<PieceCandidate> candidates,
        long cropRevision, long modelRevision)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(candidates);

        if (_epoch != board.Epoch || _cropRevision != cropRevision || _modelRevision != modelRevision)
        {
            Reset();
            _epoch = board.Epoch;
            _cropRevision = cropRevision;
            _modelRevision = modelRevision;
        }
        if (board.Age > TimeSpan.FromSeconds(2) ||
            Math.Abs((double)board.Width / board.Height - 1.6) > .02 ||
            board.Sequence <= _lastSequence)
            return new(SavedBoardRestoreStage.WaitingForCamera);
        _lastSequence = board.Sequence;

        // Check previously confirmed colors on every fresh frame. A single missed model
        // detection pauses progress; two consecutive misses return to that marker's prompt.
        for (var index = 0; index < _markerIndex; index++)
        {
            if (Matches(scores, _markers[index]))
            {
                _mismatchCounts[index] = 0;
                continue;
            }
            _inventoryVerifier.Reset();
            if (++_mismatchCounts[index] < 2)
                return new(_markerIndex < _markers.Length
                    ? SavedBoardRestoreStage.CheckingMarker : SavedBoardRestoreStage.CheckingTrains,
                    _markerIndex < _markers.Length ? _markers[_markerIndex] : null);
            _markerIndex = index;
            _markerVerifier.Reset();
            Array.Clear(_mismatchCounts);
            return new(SavedBoardRestoreStage.CheckingMarker, _markers[index],
                ScoreMarkerMoveState.WrongPosition);
        }

        if (_markerIndex < _markers.Length)
        {
            var marker = _markers[_markerIndex];
            var result = _markerVerifier.Observe(board, scores, marker.Color,
                marker.PrintedScore, _operationKey, cropRevision, modelRevision);
            if (!result.Confirmed)
                return new(SavedBoardRestoreStage.CheckingMarker, marker, result.State);
            _markerIndex++;
            _markerVerifier.Reset();
            return _markerIndex < _markers.Length
                ? new(SavedBoardRestoreStage.CheckingMarker, _markers[_markerIndex])
                : new(SavedBoardRestoreStage.CheckingTrains);
        }

        var inventory = _inventoryVerifier.Observe(board, candidates, cropRevision, modelRevision);
        return new(inventory.Confirmed ? SavedBoardRestoreStage.Confirmed :
            SavedBoardRestoreStage.CheckingTrains, Inventory: inventory);
    }

    public void Reset()
    {
        _markerIndex = 0;
        Array.Clear(_mismatchCounts);
        _markerVerifier.Reset();
        _inventoryVerifier.Reset();
        _lastSequence = 0;
        _epoch = _cropRevision = _modelRevision = -1;
    }

    private static bool Matches(IReadOnlyList<ScoreMarkerReading> scores, SavedScoreMarker marker) =>
        scores.Count(score => score.Color == marker.Color) == 1 &&
        scores.All(score => score.Color is not null) &&
        scores.Any(score => score.Color == marker.Color &&
            score.Status == ScoreMarkerReadingStatus.Read && score.Score == marker.PrintedScore);
}
