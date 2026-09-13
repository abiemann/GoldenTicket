namespace GoldenTicket.Vision;

public enum PieceCandidateKind { Train, PlayerMarker }
public enum PieceDetectionState { NoReference, Ready, Stale, CameraChanged, SceneChanged, Moving, InsufficientDetail }

/// <summary>An experimental visual candidate, never authoritative route occupancy or a game command.</summary>
public sealed record PieceCandidate(PieceCandidateKind Kind, IReadOnlyList<NormalizedPoint> Outline, double Confidence);

public sealed record PieceDetectionResult(PieceDetectionState State, IReadOnlyList<PieceCandidate> Candidates,
    long FrameSequence, long CameraEpoch, long ReferenceRevision, double ChangedFraction);

/// <summary>
/// Bounded, offline reference differencing for the classic board's five plastic piece colors.
/// Inputs are equally rectified, unannotated board images. The owner must clear this detector whenever
/// the board crop, camera, or image-processing settings change. A reference containing pieces cannot
/// identify those unchanged pieces; the intended reference is the empty board, including its score track.
/// This is an experimental appearance/shape heuristic, not a trained detector or a board verifier.
/// Instances are intentionally single-consumer; the camera pipeline serializes reference and frame work.
/// </summary>
public sealed class PieceCandidateDetector
{
    private const int MaximumSampleWidth = 960;
    private const int MaximumSampleHeight = 640;
    private static readonly TimeSpan MaximumFrameAge = TimeSpan.FromSeconds(2);
    private SampleImage? _reference;
    private SampleImage? _previous;
    private long _epoch;
    private long _referenceSequence;
    private long _lastSequence;
    private int _sensorWidth;
    private int _sensorHeight;
    private long _revision;

    public bool HasReference => _reference is not null;
    public long ReferenceRevision => _revision;

