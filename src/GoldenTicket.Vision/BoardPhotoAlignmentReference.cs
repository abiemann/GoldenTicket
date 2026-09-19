namespace GoldenTicket.Vision;

/// <summary>
/// Refines an already upright corner detection against the artwork in an upright saved photo.
/// Only small corrections are allowed; piece detections and expected route occupancy are not used.
/// </summary>
public sealed class BoardPhotoAlignmentReference
{
    private const int Width = 320;
    private const int Height = 200;
    private const double MaximumCorrection = .01;
    private readonly Sample[] _samples;
    private readonly record struct Sample(double U, double V, double Gray, int Tile);

    public BoardPhotoAlignmentReference(CameraFrame uprightPhoto)
    {
        ArgumentNullException.ThrowIfNull(uprightPhoto);
        var samples = new List<Sample>();
        var pixels = uprightPhoto.Bgra32.Span;
        // Exclude the perimeter, where crop padding and scoring markers change most often.
        for (var y = 12; y < Height - 12; y += 2)
        for (var x = 20; x < Width - 20; x += 2)
        {
            var u = (double)x / (Width - 1);
            var v = (double)y / (Height - 1);
            samples.Add(new(u, v, Gray(pixels, uprightPhoto.Width, uprightPhoto.Height, u, v),
                Math.Min(3, x * 4 / Width) + Math.Min(3, y * 4 / Height) * 4));
        }
        _samples = samples.ToArray();
    }

    public BoardRegistration Refine(CameraFrame source, BoardRegistration initial,
        CancellationToken token = default) => TryRefine(source, initial, token) ?? initial;

    /// <summary>Retain a previous crop only when it matches the current artwork better.</summary>
    public BoardRegistration? TryRefine(CameraFrame source, BoardRegistration initial,
        BoardRegistration? previous, CancellationToken token = default)
    {
        var proposed = TryRefine(source, initial, token);
        if (previous is null || ReferenceEquals(previous, initial) || !previous.Matches(source))
            return proposed;
        var retained = TryRefine(source, previous, token);
        if (retained is null) return proposed;
        return proposed is null || Similarity(source, retained, token) > Similarity(source, proposed, token) + .002
            ? retained : proposed;
    }

    /// <summary>Returns null when this artwork cannot reliably anchor the crop.</summary>
    public BoardRegistration? TryRefine(CameraFrame source, BoardRegistration initial,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(initial);
        token.ThrowIfCancellationRequested();
        if (!initial.Matches(source))
            throw new InvalidOperationException("The camera format or epoch changed before photo alignment.");
        var initialScore = Similarity(source, initial, token);
        var offsets = new double[8];
        var best = initial;
        var bestScore = initialScore;
        // Refocusing can briefly bias the corner detector enough that corresponding artwork
        // no longer overlaps. Search translation inside the original correction budget before
        // rejecting the match; neither weak artwork nor train positions authorize a crop.
        if (bestScore < .55)
        {
            for (var x = -4; x <= 4; x++)
            for (var y = -4; y <= 4; y++)
            {
                token.ThrowIfCancellationRequested();
                if (x == 0 && y == 0) continue;
                var trial = new double[8];
                for (var corner = 0; corner < 4; corner++)
                {
                    trial[corner * 2] = x * MaximumCorrection / 4;
                    trial[corner * 2 + 1] = y * MaximumCorrection / 4;
                }
                var registration = Adjust(source, initial, trial);
                if (registration is null) continue;
                var score = Similarity(source, registration, token);
                if (score <= bestScore) continue;
                offsets = trial;
                best = registration;
                bestScore = score;
            }
        }
        // An unrelated, obscured or inverted image must not steer the board crop.
        if (bestScore < .55) return null;

        // First recover translation; then allow each corner a small independent correction.
        // The decreasing steps correspond to 2, 1 and 0.5 reference-board pixels.
        foreach (var step in new[] { 2d, 1d, .5d })
        for (var pass = 0; pass < 5; pass++)
        {
            var improved = false;
            for (var mode = 0; mode < 10; mode++)
            {
                token.ThrowIfCancellationRequested();
                var winning = offsets;
                foreach (var sign in new[] { -1, 1 })
                {
                    var trial = (double[])offsets.Clone();
                    if (mode < 2)
                    {
                        for (var corner = 0; corner < 4; corner++)
                            trial[corner * 2 + mode] += sign * step /
                                (mode == 0 ? ClassicUsRouteGeometry.ReferenceWidth : ClassicUsRouteGeometry.ReferenceHeight);
                    }
                    else
                    {
                        var axis = (mode - 2) % 2;
                        trial[mode - 2] += sign * step /
                            (axis == 0 ? ClassicUsRouteGeometry.ReferenceWidth : ClassicUsRouteGeometry.ReferenceHeight);
                    }
                    if (trial.Any(value => Math.Abs(value) > MaximumCorrection)) continue;
                    var registration = Adjust(source, initial, trial);
                    if (registration is null) continue;
                    var score = Similarity(source, registration, token);
                    if (score <= bestScore + .00001) continue;
                    bestScore = score;
                    best = registration;
                    winning = trial;
                    improved = true;
                }
                offsets = winning;
            }
            if (!improved) break;
        }
        token.ThrowIfCancellationRequested();
        // A weak improvement may just be noise or a moved piece. Do not alter that crop.
        if (bestScore < .65) return null;
        return bestScore - initialScore >= .002 ? best : initial;
    }

