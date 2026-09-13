using System.Numerics;

namespace GoldenTicket.Vision;

/// <summary>CPU reference for the shipped shader. All channels use the same spatial weights.</summary>
internal static class FrameProcessingKernel
{
    internal static byte[] Process(CameraFrame frame, int width, int height, CancellationToken token)
    {
        var source = frame.Bgra32;
        var filtered = new byte[source.Length];
        var options = new ParallelOptions { CancellationToken = token,
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8)) };
        Parallel.For(0, frame.Height, options, y =>
        {
            var pixels = source.Span;
            for (var x = 0; x < frame.Width; x++)
            {
                var center = Read(pixels, frame.Width, frame.Height, x, y);
                var blur = Vector3.Zero;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                    blur += Read(pixels, frame.Width, frame.Height, x + dx, y + dy) *
                        ((dx == 0 ? 2 : 1) * (dy == 0 ? 2 : 1));
                blur /= 16;
                var detail = Vector3.Dot(center - blur, new Vector3(0.114f, 0.587f, 0.299f));
                // Smooth very low contrast noise; enhance stronger edges with a strict eight-level cap.
                var adjustment = MathF.Abs(detail) < 3 ? -0.35f * detail : Math.Clamp(0.35f * detail, -8, 8);
                Write(filtered, (y * frame.Width + x) * 4, center + new Vector3(adjustment));
            }
        });
        if (width == frame.Width && height == frame.Height) return filtered;

        var output = new byte[checked(width * height * 4)];
        Parallel.For(0, height, options, y =>
        {
            var sy = SnapPixelCenter((y + 0.5f) * frame.Height / height - 0.5f);
            var iy = (int)MathF.Floor(sy);
            for (var x = 0; x < width; x++)
            {
                var sx = SnapPixelCenter((x + 0.5f) * frame.Width / width - 0.5f);
                var ix = (int)MathF.Floor(sx);
                var color = Vector3.Zero;
                var minimum = new Vector3(255);
                var maximum = Vector3.Zero;
                for (var dy = -1; dy <= 2; dy++)
                for (var dx = -1; dx <= 2; dx++)
                {
                    var sample = Read(filtered, frame.Width, frame.Height, ix + dx, iy + dy);
                    color += sample * (Cubic(sx - (ix + dx)) * Cubic(sy - (iy + dy)));
                    if (dx is 0 or 1 && dy is 0 or 1)
                    {
                        minimum = Vector3.Min(minimum, sample);
                        maximum = Vector3.Max(maximum, sample);
                    }
                }
                // Clamp cubic ringing to the local four source pixels, preserving color boundaries.
                Write(output, (y * width + x) * 4, Vector3.Clamp(color, minimum, maximum));
            }
        });
        return output;
    }

    private static float Cubic(float distance)
    {
        var x = MathF.Abs(distance);
        return x <= 1 ? (1.5f * x - 2.5f) * x * x + 1 :
            x < 2 ? ((-0.5f * x + 2.5f) * x - 4) * x + 2 : 0;
    }

    private static float SnapPixelCenter(float value) =>
        MathF.Abs(value - MathF.Round(value)) < 0.0001f ? MathF.Round(value) : value;

    private static Vector3 Read(ReadOnlySpan<byte> pixels, int width, int height, int x, int y)
    {
        var index = (Math.Clamp(y, 0, height - 1) * width + Math.Clamp(x, 0, width - 1)) * 4;
        return new(pixels[index], pixels[index + 1], pixels[index + 2]);
    }

    private static void Write(Span<byte> pixels, int index, Vector3 color)
    {
        pixels[index] = (byte)Math.Clamp((int)MathF.Floor(color.X + 0.5f), 0, 255);
        pixels[index + 1] = (byte)Math.Clamp((int)MathF.Floor(color.Y + 0.5f), 0, 255);
        pixels[index + 2] = (byte)Math.Clamp((int)MathF.Floor(color.Z + 0.5f), 0, 255);
        pixels[index + 3] = 255;
    }
}
