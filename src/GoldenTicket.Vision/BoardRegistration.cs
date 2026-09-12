namespace GoldenTicket.Vision;

public readonly record struct NormalizedPoint(double X, double Y);

/// <summary>
/// Operator-selected image crop, not a verified edition/landmark calibration. Coordinates are
/// TL, TR, BR, BL in the displayed sensor image. Reconnection/format changes invalidate it.
/// </summary>
public sealed class BoardRegistration
{
    private readonly double[] _map;
    private readonly NormalizedPoint[] _corners;

    private BoardRegistration(CameraFrame frame, NormalizedPoint[] corners, double[] map)
    {
        CameraEpoch = frame.Epoch;
        SensorWidth = frame.Width;
        SensorHeight = frame.Height;
        _corners = corners;
        _map = map;
    }

    public long CameraEpoch { get; }
    public int SensorWidth { get; }
    public int SensorHeight { get; }
    public IReadOnlyList<NormalizedPoint> Corners => Array.AsReadOnly(_corners);

    public static BoardRegistration Create(CameraFrame frame, IReadOnlyList<NormalizedPoint> corners)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(corners);
        if (corners.Count != 4) throw new ArgumentException("Select exactly four board corners.", nameof(corners));
        var points = corners.ToArray();
        if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1))
            throw new ArgumentException("All four board corners must be inside the camera image.", nameof(corners));
        for (var i = 0; i < 4; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % 4];
            var c = points[(i + 2) % 4];
            var cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (cross <= 0.003) throw new ArgumentException("Select a clear clockwise rectangle: top-left, top-right, bottom-right, bottom-left. The corners cannot cross or nearly overlap.", nameof(corners));
        }
        var area = Math.Abs(Enumerable.Range(0, 4).Sum(i =>
            points[i].X * points[(i + 1) % 4].Y - points[(i + 1) % 4].X * points[i].Y)) / 2;
        if (area < 0.08) throw new ArgumentException("The board occupies too little of the image. Move closer or select the outer board corners.", nameof(corners));

        // Solve normalized destination-to-source homography; no package or native CV dependency.
        NormalizedPoint[] target = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        var matrix = new double[8, 9];
        for (var i = 0; i < 4; i++)
        {
            var (u, v) = target[i];
            var (x, y) = points[i];
            matrix[i * 2, 0] = u; matrix[i * 2, 1] = v; matrix[i * 2, 2] = 1;
            matrix[i * 2, 6] = -u * x; matrix[i * 2, 7] = -v * x; matrix[i * 2, 8] = x;
            matrix[i * 2 + 1, 3] = u; matrix[i * 2 + 1, 4] = v; matrix[i * 2 + 1, 5] = 1;
            matrix[i * 2 + 1, 6] = -u * y; matrix[i * 2 + 1, 7] = -v * y; matrix[i * 2 + 1, 8] = y;
        }
        for (var column = 0; column < 8; column++)
        {
            var pivot = column;
            for (var row = column + 1; row < 8; row++)
                if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column])) pivot = row;
            if (Math.Abs(matrix[pivot, column]) < 1e-9) throw new ArgumentException("These board corners cannot form a stable crop.", nameof(corners));
            for (var j = column; j < 9; j++) (matrix[column, j], matrix[pivot, j]) = (matrix[pivot, j], matrix[column, j]);
            var divisor = matrix[column, column];
            for (var j = column; j < 9; j++) matrix[column, j] /= divisor;
            for (var row = 0; row < 8; row++)
            {
                if (row == column) continue;
                var factor = matrix[row, column];
                for (var j = column; j < 9; j++) matrix[row, j] -= factor * matrix[column, j];
            }
        }
        return new(frame, points, Enumerable.Range(0, 8).Select(i => matrix[i, 8]).ToArray());
    }

    public bool Matches(CameraFrame frame) => CameraEpoch == frame.Epoch && SensorWidth == frame.Width && SensorHeight == frame.Height;

    public NormalizedPoint MapToSensor(double u, double v)
    {
        if (!double.IsFinite(u) || !double.IsFinite(v) || u < 0 || u > 1 || v < 0 || v > 1)
            throw new ArgumentOutOfRangeException(nameof(u));
        var denominator = _map[6] * u + _map[7] * v + 1;
        return new((_map[0] * u + _map[1] * v + _map[2]) / denominator,
            (_map[3] * u + _map[4] * v + _map[5]) / denominator);
    }

    public CameraFrame Rectify(CameraFrame frame, int width = 1280, int height = 800)
    {
        if (!Matches(frame)) throw new InvalidOperationException("The camera restarted or changed format. Select the board corners again before saving a cropped photo.");
        CameraFrame.ValidateSize(width, height, checked(width * height * 4));
        var output = new byte[width * height * 4];
        var pixels = frame.Bgra32.Span;
        for (var row = 0; row < height; row++)
        for (var column = 0; column < width; column++)
        {
            var p = MapToSensor(width == 1 ? 0 : (double)column / (width - 1), height == 1 ? 0 : (double)row / (height - 1));
            var x = Math.Clamp(p.X * (frame.Width - 1), 0, frame.Width - 1);
            var y = Math.Clamp(p.Y * (frame.Height - 1), 0, frame.Height - 1);
            var x0 = (int)x;
            var y0 = (int)y;
            var x1 = Math.Min(x0 + 1, frame.Width - 1);
            var y1 = Math.Min(y0 + 1, frame.Height - 1);
            var wx = x - x0;
            var wy = y - y0;
            var destination = (row * width + column) * 4;
            for (var channel = 0; channel < 3; channel++)
            {
                var upper = pixels[(y0 * frame.Width + x0) * 4 + channel] * (1 - wx) + pixels[(y0 * frame.Width + x1) * 4 + channel] * wx;
                var lower = pixels[(y1 * frame.Width + x0) * 4 + channel] * (1 - wx) + pixels[(y1 * frame.Width + x1) * 4 + channel] * wx;
                output[destination + channel] = (byte)Math.Round(upper * (1 - wy) + lower * wy);
            }
            output[destination + 3] = 255;
        }
        return CameraFrame.TakeOwnership(width, height, output, frame.Sequence, frame.Epoch, frame.CapturedAt, frame.MonotonicTimestamp);
    }
}
