namespace GoldenTicket.Vision;

/// <summary>
/// Resolves a second, train-class outline of an independently read scoring marker for board
/// inventory checks. Raw model detections remain available for review and model training.
/// </summary>
public static class ScoringMarkerTrainResolver
{
    private const double MinimumMarkerConfidence = .55;

    public static IReadOnlyList<PieceCandidate> Resolve(CameraFrame board,
        IReadOnlyList<PieceCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(candidates);
        if (!candidates.Any(candidate => candidate.Kind == PieceCandidateKind.Train) ||
            !candidates.Any(candidate => candidate.Kind == PieceCandidateKind.PlayerMarker &&
                double.IsFinite(candidate.Confidence) && candidate.Confidence >= MinimumMarkerConfidence))
            return candidates;

        var markers = ScoreMarkerReader.Read(board, candidates)
            .Where(reading => reading.Status == ScoreMarkerReadingStatus.Read && reading.Score is not null &&
                reading.Color is not null && candidates[reading.CandidateIndex].Confidence is >= MinimumMarkerConfidence and <= 1)
            .Select(reading => (reading.Color, Box: Bounds(candidates[reading.CandidateIndex])))
            .Where(marker => marker.Box is { } box && Compact(box)).ToArray();
        if (markers.Length == 0) return candidates;

        List<PieceCandidate>? kept = null;
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var duplicate = candidate.Kind == PieceCandidateKind.Train && Bounds(candidate) is { } box &&
                Compact(box) && markers.Any(marker => SameBody(box, marker.Box!.Value) &&
                    RoutePlacementVerifier.ReadCandidateColor(board, candidate) == marker.Color);
            if (duplicate)
                kept ??= candidates.Take(index).ToList();
            else
                kept?.Add(candidate);
        }
        return kept is null ? candidates : kept.ToArray();
    }

    private static bool Compact(Box box) =>
        Math.Min(box.Width, box.Height) >= 14 && Math.Max(box.Width, box.Height) <= 72 &&
        Math.Max(box.Width, box.Height) / Math.Min(box.Width, box.Height) <= 1.6;

    private static bool SameBody(Box train, Box marker)
    {
        var intersection = Math.Max(0, Math.Min(train.Right, marker.Right) - Math.Max(train.Left, marker.Left)) *
            Math.Max(0, Math.Min(train.Bottom, marker.Bottom) - Math.Max(train.Top, marker.Top));
        var union = train.Width * train.Height + marker.Width * marker.Height - intersection;
        if (intersection / union < .65) return false;
        var dx = train.CenterX - marker.CenterX;
        var dy = train.CenterY - marker.CenterY;
        var tolerance = Math.Min(Math.Min(train.Width, train.Height), Math.Min(marker.Width, marker.Height)) * .2;
        return dx * dx + dy * dy <= tolerance * tolerance;
    }

    private static Box? Bounds(PieceCandidate candidate)
    {
        if (candidate.Outline.Count < 4 || candidate.Outline.Any(point =>
                !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                point.X is < 0 or > 1 || point.Y is < 0 or > 1))
            return null;
        // Reference-board pixels make both axes and all camera resolutions comparable.
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
