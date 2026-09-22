namespace GoldenTicket.Vision;

public enum MarkerColor { Blue, Red, Green, Yellow, Black }

public enum ScoreMarkerReadingStatus { Read, UnknownColor, OffTrack, AmbiguousPosition }

public sealed record ScoreMarkerReading(int CandidateIndex, MarkerColor? Color, int? Score,
    ScoreMarkerReadingStatus Status, string Reason);

/// <summary>
/// Reads the printed 1–100 perimeter of a rectified whole USA board. The ML model supplies marker
/// detections only; marker color comes from their pixel interiors. This does not infer completed laps.
/// </summary>
public static class ScoreMarkerReader
{
    private const double Left = .017;
    private const double Right = .983;
    private const double Top = .026;
    private const double Bottom = .974;
    private const double HorizontalStep = (Right - Left) / 30;
    private const double VerticalStep = (Bottom - Top) / 20;
    private const double VerticalTrackBand = .028;
    private const double HorizontalTrackBand = .042;
    public static NormalizedPoint ScoreOneCenter => new(Left, Bottom - VerticalStep);

    public static IReadOnlyList<ScoreMarkerReading> Read(CameraFrame board,
        IReadOnlyList<PieceCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(candidates);
        var markers = candidates.Select((candidate, index) => (Candidate: candidate, Index: index))
            .Where(item => item.Candidate.Kind == PieceCandidateKind.PlayerMarker)
            .Select(item =>
            {
                var box = Bounds(item.Candidate.Outline);
                return new Marker(item.Index, box, box is { } valid ? ReadColor(board, valid) : null);
            }).ToArray();
        var anchors = markers.Where(marker => marker.Box is not null)
            .SelectMany(marker => DirectOffers(marker.Box!.Value)
                .Select(offer => new Anchor(marker.Index, marker.Box.Value, marker.Color, offer))).ToArray();
        var readings = new List<ScoreMarkerReading>(markers.Length);
        foreach (var marker in markers)
        {
            if (marker.Box is not { } box)
            {
                readings.Add(new(marker.Index, null, null, ScoreMarkerReadingStatus.AmbiguousPosition,
                    "The marker outline is invalid."));
                continue;
            }
            var color = marker.Color;
            var offers = DirectOffers(box).ToList();
            var direct = offers.OrderBy(offer => offer.Distance).FirstOrDefault();
            // A marker centered on its own perimeter edge must keep that reading. In particular,
            // a corner marker cannot turn the next numbered cell on the perpendicular edge into
            // an inward neighbor of the corner. Still allow propagation for markers farther inward.
            var clearDirect = direct is not null && direct.Distance <= .25 &&
                !offers.Any(offer => offer.Score != direct.Score && offer.Distance < direct.Distance + .2);
            // Two markers can share a corner diagonally, rather than an exact row or column.
            // Only a clearly read corner marker can support this small inward region.
            var directScores = offers.Select(offer => offer.Score).ToHashSet();
            if (!clearDirect)
                offers.AddRange(SharedCornerOffers(marker, anchors, directScores));
            // Nearby markers may sit inward beside a perimeter marker. Their shared row/column
            // takes precedence over proximity to an unrelated edge near a corner. No unique-score
            // assignment is performed: every marker keeps its own evidence and can share a score.
            foreach (var anchor in anchors)
            {
                if (clearDirect) break;
                if (anchor.Index == marker.Index || anchor.Offer.Distance > .65) continue;
                var vertical = anchor.Offer.Side is Edge.Left or Edge.Right;
                var step = vertical ? VerticalStep : HorizontalStep;
                var along = vertical ? Math.Abs(box.CenterY - anchor.Box.CenterY) : Math.Abs(box.CenterX - anchor.Box.CenterX);
                if (along > step * .38) continue;
                var perpendicular = Distance(box, anchor.Offer.Side);
                var anchorDistance = Distance(anchor.Box, anchor.Offer.Side);
                // Only propagate inward, within room for several marker bodies. A lone marker
                // deep in the map cannot establish a track reading by itself.
                if (perpendicular <= anchorDistance || perpendicular > (vertical ? .14 : .19)) continue;
                if (!InwardOf(box, anchor.Box, anchor.Offer.Side)) continue;
                // A nearby marker on the same score is stronger support than one far away
                // on a perpendicular edge. Without this separation cost, Blue 21 on the
                // top edge can make Red 17 beside Green 17 on the left look ambiguous.
                var inwardGap = vertical ? Math.Abs(box.CenterX - anchor.Box.CenterX)
                    : Math.Abs(box.CenterY - anchor.Box.CenterY);
                var inwardStep = vertical ? HorizontalStep : VerticalStep;
                offers.Add(anchor.Offer with
                {
                    Distance = anchor.Offer.Distance + .1 + along / step * .5 + inwardGap / inwardStep * .5
                });
            }
            var ordered = offers.OrderBy(offer => offer.Distance).ToArray();
            if (ordered.Length == 0)
            {
                var nearTrack = Enum.GetValues<Edge>().Any(edge => Distance(box, edge) <= Band(edge));
                readings.Add(new(marker.Index, color, null,
                    nearTrack ? ScoreMarkerReadingStatus.AmbiguousPosition : ScoreMarkerReadingStatus.OffTrack,
                    nearTrack ? "The marker is between score positions." : "The marker is away from the score track."));
                continue;
            }
            var best = ordered[0];
            if (ordered.Any(offer => offer.Score != best.Score && offer.Distance < best.Distance + .2))
            {
                readings.Add(new(marker.Index, color, null, ScoreMarkerReadingStatus.AmbiguousPosition,
                    "The marker could belong to more than one score position."));
                continue;
            }
            readings.Add(color is null
                ? new(marker.Index, null, null, ScoreMarkerReadingStatus.UnknownColor, "The marker color is unclear.")
                : new(marker.Index, color, best.Score, ScoreMarkerReadingStatus.Read, "Printed score track position read."));
        }
        return CollapseDuplicateDetections(readings, markers, candidates);
    }

