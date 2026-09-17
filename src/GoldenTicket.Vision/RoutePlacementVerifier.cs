namespace GoldenTicket.Vision;

public enum RoutePlacementState
{
    Unsupported,
    WaitingForFreshFrame,
    Incomplete,
    WrongColor,
    Ambiguous,
    Stabilizing,
    Confirmed
}

public sealed record RoutePlacementObservation(RoutePlacementState State, int MatchedCount)
{
    public bool Confirmed => State == RoutePlacementState.Confirmed;
}

/// <summary>
/// Conservative recognition of requested trains in measured physical route slots. The
/// learned model supplies train boxes, while this verifier reads their colors from the same
/// upright, rectified board frame. Every printed space must have a separate detection.
/// </summary>
public sealed class RoutePlacementVerifier
{
    private const double MinimumConfidence = .55;
    private const double MaximumAlongTolerance = 36; // physical trains can sit toward one end of a printed space
    private const double AcrossTolerance = 13;
    private const double SingleSpaceAlongTolerance = 20;
    private static readonly TimeSpan MaximumFrameAge = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumStableInterval = TimeSpan.FromSeconds(1);

    private readonly record struct Slot(double X, double Y, double TangentX, double TangentY);
    private readonly record struct LocatedCandidate(PieceCandidate Candidate, double X, double Y);
    private readonly record struct Identity(string OperationKey, string RouteId, MarkerColor Color,
        int TrainCount, long CropRevision, long ModelRevision, long CameraEpoch);

    private Identity? _identity;
    private long _lastSequence;
    private DateTimeOffset? _firstMatchingAt;
    private bool _confirmed;

    public static bool Supports(string routeId, int trainCount) =>
        trainCount is >= 1 and <= 6 &&
        ClassicUsRouteGeometry.TryGetSlots(routeId, out var slots) && slots.Count == trainCount;

    public void Reset()
    {
        _identity = null;
        _lastSequence = 0;
        _firstMatchingAt = null;
        _confirmed = false;
    }

    /// <summary>
    /// Observe one distinct ML result from the same upright crop that is displayed to players.
    /// A confirmed result is emitted once per operation. The caller must still validate the
    /// operation/state version before submitting camera evidence to the game engine.
    /// </summary>
    public RoutePlacementObservation Observe(CameraFrame uprightRectifiedBoard,
        IReadOnlyList<PieceCandidate> candidates, string routeId, MarkerColor color, int trainCount,
        string operationKey, long cropRevision, long modelRevision)
    {
        ArgumentNullException.ThrowIfNull(uprightRectifiedBoard);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);

        var identity = new Identity(operationKey, routeId, color, trainCount, cropRevision,
            modelRevision, uprightRectifiedBoard.Epoch);
        if (_identity != identity)
        {
            Reset();
            _identity = identity;
        }

        if (!Supports(routeId, trainCount))
            return new(RoutePlacementState.Unsupported, 0);
        if (uprightRectifiedBoard.Age > MaximumFrameAge ||
            Math.Abs((double)uprightRectifiedBoard.Width / uprightRectifiedBoard.Height - 1.6) > .02 ||
            uprightRectifiedBoard.Sequence <= _lastSequence)
        {
            _firstMatchingAt = null;
            return new(RoutePlacementState.WaitingForFreshFrame, 0);
        }
        _lastSequence = uprightRectifiedBoard.Sequence;
        if (_confirmed) return new(RoutePlacementState.Stabilizing, trainCount);

