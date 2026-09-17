namespace GoldenTicket.Vision;

/// <summary>
/// A small upright image of this physical board. Printed artwork supplies orientation even after
/// scoring markers move or colored trains cover some routes. A match is deliberately conservative:
/// callers must hold the public crop when no rotation is unambiguous.
/// </summary>
public sealed class BoardOrientationReference
{
    public const int Width = 80;
    public const int Height = 50;
    public const double MinimumSimilarity = .43;
    public const double MinimumLead = .12;

    private readonly byte[] _pixels;

    public BoardOrientationReference(CameraFrame uprightBoard)
    {
        ArgumentNullException.ThrowIfNull(uprightBoard);
        if (uprightBoard.Width != Width || uprightBoard.Height != Height)
            throw new ArgumentException($"An orientation reference must be {Width} × {Height} pixels.", nameof(uprightBoard));
        _pixels = uprightBoard.Bgra32.ToArray();
    }

    /// <summary>Lighting-independent correlation over the interior of the board.</summary>
    public double Similarity(CameraFrame candidate, bool turnHalfCircle = false)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Width != Width || candidate.Height != Height)
            throw new ArgumentException($"An orientation candidate must be {Width} × {Height} pixels.", nameof(candidate));
        var current = candidate.Bgra32.Span;
        double sum = 0;
        for (var channel = 0; channel < 3; channel++)
        {
            double referenceMean = 0, candidateMean = 0;
            var count = 0;
            for (var y = 3; y < Height - 3; y++)
            for (var x = 4; x < Width - 4; x++)
            {
                var source = (y * Width + x) * 4 + channel;
                var target = turnHalfCircle
                    ? (((Height - 1 - y) * Width + Width - 1 - x) * 4 + channel)
                    : source;
                referenceMean += _pixels[source];
                candidateMean += current[target];
                count++;
            }
            referenceMean /= count;
            candidateMean /= count;
            double covariance = 0, referenceEnergy = 0, candidateEnergy = 0;
            for (var y = 3; y < Height - 3; y++)
            for (var x = 4; x < Width - 4; x++)
            {
                var source = (y * Width + x) * 4 + channel;
                var target = turnHalfCircle
                    ? (((Height - 1 - y) * Width + Width - 1 - x) * 4 + channel)
                    : source;
                var a = _pixels[source] - referenceMean;
                var b = current[target] - candidateMean;
                covariance += a * b;
                referenceEnergy += a * a;
                candidateEnergy += b * b;
            }
            if (referenceEnergy < 1 || candidateEnergy < 1) return 0;
            sum += covariance / Math.Sqrt(referenceEnergy * candidateEnergy);
        }
        return sum / 3;
    }

    public bool IsAligned(CameraFrame candidate)
    {
        var upright = Similarity(candidate);
        return upright >= MinimumSimilarity && upright - Similarity(candidate, turnHalfCircle: true) >= MinimumLead;
    }

    public int? ChooseOrientation(IReadOnlyList<CameraFrame> clockwiseCandidates)
    {
        ArgumentNullException.ThrowIfNull(clockwiseCandidates);
        if (clockwiseCandidates.Count != 4) throw new ArgumentException("Four rotations are required.", nameof(clockwiseCandidates));
        var scores = clockwiseCandidates.Select(candidate => Similarity(candidate)).ToArray();
        var best = Enumerable.Range(0, 4).OrderByDescending(index => scores[index]).First();
        var second = scores.Where((_, index) => index != best).Max();
        return scores[best] >= MinimumSimilarity && scores[best] - second >= MinimumLead ? best : null;
    }
}
