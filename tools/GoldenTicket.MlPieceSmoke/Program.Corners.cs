using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenTicket.Vision;

internal static partial class Program
{
    private static int RunCorners(string[] args)
    {
        if (args.Length is < 3 or > 4 || args.Length == 4 && args[3] is not "--cpu" and not "--compare")
        {
            Console.Error.WriteLine("Usage: GoldenTicket.MlPieceSmoke --corners <model-directory> <full-camera-image.png> <output-directory> [--cpu|--compare]");
            return 2;
        }
        var destination = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(destination);
        var frame = Read(args[1]);
        var runs = new List<object>();
        var results = new List<LearnedBoardCornerDetection>();
        foreach (var gpu in args.Contains("--compare") ? new[] { false, true } : new[] { !args.Contains("--cpu") })
        {
            var suffix = gpu ? "gpu" : "cpu";
            var timer = Stopwatch.StartNew();
            using var detector = LearnedBoardCornerDetector.Load(args[0], gpu, Path.Combine(destination, suffix + "-profile"));
            var startup = timer.Elapsed.TotalMilliseconds;
            // The last of three warmed runs is drawn; all inference timings are retained.
            LearnedBoardCornerDetection result = detector.Detect(frame);
            var timings = new List<double> { result.Elapsed.TotalMilliseconds };
            for (var i = 0; i < 2; i++)
            {
                result = detector.Detect(frame);
                timings.Add(result.Elapsed.TotalMilliseconds);
            }
            results.Add(result);
            DrawCorners(frame, result, Path.Combine(destination, $"board-corners-{suffix}.png"));
            var padding = result.Accepted ? BoardCropPadding.Expand(frame, result.Corners) : null;
            if (padding is not null)
                DrawCorners(frame, result with { Corners = padding.Corners }, Path.Combine(destination, $"board-crop-padded-{suffix}.png"));
            runs.Add(new
            {
                RequestedGpu = gpu, detector.ModelId, detector.ModelSha256, detector.Backend, detector.FallbackReason,
                StartupMilliseconds = startup, InferenceMilliseconds = timings, result.Accepted, result.RejectionReason,
                result.Corners, result.Confidences, NodeProviders = Providers(detector.StartupProfilePath),
                CropCorners = padding?.Corners, PaddingLimitedByFrame = padding?.LimitedByFrame,
                NativeLibraries = NativeLibraries()
            });
        }
        double? maximumCornerDeltaPixels = null;
        if (results.Count == 2 && results.All(result => result.Accepted))
            maximumCornerDeltaPixels = results[0].Corners.Zip(results[1].Corners, (a, b) =>
                Math.Sqrt(Math.Pow((a.X - b.X) * (frame.Width - 1), 2) + Math.Pow((a.Y - b.Y) * (frame.Height - 1), 2))).Max();
        var json = JsonSerializer.Serialize(new
        {
            Source = Path.GetFullPath(args[1]), frame.Width, frame.Height, Runs = runs,
            MaximumCpuGpuCornerDeltaPixels = maximumCornerDeltaPixels
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(destination, "corner-report.json"), json);
        Console.WriteLine(json);
        return 0;
    }

    private static void DrawCorners(CameraFrame source, LearnedBoardCornerDetection result, string path)
    {
        var bitmap = BitmapSource.Create(source.Width, source.Height, 96, 96, PixelFormats.Bgra32, null,
            source.Bgra32.ToArray(), source.Stride);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(bitmap, new Rect(0, 0, source.Width, source.Height));
            if (result.Accepted)
            {
                var points = result.Corners.Select(point => new Point(point.X * (source.Width - 1), point.Y * (source.Height - 1))).ToArray();
                for (var i = 0; i < 4; i++)
                {
                    drawing.DrawLine(new Pen(Brushes.Black, 6), points[i], points[(i + 1) % 4]);
                    drawing.DrawLine(new Pen(Brushes.Yellow, 3), points[i], points[(i + 1) % 4]);
                }
                for (var i = 0; i < 4; i++)
                {
                    drawing.DrawEllipse(Brushes.Yellow, new Pen(Brushes.Black, 2), points[i], 13, 13);
                    var label = new FormattedText((i + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface("Segoe UI"), 16, Brushes.Black, 1);
                    drawing.DrawText(label, new Point(points[i].X - label.Width / 2, points[i].Y - label.Height / 2));
                }
            }
        }
        var output = new RenderTargetBitmap(source.Width, source.Height, 96, 96, PixelFormats.Pbgra32);
        output.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(output));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        encoder.Save(stream);
    }
}