        ClassicUsRouteGeometry.TryGetSlots(routeId, out var measured);
        var spots = measured.Select(point =>
            new Slot(point.X * 1996, point.Y * 1248, point.TangentX, point.TangentY)).ToArray();
        var plausible = candidates.Where(candidate =>
                candidate.Kind == PieceCandidateKind.Train &&
                candidate.Confidence >= MinimumConfidence && candidate.Outline.Count >= 4)
            .Select(candidate => new LocatedCandidate(candidate,
                candidate.Outline.Average(point => point.X) * 1996,
                candidate.Outline.Average(point => point.Y) * 1248))
            .ToArray();
        var matched = 0;
        var state = RoutePlacementState.Incomplete;
        var used = new HashSet<PieceCandidate>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < spots.Length; index++)
        {
            var onSlot = plausible.Where(candidate => IsOnSlot(candidate, index, spots) &&
                    !CloserToParallelLane(candidate, routeId, spots[index]))
                .ToArray();
            if (onSlot.Length > 1)
            {
                state = RoutePlacementState.Ambiguous;
                break;
            }
            if (onSlot.Length == 0) continue;
            if (!used.Add(onSlot[0].Candidate))
            {
                state = RoutePlacementState.Ambiguous;
                break;
            }
            var read = ReadColor(uprightRectifiedBoard, onSlot[0].Candidate);
            if (read is null)
            {
                state = RoutePlacementState.Ambiguous;
                break;
            }
            if (read != color)
            {
                state = RoutePlacementState.WrongColor;
                break;
            }
            matched++;
        }

        if (state is RoutePlacementState.WrongColor or RoutePlacementState.Ambiguous || matched != trainCount)
        {
            _firstMatchingAt = null;
            return new(state, matched);
        }

        if (_firstMatchingAt is null || uprightRectifiedBoard.CapturedAt < _firstMatchingAt)
        {
            _firstMatchingAt = uprightRectifiedBoard.CapturedAt;
            return new(RoutePlacementState.Stabilizing, matched);
        }
        if (uprightRectifiedBoard.CapturedAt - _firstMatchingAt < MinimumStableInterval)
            return new(RoutePlacementState.Stabilizing, matched);

        _confirmed = true;
        return new(RoutePlacementState.Confirmed, matched);
    }

    private static bool IsOnSlot(LocatedCandidate candidate, int index, IReadOnlyList<Slot> route)
    {
        var slot = route[index];
        // The measured tangent is local: several six-space routes curve, and a city-to-city
        // axis could assign trains to neighboring lanes at one end of the route.
        var dx = slot.TangentX;
        var dy = slot.TangentY;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < .5) return false;
        var neighborDistance = route.Count == 1 ? double.PositiveInfinity : index == 0
            ? Distance(route[1].X - slot.X, route[1].Y - slot.Y)
            : index == route.Count - 1
                ? Distance(slot.X - route[index - 1].X, slot.Y - route[index - 1].Y)
                : Math.Min(Distance(slot.X - route[index - 1].X, slot.Y - route[index - 1].Y),
                    Distance(route[index + 1].X - slot.X, route[index + 1].Y - slot.Y));
        var alongTolerance = route.Count == 1 ? SingleSpaceAlongTolerance :
            Math.Min(MaximumAlongTolerance, neighborDistance * .49);
        var along = ((candidate.X - slot.X) * dx + (candidate.Y - slot.Y) * dy) / length;
        var across = ((candidate.X - slot.X) * -dy + (candidate.Y - slot.Y) * dx) / length;
        return Math.Abs(along) <= alongTolerance && Math.Abs(across) <= AcrossTolerance;
    }

    private static bool CloserToParallelLane(LocatedCandidate candidate, string routeId, Slot requested)
    {
        var suffix = routeId.EndsWith("--a", StringComparison.Ordinal) ? "--b" :
            routeId.EndsWith("--b", StringComparison.Ordinal) ? "--a" : null;
        if (suffix is null || !ClassicUsRouteGeometry.TryGetSlots(routeId[..^3] + suffix, out var otherLane))
            return false;
        var requestedDistance = Distance(candidate.X - requested.X, candidate.Y - requested.Y);
        // A piece halfway between printed parallel lanes is not trustworthy evidence for
        // either claim. Give the *other* lane a small tie margin and wait for a clearer view.
        return otherLane.Any(point =>
            Distance(candidate.X - point.X * 1996, candidate.Y - point.Y * 1248) <= requestedDistance + 2);
    }

    private static double Distance(double x, double y) => Math.Sqrt(x * x + y * y);

    private static MarkerColor? ReadColor(CameraFrame frame, PieceCandidate candidate)
    {
        var left = candidate.Outline.Min(point => point.X);
        var right = candidate.Outline.Max(point => point.X);
        var top = candidate.Outline.Min(point => point.Y);
        var bottom = candidate.Outline.Max(point => point.Y);
        var width = (right - left) * frame.Width;
        var height = (bottom - top) * frame.Height;
        if (Math.Min(width, height) < 4) return null;

        var cx = (left + right) * frame.Width / 2;
        var cy = (top + bottom) * frame.Height / 2;
        var pixels = frame.Bgra32.Span;
        Span<int> votes = stackalloc int[6];
        var samples = 0;
        for (var gy = -3; gy <= 3; gy++)
        for (var gx = -3; gx <= 3; gx++)
        {
            if (gx * gx + gy * gy > 9) continue;
            var x = Math.Clamp((int)Math.Round(cx + gx * width / 12), 0, frame.Width - 1);
            var y = Math.Clamp((int)Math.Round(cy + gy * height / 12), 0, frame.Height - 1);
            var offset = y * frame.Stride + x * 4;
            votes[ClassifyColor(pixels[offset + 2], pixels[offset + 1], pixels[offset])]++;
            samples++;
        }
        var best = 0;
        var second = 0;
        for (var index = 1; index <= 5; index++)
        {
            if (votes[index] > votes[best])
            {
                second = best;
                best = index;
            }
            else if (votes[index] > votes[second] && index != best)
            {
                second = index;
            }
        }
        if (best == 0 || votes[best] < samples * .55 || votes[best] - votes[second] < samples * .35)
            return null;
        return (MarkerColor)(best - 1);
    }

    // Mirrors the five physical-piece hue bands used for score markers, without counting the
    // printed route color as evidence: only pixels inside an ML-detected train are sampled.
    private static int ClassifyColor(int r, int g, int b)
    {
        var maximum = Math.Max(r, Math.Max(g, b));
        var minimum = Math.Min(r, Math.Min(g, b));
        var chroma = maximum - minimum;
        if (maximum <= 75 && chroma <= Math.Max(15, maximum * .45)) return (int)MarkerColor.Black + 1;
        if (maximum < 35 || chroma < 18 || chroma < maximum * .24) return 0;
        var hue = maximum == r ? 60d * (g - b) / chroma
            : maximum == g ? 120 + 60d * (b - r) / chroma : 240d + 60d * (r - g) / chroma;
        if (hue < 0) hue += 360;
        if (hue <= 25 || hue >= 345) return (int)MarkerColor.Red + 1;
        if (hue is >= 35 and <= 75) return (int)MarkerColor.Yellow + 1;
        if (hue is >= 85 and <= 170) return (int)MarkerColor.Green + 1;
        if (hue is >= 185 and <= 255) return (int)MarkerColor.Blue + 1;
        return 0;
    }
}
