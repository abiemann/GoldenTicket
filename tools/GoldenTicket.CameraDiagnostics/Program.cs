using System.Text.Json;
using System.Windows;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var nameFilter = args.Length > 0 ? args[0] : "Pixel";
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var groups = await MediaFrameSourceGroup.FindAllAsync().AsTask(timeout.Token);
                var videoGroups = groups.Where(g => g.SourceInfos.Any(IsColorVideo)).ToArray();
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    Mode = "SharedReadOnly format inventory; no format changes, frame reader, recording or capture",
                    CameraNames = videoGroups.Select(g => g.DisplayName).ToArray()
                }));
                var matches = videoGroups.Where(g => g.DisplayName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ||
                    (nameFilter == "Pixel" && g.DisplayName.Contains("Android", StringComparison.OrdinalIgnoreCase))).ToArray();
                if (matches.Length == 0)
                {
                    Console.WriteLine("No matching camera is connected; no camera was initialized.");
                    return;
                }
                foreach (var group in matches)
                {
                    using var capture = new MediaCapture();
                    await capture.InitializeAsync(new MediaCaptureInitializationSettings
                    {
                        SourceGroup = group,
                        StreamingCaptureMode = StreamingCaptureMode.Video,
                        MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                        SharingMode = MediaCaptureSharingMode.SharedReadOnly
                    }).AsTask(timeout.Token);
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        Camera = group.DisplayName,
                        Sources = capture.FrameSources.Values.Where(s => IsColorVideo(s.Info)).Select(s => new
                        {
                            Stream = s.Info.MediaStreamType.ToString(),
                            Current = Describe(s.CurrentFormat),
                            AdvertisedFormats = s.SupportedFormats.Select(Describe).OrderByDescending(f => (long)f.Width * f.Height)
                                .ThenByDescending(f => f.FramesPerSecond).ToArray()
                        }).ToArray()
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Read-only camera inspection failed: {ex.Message}");
                Environment.ExitCode = 1;
            }
            finally { app.Shutdown(); }
        };
        app.Run();
    }

    private static bool IsColorVideo(MediaFrameSourceInfo info) => info.SourceKind == MediaFrameSourceKind.Color &&
        info.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord;

    private static FormatInfo Describe(MediaFrameFormat format) => new(format.VideoFormat.Width, format.VideoFormat.Height,
        format.FrameRate.Denominator == 0 ? 0 : (double)format.FrameRate.Numerator / format.FrameRate.Denominator, format.Subtype);

    private sealed record FormatInfo(uint Width, uint Height, double FramesPerSecond, string Subtype);
}
