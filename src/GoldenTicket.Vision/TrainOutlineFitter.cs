namespace GoldenTicket.Vision;

/// <summary>
/// Fits visible foreground within an ML detection for display, body-center positioning and
/// guarded interior color sampling.
/// This does not detect pieces, change ML confidence, or infer direction from a printed route.
/// Uncertain fits keep the ML box.
/// </summary>
internal static class TrainOutlineFitter
{
    private const int MaximumCropSide = 128;

    internal static IReadOnlyList<PieceCandidate> Refine(CameraFrame board,
        IReadOnlyList<PieceCandidate> candidates, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(candidates);
        token.ThrowIfCancellationRequested();
        var result = candidates.ToArray();
        for (var i = 0; i < Math.Min(result.Length, 512); i++)
        {
            token.ThrowIfCancellationRequested();
            if (result[i].Kind != PieceCandidateKind.Train) continue;
            result[i] = result[i] with { OrientedOutline = Fit(board, result[i].Outline, token) };
        }
        return result;
    }

    private static IReadOnlyList<NormalizedPoint>? Fit(CameraFrame frame,
        IReadOnlyList<NormalizedPoint> outline, CancellationToken token)
    {
        if (outline.Count != 4 || outline.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
            p.X is <= 0 or >= 1 || p.Y is <= 0 or >= 1)) return null;
        var left = outline.Min(p => p.X) * frame.Width;
        var top = outline.Min(p => p.Y) * frame.Height;
        var boxWidth = (outline.Max(p => p.X) * frame.Width) - left;
        var boxHeight = (outline.Max(p => p.Y) * frame.Height) - top;
        // Tiny detections cannot provide an independent angle; huge proposals are not single trains.
        if (Math.Min(boxWidth, boxHeight) < 7 || Math.Max(boxWidth, boxHeight) > 256 ||
            left < 1 || top < 1 || left + boxWidth > frame.Width - 1 ||
            top + boxHeight > frame.Height - 1) return null;
        var step = Math.Max(1, Math.Max(boxWidth, boxHeight) / MaximumCropSide);
        var width = Math.Max(1, (int)Math.Ceiling(boxWidth / step));
        var height = Math.Max(1, (int)Math.Ceiling(boxHeight / step));
        var classes = new byte[width * height];
        var votes = new int[6];
        var centralSamples = 0;
        var pixels = frame.Bgra32.Span;
        for (var y = 0; y < height; y++)
        {
            if ((y & 7) == 0) token.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((int)(left + (x + .5) * step), 0, frame.Width - 1);
                var sy = Math.Clamp((int)(top + (y + .5) * step), 0, frame.Height - 1);
                var offset = sy * frame.Stride + sx * 4;
                var category = ForegroundClass(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
                classes[y * width + x] = category;
                var dx = (x + .5 - width / 2d) / (width * .27);
                var dy = (y + .5 - height / 2d) / (height * .27);
                if (dx * dx + dy * dy <= 1)
                {
                    votes[category]++;
                    centralSamples++;
                }
            }
        }
        var selectedClass = 1;
        for (var category = 2; category < votes.Length; category++)
            if (votes[category] > votes[selectedClass]) selectedClass = category;
        if (votes[selectedClass] < Math.Max(8, centralSamples * .4)) return null;

        // Use the central connected foreground, not every similarly colored printed slot in the crop.
        var visited = new bool[classes.Length];
        var queue = new int[classes.Length];
        List<Point>? best = null;
        var bestScore = 0d;
        for (var index = 0; index < classes.Length; index++)
        {
            if (visited[index] || classes[index] != selectedClass) continue;
            token.ThrowIfCancellationRequested();
            var head = 0;
            var tail = 1;
            queue[0] = index;
            visited[index] = true;
            var points = new List<Point>();
            var sumX = 0d;
            var sumY = 0d;
            while (head < tail)
            {
                var current = queue[head++];
                var x = current % width;
                var y = current / width;
                points.Add(new(x + .5, y + .5));
                sumX += x + .5;
                sumY += y + .5;
                for (var ny = Math.Max(0, y - 1); ny <= Math.Min(height - 1, y + 1); ny++)
                for (var nx = Math.Max(0, x - 1); nx <= Math.Min(width - 1, x + 1); nx++)
                {
                    var next = ny * width + nx;
                    if (visited[next] || classes[next] != selectedClass) continue;
                    visited[next] = true;
                    queue[tail++] = next;
                }
            }
            var dx = (sumX / points.Count - width / 2d) / width;
            var dy = (sumY / points.Count - height / 2d) / height;
            if (Math.Abs(dx) > .22 || Math.Abs(dy) > .22) continue;
            var score = points.Count * (1 - Math.Sqrt(dx * dx + dy * dy));
            if (score <= bestScore) continue;
            bestScore = score;
            best = points;
        }
        if (best is null || best.Count < Math.Max(20, classes.Length * .15) ||
            best.Count > classes.Length * .9) return null;

