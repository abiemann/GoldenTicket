namespace GoldenTicket.Vision;

internal readonly record struct BoardCornerLetterbox(int SourceWidth, int SourceHeight,
    int Width, int Height, int Left, int Top)
{
    internal NormalizedPoint ToSensor(double heatmapX, double heatmapY)
    {
        // Heatmap cell centers map to input pixel centers, then undo the half-pixel resize.
        var x = ((heatmapX + .5) * 2 - Left) * SourceWidth / Width - .5;
        var y = ((heatmapY + .5) * 2 - Top) * SourceHeight / Height - .5;
        return new(x / (SourceWidth - 1), y / (SourceHeight - 1));
    }
}

internal sealed record BoardCornerGeometryResult(IReadOnlyList<NormalizedPoint> Corners,
    IReadOnlyList<double> Confidences, string? RejectionReason);

internal static class BoardCornerModelGeometry
{
    internal const int InputSize = 384;
    internal const int HeatmapSize = 192;

    internal static BoardCornerLetterbox Letterbox(int width, int height)
    {
        if (width < 2 || height < 2) throw new ArgumentOutOfRangeException(nameof(width));
        var scale = Math.Min((double)InputSize / width, (double)InputSize / height);
        var resizedWidth = Math.Clamp((int)Math.Floor(width * scale + .5), 1, InputSize);
        var resizedHeight = Math.Clamp((int)Math.Floor(height * scale + .5), 1, InputSize);
        return new(width, height, resizedWidth, resizedHeight,
            (InputSize - resizedWidth) / 2, (InputSize - resizedHeight) / 2);
    }

    internal static BoardCornerLetterbox FillInput(CameraFrame source, Span<float> input, CancellationToken token)
    {
        if (input.Length != 3 * InputSize * InputSize)
            throw new ArgumentException("The corner-model input has an incorrect length.", nameof(input));
        var transform = Letterbox(source.Width, source.Height);
        var resized = PieceModelGeometry.Resize(source, transform.Width, transform.Height, token);
        var bytes = resized.Bgra32.Span;
        var plane = InputSize * InputSize;
        input.Fill(114f / 255);
        for (var y = 0; y < transform.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < transform.Width; x++)
            {
                var src = (y * transform.Width + x) * 4;
                var dst = (y + transform.Top) * InputSize + x + transform.Left;
                input[dst] = bytes[src + 2] / 255f;
                input[plane + dst] = bytes[src + 1] / 255f;
                input[2 * plane + dst] = bytes[src] / 255f;
            }
        }
        return transform;
    }

    internal static BoardCornerGeometryResult Decode(ReadOnlySpan<float> heatmaps,
        BoardCornerLetterbox transform, double threshold, CameraFrame frame)
    {
        if (heatmaps.Length != 4 * HeatmapSize * HeatmapSize)
            throw new InvalidDataException("The board-corner model returned an incorrect heatmap size.");
        var corners = new NormalizedPoint[4];
        var confidence = new double[4];
        for (var corner = 0; corner < 4; corner++)
        {
            var map = heatmaps.Slice(corner * HeatmapSize * HeatmapSize, HeatmapSize * HeatmapSize);
            var peak = 0;
            for (var index = 0; index < map.Length; index++)
            {
                if (!float.IsFinite(map[index]) || map[index] is < 0 or > 1)
                    throw new InvalidDataException("The board-corner model produced invalid heatmap probabilities.");
                if (map[index] > map[peak]) peak = index;
            }
            confidence[corner] = map[peak];
            double mass = 0, weightedX = 0, weightedY = 0;
            var peakX = peak % HeatmapSize;
            var peakY = peak / HeatmapSize;
            for (var y = Math.Max(0, peakY - 2); y <= Math.Min(HeatmapSize - 1, peakY + 2); y++)
            for (var x = Math.Max(0, peakX - 2); x <= Math.Min(HeatmapSize - 1, peakX + 2); x++)
            {
                var weight = (double)map[y * HeatmapSize + x];
                mass += weight;
                weightedX += x * weight;
                weightedY += y * weight;
            }
            corners[corner] = mass > 0 ? transform.ToSensor(weightedX / mass, weightedY / mass) : default;
        }

        // Never manufacture an unobserved corner or select a partial board. A rejected result has
        // no usable crop; callers retain their manual selection and can explicitly try again.
        if (confidence.Any(value => value < threshold))
            return Reject("The model could not confidently locate all four board corners.");
        if (corners.Any(point => point.X is < 0 or > 1 || point.Y is < 0 or > 1))
            return Reject("The model's corners reach outside the camera image. Include the whole board.");
        if ((corners[0].X + corners[3].X) >= (corners[1].X + corners[2].X) ||
            (corners[0].Y + corners[1].Y) >= (corners[2].Y + corners[3].Y))
            return Reject("The model did not locate the corners in a consistent image order.");
        try { _ = BoardRegistration.Create(frame, corners); }
        catch (ArgumentException)
        { return Reject("The model's four corners do not form a large, stable board crop."); }
        return new(Array.AsReadOnly(corners), Array.AsReadOnly(confidence), null);

        BoardCornerGeometryResult Reject(string reason) => new([], Array.AsReadOnly(confidence), reason);
    }
}
