using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Vision;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;

namespace GoldenTicket.Desktop;

/// <summary>Opt-in, windowless checks using the executable's actually loaded bundled runtimes.</summary>
internal static class PackageDiagnostics
{
    internal static async Task<int> RunAsync(string reportPath)
    {
        var output = Path.GetFullPath(reportPath);
        // Caller chooses an existing output directory. CreateNew refuses to overwrite evidence.
        await using var reportFile = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var started = DateTimeOffset.UtcNow;
        var checks = new List<Check>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;

        async Task CheckAsync(string name, Func<Task> action)
        {
            try { await action(); checks.Add(new(name, true, null)); }
            catch (Exception ex) { checks.Add(new(name, false, ex.GetType().Name)); }
        }
        Task CheckSync(string name, Action action) => CheckAsync(name, () => { action(); return Task.CompletedTask; });
        static void Require(bool condition) { if (!condition) throw new InvalidDataException("Package check failed."); }

        await CheckSync("Bundled CoreCLR", () => Require(string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(RuntimeEnvironment.GetRuntimeDirectory())),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)), StringComparison.OrdinalIgnoreCase)));

        await CheckSync("Shipped board data checksum", () =>
        {
            var manifest = ManifestLoader.LoadClassicUs(Path.Combine(AppContext.BaseDirectory, "data", "classic-us", ManifestLoader.ClassicUsFileName));
            Require(manifest.Cities.Length == 36 && manifest.Routes.Length == 100 && manifest.Tickets.Length == 30);
        });
        await CheckSync("WPF theme and native rendering", () =>
        {
            var button = new Button { Content = "GoldenTicket", Style = (Style)System.Windows.Application.Current.FindResource("PrimaryButton") };
            button.Measure(new Size(180, 40));
            button.Arrange(new Rect(0, 0, 180, 40));
            button.UpdateLayout();
            var bitmap = new RenderTargetBitmap(180, 40, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(button);
            var pixels = new byte[180 * 40 * 4];
            bitmap.CopyPixels(pixels, 180 * 4, 0);
            Require(pixels.Any(value => value != 0));
        });
        await CheckAsync("Native SQLite in memory", async () =>
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT sqlite_version()";
            Require(await command.ExecuteScalarAsync(token) is string { Length: > 0 });
        });
        await CheckAsync("Windows PNG encoding and WPF decoding", async () =>
        {
            var pixels = Enumerable.Repeat((byte)180, 16 * 16 * 4).ToArray();
            var frame = CameraFrame.CopyFromBgra32(16, 16, pixels);
            var png = await frame.EncodePngAsync(token);
            try
            {
                using var stream = new MemoryStream(png, writable: false);
                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Require(decoder.Frames[0].PixelWidth == 16 && decoder.Frames[0].PixelHeight == 16);
            }
            finally { CryptographicOperations.ZeroMemory(png); }
        });
        await CheckAsync("ASP.NET host construction without listening", async () =>
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = [], ContentRootPath = AppContext.BaseDirectory,
                ApplicationName = typeof(GoldenTicket.CompanionHost.CompanionServer).Assembly.FullName
            });
            await using var app = builder.Build();
            Require(app.Services is not null);
        });
        await CheckSync("Bundled companion assets", () =>
        {
            foreach (var asset in new[] { "index.html", "app.js", "app.css", "icon.svg" })
                Require(new FileInfo(Path.Combine(AppContext.BaseDirectory, "companion-web", asset)).Length > 0);
        });

        var passed = checks.All(check => check.Passed);
        await JsonSerializer.SerializeAsync(reportFile, new
        {
            formatVersion = 1, startedAtUtc = started, completedAtUtc = DateTimeOffset.UtcNow, passed,
            framework = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory(), applicationDirectory = AppContext.BaseDirectory,
            visibleWindowOpened = false, cameraOpened = false, networkListenerStarted = false, playerSaveOpened = false,
            checks,
            manualAcceptance = "Clean machine, disconnected Internet, real camera, real phone and full game walkthrough remain pending."
        }, new JsonSerializerOptions { WriteIndented = true });
        await reportFile.FlushAsync();
        return passed ? 0 : 1;
    }

    private sealed record Check(string Name, bool Passed, string? ErrorType);
}