    private static BoardRegistration? Adjust(CameraFrame source, BoardRegistration initial, double[] offsets)
    {
        var corners = initial.Corners;
        var horizontal = new NormalizedPoint(
            (corners[1].X - corners[0].X + corners[2].X - corners[3].X) / 2,
            (corners[1].Y - corners[0].Y + corners[2].Y - corners[3].Y) / 2);
        var vertical = new NormalizedPoint(
            (corners[3].X - corners[0].X + corners[2].X - corners[1].X) / 2,
            (corners[3].Y - corners[0].Y + corners[2].Y - corners[1].Y) / 2);
        var adjusted = corners.Select((point, index) => new NormalizedPoint(
            point.X + horizontal.X * offsets[index * 2] + vertical.X * offsets[index * 2 + 1],
            point.Y + horizontal.Y * offsets[index * 2] + vertical.Y * offsets[index * 2 + 1])).ToArray();
        if (adjusted.Any(point => point.X is < 0 or > 1 || point.Y is < 0 or > 1)) return null;
        try { return BoardRegistration.Create(source, adjusted); }
        catch (ArgumentException) { return null; }
    }

    private double Similarity(CameraFrame source, BoardRegistration registration, CancellationToken token)
    {
        Span<double> count = stackalloc double[16];
        Span<double> sumA = stackalloc double[16];
        Span<double> sumB = stackalloc double[16];
        Span<double> aa = stackalloc double[16];
        Span<double> bb = stackalloc double[16];
        Span<double> ab = stackalloc double[16];
        count.Clear(); sumA.Clear(); sumB.Clear();
        aa.Clear(); bb.Clear(); ab.Clear();
        var pixels = source.Bgra32.Span;
        for (var index = 0; index < _samples.Length; index++)
        {
            if ((index & 1023) == 0) token.ThrowIfCancellationRequested();
            var sample = _samples[index];
            var point = registration.MapToSensor(sample.U, sample.V);
            var value = Gray(pixels, source.Width, source.Height, point.X, point.Y);
            var tile = sample.Tile;
            count[tile]++;
            sumA[tile] += sample.Gray; sumB[tile] += value;
            aa[tile] += sample.Gray * sample.Gray; bb[tile] += value * value;
            ab[tile] += sample.Gray * value;
        }
        var score = 0d;
        var tiles = 0;
        for (var tile = 0; tile < 16; tile++)
        {
            if (count[tile] < 20) continue;
            var energyA = aa[tile] - sumA[tile] * sumA[tile] / count[tile];
            var energyB = bb[tile] - sumB[tile] * sumB[tile] / count[tile];
            if (energyA < count[tile] * 25 || energyB < count[tile] * 25) continue;
            score += (ab[tile] - sumA[tile] * sumB[tile] / count[tile]) / Math.Sqrt(energyA * energyB);
            tiles++;
        }
        // Independent local normalization tolerates illumination gradients and shadows.
        return tiles < 12 ? 0 : score / tiles;
    }

    private static double Gray(ReadOnlySpan<byte> pixels, int width, int height, double u, double v)
    {
        var x = Math.Clamp(u * (width - 1), 0, width - 1);
        var y = Math.Clamp(v * (height - 1), 0, height - 1);
        var left = (int)x; var top = (int)y;
        var right = Math.Min(left + 1, width - 1); var bottom = Math.Min(top + 1, height - 1);
        var wx = x - left; var wy = y - top;
        var a = Luma(pixels, (top * width + left) * 4);
        var b = Luma(pixels, (top * width + right) * 4);
        var c = Luma(pixels, (bottom * width + left) * 4);
        var d = Luma(pixels, (bottom * width + right) * 4);
        return (a * (1 - wx) + b * wx) * (1 - wy) + (c * (1 - wx) + d * wx) * wy;
    }

    private static double Luma(ReadOnlySpan<byte> pixels, int offset) =>
        pixels[offset] * .114 + pixels[offset + 1] * .587 + pixels[offset + 2] * .299;
}