    private static IEnumerable<Offer> SharedCornerOffers(Marker marker,
        IReadOnlyList<Anchor> anchors, IReadOnlySet<int> directScores)
    {
        if (marker.Box is not { } box) yield break;
        foreach (var anchor in anchors)
        {
            var score = anchor.Offer.Score;
            if (anchor.Index == marker.Index || anchor.Color is null || anchor.Offer.Distance > .25 ||
                score is not (20 or 50 or 70 or 100) || directScores.Any(value => value != score)) continue;
            // Both adjoining edges must independently agree. Never chain another marker's
            // inferred position or turn a valid neighboring cell into a shared corner.
            if (!anchors.Any(other => other.Index == anchor.Index && other.Offer.Score == score &&
                    other.Offer.Side != anchor.Offer.Side && other.Offer.Distance <= .25)) continue;
            var leftCorner = score is 20 or 100;
            var topCorner = score is 20 or 50;
            var cornerX = leftCorner ? Left : Right;
            var cornerY = topCorner ? Top : Bottom;
            var directionX = leftCorner ? 1 : -1;
            var directionY = topCorner ? 1 : -1;
            var inwardX = (box.CenterX - cornerX) * directionX / HorizontalStep;
            var inwardY = (box.CenterY - cornerY) * directionY / VerticalStep;
            // Half a corner cell, with a small allowance for detection jitter. Requiring
            // an inward displacement on both axes excludes ordinary along-track boundaries.
            if (inwardX is < .2 or > .55 || inwardY is < .2 or > .55 ||
                (box.CenterX - anchor.Box.CenterX) * directionX <= 0 ||
                (box.CenterY - anchor.Box.CenterY) * directionY <= 0) continue;
            yield return anchor.Offer with
            {
                Distance = anchor.Offer.Distance + .1 + Math.Max(inwardX, inwardY) * .5
            };
        }
    }