        var meanX = best.Average(p => p.X);
        var meanY = best.Average(p => p.Y);
        var xx = 0d;
        var yy = 0d;
        var xy = 0d;
        foreach (var p in best)
        {
            var dx = p.X - meanX;
            var dy = p.Y - meanY;
            xx += dx * dx;
            yy += dy * dy;
            xy += dx * dy;
        }
        var discriminant = Math.Sqrt((xx - yy) * (xx - yy) + 4 * xy * xy);
        if ((xx + yy + discriminant) / Math.Max(1, xx + yy - discriminant) < 2.8) return null;
        var principalAngle = .5 * Math.Atan2(2 * xy, xx - yy);
        // Only boundary samples can determine extremal projections. Searching those is equivalent
        // to searching the filled component, but avoids rescanning its interior for every angle.
        var boundary = new List<Point>();
        foreach (var point in best)
        {
            var x = (int)point.X;
            var y = (int)point.Y;
            var offset = y * width + x;
            if (x == 0 || y == 0 || x == width - 1 || y == height - 1 ||
                classes[offset - 1] != selectedClass || classes[offset + 1] != selectedClass ||
                classes[offset - width] != selectedClass || classes[offset + width] != selectedClass)
                boundary.Add(point);
        }
        var fit = Bounds(boundary, principalAngle);
        // Pixel evidence supplies the angle; the local minimum-area search removes PCA skew from
        // highlights/shadows without allowing an unrelated perpendicular board edge to take over.
        for (var degrees = -16; degrees <= 16; degrees += 2)
        {
            var trial = Bounds(boundary, principalAngle + degrees * Math.PI / 180);
            if (trial.Area < fit.Area) fit = trial;
        }
        fit = Bounds(best, fit.Angle, trimOutliers: true);
        var length = fit.MaxU - fit.MinU;
        var breadth = fit.MaxV - fit.MinV;
        if (length < breadth || length / Math.Max(1, breadth) is < 1.65 or > 5.8 ||
            breadth * step < 5 || best.Count / fit.Area < .48) return null;

        // A narrow colored stripe or a neighboring route must not replace most of the ML proposal.
        var cos = Math.Cos(fit.Angle);
        var sin = Math.Sin(fit.Angle);
        var extentX = Math.Abs(cos) * length + Math.Abs(sin) * breadth;
        var extentY = Math.Abs(sin) * length + Math.Abs(cos) * breadth;
        if (extentX < width * .64 || extentY < height * .64) return null;
        var padding = Math.Max(1, 1.25 / step);
        Point[] corners = [new(fit.MinU - padding, fit.MinV - padding),
            new(fit.MaxU + padding, fit.MinV - padding),
            new(fit.MaxU + padding, fit.MaxV + padding),
            new(fit.MinU - padding, fit.MaxV + padding)];
        var normalized = new NormalizedPoint[4];
        for (var i = 0; i < corners.Length; i++)
        {
            var x = left + (corners[i].X * cos - corners[i].Y * sin) * step;
            var y = top + (corners[i].X * sin + corners[i].Y * cos) * step;
            if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height ||
                x < left - boxWidth * .12 - 2 || x > left + boxWidth * 1.12 + 2 ||
                y < top - boxHeight * .12 - 2 || y > top + boxHeight * 1.12 + 2) return null;
            normalized[i] = new(x / frame.Width, y / frame.Height);
        }
        return Array.AsReadOnly(normalized);
    }

    private static byte ForegroundClass(int r, int g, int b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        if (max < 88 && max - min < 45) return 1;
        if (r > g * 1.23 && r > b * 1.25 && r - min > 25) return 2;
        if (Math.Min(r, g) > b * 1.3 && Math.Abs(r - g) < max * .35 && (r + g) / 2 - b > 35) return 3;
        if (g > r * 1.12 && g > b * 1.05 && g - min > 20) return 4;
        if (b > r * 1.2 && b >= g * .9 && b - min > 20) return 5;
        return 0;
    }

    private static Rectangle Bounds(List<Point> points, double angle, bool trimOutliers = false)
    {
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        if (!trimOutliers)
        {
            var minU = double.PositiveInfinity;
            var minV = double.PositiveInfinity;
            var maxU = double.NegativeInfinity;
            var maxV = double.NegativeInfinity;
            foreach (var p in points)
            {
                var projectedU = p.X * cos + p.Y * sin;
                var projectedV = -p.X * sin + p.Y * cos;
                minU = Math.Min(minU, projectedU);
                maxU = Math.Max(maxU, projectedU);
                minV = Math.Min(minV, projectedV);
                maxV = Math.Max(maxV, projectedV);
            }
            return new(angle, minU - .5, maxU + .5, minV - .5, maxV + .5);
        }
        var u = new double[points.Count];
        var v = new double[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            u[i] = points[i].X * cos + points[i].Y * sin;
            v[i] = -points[i].X * sin + points[i].Y * cos;
        }
        Array.Sort(u);
        Array.Sort(v);
        // Ignore isolated corner pixels; include a pixel's full area rather than just its center.
        var trim = (int)(points.Count * .01);
        return new(angle, u[trim] - .5, u[^(trim + 1)] + .5,
            v[trim] - .5, v[^(trim + 1)] + .5);
    }

    private readonly record struct Point(double X, double Y);
    private readonly record struct Rectangle(double Angle, double MinU, double MaxU, double MinV, double MaxV)
    {
        internal double Area => (MaxU - MinU) * (MaxV - MinV);
    }
}
