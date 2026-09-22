namespace GoldenTicket.Vision;

public sealed record BoardInventoryRoute(string RouteId, MarkerColor Color, int TrainCount);
public sealed record UnexpectedTrainLocation(string RouteId, MarkerColor? Color, int Count);
// Bounds refer to the upright board frame that produced the observation, in the range 0..1.
// A detection can have a precise image location even when its route or color is uncertain.
public sealed record BoardInventoryDetection(double X, double Y, double Width, double Height,
    double Confidence, MarkerColor? Color, string? RouteId);

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
    int? PendingSlotMask = null, UnexpectedTrainLocation? UnexpectedTrains = null)
{
    public bool Confirmed => State == BoardInventoryState.Confirmed;
    public IReadOnlyList<BoardInventoryDetection> UnexpectedDetections { get; init; } = [];
}

/// <summary>
/// Checks that the whole rectified board contains exactly the physical trains recorded in
/// the claimed routes, optionally including a subset of one authorized pending placement.
/// The game state supplies the routes; matching pending slots do not claim them. Two distinct
/// fresh camera results must agree on the same occupied slots. Save/reload audits verify colors;
/// ordinary gameplay may retain committed route colors while checking current occupancy.
/// </summary>
public sealed class BoardInventoryVerifier
{
    private const double MinimumConfidence = .55;
    private static readonly TimeSpan MaximumFrameAge = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumStableInterval = TimeSpan.FromSeconds(1);
    private readonly record struct ExpectedSlot(double X, double Y, double TangentX, double TangentY,
        double AlongTolerance);
    private static readonly (string RouteId, ExpectedSlot Slot)[] AllMeasuredSlots =
        ClassicUsRouteGeometry.RouteIds.SelectMany(routeId =>
            ExpectedSlots(routeId).Select(slot => (routeId, slot))).ToArray();

