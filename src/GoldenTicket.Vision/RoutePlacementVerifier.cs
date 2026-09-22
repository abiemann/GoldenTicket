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

public sealed record RoutePlacementObservation(RoutePlacementState State, int MatchedCount,
    int UnverifiedSlotMask = 0)
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
    // Reference-board pixels. Allow small sideways placement/parallax errors while the
    // parallel-lane guard below still rejects pieces between lanes or closer to the other lane.
    internal const double AcrossTolerance = 16;
    private const double SingleSpaceAlongTolerance = 20;
    private static readonly TimeSpan MaximumFrameAge = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumStableInterval = TimeSpan.FromSeconds(1);

    private readonly record struct Slot(double X, double Y, double TangentX, double TangentY);
    private readonly record struct LocatedCandidate(PieceCandidate Candidate, double X, double Y);
    private readonly record struct Identity(string OperationKey, string RouteId, MarkerColor Color,
        int TrainCount, long CropRevision, long ModelRevision, long CameraEpoch, bool VerifyColor);

    private Identity? _identity;
    private long _lastSequence;
    private DateTimeOffset? _firstMatchingAt;

    public static bool Supports(string routeId, int trainCount) =>
        trainCount is >= 1 and <= 6 &&
        ClassicUsRouteGeometry.TryGetSlots(routeId, out var slots) && slots.Count == trainCount;

    public void Reset()
    {
        _identity = null;
        _lastSequence = 0;
        _firstMatchingAt = null;
    }

    /// <summary>
    /// Observe one distinct ML result from the same upright crop that is displayed to players.
    /// A confirmed result remains valid only while each fresh frame still matches the requested
    /// route and color. The caller must still validate the operation/state version before
    /// submitting camera evidence to the game engine.
    /// </summary>
    public RoutePlacementObservation Observe(CameraFrame uprightRectifiedBoard,
        IReadOnlyList<PieceCandidate> candidates, string routeId, MarkerColor color, int trainCount,
        string operationKey, long cropRevision, long modelRevision)
        => ObserveCore(uprightRectifiedBoard, candidates, routeId, color, trainCount,
            operationKey, cropRevision, modelRevision, verifyColor: true);

    // Normal gameplay inherits the recorded color of a committed claim. Its current pieces
    // must still occupy distinct measured spaces; new claims and checkpoint audits read colors.
    internal RoutePlacementObservation ObserveOccupancy(CameraFrame uprightRectifiedBoard,
        IReadOnlyList<PieceCandidate> candidates, string routeId, MarkerColor color, int trainCount,
        string operationKey, long cropRevision, long modelRevision)
        => ObserveCore(uprightRectifiedBoard, candidates, routeId, color, trainCount,
            operationKey, cropRevision, modelRevision, verifyColor: false);

    private RoutePlacementObservation ObserveCore(CameraFrame uprightRectifiedBoard,
        IReadOnlyList<PieceCandidate> candidates, string routeId, MarkerColor color, int trainCount,
        string operationKey, long cropRevision, long modelRevision, bool verifyColor)
    {
        ArgumentNullException.ThrowIfNull(uprightRectifiedBoard);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);

        var identity = new Identity(operationKey, routeId, color, trainCount, cropRevision,
            modelRevision, uprightRectifiedBoard.Epoch, verifyColor);
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

        ClassicUsRouteGeometry.TryGetSlots(routeId, out var measured);
        var spots = measured.Select(point =>
            new Slot(point.X * 1996, point.Y * 1248, point.TangentX, point.TangentY)).ToArray();
        var plausible = candidates.Where(candidate =>
                candidate.Kind == PieceCandidateKind.Train &&
                candidate.Confidence >= MinimumConfidence && candidate.Outline.Count >= 4)
            .Select(candidate =>
            {
                var center = TrainCandidateGeometry.GetCenter(uprightRectifiedBoard, candidate);
                return new LocatedCandidate(candidate, center.X * 1996, center.Y * 1248);
            })
            .ToArray();
        var matched = 0;
        var unverifiedSlotMask = 0;
        var state = RoutePlacementState.Incomplete;
        var used = new HashSet<PieceCandidate>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < spots.Length; index++)
        {
            var onSlot = plausible.Where(candidate => IsOnSlot(candidate, index, spots) &&
                    !CloserToParallelLane(candidate, routeId, spots[index]))
                .ToArray();
            if (onSlot.Length > 1)
            {
                unverifiedSlotMask |= 1 << index;
                state = RoutePlacementState.Ambiguous;
                break;
            }
            if (onSlot.Length == 0)
            {
                unverifiedSlotMask |= 1 << index;
                continue;
            }
            if (!used.Add(onSlot[0].Candidate))
            {
                unverifiedSlotMask |= 1 << index;
                state = RoutePlacementState.Ambiguous;
                break;
            }
            if (verifyColor)
            {
                var read = ReadColor(uprightRectifiedBoard, onSlot[0].Candidate);
                if (read is null)
                {
                    unverifiedSlotMask |= 1 << index;
                    state = RoutePlacementState.Ambiguous;
                    break;
                }
                if (read != color)
                {
                    unverifiedSlotMask |= 1 << index;
                    state = RoutePlacementState.WrongColor;
                    break;
                }
            }
            matched++;
        }

        if (state is RoutePlacementState.WrongColor or RoutePlacementState.Ambiguous || matched != trainCount)
        {
            _firstMatchingAt = null;
            return new(state, matched, unverifiedSlotMask);
        }

        if (_firstMatchingAt is null || uprightRectifiedBoard.CapturedAt < _firstMatchingAt)
        {
            _firstMatchingAt = uprightRectifiedBoard.CapturedAt;
            return new(RoutePlacementState.Stabilizing, matched);
        }
        if (uprightRectifiedBoard.CapturedAt - _firstMatchingAt < MinimumStableInterval)
            return new(RoutePlacementState.Stabilizing, matched);

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
        var reading = RankColor(votes, samples);
        if (reading.Supported && reading.ClearLead) return reading.Color;

        // A diagonal train's square ML box can include neutral board beside its body.
        // Recover only missing support, never a disagreement between physical colors.
        // The image fit must reinforce the original leader with the same thresholds;
        // neither the expected player color nor the printed route chooses this sample area.
        if (!reading.ClearLead || candidate.Kind != PieceCandidateKind.Train) return null;
        var fitted = ReadFittedColor(frame, candidate, left * frame.Width, top * frame.Height,
            width, height);
        return fitted.Supported && fitted.ClearLead && fitted.Color == reading.Color
            ? fitted.Color : null;
    }

    private readonly record struct ColorReading(int Category, int Support, int RunnerUp, int Samples)
    {
        public bool Supported => Samples > 0 && Support >= Samples * .55;
        public bool ClearLead => Samples > 0 && Support - RunnerUp >= Samples * .35;
        public MarkerColor Color => (MarkerColor)(Category - 1);
    }

    private static ColorReading RankColor(ReadOnlySpan<int> votes, int samples)
    {
        // Neutral highlights reduce total support, but are not a competing piece color.
        var best = 1;
        var second = 2;
        if (votes[second] > votes[best]) (best, second) = (second, best);
        for (var index = 3; index <= 5; index++)
        {
            if (votes[index] > votes[best])
            {
                second = best;
                best = index;
            }
            else if (votes[index] > votes[second])
            {
                second = index;
            }
        }
        return new(best, votes[best], votes[second], samples);
    }

    private static ColorReading ReadFittedColor(CameraFrame frame, PieceCandidate candidate,
        double left, double top, double width, double height)
    {
        if (!TrainCandidateGeometry.TryGetFittedBody(frame, candidate, out var body)) return default;

        Span<int> votes = stackalloc int[6];
        var samples = 0;
        var pixels = frame.Bgra32.Span;
        for (var gy = -3; gy <= 3; gy++)
        for (var gx = -3; gx <= 3; gx++)
        {
            if (gx * gx + gy * gy > 9) continue;
            var sx = body.CenterX + (gx * body.Ux + gy * body.Vx) / 12;
            var sy = body.CenterY + (gx * body.Uy + gy * body.Vy) / 12;
            var x = (int)Math.Round(sx);
            var y = (int)Math.Round(sy);
            // Fitted corners may include padding outside the box; sampled pixels may not.
            // Never clamp or drop a sample to manufacture stronger support.
            if (sx < left || sx > left + width || sy < top || sy > top + height ||
                x < left || x > left + width || y < top || y > top + height ||
                x < 0 || x >= frame.Width || y < 0 || y >= frame.Height) return default;
            var offset = y * frame.Stride + x * 4;
            votes[ClassifyColor(pixels[offset + 2], pixels[offset + 1], pixels[offset])]++;
            samples++;
        }
        return RankColor(votes, samples);
    }

    /// <summary>Expose the verifier's own color reading for board-audit diagnostics.</summary>
    public static MarkerColor? ReadCandidateColor(CameraFrame frame, PieceCandidate candidate) =>
        ReadColor(frame, candidate);

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