    public void SetReference(CameraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Age > MaximumFrameAge)
            throw new InvalidOperationException("Use a fresh board image for the empty-board reference.");
        var sample = Sample(frame);
        if (Detail(sample) < 2.0)
            throw new InvalidOperationException("The empty-board image has too little detail. Check the board crop, focus and lighting.");
        _reference = sample;
        _previous = null;
        _epoch = frame.Epoch;
        _referenceSequence = _lastSequence = frame.Sequence;
        _sensorWidth = frame.Width;
        _sensorHeight = frame.Height;
        _revision++;
    }

    public void Clear()
    {
        _reference = _previous = null;
        _revision++;
    }

    public PieceDetectionResult Detect(CameraFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        if (_reference is not { } reference) return Hold(PieceDetectionState.NoReference, frame);
        if (frame.Epoch != _epoch || frame.Width != _sensorWidth || frame.Height != _sensorHeight)
            return Hold(PieceDetectionState.CameraChanged, frame);
        if (frame.Age > MaximumFrameAge || frame.Sequence <= _referenceSequence || frame.Sequence <= _lastSequence)
            return Hold(PieceDetectionState.Stale, frame);
        _lastSequence = frame.Sequence;
        var current = Sample(frame);
        if (Detail(current) < 2.0) return Hold(PieceDetectionState.InsufficientDetail, frame);

        var (shiftX, shiftY, error) = Align(reference, current, cancellationToken);
        // Small translation accommodates sensor/crop rounding. Large motion, rotation or changed lighting
        // must suppress candidates instead of outlining the printed routes as newly placed trains.
        if (Math.Abs(shiftX) >= 4 || Math.Abs(shiftY) >= 4 || error > 14)
            return Hold(PieceDetectionState.SceneChanged, frame);

        var difference = Difference(reference, current, shiftX, shiftY, cancellationToken);
        var changed = (double)difference.Count(value => value > 24) / difference.Length;
        if (changed > .14) return Hold(PieceDetectionState.SceneChanged, frame, changed);
        if (_previous is { } previous && Motion(previous, current) > .045)
        {
            _previous = current;
            return new(PieceDetectionState.Moving, Array.Empty<PieceCandidate>(), frame.Sequence, frame.Epoch, _revision, changed);
        }
        _previous = current;

        var components = new List<Component>();
        for (var color = 0; color < 5; color++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mask = MakeMask(current, difference, color);
            Close(mask, current.Width, current.Height);
            CollectComponents(current, mask, color, components, cancellationToken);
        }
        var candidates = SelectCandidates(components, current.Width, current.Height);
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.Age > MaximumFrameAge) return Hold(PieceDetectionState.Stale, frame, changed);
        return new(PieceDetectionState.Ready, candidates.AsReadOnly(), frame.Sequence, frame.Epoch, _revision, changed);
    }

    private PieceDetectionResult Hold(PieceDetectionState state, CameraFrame frame, double changed = 0)
    {
        _previous = null;
        return new(state, Array.Empty<PieceCandidate>(), frame.Sequence, frame.Epoch, _revision, changed);
    }

    private static SampleImage Sample(CameraFrame frame)
    {
        if (frame.Width < 320 || frame.Height < 200)
            throw new ArgumentException("Piece preview needs a board image of at least 320 by 200 pixels.", nameof(frame));
        var scale = Math.Min(1, Math.Min((double)MaximumSampleWidth / frame.Width, (double)MaximumSampleHeight / frame.Height));
        var width = (int)Math.Round(frame.Width * scale);
        var height = (int)Math.Round(frame.Height * scale);
        var rgb = new byte[width * height * 3];
        var source = frame.Bgra32.Span;
        // Area sampling avoids inventing high-frequency evidence from 4K enhancement or nearest-neighbor aliasing.
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var left = x * frame.Width / width;
            var right = Math.Max(left + 1, (x + 1) * frame.Width / width);
            var top = y * frame.Height / height;
            var bottom = Math.Max(top + 1, (y + 1) * frame.Height / height);
            var red = 0; var green = 0; var blue = 0;
            for (var sy = top; sy < bottom; sy++)
            for (var sx = left; sx < right; sx++)
            {
                var index = (sy * frame.Width + sx) * 4;
                blue += source[index]; green += source[index + 1]; red += source[index + 2];
            }
            var count = (right - left) * (bottom - top);
            var destination = (y * width + x) * 3;
            rgb[destination] = (byte)(red / count);
            rgb[destination + 1] = (byte)(green / count);
            rgb[destination + 2] = (byte)(blue / count);
        }
        return new(width, height, rgb);
    }

    private static double Detail(SampleImage image)
    {
        long total = 0;
        var count = 0;
        for (var y = 2; y < image.Height - 2; y += 3)
        for (var x = 2; x < image.Width - 2; x += 3)
        {
            var index = (y * image.Width + x) * 3;
            total += Math.Abs(image.Rgb[index + 1] - image.Rgb[index - 3 + 1]);
            total += Math.Abs(image.Rgb[index + 1] - image.Rgb[index - image.Width * 3 + 1]);
            count += 2;
        }
        return (double)total / count;
    }

    private static (int X, int Y, double Error) Align(SampleImage reference, SampleImage current, CancellationToken token)
    {
        var best = double.MaxValue;
        var bestX = 0; var bestY = 0;
        for (var dy = -4; dy <= 4; dy++)
        for (var dx = -4; dx <= 4; dx++)
        {
            token.ThrowIfCancellationRequested();
            long error = 0;
            var count = 0;
            for (var y = 8; y < current.Height - 8; y += 6)
            for (var x = 8; x < current.Width - 8; x += 6)
            {
                var a = ((y + dy) * current.Width + x + dx) * 3;
                var b = (y * current.Width + x) * 3;
                var delta = Math.Abs(reference.Rgb[a] - current.Rgb[b]) +
                    Math.Abs(reference.Rgb[a + 1] - current.Rgb[b + 1]) +
                    Math.Abs(reference.Rgb[a + 2] - current.Rgb[b + 2]);
                error += Math.Min(delta, 90);
                count++;
            }
            var mean = (double)error / count / 3;
            // Prefer no motion in tied/repeated board textures.
            if (mean < best - .001 || Math.Abs(mean - best) <= .001 && Math.Abs(dx) + Math.Abs(dy) < Math.Abs(bestX) + Math.Abs(bestY))
            { best = mean; bestX = dx; bestY = dy; }
        }
        return (bestX, bestY, best);
    }

    private static byte[] Difference(SampleImage reference, SampleImage current, int dx, int dy, CancellationToken token)
    {
        var result = new byte[current.Width * current.Height];
        for (var y = 1; y < current.Height - 1; y++)
        {
            if ((y & 31) == 0) token.ThrowIfCancellationRequested();
            for (var x = 1; x < current.Width - 1; x++)
            {
                var b = (y * current.Width + x) * 3;
                var minimum = 255;
                // Compare a small neighborhood to tolerate subpixel shifts, not a generated/sharpened image.
                for (var oy = -1; oy <= 1; oy++)
                for (var ox = -1; ox <= 1; ox++)
                {
                    var sx = Math.Clamp(x + dx + ox, 0, current.Width - 1);
                    var sy = Math.Clamp(y + dy + oy, 0, current.Height - 1);
                    var a = (sy * current.Width + sx) * 3;
                    var delta = Math.Max(Math.Abs(reference.Rgb[a] - current.Rgb[b]),
                        Math.Max(Math.Abs(reference.Rgb[a + 1] - current.Rgb[b + 1]), Math.Abs(reference.Rgb[a + 2] - current.Rgb[b + 2])));
                    minimum = Math.Min(minimum, delta);
                }
                result[y * current.Width + x] = (byte)minimum;
            }
        }
        return result;
    }

    private static double Motion(SampleImage previous, SampleImage current)
    {
        var changed = 0; var count = 0;
        for (var i = 0; i < current.Rgb.Length; i += 12)
        {
            if (Math.Abs(previous.Rgb[i] - current.Rgb[i]) + Math.Abs(previous.Rgb[i + 1] - current.Rgb[i + 1]) +
                Math.Abs(previous.Rgb[i + 2] - current.Rgb[i + 2]) > 90) changed++;
            count++;
        }
        return (double)changed / count;
    }

    private static bool[] MakeMask(SampleImage current, byte[] difference, int color)
    {
        var mask = new bool[difference.Length];
        for (var i = 0; i < mask.Length; i++)
        {
            if (difference[i] <= (color == 0 ? 10 : 16)) continue;
            var index = i * 3;
            var r = current.Rgb[index]; var g = current.Rgb[index + 1]; var b = current.Rgb[index + 2];
            mask[i] = color switch
            {
                0 => Math.Max(r, Math.Max(g, b)) < 70,
                1 => r > g * 1.3 && r > b * 1.3 && r > 60,
                2 => g > r * 1.15 && g > b * 1.08 && g > 30,
                3 => b > r * 1.35 && b > g * 1.05 && b > 40,
                _ => r > 80 && g > 70 && b < Math.Min(r, g) * .7 && r < g * 1.5 && g < r * 1.4
            };
        }
        return mask;
    }

    private static void Close(bool[] mask, int width, int height)
    {
        var dilation = new bool[mask.Length];
        for (var y = 1; y < height - 1; y++)
        for (var x = 1; x < width - 1; x++)
        {
            var index = y * width + x;
            var found = false;
            for (var dy = -1; dy <= 1 && !found; dy++)
            for (var dx = -1; dx <= 1; dx++)
                if (mask[index + dy * width + dx]) { found = true; break; }
            dilation[index] = found;
        }
        Array.Clear(mask);
        for (var y = 1; y < height - 1; y++)
        for (var x = 1; x < width - 1; x++)
        {
            var index = y * width + x;
            var found = true;
            for (var dy = -1; dy <= 1 && found; dy++)
            for (var dx = -1; dx <= 1; dx++)
                if (!dilation[index + dy * width + dx]) { found = false; break; }
            mask[index] = found;
        }
    }

    private static void CollectComponents(SampleImage current, bool[] mask, int color, List<Component> output, CancellationToken token)
    {
        var queue = new int[mask.Length];
        for (var start = 0; start < mask.Length; start++)
        {
            if ((start & 16383) == 0) token.ThrowIfCancellationRequested();
            if (!mask[start]) continue;
            var head = 0; var tail = 1;
            queue[0] = start; mask[start] = false;
            while (head < tail)
            {
                var position = queue[head++];
                var x = position % current.Width; var y = position / current.Width;
                Add(x > 0 ? position - 1 : -1);
                Add(x + 1 < current.Width ? position + 1 : -1);
                Add(y > 0 ? position - current.Width : -1);
                Add(y + 1 < current.Height ? position + current.Width : -1);
            }
            // Very large regions are hands/occlusions or a changed scene, not piece candidates.
            var scale = current.Width / 960d;
            if (tail >= Math.Max(8, 20 * scale * scale) && tail <= 6000 * scale * scale)
            {
                var component = Component.Create(queue.AsSpan(0, tail), current.Width, color);
                var border = component.MinX < current.Width * .05 || component.MaxX > current.Width * .95 ||
                    component.MinY < current.Height * .05 || component.MaxY > current.Height * .95;
                if (border && component.Length > 10 * scale && component.Thickness > 8 * scale &&
                    component.Length / component.Thickness < 2.4 && component.Length < 55 * scale)
                {
                    component.Marker = true;
                    output.Add(component);
                }
                else if (!border && component.Length > 20 * scale && component.Thickness > 4 * scale &&
                    component.Thickness < 22 * scale && component.Length / component.Thickness > 1.9 &&
                    component.Length < 140 * scale && tail / (component.Length * component.Thickness) > .20)
                    output.Add(component);
            }

            void Add(int position)
            {
                if (position < 0 || !mask[position]) return;
                mask[position] = false;
                queue[tail++] = position;
            }
        }
    }

    private static List<PieceCandidate> SelectCandidates(List<Component> components, int width, int height)
    {
        var accepted = new List<Component>();
        foreach (var candidate in components.OrderByDescending(component => component.Pixels *
            (component.Marker && component.Color != 0 ? 2 : 1)))
        {
            var duplicate = accepted.Any(other => other.Marker == candidate.Marker && (candidate.Marker
                ? Math.Pow(candidate.CenterX - other.CenterX, 2) + Math.Pow(candidate.CenterY - other.CenterY, 2) < Math.Pow(width * .024, 2)
                : Overlaps(candidate, other)));
            if (!duplicate) accepted.Add(candidate);
            if (accepted.Count >= 160) break;
        }
        var result = new List<PieceCandidate>();
        foreach (var component in accepted)
        {
            if (component.Marker)
            {
                var side = Math.Clamp(Math.Max(component.MaxX - component.MinX, component.MaxY - component.MinY), width * .020, width * .028);
                var x = Math.Clamp(component.CenterX, side / 2, width - 1 - side / 2);
                var y = Math.Clamp(component.CenterY, side / 2, height - 1 - side / 2);
                Add(PieceCandidateKind.PlayerMarker, [(x - side / 2, y - side / 2), (x + side / 2, y - side / 2),
                    (x + side / 2, y + side / 2), (x - side / 2, y + side / 2)], .6);
                continue;
            }
            // Adjacent same-color trains can touch. Split elongated appearance groups at the expected
            // physical train scale of the classic board. These are candidate extents, never verified counts.
            var nominalLength = width * .035;
            var count = Math.Clamp((int)Math.Round(component.Length / nominalLength), 1, 6);
            var padding = Math.Max(1, width / 960d);
            var thicknessLow = component.MinV - padding;
            var thicknessHigh = component.MaxV + padding;
            for (var i = 0; i < count; i++)
            {
                var low = component.MinU + component.Length * i / count - padding;
                var high = component.MinU + component.Length * (i + 1) / count + padding;
                Add(PieceCandidateKind.Train, [component.Point(low, thicknessLow), component.Point(high, thicknessLow),
                    component.Point(high, thicknessHigh), component.Point(low, thicknessHigh)], component.Color == 0 ? .45 : .65);
            }
        }
        return result;

        void Add(PieceCandidateKind kind, (double X, double Y)[] points, double score)
        {
            var normalized = points.Select(p => new NormalizedPoint(Math.Clamp(p.X / (width - 1), 0, 1),
                Math.Clamp(p.Y / (height - 1), 0, 1))).ToArray();
            result.Add(new(kind, Array.AsReadOnly(normalized), score));
        }
    }

    private static bool Overlaps(Component a, Component b)
    {
        var intersection = Math.Max(0, Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX)) *
            Math.Max(0, Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY));
        var smaller = Math.Min((a.MaxX - a.MinX) * (a.MaxY - a.MinY), (b.MaxX - b.MinX) * (b.MaxY - b.MinY));
        return smaller > 0 && intersection / smaller > .55;
    }

    private sealed record SampleImage(int Width, int Height, byte[] Rgb);

    private sealed class Component
    {
        public int Color { get; init; }
        public int Pixels { get; init; }
        public bool Marker { get; set; }
        public double CenterX { get; init; }
        public double CenterY { get; init; }
        public double AxisX { get; init; }
        public double AxisY { get; init; }
        public double MinX { get; init; }
        public double MaxX { get; init; }
        public double MinY { get; init; }
        public double MaxY { get; init; }
        public double MinU { get; init; }
        public double MaxU { get; init; }
        public double MinV { get; init; }
        public double MaxV { get; init; }
        public double Length => MaxU - MinU + 1;
        public double Thickness => MaxV - MinV + 1;
        public (double X, double Y) Point(double u, double v) => (CenterX + AxisX * u - AxisY * v, CenterY + AxisY * u + AxisX * v);

        public static Component Create(ReadOnlySpan<int> points, int width, int color)
        {
            double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0, sumYY = 0;
            var minX = double.MaxValue; var minY = double.MaxValue;
            var maxX = double.MinValue; var maxY = double.MinValue;
            foreach (var position in points)
            {
                var x = position % width; var y = position / width;
                sumX += x; sumY += y; sumXX += x * x; sumXY += x * y; sumYY += y * y;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            var centerX = sumX / points.Length; var centerY = sumY / points.Length;
            var xx = sumXX / points.Length - centerX * centerX;
            var xy = sumXY / points.Length - centerX * centerY;
            var yy = sumYY / points.Length - centerY * centerY;
            var angle = .5 * Math.Atan2(2 * xy, xx - yy);
            var axisX = Math.Cos(angle); var axisY = Math.Sin(angle);
            var minU = double.MaxValue; var minV = double.MaxValue;
            var maxU = double.MinValue; var maxV = double.MinValue;
            foreach (var position in points)
            {
                var x = position % width - centerX; var y = position / width - centerY;
                var u = x * axisX + y * axisY; var v = -x * axisY + y * axisX;
                minU = Math.Min(minU, u); maxU = Math.Max(maxU, u); minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
            }
            return new() { Color = color, Pixels = points.Length, CenterX = centerX, CenterY = centerY, AxisX = axisX, AxisY = axisY,
                MinX = minX, MaxX = maxX, MinY = minY, MaxY = maxY, MinU = minU, MaxU = maxU, MinV = minV, MaxV = maxV };
        }
    }
}