    private static IReadOnlyList<ScoreMarkerReading> CollapseDuplicateDetections(
        IReadOnlyList<ScoreMarkerReading> readings, IReadOnlyList<Marker> markers,
        IReadOnlyList<PieceCandidate> candidates)
    {
        // The tiled model can outline one glossy scoring marker twice. If both
        // outlines overlap and yield the same color and printed score, keep the
        // stronger detection. Separate markers, different colors, and uncertain
        // readings remain visible to the verifier as separate evidence.
        var boxes = markers.Where(marker => marker.Box is not null)
            .ToDictionary(marker => marker.Index, marker => marker.Box!.Value);
        var kept = new List<ScoreMarkerReading>(readings.Count);
        foreach (var reading in readings.OrderByDescending(reading =>
                     candidates[reading.CandidateIndex].Confidence))
        {
            if (reading.Status == ScoreMarkerReadingStatus.Read &&
                boxes.TryGetValue(reading.CandidateIndex, out var box) &&
                kept.Any(previous => previous.Status == ScoreMarkerReadingStatus.Read &&
                    previous.Color == reading.Color && previous.Score == reading.Score &&
                    boxes.TryGetValue(previous.CandidateIndex, out var other) &&
                    IsSamePhysicalMarker(box, other)))
                continue;
            kept.Add(reading);
        }
        return kept.OrderBy(reading => reading.CandidateIndex).ToArray();
    }

