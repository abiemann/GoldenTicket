using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenTicket.Vision;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--corners") return RunCorners(args[1..]);
        if (args.Length is < 3 or > 4 || args.Length == 4 && args[3] is not "--cpu" and not "--compare")
        {
            Console.Error.WriteLine("Usage: GoldenTicket.MlPieceSmoke <model-directory> <cropped-board.png> <output-directory> [--cpu|--compare]");
            return 2;
        }
        // Explicit offline files only. This tool never opens a camera, game, or network connection.
        var destination = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(destination);
        var board = Read(args[1]);
        var runs = new List<object>();
        var results = new List<LearnedPieceDetection>();
        var modes = args.Contains("--compare") ? new[] { false, true } : new[] { !args.Contains("--cpu") };
        foreach (var gpu in modes)
        {
            var timer = Stopwatch.StartNew();
            using var detector = LearnedPieceDetector.Load(args[0], gpu,
                Path.Combine(destination, gpu ? "gpu-profile" : "cpu-profile"));
            var startup = timer.Elapsed.TotalMilliseconds;
            var result = detector.Detect(board);
            var detectionTimings = new List<double> { result.Elapsed.TotalMilliseconds };
            var fittingTimings = new List<double> { result.OutlineFittingElapsed.TotalMilliseconds };
            for (var sample = 0; sample < 2; sample++)
            {
                result = detector.Detect(board);
                detectionTimings.Add(result.Elapsed.TotalMilliseconds);
                fittingTimings.Add(result.OutlineFittingElapsed.TotalMilliseconds);
            }
            results.Add(result);
            var providers = Providers(detector.StartupProfilePath);
            var suffix = gpu ? "gpu" : "cpu";
            Draw(board, result.Candidates, Path.Combine(destination, $"piece-outlines-{suffix}.png"));
            Draw(board, result.Candidates, Path.Combine(destination, $"piece-outlines-preview-{suffix}.png"), maximumWidth: 1600);
            runs.Add(new
            {
                RequestedGpu = gpu, detector.ModelId, detector.ModelSha256, detector.Backend, detector.FallbackReason,
                StartupMilliseconds = startup, InferenceMilliseconds = (result.Elapsed - result.OutlineFittingElapsed).TotalMilliseconds,
                DetectionMilliseconds = result.Elapsed.TotalMilliseconds,
                OutlineFittingMilliseconds = result.OutlineFittingElapsed.TotalMilliseconds,
                DetectionTimingsMilliseconds = detectionTimings, OutlineFittingTimingsMilliseconds = fittingTimings,
                NodeProviders = providers, detector.StartupProfilePath,
                NativeLibraries = NativeLibraries(),
                TrainCount = result.Candidates.Count(item => item.Kind == PieceCandidateKind.Train),
                MarkerCount = result.Candidates.Count(item => item.Kind == PieceCandidateKind.PlayerMarker),
                ScoreMarkers = ScoreMarkerReader.Read(board, result.Candidates).Select(reading => new
                {
                    reading.CandidateIndex, Color = reading.Color?.ToString(), reading.Score,
                    Status = reading.Status.ToString(), reading.Reason
                }),
                OrientedTrainCount = result.Candidates.Count(item => item.Kind == PieceCandidateKind.Train && item.OrientedOutline is not null),
                Candidates = result.Candidates.Select(item => new { Kind = item.Kind.ToString(), item.Confidence, item.Outline, item.OrientedOutline })
            });
        }
        object? parity = null;
        if (results.Count == 2)
        {
            var remaining = results[1].Candidates.ToList();
            var overlaps = new List<double>();
            var confidenceDeltas = new List<double>();
            foreach (var cpu in results[0].Candidates)
            {
                var match = remaining.Where(gpu => gpu.Kind == cpu.Kind)
                    .Select(gpu => (Box: gpu, IoU: IoU(cpu, gpu))).OrderByDescending(pair => pair.IoU).FirstOrDefault();
                if (match.Box is null || match.IoU < .5) continue;
                remaining.Remove(match.Box);
                overlaps.Add(match.IoU);
                confidenceDeltas.Add(Math.Abs(cpu.Confidence - match.Box.Confidence));
            }
            parity = new
            {
                MatchedAtIoU50 = overlaps.Count, CpuCount = results[0].Candidates.Count,
                GpuCount = results[1].Candidates.Count, UnmatchedGpu = remaining.Count,
                MinimumMatchedIoU = overlaps.Count == 0 ? (double?)null : overlaps.Min(),
                MaximumMatchedConfidenceDifference = confidenceDeltas.Count == 0 ? (double?)null : confidenceDeltas.Max()
            };
        }
        var report = new { Source = Path.GetFullPath(args[1]), board.Width, board.Height, Runs = runs, Parity = parity };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(destination, "inference-report.json"), json);
        Console.WriteLine(json);
        return 0;
    }

    private static Dictionary<string, int> Providers(string? path)
    {
        var counts = new Dictionary<string, int>();
        if (path is null) return counts;
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var entry in json.RootElement.EnumerateArray())
            if (entry.TryGetProperty("args", out var args) && args.TryGetProperty("provider", out var provider) &&
                provider.GetString() is { Length: > 0 } name)
                counts[name] = counts.GetValueOrDefault(name) + 1;
        return counts;
    }

    private static object[] NativeLibraries()
    {
        using var process = Process.GetCurrentProcess();
        return process.Modules.Cast<ProcessModule>()
            .Where(module => module.ModuleName.Equals("DirectML.dll", StringComparison.OrdinalIgnoreCase) ||
                module.ModuleName.Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase))
            .Select(module => (object)new
            {
                module.ModuleName, Path = module.FileName, Version = module.FileVersionInfo.FileVersion,
                Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(module.FileName))).ToLowerInvariant()
            }).ToArray();
    }

    private static CameraFrame Read(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = decoder.Frames[0];
        if (source.PixelWidth > CameraFrame.MaximumWidth || source.PixelHeight > CameraFrame.MaximumHeight)
            throw new InvalidDataException("The selected photo exceeds the camera frame bounds.");
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return CameraFrame.CopyFromBgra32(converted.PixelWidth, converted.PixelHeight, pixels);
    }

    private static void Draw(CameraFrame source, IReadOnlyList<PieceCandidate> candidates, string destination,
        int maximumWidth = int.MaxValue)
    {
        var bitmap = BitmapSource.Create(source.Width, source.Height, 96, 96, PixelFormats.Bgra32, null,
            source.Bgra32.ToArray(), source.Stride);
        var visual = new DrawingVisual();
        var scale = Math.Min(1, (double)maximumWidth / source.Width);
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new ScaleTransform(scale, scale));
            drawing.DrawImage(bitmap, new Rect(0, 0, source.Width, source.Height));
            var black = new Pen(Brushes.Black, 5);
            var white = new Pen(Brushes.White, 2);
            foreach (var candidate in candidates)
            {
                var points = candidate.DisplayOutline.Select(point => new Point(
                    point.X * source.Width, point.Y * source.Height)).ToArray();
                if (candidate.Kind == PieceCandidateKind.PlayerMarker)
                {
                    var left = points.Min(point => point.X);
                    var top = points.Min(point => point.Y);
                    var right = points.Max(point => point.X);
                    var bottom = points.Max(point => point.Y);
                    var size = Math.Max(right - left, bottom - top);
                    var x = (left + right - size) / 2;
                    var y = (top + bottom - size) / 2;
                    points = [new(x, y), new(x + size, y), new(x + size, y + size), new(x, y + size)];
                }
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(points[0], isFilled: false, isClosed: true);
                    context.PolyLineTo(points[1..], isStroked: true, isSmoothJoin: false);
                }
                drawing.DrawGeometry(null, black, geometry);
                drawing.DrawGeometry(null, white, geometry);
            }
        }
        var output = new RenderTargetBitmap((int)Math.Ceiling(source.Width * scale),
            (int)Math.Ceiling(source.Height * scale), 96, 96, PixelFormats.Pbgra32);
        output.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(output));
        using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
        encoder.Save(stream);
    }

    private static Rect Bounds(PieceCandidate candidate) => new(
        candidate.Outline.Min(p => p.X), candidate.Outline.Min(p => p.Y),
        candidate.Outline.Max(p => p.X) - candidate.Outline.Min(p => p.X),
        candidate.Outline.Max(p => p.Y) - candidate.Outline.Min(p => p.Y));

    private static double IoU(PieceCandidate a, PieceCandidate b)
    {
        var left = Bounds(a);
        var right = Bounds(b);
        var intersection = Rect.Intersect(left, right);
        var area = intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
        return area / (left.Width * left.Height + right.Width * right.Height - area);
    }
}
