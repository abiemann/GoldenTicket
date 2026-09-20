using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using GoldenTicket.Desktop.Services;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Persistence;

internal static partial class Program
{
    private static async Task VerifyWindowPresentationPersistence()
    {
        BindingLog.Context = "window-presentation-persistence";
        var fixtureRoot = Path.Combine(Output, "window-presentation-settings", Guid.NewGuid().ToString("N"));
        var checks = new List<object>();
        var workArea = SystemParameters.WorkArea;
        var width = Math.Clamp(1200d, 1000, Math.Max(1000, workArea.Width));
        var height = Math.Clamp(760d, 620, Math.Max(620, workArea.Height));

        // A native but unshown window supplies real state/RestoreBounds notifications. Without
        // Show/Loaded these checks never enumerate cameras or load the user's saved matches.
        var normalRoot = Path.Combine(fixtureRoot, "normal");
        await using (var initial = new WindowPresentationFixture(normalRoot))
        {
            initial.Window.Width = width;
            initial.Window.Height = height;
            await initial.CloseAsync();
        }
        await using (var restored = new WindowPresentationFixture(normalRoot))
        {
            AssertWindowPresentation(restored, width, height, maximized: false, fullScreen: false,
                "normal resize and relaunch");
            checks.Add(new { Scenario = "normal resize and relaunch", Width = width, Height = height, Passed = true });
        }

        var maximizedRoot = Path.Combine(fixtureRoot, "maximized");
        await using (var initial = new WindowPresentationFixture(maximizedRoot))
        {
            initial.Window.Width = width;
            initial.Window.Height = height;
            initial.Window.WindowState = WindowState.Maximized;
            await initial.CloseAsync();
        }
        await using (var restored = new WindowPresentationFixture(maximizedRoot))
        {
            AssertWindowPresentation(restored, width, height, maximized: true, fullScreen: false,
                "maximized relaunch retains normal size");
            restored.Window.WindowState = WindowState.Normal;
            AssertNormalSize(restored.Window, width, height, "restore maximized window");
            checks.Add(new { Scenario = "maximized relaunch retains normal size", Width = width, Height = height, Passed = true });
        }

        foreach (var maximized in new[] { false, true })
        {
            var scenario = maximized ? "fullscreen over maximized" : "fullscreen over normal";
            var root = Path.Combine(fixtureRoot, maximized ? "fullscreen-maximized" : "fullscreen-normal");
            var seed = new MainViewModel(ManifestLoader.LoadClassicUs(), new SqliteSessionStore(root));
            try
            {
                seed.SaveWindowPresentation(new(width, height, maximized));
                seed.DisplayMode = DisplayMode.FullScreen;
            }
            finally { await seed.DisposeToolsAsync(); }

            await using (var initial = new WindowPresentationFixture(root))
            {
                AssertWindowPresentation(initial, width, height, maximized, fullScreen: true, scenario);
                initial.Model.DisplayMode = DisplayMode.Resizable;
                AssertWindowPresentation(initial, width, height, maximized, fullScreen: false,
                    scenario + " switched back");
                initial.Model.DisplayMode = DisplayMode.FullScreen;
                await initial.CloseAsync();
            }
            await using (var restored = new WindowPresentationFixture(root))
            {
                AssertWindowPresentation(restored, width, height, maximized, fullScreen: true,
                    scenario + " relaunched");
                restored.Model.DisplayMode = DisplayMode.Resizable;
                AssertWindowPresentation(restored, width, height, maximized, fullScreen: false,
                    scenario + " restored after relaunch");
                checks.Add(new { Scenario = scenario, WindowedMaximized = maximized, Passed = true });
            }
        }

        foreach (var maximized in new[] { false, true })
        {
            var scenario = maximized ? "minimized maximized window" : "minimized normal window";
            var root = Path.Combine(fixtureRoot, maximized ? "minimized-maximized" : "minimized-normal");
            await using (var initial = new WindowPresentationFixture(root))
            {
                initial.Window.Width = width;
                initial.Window.Height = height;
                if (maximized) initial.Window.WindowState = WindowState.Maximized;
                initial.Window.WindowState = WindowState.Minimized;
                await initial.CloseAsync();
            }
            await using (var restored = new WindowPresentationFixture(root))
            {
                AssertWindowPresentation(restored, width, height, maximized, fullScreen: false, scenario);
                checks.Add(new { Scenario = scenario, RestoredMaximized = maximized, Passed = true });
            }
        }

        await File.WriteAllTextAsync(Path.Combine(Output, "window-presentation-interactions.json"),
            JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Window presentation: normal size, maximized restore size, fullscreen startup precedence and minimized-state persistence passed.");
    }

    private static void AssertWindowPresentation(WindowPresentationFixture fixture, double width, double height,
        bool maximized, bool fullScreen, string scenario)
    {
        var saved = fixture.Model.SavedWindowPresentation;
        if (saved is null || Math.Abs(saved.Width - width) > 0.5 || Math.Abs(saved.Height - height) > 0.5 ||
            saved.Maximized != maximized)
            throw new InvalidOperationException($"{scenario}: persisted normal dimensions or maximized state changed. Saved: {saved}.");
        var expectedState = fullScreen || maximized ? WindowState.Maximized : WindowState.Normal;
        if (fixture.Model.DisplayMode != (fullScreen ? DisplayMode.FullScreen : DisplayMode.Resizable) ||
            fixture.Window.WindowStyle != (fullScreen ? WindowStyle.None : WindowStyle.SingleBorderWindow) ||
            fixture.Window.ResizeMode != (fullScreen ? ResizeMode.NoResize : ResizeMode.CanResize) ||
            fixture.Window.WindowState != expectedState)
            throw new InvalidOperationException($"{scenario}: startup or restored display mode did not match the saved choice.");
        if (!fullScreen && !maximized) AssertNormalSize(fixture.Window, width, height, scenario);
    }

    private static void AssertNormalSize(Window window, double width, double height, string scenario)
    {
        if (Math.Abs(window.Width - width) > 0.5 || Math.Abs(window.Height - height) > 0.5)
            throw new InvalidOperationException($"{scenario}: expected normal size {width}×{height}, got {window.Width}×{window.Height}.");
    }

    private sealed class WindowPresentationFixture : IAsyncDisposable
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loadedCount;
        private bool _closeRequested;

        public WindowPresentationFixture(string root)
        {
            Model = new MainViewModel(ManifestLoader.LoadClassicUs(), new SqliteSessionStore(root));
            Window = new GoldenTicket.Desktop.MainWindow(Model, _ => true);
            Window.Loaded += (_, _) => _loadedCount++;
            Window.Closed += (_, _) => _closed.TrySetResult();
            if (new WindowInteropHelper(Window).EnsureHandle() == IntPtr.Zero)
                throw new InvalidOperationException("Window preference smoke requires an initialized native window.");
            if (_loadedCount != 0 || Window.IsVisible)
                throw new InvalidOperationException("Initializing the native window must not show it or raise Loaded.");
        }

        public MainViewModel Model { get; }
        public GoldenTicket.Desktop.MainWindow Window { get; }

        public async Task CloseAsync()
        {
            if (!_closeRequested)
            {
                _closeRequested = true;
                Window.Close();
            }
            await _closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (_loadedCount != 0 || Window.IsVisible)
                throw new InvalidOperationException("Window preference smoke must remain offscreen without raising Loaded.");
        }

        public async ValueTask DisposeAsync()
        {
            try { await CloseAsync(); }
            finally { await Model.DisposeToolsAsync(); }
        }
    }
}