    private readonly BoardInventoryRoute[] _routes;
    private readonly ExpectedSlot[] _slots;
    private readonly IReadOnlyDictionary<MarkerColor, int> _expectedByColor;
    private readonly BoardInventoryRoute? _pendingRoute;
    private readonly bool _verifyClaimedRouteColors;
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
        BoardInventoryRoute? pendingRoute = null, int? pendingSlotMask = null,
        bool verifyClaimedRouteColors = true)
    {
        ArgumentNullException.ThrowIfNull(expectedRoutes);
        _routes = expectedRoutes.ToArray();
        _pendingRoute = pendingRoute;
        _verifyClaimedRouteColors = verifyClaimedRouteColors;
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
        candidates = ScoringMarkerTrainResolver.Resolve(board, candidates);
        var observation = ObserveCore(board, candidates, cropRevision, modelRevision);
        return observation.State is BoardInventoryState.UnexpectedTrain or BoardInventoryState.Ambiguous
            or BoardInventoryState.WrongColor or BoardInventoryState.MissingTrains
            ? observation with { UnexpectedDetections = LocateSuspectDetections(board, candidates) }
            : observation;
    }

    private BoardInventoryObservation ObserveCore(CameraFrame board, IReadOnlyList<PieceCandidate> candidates,
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
            // Committed claims retain their owner/color during gameplay, but never substitute
            // for current piece detections. Checkpoint audits also re-read their colors.
            var verifier = new RoutePlacementVerifier();
            var observation = _verifyClaimedRouteColors
                ? verifier.Observe(board, candidates, route.RouteId, route.Color,
                    route.TrainCount, _operationKey, cropRevision, modelRevision)
                : verifier.ObserveOccupancy(board, candidates, route.RouteId, route.Color,
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
            return Unexpected(board, plausible, slots);
        if (plausible.Length < slots.Length)
            return Fail(BoardInventoryState.MissingTrains);
        var assignment = CheckAssignments(plausible, slots);
        if (assignment == BoardInventoryState.UnexpectedTrain)
            return Unexpected(board, plausible, slots);
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
                if (!Fits(x, y, slot)) continue;
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

    private BoardInventoryObservation Unexpected(CameraFrame board,
        IReadOnlyList<PieceCandidate> candidates, IReadOnlyList<ExpectedSlot> expectedSlots)
    {
        // Location is guidance only: it never changes which trains can verify the board.
        // Name a route only when the detection fits its measured spaces without a lane tie.
        var located = new List<(string RouteId, MarkerColor? Color)>();
        foreach (var candidate in candidates)
        {
            if (candidate.Outline.Any(point => !double.IsFinite(point.X) ||
                !double.IsFinite(point.Y) || point.X is < 0 or > 1 || point.Y is < 0 or > 1)) continue;
            var x = candidate.Outline.Average(point => point.X) * ClassicUsRouteGeometry.ReferenceWidth;
            var y = candidate.Outline.Average(point => point.Y) * ClassicUsRouteGeometry.ReferenceHeight;
            if (expectedSlots.Any(slot => Fits(x, y, slot))) continue;
            var nearest = AllMeasuredSlots.Where(item => Fits(x, y, item.Slot))
                .Select(item => (item.RouteId, Distance: Math.Sqrt(
                    Math.Pow(x - item.Slot.X, 2) + Math.Pow(y - item.Slot.Y, 2))))
                .OrderBy(item => item.Distance).Take(2).ToArray();
            if (nearest.Length == 0 || nearest.Length > 1 &&
                nearest[1].Distance <= nearest[0].Distance + 2) continue;
            var routeId = nearest[0].RouteId;
            if (_routes.Any(route => route.RouteId == routeId) || _pendingRoute?.RouteId == routeId)
                continue;
            located.Add((routeId, RoutePlacementVerifier.ReadCandidateColor(board, candidate)));
        }
        var issue = located.GroupBy(item => item)
            .Select(group => new UnexpectedTrainLocation(group.Key.RouteId, group.Key.Color, group.Count()))
            .OrderByDescending(group => group.Count).ThenBy(group => group.RouteId, StringComparer.Ordinal)
            .ThenBy(group => group.Color).FirstOrDefault();
        _firstMatchingAt = null;
        return new(BoardInventoryState.UnexpectedTrain, new Dictionary<MarkerColor, int>(),
            UnexpectedTrains: issue);
    }

    private IReadOnlyList<BoardInventoryDetection> LocateSuspectDetections(CameraFrame board,
        IReadOnlyList<PieceCandidate> candidates)
    {
        // Diagnostics never change acceptance. Retain every suspect box, including extras that
        // cannot be named and overlapping detections over an otherwise expected train space.
        var expected = _routes.SelectMany(route => ExpectedSlots(route.RouteId)
            .Select(slot => (route.RouteId, route.Color, VerifyColor: _verifyClaimedRouteColors, Slot: slot)))
            .ToList();
        if (_pendingRoute is { } pending)
            expected.AddRange(ExpectedSlots(pending.RouteId)
                .Where((_, index) => _requiredPendingMask is not { } mask || (mask & (1 << index)) != 0)
                .Select(slot => (pending.RouteId, pending.Color, VerifyColor: true, Slot: slot)));
        var plausible = candidates.Where(candidate => candidate.Kind == PieceCandidateKind.Train &&
                candidate.Confidence >= MinimumConfidence && candidate.Outline.Count >= 4 &&
                candidate.Outline.All(point => double.IsFinite(point.X) && double.IsFinite(point.Y) &&
                    point.X is >= 0 and <= 1 && point.Y is >= 0 and <= 1))
            .ToArray();
        var suspects = new bool[plausible.Length];
        var assignments = new List<int>[expected.Count];
        var colors = new MarkerColor?[plausible.Length];
        var routeIds = new string?[plausible.Length];
        for (var index = 0; index < plausible.Length; index++)
        {
            var candidate = plausible[index];
            var x = candidate.Outline.Average(point => point.X) * ClassicUsRouteGeometry.ReferenceWidth;
            var y = candidate.Outline.Average(point => point.Y) * ClassicUsRouteGeometry.ReferenceHeight;
            colors[index] = RoutePlacementVerifier.ReadCandidateColor(board, candidate);
            var measured = AllMeasuredSlots.Where(item => Fits(x, y, item.Slot))
                .Select(item => (item.RouteId, Distance: SlotDistance(x, y, item.Slot)))
                .OrderBy(item => item.Distance).Take(2).ToArray();
            if (measured.Length > 0 && (measured.Length == 1 ||
                    measured[1].Distance > measured[0].Distance + 2))
                routeIds[index] = measured[0].RouteId;
            var nearest = expected.Select((item, slotIndex) => (Expected: item, SlotIndex: slotIndex))
                .Where(item => Fits(x, y, item.Expected.Slot))
                .Select(item => (item.Expected, item.SlotIndex, Distance: SlotDistance(x, y, item.Expected.Slot)))
                .OrderBy(item => item.Distance).Take(2).ToArray();
            if (nearest.Length == 0)
            {
                suspects[index] = true;
                continue;
            }
            var closest = nearest[0];
            (assignments[closest.SlotIndex] ??= []).Add(index);
            suspects[index] = nearest.Length > 1 && nearest[1].Distance <= closest.Distance + 2 ||
                routeIds[index] != closest.Expected.RouteId ||
                closest.Expected.VerifyColor && colors[index] != closest.Expected.Color;
        }
        foreach (var duplicate in assignments.Where(indices => indices is { Count: > 1 }))
            foreach (var index in duplicate)
                suspects[index] = true;

        return plausible.Select((candidate, index) => (candidate, index))
            .Where(item => suspects[item.index])
            .Select(item =>
            {
                var left = item.candidate.Outline.Min(point => point.X);
                var top = item.candidate.Outline.Min(point => point.Y);
                return new BoardInventoryDetection(left, top,
                    item.candidate.Outline.Max(point => point.X) - left,
                    item.candidate.Outline.Max(point => point.Y) - top, item.candidate.Confidence,
                    colors[item.index], routeIds[item.index]);
            }).ToArray();

        static double SlotDistance(double x, double y, ExpectedSlot slot) =>
            Math.Sqrt(Math.Pow(x - slot.X, 2) + Math.Pow(y - slot.Y, 2));
    }

    private static bool Fits(double x, double y, ExpectedSlot slot)
    {
        var dx = x - slot.X;
        var dy = y - slot.Y;
        return Math.Abs(dx * slot.TangentX + dy * slot.TangentY) <= slot.AlongTolerance &&
            Math.Abs(-dx * slot.TangentY + dy * slot.TangentX) <= RoutePlacementVerifier.AcrossTolerance;
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
