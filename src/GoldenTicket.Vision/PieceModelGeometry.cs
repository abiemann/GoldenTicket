namespace GoldenTicket.Vision;

internal readonly record struct PieceModelBox(PieceCandidateKind Kind, double X, double Y,
    double Width, double Height, double Confidence);

internal static class PieceModelGeometry
{
    internal const int TileSize = 640;
    internal const int OutputRows = 8400;
    internal const int OutputColumns = 7;

    internal static int[] TileStarts(int length, int tileSize = TileSize, int stride = 512)
    {
        if (length <= 0 || tileSize <= 0 || stride <= 0 || stride > tileSize)
            throw new ArgumentOutOfRangeException(nameof(length));
        var last = Math.Max(0, length - tileSize);
        var starts = new List<int>();
        for (var start = 0; start <= last; start += stride) starts.Add(start);
        if (starts[^1] != last) starts.Add(last);
        return starts.ToArray();
    }

    internal static CameraFrame Resize(CameraFrame source, int width, int height, CancellationToken token)
    {
        if (source.Width == width && source.Height == height) return source;
        CameraFrame.ValidateSize(width, height, checked(width * height * 4));
        var input = source.Bgra32.Span;
        var output = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            var sy = Math.Clamp((y + .5) * source.Height / height - .5, 0, source.Height - 1);
            var y0 = (int)sy;
            var y1 = Math.Min(y0 + 1, source.Height - 1);
            var wy = sy - y0;
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((x + .5) * source.Width / width - .5, 0, source.Width - 1);
                var x0 = (int)sx;
                var x1 = Math.Min(x0 + 1, source.Width - 1);
                var wx = sx - x0;
                var offset = (y * width + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    var top = input[(y0 * source.Width + x0) * 4 + c] * (1 - wx) + input[(y0 * source.Width + x1) * 4 + c] * wx;
                    var bottom = input[(y1 * source.Width + x0) * 4 + c] * (1 - wx) + input[(y1 * source.Width + x1) * 4 + c] * wx;
                    output[offset + c] = (byte)Math.Clamp((int)Math.Floor(top * (1 - wy) + bottom * wy + .5), 0, 255);
                }
                output[offset + 3] = 255;
            }
        }
        return source.Derive(width, height, output);
    }

    internal static void FillInput(CameraFrame board, int startX, int startY, Span<float> input,
        CancellationToken token)
    {
        var plane = TileSize * TileSize;
        if (input.Length != plane * 3 || startX < 0 || startY < 0 || startX >= board.Width || startY >= board.Height)
            throw new ArgumentException("The tile input does not match the model contract.");
        input.Fill(114);
        var pixels = board.Bgra32.Span;
        for (var y = 0; y < Math.Min(TileSize, board.Height - startY); y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < Math.Min(TileSize, board.Width - startX); x++)
            {
                var source = ((y + startY) * board.Width + x + startX) * 4;
                var destination = y * TileSize + x;
                input[destination] = pixels[source];
                input[plane + destination] = pixels[source + 1];
                input[plane * 2 + destination] = pixels[source + 2];
            }
        }
    }

    internal static void Decode(ReadOnlySpan<float> output, int startX, int startY, double threshold,
        List<PieceModelBox> boxes)
    {
        if (output.Length != OutputRows * OutputColumns)
            throw new InvalidDataException("The piece model returned the wrong output size.");
        for (var row = 0; row < OutputRows; row++)
        {
            var p = output.Slice(row * OutputColumns, OutputColumns);
            var objectness = p[4];
            var kind = p[5] >= p[6] ? PieceCandidateKind.Train : PieceCandidateKind.PlayerMarker;
            var probability = Math.Max(p[5], p[6]);
            if (!float.IsFinite(objectness) || !float.IsFinite(probability) ||
                objectness is < 0 or > 1 || probability is < 0 or > 1) continue;
            var score = (double)objectness * probability;
            if (score < threshold || !float.IsFinite(p[0]) || !float.IsFinite(p[1]) ||
                !float.IsFinite(p[2]) || !float.IsFinite(p[3]) || p[2] <= 0 || p[3] <= 0) continue;
            // Intersect with the actual tile before translating; never infer content beyond that input.
            var x0 = Math.Clamp(p[0] - p[2] / 2d, 0, TileSize);
            var y0 = Math.Clamp(p[1] - p[3] / 2d, 0, TileSize);
            var x1 = Math.Clamp(p[0] + p[2] / 2d, 0, TileSize);
            var y1 = Math.Clamp(p[1] + p[3] / 2d, 0, TileSize);
            if (x1 - x0 < 1 || y1 - y0 < 1) continue;
            boxes.Add(new(kind, x0 + startX, y0 + startY, x1 - x0, y1 - y0, score));
        }
    }

    internal static IReadOnlyList<PieceCandidate> Merge(IEnumerable<PieceModelBox> proposals,
        int width, int height, double nmsThreshold)
    {
        var ranked = proposals.OrderByDescending(box => box.Confidence).Take(4096);
        var kept = new List<PieceModelBox>();
        foreach (var candidate in ranked)
        {
            var x = Math.Clamp(candidate.X, 0, width);
            var y = Math.Clamp(candidate.Y, 0, height);
            var right = Math.Clamp(candidate.X + candidate.Width, 0, width);
            var bottom = Math.Clamp(candidate.Y + candidate.Height, 0, height);
            var box = candidate with { X = x, Y = y, Width = right - x, Height = bottom - y };
            if (box.Width < 1 || box.Height < 1 || kept.Any(previous => previous.Kind == box.Kind && IoU(previous, box) > nmsThreshold)) continue;
            kept.Add(box);
            if (kept.Count == 512) break;
        }
        return kept.Select(box => new PieceCandidate(box.Kind, Array.AsReadOnly<NormalizedPoint>([
            new(box.X / width, box.Y / height), new((box.X + box.Width) / width, box.Y / height),
            new((box.X + box.Width) / width, (box.Y + box.Height) / height), new(box.X / width, (box.Y + box.Height) / height)
        ]), box.Confidence)).ToArray();
    }

    internal static double IoU(PieceModelBox a, PieceModelBox b)
    {
        var intersection = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)) *
            Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        return intersection / (a.Width * a.Height + b.Width * b.Height - intersection);
    }

    internal static bool OwnsCenter(PieceModelBox box, int tileX, int tileY, int[] xStarts, int[] yStarts)
    {
        return Owns(box.X + box.Width / 2, tileX, xStarts, LearnedPieceDetector.BoardWidth) &&
            Owns(box.Y + box.Height / 2, tileY, yStarts, LearnedPieceDetector.BoardHeight);

        static bool Owns(double center, int start, int[] starts, int length)
        {
            var index = Array.IndexOf(starts, start);
            if (index < 0) throw new ArgumentException("Unknown model tile.", nameof(start));
            var lower = index == 0 ? 0 : (starts[index - 1] + TileSize + start) / 2d;
            var upper = index == starts.Length - 1 ? length : (start + TileSize + starts[index + 1]) / 2d;
            return center >= lower && (index == starts.Length - 1 ? center <= upper : center < upper);
        }
    }
}
