using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenTicket.Vision;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Explicit paths only: this diagnostic never opens a camera, game database, or network connection.
        if (args.Length < 3 || args.Skip(3).Any(option => option is not "--enhanced" and not "--enhanced-reference"))
        {
            Console.Error.WriteLine("Usage: GoldenTicket.PieceDetectionSmoke <empty-board.png> <board-with-pieces.png> <output-directory> [--enhanced] [--enhanced-reference]");
            return 2;
        }
        var destination = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(destination);
        var enhanceReference = args.Contains("--enhanced-reference");
        var enhanceCurrent = args.Contains("--enhanced") || enhanceReference;
        FrameProcessor? processor = null;
        FrameProcessorStatus? processingStatus = null;
        if (enhanceCurrent)
        {
            processor = new FrameProcessor();
            // Initialize before reading fixtures so GPU startup does not manufacture stale source evidence.
            processingStatus = processor.InitializeAsync(FrameComputeMode.Auto).GetAwaiter().GetResult();
        }
        var empty = Read(args[0], 1);
        var current = Read(args[1], 2);
        if (empty.Width != current.Width || empty.Height != current.Height)
            throw new ArgumentException("The two input images must have the same rectified board geometry and dimensions.");
        var preparation = Stopwatch.StartNew();
        try
        {
            if (enhanceReference) empty = processor!.ProcessAsync(empty).GetAwaiter().GetResult().Frame;
            if (enhanceCurrent) current = processor!.ProcessAsync(current).GetAwaiter().GetResult().Frame;
        }
        finally
        {
            // Initialization success alone does not prove that later frames stayed on the GPU.
            if (processor is not null) processingStatus = processor.Status;
            processor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        NormalizedPoint[] corners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        empty = BoardRegistration.Create(empty, corners).Rectify(empty, 1920, 1200);
        current = BoardRegistration.Create(current, corners).Rectify(current, 1920, 1200);
        preparation.Stop();
        var detector = new PieceCandidateDetector();
        var stopwatch = Stopwatch.StartNew();
        detector.SetReference(empty);
        var result = detector.Detect(current);
        stopwatch.Stop();
        var report = new
        {
            Status = result.State.ToString(),
            TrainCandidates = result.Candidates.Count(candidate => candidate.Kind == PieceCandidateKind.Train),
            MarkerCandidates = result.Candidates.Count(candidate => candidate.Kind == PieceCandidateKind.PlayerMarker),
            Milliseconds = stopwatch.Elapsed.TotalMilliseconds,
            PreparationMilliseconds = preparation.Elapsed.TotalMilliseconds,
            EnhancedCurrent = enhanceCurrent,
            EnhancedReference = enhanceReference,
            Backend = processingStatus?.Backend.ToString(),
            AdapterName = processingStatus?.AdapterName,
            FallbackReason = processingStatus?.FallbackReason,
            result.ChangedFraction,
            Candidates = result.Candidates.Select(candidate => new { Kind = candidate.Kind.ToString(), candidate.Outline, candidate.Confidence }),
            Limitations = "Experimental reference/shape candidates; not ML, verified counts, ownership, score, route occupancy or game moves."
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(destination, "piece-candidates.json"), json);
        Console.WriteLine(json);

        var source = BitmapSource.Create(current.Width, current.Height, 96, 96, PixelFormats.Bgra32, null,
            current.Bgra32.ToArray(), current.Stride);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, current.Width, current.Height));
            var pen = new Pen(Brushes.White, Math.Max(2, current.Width / 640d));
            foreach (var candidate in result.Candidates)
            {
                var geometry = new StreamGeometry();
                using (var shape = geometry.Open())
                {
                    shape.BeginFigure(Point(candidate.Outline[0]), isFilled: false, isClosed: true);
                    shape.PolyLineTo(candidate.Outline.Skip(1).Select(Point).ToArray(), isStroked: true, isSmoothJoin: false);
                }
                context.DrawGeometry(null, pen, geometry);
            }
        }
        var rendered = new RenderTargetBitmap(current.Width, current.Height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        using (var output = File.Create(Path.Combine(destination, "piece-candidates.png"))) encoder.Save(output);
        return result.State == PieceDetectionState.Ready ? 0 : 1;

        Point Point(NormalizedPoint point) => new(point.X * (current.Width - 1), point.Y * (current.Height - 1));

        static CameraFrame Read(string path, long sequence)
        {
            using var stream = File.OpenRead(Path.GetFullPath(path));
            if (stream.Length > 48 * 1024 * 1024) throw new InvalidDataException("Input photo is too large.");
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var image = decoder.Frames[0];
            if (image.PixelWidth > CameraFrame.MaximumWidth || image.PixelHeight > CameraFrame.MaximumHeight)
                throw new InvalidDataException("Input photo is larger than the 4K processing limit.");
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[checked(converted.PixelWidth * converted.PixelHeight * 4)];
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
            return CameraFrame.CopyFromBgra32(converted.PixelWidth, converted.PixelHeight, pixels, sequence);
        }
    }
}