    private static bool IsSamePhysicalMarker(Box first, Box second)
    {
        var overlapX = Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left);
        var overlapY = Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top);
        if (overlapX <= 0 || overlapY <= 0) return false;
        // Use board-pixel distances so horizontal and vertical offsets carry the
        // same meaning on the 8:5 crop. Nearby but distinct marker bodies should
        // not be merged merely because they share a score row.
        var dx = (first.CenterX - second.CenterX) * 1996;
        var dy = (first.CenterY - second.CenterY) * 1248;
        var firstSize = Math.Max((first.Right - first.Left) * 1996,
            (first.Bottom - first.Top) * 1248);
        var secondSize = Math.Max((second.Right - second.Left) * 1996,
            (second.Bottom - second.Top) * 1248);
        return dx * dx + dy * dy <= Math.Pow(Math.Max(firstSize, secondSize) * .8, 2);
    }

    private static Box? Bounds(IReadOnlyList<NormalizedPoint>? points)
    {
        if (points is null || points.Count < 3 || points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
            p.X is < 0 or > 1 || p.Y is < 0 or > 1)) return null;
        var left = points.Min(p => p.X);
        var top = points.Min(p => p.Y);
        var right = points.Max(p => p.X);
        var bottom = points.Max(p => p.Y);
        return right > left && bottom > top ? new(left, top, right, bottom) : null;
    }

    private static IEnumerable<Offer> DirectOffers(Box box)
    {
        foreach (var side in Enum.GetValues<Edge>())
        {
            var distance = Distance(box, side) / Band(side);
            if (distance > 1) continue;
            var position = side switch
            {
                Edge.Left => (Bottom - box.CenterY) / VerticalStep,
                Edge.Top => (box.CenterX - Left) / HorizontalStep,
                Edge.Right => (box.CenterY - Top) / VerticalStep,
                _ => (Right - box.CenterX) / HorizontalStep
            };
            var count = side is Edge.Left or Edge.Right ? 20 : 30;
            var index = (int)Math.Round(position);
            // Leave a gap around cell boundaries: snapping an uncertain marker would invent a score.
            if (index < 0 || index > count || Math.Abs(position - index) > .42) continue;
            var score = side switch
            {
                Edge.Left => index == 0 ? 100 : index,
                Edge.Top => 20 + index,
                Edge.Right => 50 + index,
                _ => 70 + index
            };
            yield return new(side, score, distance);
        }
    }

    private static double Band(Edge edge) => edge is Edge.Left or Edge.Right ? VerticalTrackBand : HorizontalTrackBand;

    private static double Distance(Box box, Edge edge) => edge switch
    {
        Edge.Left => Math.Abs(box.CenterX - Left),
        Edge.Right => Math.Abs(box.CenterX - Right),
        Edge.Top => Math.Abs(box.CenterY - Top),
        _ => Math.Abs(box.CenterY - Bottom)
    };

    private static bool InwardOf(Box box, Box anchor, Edge edge) => edge switch
    {
        Edge.Left => box.CenterX > anchor.CenterX,
        Edge.Right => box.CenterX < anchor.CenterX,
        Edge.Top => box.CenterY > anchor.CenterY,
        _ => box.CenterY < anchor.CenterY
    };

    private static MarkerColor? ReadColor(CameraFrame frame, Box box)
    {
        var width = (box.Right - box.Left) * frame.Width;
        var height = (box.Bottom - box.Top) * frame.Height;
        if (Math.Min(width, height) < 4) return null;
        var votes = new int[6];
        var sampled = new HashSet<int>();
        var pixels = frame.Bgra32.Span;
        for (var gy = -10; gy <= 10; gy++)
        for (var gx = -10; gx <= 10; gx++)
        {
            if (gx * gx + gy * gy > 100) continue;
            var x = Math.Clamp((int)(box.CenterX * frame.Width + gx / 10d * width * .28), 0, frame.Width - 1);
            var y = Math.Clamp((int)(box.CenterY * frame.Height + gy / 10d * height * .28), 0, frame.Height - 1);
            var offset = y * frame.Stride + x * 4;
            if (!sampled.Add(offset)) continue;
            votes[ColorClass(pixels[offset + 2], pixels[offset + 1], pixels[offset])]++;
        }
        if (sampled.Count < 12) return null;
        var ranked = Enumerable.Range(1, 5).OrderByDescending(index => votes[index]).ToArray();
        if (votes[ranked[0]] < sampled.Count * .65 || votes[ranked[0]] - votes[ranked[1]] < sampled.Count * .3) return null;
        return (MarkerColor)(ranked[0] - 1);
    }

    private static int ColorClass(int r, int g, int b)
    {
        var maximum = Math.Max(r, Math.Max(g, b));
        var minimum = Math.Min(r, Math.Min(g, b));
        var chroma = maximum - minimum;
        // Very dark saturated blue/green is uncertain, not automatically a black marker.
        if (maximum <= 75 && chroma <= Math.Max(15, maximum * .45)) return (int)MarkerColor.Black + 1;
        if (maximum < 35 || chroma < 18 || chroma < maximum * .24) return 0;
        var hue = maximum == r ? 60d * (g - b) / chroma
            : maximum == g ? 120 + 60d * (b - r) / chroma : 240 + 60d * (r - g) / chroma;
        if (hue < 0) hue += 360;
        if (hue <= 25 || hue >= 345) return (int)MarkerColor.Red + 1;
        if (hue is >= 35 and <= 75) return (int)MarkerColor.Yellow + 1;
        if (hue is >= 85 and <= 170) return (int)MarkerColor.Green + 1;
        if (hue is >= 185 and <= 255) return (int)MarkerColor.Blue + 1;
        return 0;
    }

    private enum Edge { Left, Top, Right, Bottom }
    private readonly record struct Box(double Left, double Top, double Right, double Bottom)
    {
        public double CenterX => (Left + Right) / 2;
        public double CenterY => (Top + Bottom) / 2;
    }
    private sealed record Marker(int Index, Box? Box, MarkerColor? Color);
    private sealed record Offer(Edge Side, int Score, double Distance);
    private sealed record Anchor(int Index, Box Box, MarkerColor? Color, Offer Offer);
}
