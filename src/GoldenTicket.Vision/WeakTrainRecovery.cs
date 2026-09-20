namespace GoldenTicket.Vision;

/// <summary>
/// Uses weak detections only to choose a bounded alternate view of the same image.
/// A retry must supply its own strong, spatially consistent train detection.
/// </summary>
internal static class WeakTrainRecovery
{
    internal sealed record RetryRegion(int X, int Y, PieceModelBox Seed);

    private const double GameplayConfidence = .55;
    private const double DuplicateOverlap = .45;
    private const double StrongPieceOverlap = .25;
    private static readonly int[] BaselineX = PieceModelGeometry.TileStarts(LearnedPieceDetector.BoardWidth);
    private static readonly int[] BaselineY = PieceModelGeometry.TileStarts(LearnedPieceDetector.BoardHeight);

    internal static IReadOnlyList<RetryRegion> Select(IReadOnlyList<PieceModelBox> boxes,
        double manifestThreshold)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        ValidateThreshold(manifestThreshold);
        var strongThreshold = Math.Max(GameplayConfidence, manifestThreshold);
        var strong = boxes.Where(box => ValidBox(box) && box.Confidence >= strongThreshold).ToArray();
        var selected = new List<RetryRegion>(2);
        foreach (var seed in Ranked(boxes.Where(box => EligibleSeed(box, manifestThreshold))))
        {
            if (strong.Any(box => PieceModelGeometry.IoU(seed, box) > StrongPieceOverlap) ||
                selected.Any(region => PieceModelGeometry.IoU(seed, region.Seed) > DuplicateOverlap))
                continue;
            var x = Math.Clamp((int)Math.Round(seed.X + seed.Width / 2 - PieceModelGeometry.TileSize / 2d),
                0, LearnedPieceDetector.BoardWidth - PieceModelGeometry.TileSize);
            var y = Math.Clamp((int)Math.Round(seed.Y + seed.Height / 2 - PieceModelGeometry.TileSize / 2d),
                0, LearnedPieceDetector.BoardHeight - PieceModelGeometry.TileSize);
            if (BaselineX.Contains(x) && BaselineY.Contains(y)) continue;
            selected.Add(new(x, y, seed));
            if (selected.Count == 2) break;
        }
        return selected;
    }

    // Decode has already translated retry boxes into global board coordinates.
    internal static PieceModelBox? Accept(RetryRegion region, IReadOnlyList<PieceModelBox> retry,
        IReadOnlyList<PieceModelBox> baseline, double manifestThreshold)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(retry);
        ArgumentNullException.ThrowIfNull(baseline);
        ValidateThreshold(manifestThreshold);
        if (!EligibleSeed(region.Seed, manifestThreshold) || region.X < 0 || region.Y < 0 ||
            region.X > LearnedPieceDetector.BoardWidth - PieceModelGeometry.TileSize ||
            region.Y > LearnedPieceDetector.BoardHeight - PieceModelGeometry.TileSize)
            return null;

        var strongThreshold = Math.Max(GameplayConfidence, manifestThreshold);
        var strong = baseline.Where(box => ValidBox(box) && box.Confidence >= strongThreshold).ToArray();
        var matches = Ranked(retry.Where(box => ValidBox(box) && box.Kind == PieceCandidateKind.Train &&
            box.Confidence >= strongThreshold &&
            box.X >= region.X + 2 && box.Y >= region.Y + 2 &&
            box.X + box.Width <= region.X + PieceModelGeometry.TileSize - 2 &&
            box.Y + box.Height <= region.Y + PieceModelGeometry.TileSize - 2 &&
            AgreesWithSeed(box, region.Seed) &&
            !strong.Any(existing => PieceModelGeometry.IoU(box, existing) > StrongPieceOverlap))).ToArray();
        if (matches.Length == 0) return null;
        // Equivalent model boxes collapse under normal NMS. Distinct plausible matches
        // are ambiguous; choosing one must not hide another physical train.
        return matches.Skip(1).Any(box => PieceModelGeometry.IoU(matches[0], box) <= DuplicateOverlap)
            ? null : matches[0];
    }

    private static bool AgreesWithSeed(PieceModelBox box, PieceModelBox seed)
    {
        if (box.Width / seed.Width is < .6 or > 1.6 ||
            box.Height / seed.Height is < .6 or > 1.6 || PieceModelGeometry.IoU(box, seed) < .5)
            return false;
        var dx = box.X + box.Width / 2 - seed.X - seed.Width / 2;
        var dy = box.Y + box.Height / 2 - seed.Y - seed.Height / 2;
        var tolerance = Math.Max(6, Math.Min(seed.Width, seed.Height) * .3);
        return dx * dx + dy * dy <= tolerance * tolerance;
    }

    private static bool EligibleSeed(PieceModelBox box, double threshold) =>
        ValidBox(box) && box.Kind == PieceCandidateKind.Train &&
        box.Confidence >= threshold && box.Confidence < Math.Max(GameplayConfidence, threshold) &&
        Math.Min(box.Width, box.Height) >= 5 && Math.Max(box.Width, box.Height) <= 128;

    private static bool ValidBox(PieceModelBox box) =>
        double.IsFinite(box.X) && double.IsFinite(box.Y) && double.IsFinite(box.Width) &&
        double.IsFinite(box.Height) && double.IsFinite(box.Confidence) &&
        box.X >= 0 && box.Y >= 0 && box.Width > 0 && box.Height > 0 &&
        box.X + box.Width <= LearnedPieceDetector.BoardWidth &&
        box.Y + box.Height <= LearnedPieceDetector.BoardHeight && box.Confidence is >= 0 and <= 1;

    private static IOrderedEnumerable<PieceModelBox> Ranked(IEnumerable<PieceModelBox> boxes) =>
        boxes.OrderByDescending(box => box.Confidence).ThenBy(box => box.X).ThenBy(box => box.Y)
            .ThenBy(box => box.Width).ThenBy(box => box.Height);

    private static void ValidateThreshold(double threshold)
    {
        if (!double.IsFinite(threshold) || threshold is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold));
    }
}
