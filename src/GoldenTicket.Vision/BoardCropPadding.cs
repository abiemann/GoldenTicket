namespace GoldenTicket.Vision;

public sealed record BoardCropPaddingResult(IReadOnlyList<NormalizedPoint> Corners, bool LimitedByFrame);

/// <summary>
/// Adds a small visible margin to a detected board before it becomes the editable crop.
/// This policy does not alter model predictions or add further padding during rectification.
/// </summary>
public static class BoardCropPadding
{
    public const double MarginFraction = 0.02;

    public static BoardCropPaddingResult Expand(CameraFrame frame, IReadOnlyList<NormalizedPoint> corners)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(corners);
        var original = corners.ToArray();
        _ = BoardRegistration.Create(frame, original);
        var center = new NormalizedPoint(original.Average(p => p.X), original.Average(p => p.Y));
        // A 4% enlargement adds 2% of the board extent on each side. Normalized
        // homothety has the same effect in sensor pixels at every image resolution.
        var deltas = original.Select(p => new NormalizedPoint(
            (p.X - center.X) * (2 * MarginFraction),
            (p.Y - center.Y) * (2 * MarginFraction))).ToArray();
        var limits = original.Select((p, i) => Math.Min(
            AxisLimit(p.X, deltas[i].X), AxisLimit(p.Y, deltas[i].Y))).ToArray();
        var padded = original.Select((p, i) => Move(p, deltas[i], limits[i])).ToArray();
        var limited = limits.Any(limit => limit < 1);

        // Independently stopping outward rays preserves padding on free edges.
        // Extremely skewed quads can become non-convex, so fall back to a common
        // expansion fraction: its homothetic quad always contains the original.
        if (!IsUsable(frame, padded, original))
        {
            var commonLimit = limits.Min();
            padded = original.Select((p, i) => Move(p, deltas[i], commonLimit)).ToArray();
            if (!IsUsable(frame, padded, original)) padded = original;
            limited = true;
        }

        return new(Array.AsReadOnly(padded), limited);
    }

    private static double AxisLimit(double coordinate, double delta) => delta switch
    {
        > 0 => Math.Clamp((1 - coordinate) / delta, 0, 1),
        < 0 => Math.Clamp(-coordinate / delta, 0, 1),
        _ => 1
    };

    private static NormalizedPoint Move(NormalizedPoint point, NormalizedPoint delta, double fraction) =>
        new(Math.Clamp(point.X + delta.X * fraction, 0, 1),
            Math.Clamp(point.Y + delta.Y * fraction, 0, 1));

    private static bool IsUsable(CameraFrame frame, NormalizedPoint[] expanded, NormalizedPoint[] original)
    {
        try { _ = BoardRegistration.Create(frame, expanded); }
        catch (ArgumentException) { return false; }

        for (var i = 0; i < expanded.Length; i++)
        {
            var a = expanded[i];
            var b = expanded[(i + 1) % expanded.Length];
            foreach (var point in original)
            {
                var cross = (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);
                if (cross < -1e-12) return false;
            }
        }
        return true;
    }
}
