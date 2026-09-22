namespace GoldenTicket.Vision;

/// <summary>
/// Locates the independently fitted body inside an existing train detection. Fits never
/// create detections, change confidence, or depend on a requested route or player color.
/// Invalid or unavailable fits retain the original model-box center.
/// </summary>
public static class TrainCandidateGeometry
{
    public static NormalizedPoint GetCenter(CameraFrame frame, PieceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(candidate);
        return TryGetFittedBody(frame, candidate, out var body)
            ? new(body.CenterX / frame.Width, body.CenterY / frame.Height)
            : new(candidate.Outline.Average(point => point.X), candidate.Outline.Average(point => point.Y));
    }

    internal readonly record struct FittedBody(double CenterX, double CenterY,
        double Ux, double Uy, double Vx, double Vy);

    // These are the same bounds used for guarded interior color sampling. Positioning
    // and color sampling must agree about whether the fitted rectangle is trustworthy.
    internal static bool TryGetFittedBody(CameraFrame frame, PieceCandidate candidate, out FittedBody body)
    {
        body = default;
        var outline = candidate.OrientedOutline;
        if (candidate.Kind != PieceCandidateKind.Train || candidate.Outline.Count < 4 ||
            candidate.Outline.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                point.X is < 0 or > 1 || point.Y is < 0 or > 1) ||
            outline is not { Count: 4 } || outline.Any(point =>
                !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                point.X is < 0 or >= 1 || point.Y is < 0 or >= 1)) return false;

        var left = candidate.Outline.Min(point => point.X) * frame.Width;
        var top = candidate.Outline.Min(point => point.Y) * frame.Height;
        var width = candidate.Outline.Max(point => point.X) * frame.Width - left;
        var height = candidate.Outline.Max(point => point.Y) * frame.Height - top;
        var x0 = outline[0].X * frame.Width;
        var y0 = outline[0].Y * frame.Height;
        var ux = (outline[1].X - outline[0].X) * frame.Width;
        var uy = (outline[1].Y - outline[0].Y) * frame.Height;
        var vx = (outline[3].X - outline[0].X) * frame.Width;
        var vy = (outline[3].Y - outline[0].Y) * frame.Height;
        var uLength = Distance(ux, uy);
        var vLength = Distance(vx, vy);
        if (Math.Min(uLength, vLength) < 5 ||
            Math.Max(uLength, vLength) / Math.Min(uLength, vLength) is < 1.5 or > 6 ||
            Math.Abs(ux * vx + uy * vy) > uLength * vLength * .02 ||
            Distance(outline[2].X * frame.Width - x0 - ux - vx,
                outline[2].Y * frame.Height - y0 - uy - vy) > 1 ||
            Math.Abs(ux) + Math.Abs(vx) < width * .64 ||
            Math.Abs(uy) + Math.Abs(vy) < height * .64) return false;

        var cx = x0 + (ux + vx) / 2;
        var cy = y0 + (uy + vy) / 2;
        if (Math.Abs(cx - left - width / 2) > width * .22 ||
            Math.Abs(cy - top - height / 2) > height * .22 ||
            outline.Any(point => point.X * frame.Width < left - width * .12 - 2 ||
                point.X * frame.Width > left + width * 1.12 + 2 ||
                point.Y * frame.Height < top - height * .12 - 2 ||
                point.Y * frame.Height > top + height * 1.12 + 2)) return false;

        body = new(cx, cy, ux, uy, vx, vy);
        return true;
    }

    private static double Distance(double x, double y) => Math.Sqrt(x * x + y * y);
}
