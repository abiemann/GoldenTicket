namespace GoldenTicket.Vision;

public sealed record BoardInventoryRoute(string RouteId, MarkerColor Color, int TrainCount);

public enum BoardInventoryState
{
    Unsupported,
    WaitingForFreshFrame,
    MissingTrains,
    WrongColor,
    Ambiguous,
    UnexpectedTrain,
    Stabilizing,
    Confirmed
}

public sealed record BoardInventoryObservation(BoardInventoryState State,
    IReadOnlyDictionary<MarkerColor, int> ConfirmedByColor, string? RouteId = null)
{
    public bool Confirmed => State == BoardInventoryState.Confirmed;
}

/// <summary>
/// Checks that the whole rectified board contains exactly the physical trains recorded in
/// the claimed routes. It does not infer claims from a photograph: the game state supplies
/// the expected routes, and two distinct fresh camera results must agree before saving.
/// </summary>
public sealed class BoardInventoryVerifier
{
    private const double MinimumConfidence = .55;
    private static readonly TimeSpan MaximumFrameAge = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumStableInterval = TimeSpan.FromSeconds(1);
    private readonly record struct ExpectedSlot(double X, double Y, double TangentX, double TangentY,
        double AlongTolerance);

    private readonly BoardInventoryRoute[] _routes;
    private readonly ExpectedSlot[] _slots;
    private readonly IReadOnlyDictionary<MarkerColor, int> _expectedByColor;
    private readonly string _operationKey = Guid.NewGuid().ToString("N");
    private readonly bool _supported;
    private long _cropRevision = -1;
    private long _modelRevision = -1;
    private long _cameraEpoch = -1;
    private long _lastSequence;
    private DateTimeOffset? _firstMatchingAt;

    public BoardInventoryVerifier(IReadOnlyList<BoardInventoryRoute> expectedRoutes)
    {
        ArgumentNullException.ThrowIfNull(expectedRoutes);
        _routes = expectedRoutes.ToArray();
        _expectedByColor = _routes.GroupBy(route => route.Color)
            .ToDictionary(group => group.Key, group => group.Sum(route => route.TrainCount));
        _supported = _routes.All(route =>
                !string.IsNullOrWhiteSpace(route.RouteId) &&
                RoutePlacementVerifier.Supports(route.RouteId, route.TrainCount)) &&
            _routes.Select(route => route.RouteId).Distinct(StringComparer.Ordinal).Count() == _routes.Length;
        _slots = _supported ? _routes.SelectMany(route => ExpectedSlots(route.RouteId)).ToArray() : [];
    }

    public BoardInventoryObservation Observe(CameraFrame board, IReadOnlyList<PieceCandidate> candidates,
        long cropRevision, long modelRevision)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(candidates);
        if (!_supported) return Hold(BoardInventoryState.Unsupported);

        if (_cropRevision != cropRevision || _modelRevision != modelRevision || _cameraEpoch != board.Epoch)
        {
            Reset();
            _cropRevision = cropRevision;
            _modelRevision = modelRevision;
            _cameraEpoch = board.Epoch;
        }

        if (board.Age > MaximumFrameAge ||
            Math.Abs((double)board.Width / board.Height - 1.6) > .02 ||
            board.Sequence <= _lastSequence)
        {
            _firstMatchingAt = null;
            return Hold(BoardInventoryState.WaitingForFreshFrame);
        }
        _lastSequence = board.Sequence;

        var plausible = candidates.Where(candidate =>
                candidate.Kind == PieceCandidateKind.Train &&
                candidate.Confidence >= MinimumConfidence && candidate.Outline.Count >= 4)
            .ToArray();
        foreach (var route in _routes)
        {
            // RoutePlacementVerifier latches after its own confirmation. A new instance on
            // every frame is essential here: the *current* image must still contain all
            // pieces, even if an earlier image already matched.
            var verifier = new RoutePlacementVerifier();
            var observation = verifier.Observe(board, candidates, route.RouteId, route.Color,
                route.TrainCount, _operationKey, cropRevision, modelRevision);
            if (observation.MatchedCount == route.TrainCount &&
                observation.State is RoutePlacementState.Stabilizing or RoutePlacementState.Confirmed)
                continue;
            return Fail(observation.State switch
            {
                RoutePlacementState.WrongColor => BoardInventoryState.WrongColor,
                RoutePlacementState.Ambiguous => BoardInventoryState.Ambiguous,
                RoutePlacementState.WaitingForFreshFrame => BoardInventoryState.WaitingForFreshFrame,
                RoutePlacementState.Unsupported => BoardInventoryState.Unsupported,
                _ => BoardInventoryState.MissingTrains
            }, route.RouteId);
        }

