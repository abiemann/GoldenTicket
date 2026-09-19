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
    IReadOnlyDictionary<MarkerColor, int> ConfirmedByColor, string? RouteId = null,
    int? PendingSlotMask = null)
{
    public bool Confirmed => State == BoardInventoryState.Confirmed;
}

/// <summary>
/// Checks that the whole rectified board contains exactly the physical trains recorded in
/// the claimed routes, optionally including a subset of one authorized pending placement.
/// The game state supplies the routes; matching pending slots do not claim them. Two distinct
/// fresh camera results must agree on the same occupied slots before saving.
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
    private readonly BoardInventoryRoute? _pendingRoute;
    private readonly int? _requiredPendingMask;
    private int? _lastPendingMask;
    private readonly string _operationKey = Guid.NewGuid().ToString("N");
    private readonly bool _supported;
    private long _cropRevision = -1;
    private long _modelRevision = -1;
    private long _cameraEpoch = -1;
    private long _lastSequence;
    private DateTimeOffset? _firstMatchingAt;

    public BoardInventoryVerifier(IReadOnlyList<BoardInventoryRoute> expectedRoutes,
        BoardInventoryRoute? pendingRoute = null, int? pendingSlotMask = null)
    {
        ArgumentNullException.ThrowIfNull(expectedRoutes);
        _routes = expectedRoutes.ToArray();
        _pendingRoute = pendingRoute;
        _requiredPendingMask = pendingSlotMask;
        _expectedByColor = _routes.GroupBy(route => route.Color)
            .ToDictionary(group => group.Key, group => group.Sum(route => route.TrainCount));
        _supported = _routes.All(route =>
                !string.IsNullOrWhiteSpace(route.RouteId) &&
                RoutePlacementVerifier.Supports(route.RouteId, route.TrainCount)) &&
            _routes.Select(route => route.RouteId).Distinct(StringComparer.Ordinal).Count() == _routes.Length &&
            (pendingRoute is null ? pendingSlotMask is null :
                !string.IsNullOrWhiteSpace(pendingRoute.RouteId) &&
                RoutePlacementVerifier.Supports(pendingRoute.RouteId, pendingRoute.TrainCount) &&
                !_routes.Any(route => route.RouteId == pendingRoute.RouteId) &&
                (pendingSlotMask is null || pendingSlotMask >= 0 &&
                    pendingSlotMask < (1 << pendingRoute.TrainCount)));
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
            // Check each route independently against this frame. Prior confirmation must
            // never substitute for the current positions and colors of its pieces.
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

        var slots = _slots;
        int? pendingMask = null;
        var counts = _expectedByColor;
        if (_pendingRoute is { } pending)
        {
            var observation = new RoutePlacementVerifier().Observe(board, candidates,
                pending.RouteId, pending.Color, pending.TrainCount, _operationKey,
                cropRevision, modelRevision);
            if (observation.State is RoutePlacementState.WrongColor or RoutePlacementState.Ambiguous)
                return Fail(observation.State == RoutePlacementState.WrongColor
                    ? BoardInventoryState.WrongColor : BoardInventoryState.Ambiguous, pending.RouteId);
            if (observation.State is not (RoutePlacementState.Incomplete or RoutePlacementState.Stabilizing
                or RoutePlacementState.Confirmed))
                return Fail(BoardInventoryState.WaitingForFreshFrame, pending.RouteId);
            pendingMask = ((1 << pending.TrainCount) - 1) & ~observation.UnverifiedSlotMask;
            if (_requiredPendingMask is { } required && pendingMask != required)
                return Fail((pendingMask.Value & ~required) != 0 ? BoardInventoryState.UnexpectedTrain :
                    BoardInventoryState.MissingTrains, pending.RouteId);
            slots = [.. _slots, .. ExpectedSlots(pending.RouteId)
                .Where((_, index) => (pendingMask.Value & (1 << index)) != 0)];
            var observedCounts = _expectedByColor.ToDictionary(pair => pair.Key, pair => pair.Value);
            observedCounts.TryGetValue(pending.Color, out var committedCount);
            observedCounts[pending.Color] = committedCount + observation.MatchedCount;
            counts = observedCounts;
        }

        if (plausible.Length > slots.Length)
            return Fail(BoardInventoryState.UnexpectedTrain);
        if (plausible.Length < slots.Length)
            return Fail(BoardInventoryState.MissingTrains);
        var assignment = CheckAssignments(plausible, slots);
        if (assignment is { } badAssignment)
            return Fail(badAssignment);

        if (_lastPendingMask != pendingMask)
        {
            _lastPendingMask = pendingMask;
            _firstMatchingAt = null;
        }
        if (_firstMatchingAt is null || board.CapturedAt < _firstMatchingAt)
        {
            _firstMatchingAt = board.CapturedAt;
            return Hold(BoardInventoryState.Stabilizing);
        }
        if (board.CapturedAt - _firstMatchingAt < MinimumStableInterval)
            return Hold(BoardInventoryState.Stabilizing);

        return new(BoardInventoryState.Confirmed, counts, PendingSlotMask: pendingMask);
    }

    public void Reset()
    {
        _lastSequence = 0;
        _firstMatchingAt = null;
        _lastPendingMask = null;
        _cropRevision = _modelRevision = _cameraEpoch = -1;
    }

    private static BoardInventoryState? CheckAssignments(IReadOnlyList<PieceCandidate> candidates,
        IReadOnlyList<ExpectedSlot> slots)
    {
        var assigned = new int[slots.Count];
        foreach (var candidate in candidates)
        {
            var x = candidate.Outline.Average(point => point.X) * 1996;
            var y = candidate.Outline.Average(point => point.Y) * 1248;
            var bestIndex = -1;
            var bestDistance = double.PositiveInfinity;
            var secondDistance = double.PositiveInfinity;
            for (var index = 0; index < slots.Count; index++)
            {
                var slot = slots[index];
                var dx = x - slot.X;
                var dy = y - slot.Y;
                var along = dx * slot.TangentX + dy * slot.TangentY;
                var across = -dx * slot.TangentY + dy * slot.TangentX;
                if (Math.Abs(along) > slot.AlongTolerance ||
                    Math.Abs(across) > RoutePlacementVerifier.AcrossTolerance)
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
