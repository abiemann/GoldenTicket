using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Desktop.Views;
using GoldenTicket.Application;
using GoldenTicket.Domain.Manifest;

internal static class Program
{
    private static readonly List<object> Results = [];
    private static readonly BindingListener BindingLog = new();
    private static string Output = "";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 1) throw new ArgumentException("Usage: GoldenTicket.UiSmoke [output-directory]");
        Output = Path.GetFullPath(args.Length == 1 ? args[0] : "artifacts/ui-smoke");
        Directory.CreateDirectory(Output);
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/GoldenTicket;component/Theme/Palette.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/GoldenTicket;component/Theme/Controls.xaml") });
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(BindingLog);
        app.Startup += async (_, _) =>
        {
            MainViewModel? model = null;
            try
            {
                model = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
                model.Setup.ManualVerificationAccepted = true;
                model.Setup.Seats[0].DisplayName = "Alex";
                model.Setup.Seats[1].DisplayName = "Conductor";
                model.Setup.Seats[2].DisplayName = "Brakeman";
                await RenderSizes("setup", () => new SetupView { DataContext = model });
                foreach (var seat in model.Setup.Seats) seat.IsComputer = true;
                await model.StartMatchCommand.ExecuteAsync(null);
                for (var step = 0; step < 35 && model.Table.ClaimedRoutes.Count < 3; step++)
                {
                    if (model.Table.Placement is not null) model.Table.WholeBoardAcknowledged = true;
                    await model.ConfirmPlacementCommand.ExecuteAsync(null);
                }
                Console.WriteLine($"Table fixture: {model.Table.ClaimedRoutes.Count} routes, phase {model.Table.PhaseText}");
                await RenderSizes("table", () => new TableView { DataContext = model });
                model.Table.SaveName = "Morning acceptance test";
                await model.SaveAndPackAwayCommand.ExecuteAsync(null);
                await model.BeginRebuildCommand.ExecuteAsync(null);
                await RenderSizes("rebuild", () => new RebuildView { DataContext = model });
                await RenderSizes("camera-no-device", () => new CameraView { DataContext = model.Camera });
                await VerifyKeyboardCornerHandler();
                await RenderSizes("connection-off", () => new ConnectionView { DataContext = model.Connection });
                await model.ShowCheckpointPhotoCommand.ExecuteAsync(null);
                await RenderSizes("checkpoint-photo", () => new CheckpointPhotoView { DataContext = model.CheckpointPhoto });
                await File.WriteAllTextAsync(Path.Combine(Output, "layout-report.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
                await File.WriteAllTextAsync(Path.Combine(Output, "binding-errors.log"), BindingLog.Text.ToString());
                Console.WriteLine($"Rendered {Results.Count} real-view cases. Binding errors/warnings: {BindingLog.ErrorCount}.");
                if (BindingLog.ErrorCount > 0) Environment.ExitCode = 2;
            }
            catch (Exception ex) { Console.WriteLine(ex); Environment.ExitCode = 1; }
            finally
            {
                if (model is not null) await model.DisposeToolsAsync();
                app.Shutdown();
            }
        };
        app.Run();
    }

    private static async Task RenderSizes(string name, Func<UserControl> make)
    {
        foreach (var (width, height) in new[] { (1280, 800), (1000, 620) })
        {
            BindingLog.Context = $"{name}-{width}x{height}";
            var view = make();
            var root = new Border { Width = width, Height = height, Child = view,
                Background = (Brush)System.Windows.Application.Current.Resources["Surface.Window"] };
            TextElement.SetForeground(root, (Brush)System.Windows.Application.Current.Resources["Text.Primary"]);
            await Arrange(root, width, height);
            var beforeErrors = BindingLog.ErrorCount;
            var buttons = Descendants<ButtonBase>(root).Where(b => b.Visibility == Visibility.Visible).Select(b =>
            {
                var bounds = b.TransformToAncestor(root).TransformBounds(new Rect(b.RenderSize));
                return new { Type = b.GetType().Name, Label = Label(b), Bounds = new { bounds.X, bounds.Y, bounds.Width, bounds.Height },
                    b.IsEnabled, IsTabStop = b is Control c && c.IsTabStop,
                    Foreground = b.Foreground.ToString(),
                    ContentForegrounds = Descendants<TextBlock>(b).Select(t => t.Foreground.ToString()).Distinct().ToArray(),
                    HorizontalOverflow = bounds.Left < -1 || bounds.Right > width + 1 };
            }).ToArray();
            var scrolls = Descendants<ScrollViewer>(root).Where(s => s.ScrollableHeight > 1 || s.ScrollableWidth > 1).ToArray();
            Save(root, $"{name}-{width}x{height}-top.png", width, height);
            foreach (var scroll in scrolls) scroll.ScrollToBottom();
            await Arrange(root, width, height);
            if (scrolls.Length > 0) Save(root, $"{name}-{width}x{height}-bottom.png", width, height);
            Results.Add(new { View = name, width, height, Buttons = buttons, Scrolls = scrolls.Select(s => new { s.ScrollableHeight, s.ScrollableWidth }).ToArray(), BindingErrorsAtRender = BindingLog.ErrorCount - beforeErrors });
            Console.WriteLine($"{name} {width}x{height}: {buttons.Length} controls, {buttons.Count(b => b.HorizontalOverflow)} horizontally outside viewport, {scrolls.Length} scroll surfaces.");
            if (buttons.Any(b => b.Label.Length > 0 && b.HorizontalOverflow))
                throw new InvalidOperationException($"An interactive control extends outside the horizontal viewport: {name} {width}×{height}.");
            foreach (var button in Descendants<Button>(root).Where(b => b.Visibility == Visibility.Visible &&
                b.Style == System.Windows.Application.Current.Resources["PrimaryButton"]))
            {
                if (Descendants<TextBlock>(button).Any(t => t.Foreground.ToString() != button.Foreground.ToString()))
                    throw new InvalidOperationException($"Primary button text does not inherit its intended contrast color: {Label(button)}.");
            }
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            root.Child = null;
            view.DataContext = null;
        }
    }

    private static async Task Arrange(FrameworkElement root, int width, int height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        root.UpdateLayout();
    }

    private static async Task VerifyKeyboardCornerHandler()
    {
        await using var camera = new CameraViewModel();
        var view = new CameraView { DataContext = camera };
        var image = (Image)view.FindName("PreviewImage");
        camera.Preview = BitmapSource.Create(20, 20, 96, 96, PixelFormats.Bgra32, null, new byte[1600], 80);
        camera.SelectingCorners = true;
        await Arrange(view, 1000, 620);
        var source = new FixturePresentationSource { RootVisual = view };
        image.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Right)
            { RoutedEvent = Keyboard.KeyDownEvent });
        image.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter)
            { RoutedEvent = Keyboard.KeyDownEvent });
        if (camera.SelectedCorners.Count != 1 || camera.SelectedCorners[0].X <= .05 || camera.SelectedCorners[0].Y != .05)
            throw new InvalidOperationException("The camera keyboard corner handler did not move and place its first corner.");
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Console.WriteLine("Camera keyboard handler: synthetic Right/Enter routed events moved and placed one corner; no OS input injected.");
    }

    private static void Save(Visual root, string file, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Output, file));
        encoder.Save(stream);
    }

    private static string Label(ContentControl control) => control.Content switch
    {
        string text => text,
        TextBlock text => text.Text,
        _ => string.Join(" ", Descendants<TextBlock>(control).Select(t => t.Text))
    };

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private sealed class BindingListener : TraceListener
    {
        public readonly StringBuilder Text = new();
        public string Context = "initialization";
        public int ErrorCount;
        public override void Write(string? message) => Text.Append(message);
        public override void WriteLine(string? message)
        {
            ErrorCount++;
            Text.AppendLine($"[{Context}] {message}");
        }
    }

    private sealed class FixturePresentationSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
