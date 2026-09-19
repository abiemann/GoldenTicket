namespace GoldenTicket.Vision;

/// <summary>A bounded second model view of the same frame, never a replacement for a missing corner.</summary>
internal sealed class BoardCornerRetryRegion
{
    private readonly CameraFrame _source;
    private readonly NormalizedPoint[] _proposal;
    private readonly double _spanX;
    private readonly double _spanY;

    private BoardCornerRetryRegion(CameraFrame source, NormalizedPoint[] proposal,
        int x, int y, int width, int height, double spanX, double spanY)
    {
        _source = source;
        _proposal = proposal;
        X = x; Y = y; Width = width; Height = height;
        _spanX = spanX; _spanY = spanY;
    }

    internal int X { get; }
    internal int Y { get; }
    internal int Width { get; }
    internal int Height { get; }

    // This lower threshold only proposes a second view. It never authorizes an accepted corner.
    internal static double ProposalConfidenceThreshold(double confidenceThreshold) =>
        Math.Max(.30, confidenceThreshold - .25);

    internal static BoardCornerRetryRegion? TryCreate(CameraFrame source,
        BoardCornerGeometryResult proposal, double confidenceThreshold)
    {
        var floor = ProposalConfidenceThreshold(confidenceThreshold);
        if (proposal.RejectionReason is not null || proposal.Corners.Count != 4 ||
            proposal.Confidences.Count != 4 || !double.IsFinite(confidenceThreshold) ||
            confidenceThreshold is <= 0 or > 1 ||
            proposal.Confidences.Any(value => !double.IsFinite(value) || value < floor || value > 1) ||
            proposal.Confidences.Count(value => value < confidenceThreshold) != 1 ||
            !ValidGeometry(source, proposal.Corners)) return null;

        var corners = proposal.Corners.ToArray();
        var minX = corners.Min(point => point.X);
        var maxX = corners.Max(point => point.X);
        var minY = corners.Min(point => point.Y);
        var maxY = corners.Max(point => point.Y);
        var spanX = maxX - minX;
        var spanY = maxY - minY;
        var left = Math.Max(0, (int)Math.Floor((minX - .03 * spanX) * (source.Width - 1)));
        var top = Math.Max(0, (int)Math.Floor((minY - .03 * spanY) * (source.Height - 1)));
        var right = Math.Min(source.Width - 1, (int)Math.Ceiling((maxX + .03 * spanX) * (source.Width - 1)));
        var bottom = Math.Min(source.Height - 1, (int)Math.Ceiling((maxY + .03 * spanY) * (source.Height - 1)));
        var width = right - left + 1;
        var height = bottom - top + 1;
        // A near-identical view adds another acceptance opportunity without useful extra scale/context.
        if (width < 32 || height < 32 || (long)width * height > .95 * source.Width * source.Height)
            return null;
        return new(source, corners, left, top, width, height, spanX, spanY);
    }

    internal CameraFrame Crop(CameraFrame source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(source, _source))
            throw new ArgumentException("The corner retry must use its original camera frame.", nameof(source));
        var pixels = new byte[Width * Height * 4];
        for (var row = 0; row < Height; row++)
        {
            token.ThrowIfCancellationRequested();
            source.Bgra32.Span.Slice(((Y + row) * source.Width + X) * 4, Width * 4)
                .CopyTo(pixels.AsSpan(row * Width * 4, Width * 4));
        }
        return source.Derive(Width, Height, pixels);
    }

    internal BoardCornerGeometryResult? MapAndValidate(CameraFrame source,
        BoardCornerGeometryResult retry, double confidenceThreshold)
    {
        if (!ReferenceEquals(source, _source) || retry.RejectionReason is not null ||
            retry.Corners.Count != 4 || retry.Confidences.Count != 4 ||
            !double.IsFinite(confidenceThreshold) || confidenceThreshold is <= 0 or > 1 ||
            retry.Confidences.Any(value => !double.IsFinite(value) || value < confidenceThreshold || value > 1) ||
            retry.Corners.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                point.X is <= .005 or >= .995 || point.Y is <= .005 or >= .995)) return null;

        var mapped = retry.Corners.Select(point => new NormalizedPoint(
            (X + point.X * (Width - 1)) / (source.Width - 1),
            (Y + point.Y * (Height - 1)) / (source.Height - 1))).ToArray();
        for (var index = 0; index < mapped.Length; index++)
            if (Math.Abs(mapped[index].X - _proposal[index].X) > .02 * _spanX ||
                Math.Abs(mapped[index].Y - _proposal[index].Y) > .02 * _spanY) return null;
        if (!ValidGeometry(source, mapped)) return null;
        return new(Array.AsReadOnly(mapped), Array.AsReadOnly(retry.Confidences.ToArray()), null);
    }

    private static bool ValidGeometry(CameraFrame source, IReadOnlyList<NormalizedPoint> corners)
    {
        if (corners[0].X + corners[3].X >= corners[1].X + corners[2].X ||
            corners[0].Y + corners[1].Y >= corners[2].Y + corners[3].Y) return false;
        try { _ = BoardRegistration.Create(source, corners); return true; }
        catch (ArgumentException) { return false; }
    }
}