        if (plausible.Length > _slots.Length)
            return Fail(BoardInventoryState.UnexpectedTrain);
        if (plausible.Length < _slots.Length)
            return Fail(BoardInventoryState.MissingTrains);
        var assignment = CheckAssignments(plausible);
        if (assignment is { } badAssignment)
            return Fail(badAssignment);

        if (_firstMatchingAt is null || board.CapturedAt < _firstMatchingAt)
        {
            _firstMatchingAt = board.CapturedAt;
            return Hold(BoardInventoryState.Stabilizing);
        }
        if (board.CapturedAt - _firstMatchingAt < MinimumStableInterval)
            return Hold(BoardInventoryState.Stabilizing);

        return new(BoardInventoryState.Confirmed, _expectedByColor);
    }

    public void Reset()
    {
        _lastSequence = 0;
        _firstMatchingAt = null;
        _cropRevision = _modelRevision = _cameraEpoch = -1;
    }

    private BoardInventoryState? CheckAssignments(IReadOnlyList<PieceCandidate> candidates)
    {
        var assigned = new int[_slots.Length];
        foreach (var candidate in candidates)
        {
            var x = candidate.Outline.Average(point => point.X) * 1996;
            var y = candidate.Outline.Average(point => point.Y) * 1248;
            var bestIndex = -1;
            var bestDistance = double.PositiveInfinity;
            var secondDistance = double.PositiveInfinity;
            for (var index = 0; index < _slots.Length; index++)
            {
                var slot = _slots[index];
                var dx = x - slot.X;
                var dy = y - slot.Y;
                var along = dx * slot.TangentX + dy * slot.TangentY;
                var across = -dx * slot.TangentY + dy * slot.TangentX;
                if (Math.Abs(along) > slot.AlongTolerance || Math.Abs(across) > 13)
                    continue;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance < bestDistance)
                {
                    secondDistance = bestDistance;
                    bestDistance = distance;
                    bestIndex = index;
                }
                else if (distance < secondDistance)
                    secondDistance = distance;
            }
            if (bestIndex < 0) return BoardInventoryState.UnexpectedTrain;
            if (secondDistance <= bestDistance + 2) return BoardInventoryState.Ambiguous;
            if (++assigned[bestIndex] > 1) return BoardInventoryState.Ambiguous;
        }
        return assigned.Any(count => count != 1) ? BoardInventoryState.MissingTrains : null;
    }

    private static IEnumerable<ExpectedSlot> ExpectedSlots(string routeId)
    {
        ClassicUsRouteGeometry.TryGetSlots(routeId, out var measured);
        for (var index = 0; index < measured.Count; index++)
        {
            var spot = measured[index];
            var neighborDistance = measured.Count == 1 ? double.PositiveInfinity : index == 0
                ? Distance(measured[1], spot)
                : index == measured.Count - 1
                    ? Distance(spot, measured[index - 1])
                    : Math.Min(Distance(spot, measured[index - 1]),
                        Distance(measured[index + 1], spot));
            var tolerance = measured.Count == 1 ? 20 : Math.Min(36, neighborDistance * .49);
            yield return new ExpectedSlot(spot.ReferenceX, spot.ReferenceY,
                spot.TangentX, spot.TangentY, tolerance);
        }

        static double Distance(BoardSlotPoint first, BoardSlotPoint second)
        {
            var dx = first.ReferenceX - second.ReferenceX;
            var dy = first.ReferenceY - second.ReferenceY;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    private BoardInventoryObservation Fail(BoardInventoryState state, string? routeId = null)
    {
        _firstMatchingAt = null;
        return Hold(state, routeId);
    }

    private static BoardInventoryObservation Hold(BoardInventoryState state, string? routeId = null) =>
        new(state, new Dictionary<MarkerColor, int>(), routeId);
}
