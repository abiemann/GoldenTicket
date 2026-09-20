using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Line = System.Windows.Shapes.Line;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Desktop.Views;
using GoldenTicket.Application;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Vision;

internal static partial class Program
{
    private static readonly List<object> Results = [];
    private static readonly BindingListener BindingLog = new();
    private static string Output = "";

    [STAThread]
    private static void Main(string[] args)
    {
        var markerScoresOnly = args.Length > 0 && args[0] == "--marker-scores";
        if (args.Length > (markerScoresOnly ? 2 : 1))
            throw new ArgumentException("Usage: GoldenTicket.UiSmoke [output-directory] | --marker-scores [output-directory]");
        Output = Path.GetFullPath(markerScoresOnly
            ? args.Length == 2 ? args[1] : "artifacts/ui-smoke-marker-scores"
            : args.Length == 1 ? args[0] : "artifacts/ui-smoke");
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
                if (markerScoresOnly)
                {
                    await RunMarkerScoresSmoke();
                    return;
                }
                model = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
                model.Setup.ManualVerificationAccepted = true;
                model.Setup.Seats[0].DisplayName = "Alex";
                model.Setup.Seats[1].DisplayName = "Conductor";
                model.Setup.Seats[2].DisplayName = "Brakeman";
                await VerifyWindowShutdown();
                await VerifyWindowExitConfirmation();
                await VerifyDisplayMode();
                await VerifyWindowPresentationPersistence();
                await VerifyFinalStandingsSharing();
                await VerifyGameMenu();
                await VerifyGameTableLayout();
                await VerifyPlacementTarget();
                await VerifyGameLayerTransition();
                await VerifyResumeTurnAnnouncement();
                await VerifySavedMatchSelection();
                await VerifySavedMatchName();
                await RenderSizes("setup", () => new SetupView { DataContext = model });
                await VerifyHumanPresentation();
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
                await VerifyProcessingPresentation();
                await VerifyPreviewZoom();
                await VerifyPreviewPanGestures();
                await RenderSizes("connection-off", () => new ConnectionView { DataContext = model.Connection });
                await model.ShowCheckpointPhotoCommand.ExecuteAsync(null);
                await RenderSizes("checkpoint-photo", () => new CheckpointPhotoView { DataContext = model.CheckpointPhoto });
                await VerifyCheckpointPhotoPresentation();
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

    private static async Task VerifyWindowShutdown()
    {
        var checks = new List<object>();
        foreach (var delayCleanup in new[] { false, true })
        {
            BindingLog.Context = delayCleanup ? "window-shutdown-delayed" : "window-shutdown-idle";
            var model = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
            var dispatcher = Dispatcher.CurrentDispatcher;
            var failures = new List<Exception>();
            var gate = (SemaphoreSlim)(typeof(CameraCaptureService)
                .GetField("_lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(model.Camera.Capture)
                ?? throw new InvalidOperationException("The camera shutdown fixture needs its lifecycle semaphore."));
            var gateHeld = false;
            GoldenTicket.Desktop.MainWindow? window = null;
            var closedCount = 0;
            var loadedCount = 0;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnDispatcherFailure(object sender, DispatcherUnhandledExceptionEventArgs args)
            {
                failures.Add(args.Exception);
                args.Handled = true;
            }
            dispatcher.UnhandledException += OnDispatcherFailure;
            try
            {
                if (delayCleanup)
                {
                    await gate.WaitAsync();
                    gateHeld = true;
                }
                // Construct the real production window, but never Show it or raise Loaded:
                // there is no native input, camera, listener, or persisted-session load.
                window = new GoldenTicket.Desktop.MainWindow(model);
                window.Loaded += (_, _) => loadedCount++;
                window.Closed += (_, _) => { closedCount++; closed.TrySetResult(); };
                window.Close();
                window.Close();
                if (closedCount != 0 || window.IsEnabled)
                    throw new InvalidOperationException("Closing must disable input and defer final close until the original Closing event returns.");
                if (gateHeld)
                {
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    if (closedCount != 0)
                        throw new InvalidOperationException("The window must remain open while camera cleanup is pending.");
                    gate.Release();
                    gateHeld = false;
                }
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                if (closedCount != 1 || loadedCount != 0 || window.IsVisible || failures.Count != 0)
                    throw new InvalidOperationException("An unseen window must close exactly once without dispatcher exceptions or loading user state.", failures.FirstOrDefault());
                checks.Add(new { Scenario = delayCleanup ? "delayed camera disposal" : "synchronous idle disposal",
                    CloseRequests = 2, ClosedEvents = closedCount, LoadedEvents = loadedCount,
                    DispatcherExceptions = failures.Count, Passed = true });
            }
            finally
            {
                if (gateHeld) gate.Release();
                // A failed assertion must still drain disposal and detach the window's system
                // subscriptions before the next scenario or the runner's explicit shutdown.
                try
                {
                    await model.DisposeToolsAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    if (window is not null && closedCount == 0)
                    {
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        if (closedCount == 0) window.Close();
                        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
                finally { dispatcher.UnhandledException -= OnDispatcherFailure; }
            }
        }
        await File.WriteAllTextAsync(Path.Combine(Output, "window-shutdown-interactions.json"),
            JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Window shutdown: synchronous and delayed cleanup close exactly once after repeated close requests, with no dispatcher exceptions.");
    }

    private static async Task VerifyWindowExitConfirmation()
    {
        BindingLog.Context = "window-exit-confirmation";
        var model = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
        var dispatcher = Dispatcher.CurrentDispatcher;
        var failures = new List<Exception>();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closedCount = 0;
        var promptCount = 0;
        var allowExit = false;
        var loadedCount = 0;
        GoldenTicket.Desktop.MainWindow? window = null;
        void OnDispatcherFailure(object sender, DispatcherUnhandledExceptionEventArgs args)
        {
            failures.Add(args.Exception);
            args.Handled = true;
        }
        dispatcher.UnhandledException += OnDispatcherFailure;
        try
        {
            model.Setup.ManualVerificationAccepted = true;
            for (var index = 0; index < model.Setup.Seats.Count; index++)
                model.Setup.Seats[index].IsComputer = index != 0;
            await model.StartMatchAsync();
            if (model.PrivateSeat is null) throw new InvalidOperationException("The exit fixture needs a revealed human hand.");
            // Exercise the real closing event and production model gate, replacing only the
            // modal answer so this test never shows UI or waits for native input.
            window = new GoldenTicket.Desktop.MainWindow(model, prompt =>
            {
                promptCount++;
                if (!prompt.CanExit || !prompt.Message.Contains("saved automatically", StringComparison.Ordinal) ||
                    model.PrivateSeat is not null || model.CanRevealPrivateSeat || !window!.IsEnabled)
                    throw new InvalidOperationException("The exit warning must cover private cards without starting cleanup.");
                return allowExit;
            });
            window.Loaded += (_, _) => loadedCount++;
            window.Closed += (_, _) => { closedCount++; closed.TrySetResult(); };
            window.Close();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (promptCount != 1 || closedCount != 0 || !window.IsEnabled || model.PrivateSeat is not null || !model.CanRevealPrivateSeat)
                throw new InvalidOperationException("Canceling exit must keep the window usable and its private hand covered.");
            await model.RevealPrivateSeatAsync();
            if (model.PrivateSeat is null) throw new InvalidOperationException("A canceled exit must permit a deliberate reveal.");
            allowExit = true;
            window.Close();
            window.Close();
            if (closedCount != 0 || window.IsEnabled)
                throw new InvalidOperationException("Confirmed exit must retain deferred cleanup and final close.");
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (promptCount != 2 || closedCount != 1 || loadedCount != 0 || failures.Count != 0)
                throw new InvalidOperationException("Exit confirmation must close once without duplicate prompts or dispatcher errors.", failures.FirstOrDefault());
            await File.WriteAllTextAsync(Path.Combine(Output, "window-exit-confirmation.json"),
                JsonSerializer.Serialize(new { Prompts = promptCount, CanceledExitKeptWindowUsable = true,
                    PrivateHandStayedCovered = true, DeliberateRevealSucceeded = true,
                    RepeatedCloseDuringCleanupDidNotReprompt = true, ClosedEvents = closedCount, LoadedEvents = loadedCount,
                    DispatcherExceptions = failures.Count, Passed = true }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Exit confirmation: cancel preserves the window; confirm closes once; private cards stay covered and cleanup requests do not reprompt.");
        }
        finally
        {
            try
            {
                allowExit = true;
                await model.DisposeToolsAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (window is not null && closedCount == 0)
                {
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    if (closedCount == 0) window.Close();
                    await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            finally { dispatcher.UnhandledException -= OnDispatcherFailure; }
        }
    }

    private static async Task VerifySavedMatchSelection()
    {
        var checks = new List<string>();
        var store = new InMemorySessionStore();
        var manifest = ManifestLoader.LoadClassicUs();
        var source = new MainViewModel(manifest, store);
        var resume = new MainViewModel(manifest, store);
        var multiple = new MainViewModel(manifest, new InMemorySessionStore());
        SetupView? singleView = null;
        SetupView? multipleView = null;
        try
        {
            source.Setup.ManualVerificationAccepted = true;
            await source.StartMatchCommand.ExecuteAsync(null);
            await resume.LoadSavedSessionsCommand.ExecuteAsync(null);
            var summary = (await store.ListSessionsAsync(CancellationToken.None)).Single();
            if (resume.Setup.SelectedSavedSession?.SessionId != summary.SessionId)
                throw new InvalidOperationException("A sole saved match must be selected automatically before the user clicks Resume.");

            await RenderSizes("setup-saved-single-selected-synthetic", () => new SetupView { DataContext = resume }, view =>
            {
                var list = SavedList(view, resume);
                if (Descendants<CheckBox>(list).Single().IsChecked != true || !ResumeButton(view, resume).IsEnabled)
                    throw new InvalidOperationException("A sole saved match must have a visible checkmark and enabled Resume button.");
            });
            checks.Add("A sole saved match is automatically checked and its real bound Resume button is enabled.");

            singleView = new SetupView { DataContext = resume };
            await Arrange(singleView, 1000, 620);
            var resumeButton = ResumeButton(singleView, resume);
            var invoke = new ButtonAutomationPeer(resumeButton).GetPattern(PatternInterface.Invoke) as IInvokeProvider
                ?? throw new InvalidOperationException("The Resume button must support accessible invocation.");
            invoke.Invoke();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await (resume.ResumeMatchCommand.ExecutionTask
                ?? throw new InvalidOperationException("Invoking the real Resume button must start its bound command."));
            if (resume.Screen != Screen.Table || !resume.NeedsBoardReconciliation ||
                resume.BoardReconciliationAcknowledged || resume.PrivateSeat is not null || resume.CanRevealPrivateSeat)
                throw new InvalidOperationException("Resume must open the selected match while preserving the physical-board check and privacy gate.");
            checks.Add("Accessible invocation of the real Resume button restores the in-memory match and retains board reconciliation and covered cards.");

            var second = summary with { SessionId = SessionId.New(), SeatNames = ["Second synthetic match"] };
            multiple.Setup.LoadSavedSessions([summary, second]);
            await RenderSizes("setup-saved-multiple-unselected-synthetic", () => new SetupView { DataContext = multiple }, view =>
            {
                var list = SavedList(view, multiple);
                if (Descendants<CheckBox>(list).Any(box => box.IsChecked == true) || ResumeButton(view, multiple).IsEnabled)
                    throw new InvalidOperationException("Multiple saved matches must require a visible choice before Resume becomes enabled.");
            });
            checks.Add("Multiple saved matches begin unchecked and Resume stays disabled until a match is chosen.");

            multipleView = new SetupView { DataContext = multiple };
            await Arrange(multipleView, 1000, 620);
            CheckBox[] Choices() => Descendants<CheckBox>(SavedList(multipleView, multiple)).ToArray();
            async Task ToggleChoice(int index)
            {
                var toggle = new CheckBoxAutomationPeer(Choices()[index]).GetPattern(PatternInterface.Toggle) as IToggleProvider
                    ?? throw new InvalidOperationException("Saved-match checkboxes must support accessible toggling.");
                toggle.Toggle();
                await Arrange(multipleView, 1000, 620);
            }

            await ToggleChoice(0);
            if (multiple.Setup.SelectedSavedSession?.SessionId != summary.SessionId ||
                Choices().Count(box => box.IsChecked == true) != 1 || !ResumeButton(multipleView, multiple).IsEnabled)
                throw new InvalidOperationException("Checking the first saved match must select it and enable Resume exactly once.");
            await ToggleChoice(1);
            if (multiple.Setup.SelectedSavedSession?.SessionId != second.SessionId ||
                Choices()[0].IsChecked != false || Choices()[1].IsChecked != true)
                throw new InvalidOperationException("Checking a different match must clear the previous checkmark and select only the new match.");
            checks.Add("Toggling either real checkbox selects exactly that match and switching to another clears the previous checkmark.");

            var previousRow = multiple.Setup.SelectedSavedSession;
            multiple.Setup.LoadSavedSessions([second, summary]);
            await Arrange(multipleView, 1000, 620);
            if (multiple.Setup.SelectedSavedSession?.SessionId != second.SessionId ||
                ReferenceEquals(previousRow, multiple.Setup.SelectedSavedSession) || Choices()[0].IsChecked != true ||
                Choices().Count(box => box.IsChecked == true) != 1 || !ResumeButton(multipleView, multiple).IsEnabled)
                throw new InvalidOperationException("Refreshing saved rows must preserve the selected session identity and its visible checkmark after reordering.");
            checks.Add("Refreshing and reordering saved rows preserves the selected session ID, checkmark, and enabled Resume button.");

            await ToggleChoice(0);
            if (multiple.Setup.SelectedSavedSession is not null || Choices().Any(box => box.IsChecked == true) ||
                ResumeButton(multipleView, multiple).IsEnabled)
                throw new InvalidOperationException("Unchecking the selected match must clear selection and disable Resume.");
            checks.Add("Unchecking the selected match clears the choice and disables Resume without a double toggle.");

            await File.WriteAllTextAsync(Path.Combine(Output, "saved-match-selection-interactions.json"),
                JsonSerializer.Serialize(new { Fixture = "Synthetic in-memory matches; no user saves, native input, or camera.",
                    Checks = checks, Passed = true }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Saved-match selection: {checks.Count} synthetic UI checks passed, including actual bound Resume invocation.");
        }
        finally
        {
            if (singleView is not null) singleView.DataContext = null;
            if (multipleView is not null) multipleView.DataContext = null;
            await source.DisposeToolsAsync();
            await resume.DisposeToolsAsync();
            await multiple.DisposeToolsAsync();
        }

        static ListBox SavedList(DependencyObject view, MainViewModel model) => Descendants<ListBox>(view)
            .Single(list => ReferenceEquals(list.ItemsSource, model.Setup.SavedSessions));
        static Button ResumeButton(DependencyObject view, MainViewModel model) => Descendants<Button>(view)
            .Single(button => ReferenceEquals(button.Command, model.ResumeMatchCommand));
    }

    private static async Task VerifySavedMatchName()
    {
        var store = new InMemorySessionStore();
        var manifest = ManifestLoader.LoadClassicUs();
        var source = new MainViewModel(manifest, store);
        var picker = new MainViewModel(manifest, store);
        try
        {
            source.Setup.ManualVerificationAccepted = true;
            await source.StartMatchCommand.ExecuteAsync(null);
            await source.CommitTicketsCommand.ExecuteAsync(null);
            source.Table.SaveName = "test2";
            await source.SaveAndPackAwayCommand.ExecuteAsync(null);
            if (!source.Table.IsPackedAway)
                throw new InvalidOperationException($"The named-save fixture failed to save: {source.Status}");
            await picker.LoadSavedSessionsCommand.ExecuteAsync(null);

            await RenderSizes("setup-saved-named-test2-synthetic", () => new SetupView { DataContext = picker }, view =>
            {
                var list = Descendants<ListBox>(view)
                    .Single(item => ReferenceEquals(item.ItemsSource, picker.Setup.SavedSessions));
                var checkbox = Descendants<CheckBox>(list).Single();
                var label = checkbox.Content as TextBlock;
                if (checkbox.IsChecked != true || label is null ||
                    !label.Text.StartsWith("test2  ·  ", StringComparison.Ordinal) ||
                    !label.Text.Contains("Packed away", StringComparison.Ordinal) ||
                    label.Text.Contains("PackedAway", StringComparison.Ordinal))
                    throw new InvalidOperationException("The saved match must display the entered name first, a readable status, and its selection checkmark.");
            });
            Console.WriteLine("Saved-match name: test2 preserved through Save and Pack Away and displayed first in both rendered picker sizes.");
        }
        finally
        {
            await source.DisposeToolsAsync();
            await picker.DisposeToolsAsync();
        }
    }

    private static async Task RenderSizes(string name, Func<UserControl> make, Action<UserControl>? verify = null,
        IReadOnlyList<(int width, int height)>? sizes = null)
    {
        foreach (var (width, height) in sizes ?? [(1280, 800), (1000, 620)])
        {
            BindingLog.Context = $"{name}-{width}x{height}";
            var view = make();
            var root = new Border { Width = width, Height = height, Child = view,
                Background = (Brush)System.Windows.Application.Current.Resources["Surface.Window"] };
            TextElement.SetForeground(root, (Brush)System.Windows.Application.Current.Resources["Text.Primary"]);
            await Arrange(root, width, height);
            if (view is CameraView cameraView)
            {
                // A detached render tree has no window to deliver Loaded after its image binding and
                // layout settle. Exercise the same subscription/redraw path that the real window uses.
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                await Arrange(root, width, height);
                var overlay = (Canvas)cameraView.FindName("CornerOverlay");
                var expectedCorners = ((CameraViewModel)cameraView.DataContext).SelectedCorners.Count;
                if (overlay.Children.OfType<Border>().Count() != expectedCorners)
                    throw new InvalidOperationException("The camera overlay must render every editable corner handle.");
            }
            verify?.Invoke(view);
            var beforeErrors = BindingLog.ErrorCount;
            var buttons = Descendants<ButtonBase>(root).Where(IsElementShown).Select(b =>
            {
                var bounds = b.TransformToAncestor(root).TransformBounds(new Rect(b.RenderSize));
                return new { Type = b.GetType().Name, Label = Label(b), Bounds = new { bounds.X, bounds.Y, bounds.Width, bounds.Height },
                    b.IsEnabled, IsTabStop = b is Control c && c.IsTabStop,
                    Foreground = b.Foreground.ToString(),
                    ContentForegrounds = Descendants<TextBlock>(b).Select(t => t.Foreground.ToString()).Distinct().ToArray(),
                    HorizontalOverflow = bounds.Left < -1 || bounds.Right > width + 1 };
            }).ToArray();
            var scrolls = Descendants<ScrollViewer>(root).Where(s => IsElementShown(s) &&
                (s.ScrollableHeight > 1 || s.ScrollableWidth > 1)).ToArray();
            Save(root, $"{name}-{width}x{height}-top.png", width, height);
            foreach (var scroll in scrolls) scroll.ScrollToBottom();
            await Arrange(root, width, height);
            if (scrolls.Length > 0) Save(root, $"{name}-{width}x{height}-bottom.png", width, height);
            Results.Add(new { View = name, width, height, Buttons = buttons, Scrolls = scrolls.Select(s => new { s.ScrollableHeight, s.ScrollableWidth }).ToArray(), BindingErrorsAtRender = BindingLog.ErrorCount - beforeErrors });
            Console.WriteLine($"{name} {width}x{height}: {buttons.Length} controls, {buttons.Count(b => b.HorizontalOverflow)} horizontally outside viewport, {scrolls.Length} scroll surfaces.");
            if (buttons.Any(b => b.Label.Length > 0 && b.HorizontalOverflow))
                throw new InvalidOperationException($"An interactive control extends outside the horizontal viewport: {name} {width}×{height}.");
            foreach (var button in Descendants<Button>(root).Where(b => IsElementShown(b) &&
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

    private static bool IsElementShown(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private static async Task VerifyGameMenu()
    {
        var model = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
        try
        {
            void VerifyRoster(UserControl view)
            {
                static (byte red, byte green, byte blue) Pixel(BitmapSource source, int x, int y)
                {
                    var image = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                    var bytes = new byte[4];
                    image.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0);
                    return (bytes[2], bytes[1], bytes[0]);
                }
                var dialog = (Border)view.FindName("AiSelectionDialog");
                var back = (Button)view.FindName("AiSelectionBackButton");
                var title = (TextBlock)view.FindName("AiSelectionTitle");
                var instructions = (TextBlock)view.FindName("SeatInstructions");
                var play = (Button)view.FindName("PlayButton");
                Rect Bounds(FrameworkElement element) => element.TransformToAncestor(view)
                    .TransformBounds(new Rect(element.RenderSize));
                var dialogBounds = Bounds(dialog);
                var backBounds = Bounds(back);
                var titleBounds = Bounds(title);
                if (Math.Abs(backBounds.Left - dialogBounds.Left - 9) > 2 ||
                    Math.Abs(backBounds.Top + backBounds.Height / 2 - titleBounds.Top - titleBounds.Height / 2) > 2 ||
                    Math.Abs(titleBounds.Left + titleBounds.Width / 2 - dialogBounds.Left - dialogBounds.Width / 2) > 2 ||
                    backBounds.Right + 8 > titleBounds.Left || back.Content is not null)
                    throw new InvalidOperationException("The steampunk back arrow must sit at the dialog's left edge beside its centered title.");
                var faces = Descendants<Image>(view).Where(image => image.DataContext is GameSeatChoice).ToArray();
                if (faces.Length != 5 || play.Content as string != "SET-UP BOARD" ||
                    play.IsEnabled != model.Game.CanPlay)
                    throw new InvalidOperationException("All five character portraits and the chosen-seat board setup button must be visible.");
                var seatButtons = Descendants<Button>(dialog).Where(button => button.DataContext is GameSeatChoice).ToArray();
                if (Descendants<CheckBox>(dialog).Any() || seatButtons.Length != 5 ||
                    Bounds(instructions).Top <= seatButtons.Max(button => Bounds(button).Bottom))
                    throw new InvalidOperationException("The roster instruction must follow all five faces without a checkbox.");
                if (model.Game.SelectedSeatCount == 0 && model.Game.SeatChoices.Any(choice => choice.IsSelected))
                    throw new InvalidOperationException("No character should appear selected when the roster first opens.");
                foreach (var face in faces)
                {
                    var choice = (GameSeatChoice)face.DataContext;
                    if (choice.Role != CharacterRole.Unselected) continue;
                    if (face.Source is not BitmapSource portrait)
                        throw new InvalidOperationException("Unselected characters need a rendered portrait.");
                    foreach (var (x, y) in new[] { (128, 100), (220, 100) })
                    {
                        var (red, green, blue) = Pixel(portrait, x, y);
                        if (red != green || green != blue)
                            throw new InvalidOperationException("Faces and scenery must remain grayscale before selection.");
                    }
                    var (coatRed, coatGreen, coatBlue) = Pixel(portrait, 70, 200);
                    var coatShowsColor = choice.TrainColor switch
                    {
                        PlayerColor.Red => coatRed > coatGreen * 1.5 && coatRed > coatBlue * 1.5,
                        PlayerColor.Green => coatGreen > coatRed && coatGreen > coatBlue,
                        PlayerColor.Yellow => coatRed > coatGreen && coatGreen > coatBlue * 2,
                        PlayerColor.Blue => coatBlue > coatRed * 2 && coatBlue > coatGreen * 2,
                        PlayerColor.Black => coatRed < 60 && coatGreen < 60 && coatBlue < 60,
                        _ => false,
                    };
                    if (!coatShowsColor)
                        throw new InvalidOperationException("Only the unselected character's jacket should retain its train color.");
                }
            }

            await RenderSizes("game-welcome", () => new GameScreenView { DataContext = model }, view =>
            {
                var settings = (Button)view.FindName("SettingsButton");
                if (!IsElementShown(settings) || settings.Content as string != "Settings" ||
                    AutomationProperties.GetName(settings) != "Settings" ||
                    Descendants<System.Windows.Shapes.Path>(settings).Any())
                    throw new InvalidOperationException("The welcome tile must expose a text-only Settings button.");
            });
            var settingsView = new GameScreenView { DataContext = model };
            await Arrange(settingsView, 1000, 620);
            ((Button)settingsView.FindName("SettingsButton"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (!model.Game.IsSettings || model.Game.IsWelcome)
                throw new InvalidOperationException("The Settings button must open the shared settings panel.");
            await RenderSizes("game-settings", () => new GameScreenView { DataContext = model }, view =>
            {
                var dialog = (Border)view.FindName("SettingsDialog");
                var displayMode = Descendants<ComboBox>(dialog).Single(combo =>
                    AutomationProperties.GetName(combo) == "Display mode");
                var quality = Descendants<ComboBox>(dialog).Single(combo =>
                    AutomationProperties.GetName(combo) == "Camera quality preference");
                var processor = Descendants<ComboBox>(dialog).Single(combo =>
                    AutomationProperties.GetName(combo) == "Image processor preference");
                var destinationsWithTrainCards = Descendants<CheckBox>(dialog).Single(box =>
                    AutomationProperties.GetName(box) == "Show Destinations when viewing Train cards");
                var ok = Descendants<Button>(dialog).Single(button => button.Content as string == "OK");
                if (!IsElementShown(dialog) || displayMode.SelectedValue is not DisplayMode.Resizable ||
                    !ReferenceEquals(quality.SelectedItem, model.Camera.SelectedPreference) ||
                    !ReferenceEquals(processor.SelectedItem, model.Camera.SelectedProcessor) ||
                    destinationsWithTrainCards.IsChecked != true || ok.ActualHeight < 48 ||
                    ok.HorizontalAlignment != HorizontalAlignment.Right ||
                    Descendants<Button>(dialog).Any(button => Equals(button.Tag, "NavigationBack")))
                    throw new InvalidOperationException("Settings must use the same live camera preferences as the utility screens.");
            });
            ((Button)settingsView.FindName("SettingsOkButton"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (!model.Game.IsWelcome)
                throw new InvalidOperationException("OK from Settings must return to Choose a journey.");
            await model.Game.ActivateSelectedAsync();
            await RenderSizes("game-five-unselected", () => new GameScreenView { DataContext = model }, VerifyRoster);
            await RenderSizes("game-five-unselected-narrow", () => new GameScreenView { DataContext = model },
                VerifyRoster, [(875, 680)]);
            var backView = new GameScreenView { DataContext = model };
            await Arrange(backView, 1000, 620);
            ((Button)backView.FindName("AiSelectionBackButton"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (!model.Game.IsWelcome)
                throw new InvalidOperationException("The roster back arrow must return to the opening menu.");
            await model.Game.ActivateSelectedAsync();
            model.Game.CycleSeat(model.Game.SeatChoices[0]);
            model.Game.CycleSeat(model.Game.SeatChoices[1]);
            await RenderSizes("game-two-chosen", () => new GameScreenView { DataContext = model }, VerifyRoster);
            model.Game.CycleSeat(model.Game.SeatChoices[0]);
            await RenderSizes("game-robot-face", () => new GameScreenView { DataContext = model }, VerifyRoster);
            model.Game.SelectSeat(5);
            await model.Game.ActivateSelectedAsync();
            await RenderSizes("game-camera-setup", () => new GameScreenView { DataContext = model }, view =>
            {
                var dialog = (Border)view.FindName("CameraSetupDialog");
                var roster = (Border)view.FindName("AiSelectionDialog");
                var help = (TextBlock)view.FindName("CameraSetupHelp");
                var frame = (Border)view.FindName("CameraSetupPreviewFrame");
                var preview = (Image)view.FindName("CameraSetupPreview");
                var corners = (Canvas)view.FindName("CameraSetupCornerOverlay");
                var cancel = (Button)view.FindName("ConfirmationCancelButton");
                var play = (Button)view.FindName("ConfirmationPlayButton");
                Rect Bounds(FrameworkElement element) => element.TransformToAncestor(view)
                    .TransformBounds(new Rect(element.RenderSize));
                if (!IsElementShown(dialog) || IsElementShown(roster) ||
                    !model.Game.IsCameraSetup || model.Setup.ManualVerificationAccepted ||
                    help.Text != "Position the board game and camera so the entire board is visible" ||
                    cancel.Content as string != "CANCEL" || play.Content as string != "PLAY!" ||
                    play.IsEnabled || corners.Children.Count != 0 ||
                    Bounds(help).Bottom >= Bounds(frame).Top ||
                    Bounds(frame).Bottom >= Bounds(cancel).Top ||
                    Bounds(frame).Bottom >= Bounds(play).Top ||
                    !ReferenceEquals(preview.Source, model.Camera.Preview) ||
                    System.Windows.Data.BindingOperations.GetBindingExpression(preview, Image.SourceProperty)?
                        .ParentBinding.Path.Path != "Camera.Preview")
                    throw new InvalidOperationException("Camera setup must hide the roster, share the preview, and hold PLAY until ML finds all four corners.");
            }, [(875, 680), (1280, 800)]);
            var camera = model.Camera;
            var capture = camera.Capture;
            var captureType = typeof(CameraCaptureService);
            var cameraType = typeof(CameraViewModel);
            var pixels = new byte[320 * 180 * 4];
            for (var y = 0; y < 180; y++)
            for (var x = 0; x < 320; x++)
            {
                var pixel = (y * 320 + x) * 4;
                // Distinct printed-board detail lets the orientation check reject a 180° turn.
                pixels[pixel] = (byte)(35 + (x * 7 + y * 3 + x * y / 11) % 175);
                pixels[pixel + 1] = (byte)(30 + (x * 5 + y * 13 + x * y / 7) % 180);
                pixels[pixel + 2] = (byte)(45 + (x * 11 + y * 9 + x * y / 9) % 165);
                pixels[pixel + 3] = 255;
            }
            var frameForCorners = CameraFrame.CopyFromBgra32(320, 180, pixels, 1, 1);
            try
            {
                captureType.GetField("_epoch", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(capture, 1L);
                captureType.GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(capture, true);
                captureType.GetField("_latest", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(capture, frameForCorners);
                camera.IsRunning = true;
                camera.Preview = BitmapSource.Create(320, 180, 96, 96, PixelFormats.Bgra32,
                    null, pixels, 320 * 4);
                camera.BeginGameBoardFraming();
                cameraType.GetField("_gameBoardCapture", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(camera, (1L, 320, 180));
                cameraType.GetField("_gameBoardAcceptedAt", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(camera, DateTimeOffset.UtcNow);
                cameraType.GetProperty(nameof(CameraViewModel.GameBoardCorners))!.GetSetMethod(true)!
                    .Invoke(camera, [new NormalizedPoint[]
                    { new(.1, .12), new(.9, .12), new(.9, .88), new(.1, .88) }]);
                camera.GameBoardFramingStatus = "Missing scoring marker: red.";
                await RenderSizes("game-camera-corners-synthetic",
                    () => new GameScreenView { DataContext = model }, view =>
                    {
                        var overlay = (Canvas)view.FindName("CameraSetupCornerOverlay");
                        var notice = (Border)view.FindName("CameraSetupNotice");
                        var markerInstruction = (TextBlock)view.FindName("CameraSetupMarkerInstruction");
                        var play = (Button)view.FindName("ConfirmationPlayButton");
                        var whiteLines = overlay.Children.OfType<Line>()
                            .Where(line => ReferenceEquals(line.Stroke, Brushes.White)).ToArray();
                        if (!camera.HasFreshGameBoardCorners || camera.CanStartGameWithBoard || play.IsEnabled ||
                            markerInstruction.Visibility != Visibility.Visible ||
                            notice.Visibility != Visibility.Visible ||
                            overlay.Children.Count != 16 || whiteLines.Length != 8 ||
                            whiteLines.Any(line => line.X1 < 0 || line.X2 > overlay.ActualWidth ||
                                line.Y1 < 0 || line.Y2 > overlay.ActualHeight))
                            throw new InvalidOperationException("Four fresh ML corners must draw white plus signs but keep PLAY disabled until score pieces are checked.");
                    }, [(875, 680), (1280, 800)]);
                cameraType.GetField("_gameMarkersReady", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(camera, true);
                cameraType.GetField("_gameMarkersCapture", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(camera, (1L, 320, 180));
                cameraType.GetField("_gameMarkersAcceptedAt", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(camera, DateTimeOffset.UtcNow);
                var setupCorners = new NormalizedPoint[]
                    { new(.1, .12), new(.9, .12), new(.9, .88), new(.1, .88) };
                cameraType.GetField("_gameBoardOrientationIndex", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(camera, 0);
                var setupRegistration = BoardRegistration.Create(frameForCorners,
                    GameBoardOrientations.Enumerate(BoardCropPadding.Expand(frameForCorners,
                        setupCorners).Corners)[0]);
                cameraType.GetMethod("SetAcceptedSetupReference", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(camera, [frameForCorners, setupRegistration]);
                camera.GameBoardFramingStatus = "";
                cameraType.GetProperty(nameof(CameraViewModel.GameBoardCorners))!.GetSetMethod(true)!
                    .Invoke(camera, [setupCorners]);
                await RenderSizes("game-camera-pieces-ready-synthetic",
                    () => new GameScreenView { DataContext = model }, view =>
                    {
                        var play = (Button)view.FindName("ConfirmationPlayButton");
                        var notice = (Border)view.FindName("CameraSetupNotice");
                        var markerInstruction = (TextBlock)view.FindName("CameraSetupMarkerInstruction");
                        if (!camera.CanStartGameWithBoard || !play.IsEnabled || notice.Visibility != Visibility.Collapsed ||
                            markerInstruction.Visibility != Visibility.Collapsed)
                            throw new InvalidOperationException("Fresh corners and score pieces must enable PLAY and hide the board notice.");
                    }, [(875, 680), (1280, 800)]);
                var freshBoardFrame = CameraFrame.CopyFromBgra32(320, 180, pixels, 2, 1);
                captureType.GetField("_latest", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(capture, freshBoardFrame);
                camera.BeginGameTablePreview();
                for (var attempt = 0; attempt < 20 && camera.GameTablePreview is null; attempt++)
                    await Task.Delay(100);
                camera.EndGameBoardFraming();
                if (camera.GameTablePreview is not { PixelWidth: 960, PixelHeight: 600 })
                    throw new InvalidOperationException("PLAY must retain a cropped live board from the accepted camera frame.");
                var savedUprightPhoto = camera.GameTablePreview;
                camera.EndGameTablePreview();
                camera.RequestGameTablePreview();
                captureType.GetField("_latest", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(capture, CameraFrame.CopyFromBgra32(320, 180, pixels, 3, 1));
                foreach (var corner in new[] { new NormalizedPoint(.1, .12), new(.9, .12),
                    new(.9, .88), new(.1, .88) }) camera.SelectedCorners.Add(corner);
                cameraType.GetMethod("UpdateBoardCrop", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(camera, null);
                camera.SetGameTableReference(savedUprightPhoto);
                for (var attempt = 0; attempt < 20 && camera.GameTablePreview is null; attempt++)
                    await Task.Delay(100);
                if (camera.GameTablePreview is not { PixelWidth: 960, PixelHeight: 600 })
                    throw new InvalidOperationException("Re-registering the board in Camera must restore the game's live crop.");
                camera.SelectedCorners.Clear();
                camera.EndGameTablePreview();
            }
            finally
            {
                camera.EndGameBoardFraming();
                camera.EndGameTablePreview();
                camera.Preview = null;
                camera.IsRunning = false;
                captureType.GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(capture, false);
                captureType.GetField("_latest", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(capture, null);
            }
            var confirmationView = new GameScreenView { DataContext = model };
            await Arrange(confirmationView, 875, 680);
            ((Button)confirmationView.FindName("ConfirmationCancelButton"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (model.Game.IsCameraSetup || !model.Game.IsCharacterSelection)
                throw new InvalidOperationException("Cancelling camera setup must return to the roster.");
            var pointerView = new GameScreenView { DataContext = model };
            await Arrange(pointerView, 1000, 620);
            var fourth = Descendants<Button>(pointerView).Single(button =>
                button.DataContext is GameSeatChoice { Number: 4 });
            model.Game.SelectSeat(0);
            ((Button)pointerView.FindName("PlayButton"))
                .RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                    { RoutedEvent = Mouse.MouseEnterEvent });
            if (model.Game.SeatSelection != 0)
                throw new InvalidOperationException("Hovering SET-UP BOARD must not leave its selected fill latched.");
            fourth.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                { RoutedEvent = Mouse.MouseEnterEvent });
            if (model.Game.SeatSelection != 3)
                throw new InvalidOperationException("Hover must move the face selection indicator.");
            fourth.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(400);
            if (model.Game.SeatChoices[3].Role != CharacterRole.Human)
                throw new InvalidOperationException("Click must colorize an unselected human portrait.");
            fourth.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(400);
            if (model.Game.SeatChoices[3].Role != CharacterRole.Computer)
                throw new InvalidOperationException("A second click must show the matched robot portrait.");
            fourth.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(400);
            if (model.Game.SeatChoices[3].Role != CharacterRole.Unselected)
                throw new InvalidOperationException("A third click must return to the unselected grayscale portrait.");

            var keyboardModel = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
            try
            {
                var keyboardView = new GameScreenView { DataContext = keyboardModel };
                await Arrange(keyboardView, 1000, 620);
                var source = new FixturePresentationSource { RootVisual = keyboardView };
                void KeyPress(Key key) => keyboardView.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                    source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                KeyPress(Key.Enter);
                KeyPress(Key.Enter);
                await Task.Delay(400);
                KeyPress(Key.Right);
                KeyPress(Key.Enter);
                await Task.Delay(400);
                KeyPress(Key.Enter);
                await Task.Delay(400);
                if (!keyboardModel.Game.IsCharacterSelection || keyboardModel.Game.SelectedSeatCount != 2 ||
                    keyboardModel.Game.SeatChoices[0].Role != CharacterRole.Human ||
                    keyboardModel.Game.SeatChoices[1].Role != CharacterRole.Computer)
                    throw new InvalidOperationException("Enter and cursor keys must cycle and select the five character choices.");
            }
            finally { await keyboardModel.DisposeToolsAsync(); }

            var colorModel = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
            try
            {
                await colorModel.Game.ActivateSelectedAsync();
                foreach (var choice in colorModel.Game.SeatChoices) colorModel.Game.CycleSeat(choice);
                await RenderSizes("game-five-colored", () => new GameScreenView { DataContext = colorModel },
                    view =>
                    {
                        if (Descendants<Image>(view).Count(image => image.DataContext is GameSeatChoice &&
                            image.Source is BitmapImage) != 5)
                            throw new InvalidOperationException("All five selected humans must use color portraits.");
                    }, [(1280, 800)]);
                foreach (var choice in colorModel.Game.SeatChoices.Skip(2)) colorModel.Game.CycleSeat(choice);
                await RenderSizes("game-last-three-robots", () => new GameScreenView { DataContext = colorModel },
                    view =>
                    {
                        if (colorModel.Game.SeatChoices.Skip(2).Any(choice =>
                            choice.Role != CharacterRole.Computer || !choice.PortraitUri.Contains("robot-256")))
                            throw new InvalidOperationException("The last three characters must show their matching robot portraits.");
                    }, [(1280, 800)]);
            }
            finally { await colorModel.DisposeToolsAsync(); }

            var savedStore = new InMemorySessionStore();
            var savedSource = new MainViewModel(ManifestLoader.LoadClassicUs(), savedStore);
            var savedMenu = new MainViewModel(ManifestLoader.LoadClassicUs(), savedStore);
            try
            {
                savedSource.Setup.ManualVerificationAccepted = true;
                await savedSource.StartMatchAsync();
                await savedMenu.LoadSavedSessionsAsync();
                await RenderSizes("game-welcome-saved", () => new GameScreenView { DataContext = savedMenu }, view =>
                {
                    var reload = (Button)view.FindName("ReloadButton");
                    var settings = (Button)view.FindName("SettingsButton");
                    Rect Bounds(FrameworkElement element) => element.TransformToAncestor(view)
                        .TransformBounds(new Rect(element.RenderSize));
                    if (reload.Visibility != Visibility.Visible)
                        throw new InvalidOperationException("A saved game must offer Reload the previous game.");
                    if (Math.Abs(Bounds(settings).Right - Bounds(reload).Right) > 2 ||
                        Bounds(settings).Top <= Bounds(reload).Bottom)
                        throw new InvalidOperationException("The Settings button must sit below and right-align with Reload the previous game.");
                });
                savedMenu.Game.SelectWelcome(1);
                await savedMenu.Game.ActivateSelectedAsync();
                var builtInCamera = new CameraDevice("built-in-synthetic", "Built-in webcam");
                var overheadCamera = new CameraDevice("overhead-synthetic", "Overhead webcam");
                savedMenu.Camera.Devices.Add(builtInCamera);
                savedMenu.Camera.Devices.Add(overheadCamera);
                savedMenu.Camera.SelectedDevice = builtInCamera;
                await RenderSizes("game-reload-camera-setup", () => new GameScreenView { DataContext = savedMenu }, view =>
                {
                    var dialog = (Border)view.FindName("CameraSetupDialog");
                    var help = (TextBlock)view.FindName("CameraSetupHelp");
                    var markerInstruction = (TextBlock)view.FindName("CameraSetupMarkerInstruction");
                    var picker = (ComboBox)view.FindName("CameraSetupDevicePicker");
                    var play = (Button)view.FindName("ConfirmationPlayButton");
                    if (!IsElementShown(dialog) || !savedMenu.Game.IsReloadCameraSetup ||
                        help.Text != "Choose the webcam showing the whole board. Keep all four corners visible." ||
                        markerInstruction.Visibility != Visibility.Collapsed ||
                        !ReferenceEquals(picker.ItemsSource, savedMenu.Camera.Devices) ||
                        picker.Items.Count != 2 || !Equals(picker.SelectedItem, builtInCamera) ||
                        play.Content as string != "RELOAD GAME" || play.IsEnabled)
                        throw new InvalidOperationException("Reload must show the shared live-camera setup with a webcam picker before restoring a match.");
                    picker.SelectedItem = overheadCamera;
                    if (savedMenu.Camera.SelectedDevice != overheadCamera)
                        throw new InvalidOperationException("The reload webcam selector must change the selected camera.");
                }, [(1280, 800)]);
            }
            finally
            {
                await savedSource.DisposeToolsAsync();
                await savedMenu.DisposeToolsAsync();
            }
            Console.WriteLine("Game menu: staged renders, hover/click and cursor/Enter interactions passed.");
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static async Task VerifyGameTableLayout()
    {
        var model = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
        try
        {
            var pixels = new byte[960 * 600 * 4];
            for (var index = 0; index < pixels.Length; index += 4)
            {
                pixels[index] = 85;
                pixels[index + 1] = 120;
                pixels[index + 2] = 155;
                pixels[index + 3] = 255;
            }
            model.Game.ShowPlaying();
            model.Camera.GameTablePreview = BitmapSource.Create(960, 600, 96, 96,
                PixelFormats.Bgra32, null, pixels, 960 * 4);
            model.Camera.GameTablePreviewStatus = "";
            model.Screen = Screen.Table;
            model.Table.TurnText = "Setup";
            model.Table.ActiveSeatName = "Player 1";
            model.Table.Instruction = "Each seat keeps at least 2 of its 3 opening destinations.";
            var colors = new[] { PlayerColor.Red, PlayerColor.Blue, PlayerColor.Green,
                PlayerColor.Black, PlayerColor.Yellow };
            void AddSeat(int index) => model.Table.Seats.Add(new SeatRow(new SeatId(index + 1),
                index is 1 or 3 ? $"Computer {index / 2 + 1}" : $"Player {index / 2 + 1}",
                colors[index], "★", index is 1 or 3 ? "computer" : "human",
                0, 45, 4, index == 0 ? 2 : 3, 0, index == 0,
                index == 0 ? "Kept 2 destinations and returned 1." :
                index == 1 ? "Claimed Kansas City - Saint Louis (lane A) for 2 points." :
                "Waiting for first action."));
            AddSeat(0);
            AddSeat(1);
            for (var index = 0; index < 5; index++)
                model.Table.Market.Add(new MarketSlotRow(index, null, index == 0 ? "Locomotive" : "Card"));

            void Verify(UserControl view)
            {
                var gameTable = view is GameTableView table ? table :
                    Descendants<GameTableView>(view).Single();
                var scene = (Canvas)gameTable.FindName("TableScene");
                var board = (Image)gameTable.FindName("LiveBoardImage");
                var stations = (ItemsControl)gameTable.FindName("PlayerStations");
                var drawPilesPanel = (Border)gameTable.FindName("DrawPilesPanel");
                var marketPanel = (Border)gameTable.FindName("FaceUpMarketPanel");
                var phase = (TextBlock)gameTable.FindName("GuidancePhaseText");
                var seat = (TextBlock)gameTable.FindName("GuidanceSeatText");
                var instruction = (TextBlock)gameTable.FindName("GuidanceInstructionText");
                var market = Descendants<ItemsControl>(gameTable)
                    .Single(control => ReferenceEquals(control.ItemsSource, model.Table.Market));
                var locomotive = Descendants<TextBlock>(market)
                    .Single(text => text.DataContext is MarketSlotRow { Label: "Locomotive" });
                var locomotiveCard = Ancestors(locomotive).OfType<Border>()
                    .First(border => border.DataContext is MarketSlotRow);
                var marketButtons = Descendants<Button>(market)
                    .Where(button => button.DataContext is MarketSlotRow).ToArray();
                if (marketButtons.Length != 5)
                    throw new InvalidOperationException("The market must render five face-up card buttons.");
                var firstMarketBounds = marketButtons[0].TransformToAncestor(marketPanel)
                    .TransformBounds(new Rect(marketButtons[0].RenderSize));
                var lastMarketBounds = marketButtons[^1].TransformToAncestor(marketPanel)
                    .TransformBounds(new Rect(marketButtons[^1].RenderSize));
                if (!IsElementShown(gameTable) || scene.Width != 1440 || scene.Height != 900 ||
                    !ReferenceEquals(board.Source, model.Camera.GameTablePreview) ||
                    stations.Items.Count != model.Table.Seats.Count || market.Items.Count != 5 ||
                    locomotive.TextWrapping != TextWrapping.NoWrap || locomotiveCard.Width < 76 ||
                    firstMarketBounds.Left < marketPanel.BorderThickness.Left + marketPanel.Padding.Left - .5 ||
                    lastMarketBounds.Right > marketPanel.ActualWidth -
                        marketPanel.BorderThickness.Right - marketPanel.Padding.Right + .5 ||
                    phase.Text != "Setup" || seat.Text != "Player 1" ||
                    instruction.Text != "Each seat keeps at least 2 of its 3 opening destinations." ||
                    Descendants<Button>(gameTable).Any(button => IsElementShown(button) &&
                        AutomationProperties.GetName(button) is not
                            ("Show your train cards" or "Show your destinations" or
                             "Draw a train card from the pile" or "Draw destination tickets") &&
                        !AutomationProperties.GetName(button).StartsWith("Draw face-up ",
                            StringComparison.Ordinal)) ||
                    Descendants<Button>(gameTable).Any(button => IsElementShown(button) &&
                        AutomationProperties.GetName(button).StartsWith("Draw ",
                            StringComparison.Ordinal) && button.IsEnabled) ||
                    VisibleText(gameTable).Contains("Shift+Esc", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The game table must retain its phase, acting-seat and human-instruction guidance above the shared board crop.");
                if (Canvas.GetLeft(drawPilesPanel) != 368 || Canvas.GetLeft(marketPanel) != 622 ||
                    Canvas.GetTop(marketPanel) != 768)
                    throw new InvalidOperationException($"Draw piles and market must stay centered below the board for {model.Table.Seats.Count} players; got draw X={Canvas.GetLeft(drawPilesPanel)}, market X/Y={Canvas.GetLeft(marketPanel)}/{Canvas.GetTop(marketPanel)}.");
                var sceneBounds = scene.TransformToAncestor(view).TransformBounds(new Rect(scene.RenderSize));
                if (sceneBounds.Left < -1 || sceneBounds.Top < -1 ||
                    sceneBounds.Right > view.ActualWidth + 1 || sceneBounds.Bottom > view.ActualHeight + 1)
                    throw new InvalidOperationException("The whole game-table scene must fit in windowed and maximized layouts.");
                var stationBorders = Descendants<Border>(gameTable)
                    .Where(border => border.DataContext is GameTableSeat && border.Width == 250).ToArray();
                if (stationBorders.Length != model.Table.Seats.Count ||
                    stationBorders.Any(border =>
                    {
                        var bounds = border.TransformToAncestor(view).TransformBounds(new Rect(border.RenderSize));
                        return bounds.Left < -1 || bounds.Top < -1 ||
                            bounds.Right > view.ActualWidth + 1 || bounds.Bottom > view.ActualHeight + 1;
                    }))
                    throw new InvalidOperationException("Every player portrait and card stack must remain inside the scaled game table.");
                var firstTile = stationBorders.Single(border =>
                    border.DataContext is GameTableSeat tileSeat && tileSeat.Seat.SeatId.Value == 1);
                var tileGrid = Descendants<Grid>(firstTile).Single(grid =>
                    grid.RowDefinitions.Count == 4 && grid.ColumnDefinitions.Count == 3);
                var tileText = Descendants<TextBlock>(tileGrid).ToArray();
                var name = tileText.Single(text => text.Text == "Player 1");
                var trainLabel = tileText.Single(text => text.Text == "Train cards");
                var trainsRemaining = tileText.Single(text => text.Name == "TrainsRemainingText");
                var destinationLabel = tileText.Single(text => text.Text == "Destinations");
                var trainCount = tileText.Single(text => text.Text == "4");
                var destinationCount = tileText.Single(text => text.Text == "2");
                if (Grid.GetRow(name) != Grid.GetRow(trainLabel) ||
                    Grid.GetRow(name) != Grid.GetRow(destinationLabel) ||
                    Grid.GetRow(trainCount) != 2 || Grid.GetRow(destinationCount) != 2 ||
                    Descendants<Border>(firstTile).Any(border => border.Width == 20 && border.Height == 8))
                    throw new InvalidOperationException("Player, train-card and destination labels and counts must align without a train bar graph.");
                if (Grid.GetRow(trainsRemaining) != 0 || Grid.GetColumn(trainsRemaining) != 1 ||
                    Math.Abs(trainsRemaining.TranslatePoint(new Point(), tileGrid).X -
                             trainLabel.TranslatePoint(new Point(), tileGrid).X) > 0.5)
                    throw new InvalidOperationException("Remaining trains must sit above the T card and align with Train cards.");
                var trainStackButton = Descendants<Button>(tileGrid)
                    .Single(button => AutomationProperties.GetName(button) == "Show your train cards");
                var destinationStackButton = Descendants<Button>(tileGrid)
                    .Single(button => AutomationProperties.GetName(button) == "Show your destinations");
                if (Grid.GetColumn(trainStackButton) != 1 || Grid.GetColumn(destinationStackButton) != 2)
                    throw new InvalidOperationException("The train-card and destination stack buttons must align with their labels.");
                var trainStack = Descendants<ItemsControl>(trainStackButton).Single();
                var destinationStack = Descendants<ItemsControl>(destinationStackButton).Single();
                if (trainStack.Items.Count != 4 || destinationStack.Items.Count != 2 ||
                    Descendants<Border>(trainStack).Count(border => border.Width == 36 && border.Height == 35) != 4 ||
                    Descendants<Border>(destinationStack).Count(border => border.Width == 36 && border.Height == 35) != 2 ||
                    trainStack.Items[0] is not CardStackLayer { Left: 12, Top: 0 } ||
                    trainStack.Items[3] is not CardStackLayer { Left: 0, Top: 6, Letter: "T" } ||
                    destinationStack.Items[0] is not CardStackLayer { Left: 12, Top: 0 } ||
                    destinationStack.Items[1] is not CardStackLayer { Left: 0, Top: 6, Letter: "D" })
                    throw new InvalidOperationException("Seat stacks must render four train cards and two destinations with equal fan height.");
                var leftSeats = model.Game.TableSeats.Where(tile => tile.Left == 10).ToArray();
                var rightSeats = model.Game.TableSeats.Where(tile => tile.Left == 1180).ToArray();
                if (model.Game.TableSeats[0].Left != 10 || model.Game.TableSeats[1].Left != 1180 ||
                    leftSeats.Length + rightSeats.Length != model.Table.Seats.Count ||
                    Math.Abs(leftSeats.Length - rightSeats.Length) > 1 ||
                    (model.Table.Seats.Count == 3 &&
                     (model.Game.TableSeats[2].Left != 10 || model.Game.TableSeats[2].Top != 530)) ||
                    (model.Table.Seats.Count == 5 &&
                     (leftSeats.Length != 3 || rightSeats.Length != 2 ||
                      Math.Abs(leftSeats.Average(tile => tile.Top) - rightSeats.Average(tile => tile.Top)) > .5)))
                    throw new InvalidOperationException("Every player must flank the board in balanced side columns, with five players sharing the same vertical center across the two columns.");
                var status = Descendants<TextBlock>(gameTable)
                    .Single(text => text.DataContext is GameTableSeat tableSeat && tableSeat.Seat.SeatId.Value == 1 &&
                                    text.Text == "Kept 2 destinations and returned 1.");
                if (!IsElementShown(status) || status.TextAlignment != TextAlignment.Center ||
                    status.TextWrapping != TextWrapping.Wrap || status.Height != 26 ||
                    tileGrid.RowDefinitions[3].ActualHeight < 26 ||
                    Grid.GetRow(status) != 3 || Grid.GetColumnSpan(status) != 3)
                    throw new InvalidOperationException("The latest public action must have two centered lines reserved along the bottom of the player tile.");
            }

            await RenderSizes("game-table-two", () => new GameScreenView { DataContext = model }, Verify,
                [(1000, 620), (1280, 800)]);
            AddSeat(2);
            await RenderSizes("game-table-three", () => new GameScreenView { DataContext = model }, Verify,
                [(1000, 620), (1280, 800)]);
            AddSeat(3);
            await RenderSizes("game-table-four", () => new GameScreenView { DataContext = model }, Verify,
                [(1000, 620), (1280, 800)]);
            AddSeat(4);
            await RenderSizes("game-table-five", () => new GameScreenView { DataContext = model }, Verify,
                [(1000, 620), (1280, 800)]);

            var paymentHand = new[]
            {
                TrainCardKind.Blue, TrainCardKind.Blue, TrainCardKind.Yellow,
                TrainCardKind.Yellow, TrainCardKind.Black, TrainCardKind.Black,
                TrainCardKind.Green, TrainCardKind.Green, TrainCardKind.Red, TrainCardKind.Red,
            }.Select((kind, index) => new HeldCard(new CardId(index + 1), kind)).ToArray();
            var proposal = new BoardFirstClaimProposal(SessionId.New(), new SeatId(1),
                "Player 1", new RouteId("atlanta--raleigh--a"), "Atlanta - Raleigh (lane A)",
                1, 1, 1, 1,
                [new BoardFirstPaymentRow(new PaymentOption(TrainCardKind.Blue, 2, 0), "2 Blue"),
                 new BoardFirstPaymentRow(new PaymentOption(TrainCardKind.Yellow, 2, 0), "2 Yellow"),
                 new BoardFirstPaymentRow(new PaymentOption(TrainCardKind.Black, 2, 0), "2 Black"),
                 new BoardFirstPaymentRow(new PaymentOption(TrainCardKind.Green, 2, 0), "2 Green"),
                 new BoardFirstPaymentRow(new PaymentOption(TrainCardKind.Red, 2, 0), "2 Red")],
                paymentHand);
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.BoardFirstProposal))!
                .GetSetMethod(nonPublic: true)!.Invoke(model, [proposal]);
            await RenderSizes("game-table-board-first-payment",
                () => new GameScreenView { DataContext = model }, view =>
                {
                    var gameTable = Descendants<GameTableView>(view).Single();
                    var scene = (Canvas)gameTable.FindName("TableScene");
                    var panel = (Border)gameTable.FindName("BoardFirstClaimPanel");
                    var guidance = (Border)gameTable.FindName("HumanGuidancePanel");
                    var board = (Border)gameTable.FindName("GameBoardFrame");
                    foreach (var card in proposal.Cards) card.IsSelected = false;
                    var choices = Descendants<ToggleButton>(panel).ToArray();
                    var ok = Descendants<Button>(panel).Single(button => button.Content as string == "OK");
                    var scroller = Descendants<ScrollViewer>(panel).Single();
                    var panelTop = panel.TranslatePoint(new Point(), scene).Y;
                    var boardTop = board.TranslatePoint(new Point(), scene).Y;
                    if (!IsElementShown(panel) || IsElementShown(guidance) ||
                        panelTop + panel.ActualHeight > boardTop + 55 ||
                        choices.Length != paymentHand.Length || ok.IsEnabled ||
                        !ReferenceEquals(ok.Command, model.ConfirmBoardFirstClaimCommand) ||
                        choices.Where((choice, index) =>
                            !Equals(choice.Content, paymentHand[index].Kind) ||
                            choice.IsChecked != false).Any() ||
                        !IsGameHorizontalScroller(scroller))
                        throw new InvalidOperationException("Detected-route payment must show individual selectable cards and a disabled OK action until a legal selection is made.");
                    ((IToggleProvider)new ToggleButtonAutomationPeer(choices[0])
                        .GetPattern(PatternInterface.Toggle)!).Toggle();
                    ((IToggleProvider)new ToggleButtonAutomationPeer(choices[2])
                        .GetPattern(PatternInterface.Toggle)!).Toggle();
                    view.UpdateLayout();
                    if (ok.IsEnabled || choices[0].IsChecked != true || choices[2].IsChecked != true)
                        throw new InvalidOperationException("Mixed card colors must not enable route payment.");
                    ((IToggleProvider)new ToggleButtonAutomationPeer(choices[2])
                        .GetPattern(PatternInterface.Toggle)!).Toggle();
                    ((IToggleProvider)new ToggleButtonAutomationPeer(choices[1])
                        .GetPattern(PatternInterface.Toggle)!).Toggle();
                    view.UpdateLayout();
                    if (!ok.IsEnabled || proposal.SelectedPayment?.Option != proposal.Payments[0].Option ||
                        proposal.SelectedCardIds.Length != 2)
                        throw new InvalidOperationException("Two selected Blue cards must enable OK for the matching legal payment.");
                }, [(1000, 620), (1280, 800)]);

            Console.WriteLine("Game table: persistent human guidance, 2/5 player stations, public card stacks, shared crop, no top-left controls, and uniform resize passed.");
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static async Task VerifyDisplayMode()
    {
        var model = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
        var window = new GoldenTicket.Desktop.MainWindow(model, _ => true)
        {
            Left = 80, Top = 65, Width = 1200, Height = 760,
        };
        try
        {
            if (model.DisplayMode != DisplayMode.Resizable ||
                window.WindowStyle != WindowStyle.SingleBorderWindow || window.ResizeMode != ResizeMode.CanResize)
                throw new InvalidOperationException("The game must start in resizable window mode.");
            var game = (GameScreenView)window.FindName("GameLayer");
            model.Game.OpenSettings();
            await Arrange(game, 1000, 620);
            var displayMode = Descendants<ComboBox>(game).Single(combo =>
                AutomationProperties.GetName(combo) == "Display mode");
            displayMode.SelectedIndex = 1;
            if (model.DisplayMode != DisplayMode.FullScreen)
                throw new InvalidOperationException("The Settings choice must change the display mode.");
            if (window.WindowStyle != WindowStyle.None || window.ResizeMode != ResizeMode.NoResize ||
                window.WindowState != WindowState.Maximized)
                throw new InvalidOperationException("Full screen must hide the title bar and fill the display.");
            displayMode.SelectedIndex = 0;
            if (window.WindowStyle != WindowStyle.SingleBorderWindow || window.ResizeMode != ResizeMode.CanResize ||
                window.WindowState != WindowState.Normal || model.DisplayMode != DisplayMode.Resizable ||
                window.Left != 80 || window.Top != 65 ||
                window.Width != 1200 || window.Height != 760)
                throw new InvalidOperationException("Resizable mode must restore the title bar, size and position.");
            Console.WriteLine("Display mode: resizable default, full-screen title bar removal, and window restoration passed.");
        }
        finally
        {
            window.Close();
            await model.DisposeToolsAsync();
        }
    }

    private static async Task VerifyGameLayerTransition()
    {
        var model = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
        model.Setup.ManualVerificationAccepted = true;
        var camera = model.Camera;
        var window = new GoldenTicket.Desktop.MainWindow(model, _ => true);
        var root = (Grid)window.FindName("Root");
        var technical = (Grid)window.FindName("TechnicalLayer");
        var game = (GameScreenView)window.FindName("GameLayer");
        var returnButton = (Button)window.FindName("ReturnToGameButton");
        var reveal = typeof(GoldenTicket.Desktop.MainWindow).GetMethod("RevealTechnicalLayer",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The Shift+Escape reveal handler is missing.");
        try
        {
            await Arrange(root, 1000, 620);
            await model.StartMatchAsync();
            if (!game.IsEnabled || technical.IsEnabled)
                throw new InvalidOperationException("The game layer must be the only enabled layer at launch.");
            if (!model.IsPrivateVisible)
                throw new InvalidOperationException("The privacy fixture must reveal the opening hand before switching layers.");
            var source = new FixturePresentationSource { RootVisual = window };
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            window.RaiseEvent(escape);
            var exitMenu = (Grid)window.FindName("GameExitMenuOverlay");
            if (!escape.Handled || !model.IsGameExitMenuOpen ||
                exitMenu.Visibility != Visibility.Visible ||
                !model.ShowSoloOpeningTicketsOnBoard || !model.IsPrivateVisible)
                throw new InvalidOperationException("Plain Escape must show the exit menu without dismissing an unresolved solo opening choice.");
            await Arrange(root, 1000, 620);
            Save(root, "game-exit-menu-1000x620.png", 1000, 620);
            await Arrange(root, 1280, 800);
            Save(root, "game-exit-menu-1280x800.png", 1280, 800);
            var closeExitMenu = new KeyEventArgs(Keyboard.PrimaryDevice, source,
                Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(closeExitMenu);
            if (!closeExitMenu.Handled || model.IsGameExitMenuOpen ||
                !model.ShowSoloOpeningTicketsOnBoard || !model.IsPrivateVisible)
                throw new InvalidOperationException("A second Escape must return to the unchanged opening choice.");
            reveal.Invoke(window, null);
            reveal.Invoke(window, null);
            await Task.Delay(400);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (game.Visibility != Visibility.Collapsed || !technical.IsEnabled || model.IsPrivateVisible)
                throw new InvalidOperationException("Revealing the technical layer must cover private cards and disable the game layer.");
            await Arrange(root, 1280, 800);
            returnButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(400);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (game.Visibility != Visibility.Visible || !game.IsEnabled || technical.IsEnabled ||
                ((TranslateTransform)game.RenderTransform).X != 0 || !ReferenceEquals(camera, model.Camera) ||
                model.Screen != Screen.Table)
                throw new InvalidOperationException("Returning to the game must restore full coverage and preserve the match and camera.");
            Console.WriteLine("Game layer: reveal/return, repeat, resize, privacy and shared-camera state passed.");
        }
        finally
        {
            window.Close();
            await model.DisposeToolsAsync();
        }
    }

    private static async Task VerifyHumanPresentation()
    {
        var checks = new List<string>();
        var solo = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
        var keepAll = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
        var shared = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
        try
        {
            solo.Setup.ManualVerificationAccepted = true;
            solo.Setup.Seats[0].DisplayName = "Solo test player";
            if (!solo.IsSingleHumanGame || solo.CanConnectPhone || solo.ShowConnectionCommand.CanExecute(null))
                throw new InvalidOperationException("A single-human setup must use the laptop without a phone connection command.");
            await RenderSizes("solo-setup-synthetic", () => new SetupView { DataContext = solo }, view =>
            {
                var text = VisibleText(view);
                if (!text.Contains("No phone connection is needed.", StringComparison.Ordinal) ||
                    text.Contains("Multiple humans can pass", StringComparison.Ordinal))
                    throw new InvalidOperationException("Solo setup must show laptop guidance without multiple-human connection guidance.");
            });
            checks.Add("Single-human setup shows laptop-only guidance and disables Connect phone.");

            await solo.StartMatchCommand.ExecuteAsync(null);
            if (solo.ShowMultiHumanPhoneSetup)
                throw new InvalidOperationException("A single-human match must not show the shared-phone setup.");
            await RenderSizes("solo-no-phone-setup-synthetic", () => new GameScreenView { DataContext = solo }, view =>
            {
                var panel = (Border)view.FindName("MultiHumanPhoneSetupPanel");
                if (IsElementShown(panel) || Descendants<Button>(view).Any(button =>
                        IsElementShown(button) && AutomationProperties.GetName(button) == "Show shared phone setup"))
                    throw new InvalidOperationException("A single-human game must not display shared-phone setup or its QR shortcut.");
            });
            RequireHumanPrivateView(solo, "Solo test player", mustChooseTickets: true);
            var soloBoardPixels = new byte[960 * 600 * 4];
            for (var pixel = 0; pixel < soloBoardPixels.Length; pixel += 4)
            {
                soloBoardPixels[pixel] = 85;
                soloBoardPixels[pixel + 1] = 120;
                soloBoardPixels[pixel + 2] = 155;
                soloBoardPixels[pixel + 3] = 255;
            }
            solo.Camera.GameTablePreview = BitmapSource.Create(960, 600, 96, 96,
                PixelFormats.Bgra32, null, soloBoardPixels, 960 * 4);
            solo.Camera.GameTablePreviewStatus = "";
            if (solo.Table.ActiveSeatName != "Solo test player" ||
                solo.Table.Instruction != "Choose whether to keep all destinations or drop one.")
                throw new InvalidOperationException("Solo setup guidance must explain the keep-or-drop choice.");
            await RenderSizes("solo-opening-tickets-synthetic", () => new PrivateSeatView { DataContext = solo },
                view => VerifyPrivateLabels(view, solo, "Solo test player", singleHuman: true));
            await RenderSizes("solo-opening-on-board-synthetic", () =>
            {
                var layers = new Grid();
                layers.Children.Add(new GameTableView { DataContext = solo });
                layers.Children.Add(new PrivateSeatView { DataContext = solo });
                return new UserControl { Content = layers, DataContext = solo };
            }, view =>
            {
                var overlay = Descendants<Grid>(view).Single(grid => grid.Name == "SoloOpeningOverlay");
                var board = Descendants<Border>(view).Single(border => border.Name == "GameBoardFrame");
                var drawPiles = Descendants<Border>(view).Single(border => border.Name == "DrawPilesPanel");
                var faceUp = Descendants<Border>(view).Single(border => border.Name == "FaceUpMarketPanel");
                var guidanceSeat = Descendants<TextBlock>(view).Single(text => text.Name == "GuidanceSeatText");
                var guidanceInstruction = Descendants<TextBlock>(view).Single(text => text.Name == "GuidanceInstructionText");
                var cityMarkers = Descendants<ItemsControl>(view).Single(control => control.Name == "DestinationCityMarkers");
                var destinationLines = Descendants<ItemsControl>(view).Single(control => control.Name == "DestinationLines");
                if (!IsShown(overlay, view) || !VisibleText(view).Contains("Your Cards", StringComparison.Ordinal) ||
                    Canvas.GetTop(board) != 120 || board.ActualHeight + Canvas.GetTop(board) > 690 ||
                    guidanceSeat.Text != "Solo test player" ||
                    guidanceInstruction.Text != "Choose whether to keep all destinations or drop one." ||
                    !IsShown(cityMarkers, view) || cityMarkers.Items.Count == 0 ||
                    !IsShown(destinationLines, view) || destinationLines.Items.Count != 3 ||
                    IsShown(drawPiles, view) || IsShown(faceUp, view))
                    throw new InvalidOperationException("Solo setup must keep the board and cards visible together without the usual table controls or draw piles.");
            }, [(1280, 800), (1000, 620), (1920, 1080)]);
            await RenderSizes("solo-drop-confirmation-synthetic", () => new PrivateSeatView { DataContext = solo }, view =>
            {
                Descendants<Button>(view).First(button => button.DataContext is TicketChoiceRow)
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                view.UpdateLayout();
                if (((Grid)view.FindName("SoloDropConfirmation")).Visibility != Visibility.Visible)
                    throw new InvalidOperationException("A selected destination must show a confirm-or-cancel prompt.");
            });
            checks.Add("Starting a solo match presents three private destinations over the board.");

            var animatedTable = new GameTableView { DataContext = solo };
            await Arrange(animatedTable, 1280, 800);
            var animatedPiles = (Border)animatedTable.FindName("DrawPilesPanel");
            var animatedMarket = (Border)animatedTable.FindName("FaceUpMarketPanel");
            var openingView = new PrivateSeatView { DataContext = solo };
            await Arrange(openingView, 1280, 800);
            var dropped = solo.PrivateSeat!.Offer[2];
            var card = Descendants<Button>(openingView).Single(button => ReferenceEquals(button.DataContext, dropped));
            var confirmation = (Grid)openingView.FindName("SoloDropConfirmation");
            card.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (confirmation.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Clicking a destination must ask for confirmation before dropping it.");
            var openingSeat = solo.PrivateSeat;
            solo.SetWindowActive(false);
            if (!ReferenceEquals(openingSeat, solo.PrivateSeat) ||
                !solo.ShowSoloOpeningTicketsOnBoard || confirmation.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Losing focus must preserve the opening destination choice and its drop confirmation.");
            solo.SetWindowActive(true);
            Descendants<Button>(confirmation).Single(button => button.Content as string == "NO, GO BACK")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (confirmation.Visibility != Visibility.Collapsed || solo.PrivateSeat.Offer.Any(choice => !choice.Keep))
                throw new InvalidOperationException("Canceling the drop must leave all destinations selected.");
            card.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Descendants<Button>(confirmation).Single(button => button.Content as string == "YES, DROP IT")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            // Reduced-motion Windows can finish the commit during the click, clearing PrivateSeat.
            if (openingSeat.DestinationLines.Single(line => ReferenceEquals(line.Choice, dropped)).IsVisible ||
                (ReferenceEquals(solo.PrivateSeat, openingSeat) && card.Visibility != Visibility.Collapsed))
                throw new InvalidOperationException("The rejected card and its board connection must disappear together.");
            await Task.Delay(330);
            var cardsTile = (Border)openingView.FindName("SoloCardsTile");
            if (SystemParameters.ClientAreaAnimation &&
                cardsTile.RenderTransform is not TranslateTransform { Y: > 0 and < 225 })
                throw new InvalidOperationException("The remaining Your Cards tile must slide down after the rejected card disappears.");
            for (var attempt = 0; attempt < 50 && solo.ShowSoloOpeningTicketsOnBoard; attempt++)
                await Task.Delay(50);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            animatedTable.UpdateLayout();
            if (solo.ShowSoloOpeningTicketsOnBoard || !IsElementShown(animatedPiles) ||
                !IsElementShown(animatedMarket))
                throw new InvalidOperationException("The table panels must become visible after the card tile exits.");
            await Task.Delay(180);
            var pilesDuringSlide = Canvas.GetLeft(animatedPiles);
            var marketDuringSlide = Canvas.GetLeft(animatedMarket);
            if (SystemParameters.ClientAreaAnimation &&
                (pilesDuringSlide <= 14 || pilesDuringSlide >= 368 ||
                 marketDuringSlide <= 622 || marketDuringSlide >= 975))
                throw new InvalidOperationException("The draw piles and face-up cards must slide toward the center after the opening choice.");
            for (var attempt = 0; attempt < 50 && !solo.CanRevealPrivateSeat; attempt++)
                await Task.Delay(50);
            if (solo.Table.Seats.Single(seat => seat.DisplayName == "Solo test player").TicketCount != 2 ||
                solo.PrivateSeat is not null || !solo.CanRevealPrivateSeat)
                throw new InvalidOperationException("Confirming a drop must save two destinations and return to the public board.");
            await Task.Delay(600);
            if (Math.Abs(Canvas.GetLeft(animatedPiles) - 368) > 0.5 ||
                Math.Abs(Canvas.GetLeft(animatedMarket) - 622) > 0.5)
                throw new InvalidOperationException("The bottom panels must finish centered for a two-player game.");
            var humanTrainStack = Descendants<Button>(animatedTable).Single(button =>
                AutomationProperties.GetName(button) == "Show your train cards" &&
                button.DataContext is GameTableSeat tile && tile.Seat.DisplayName == "Solo test player");
            var humanDestinationsStack = Descendants<Button>(animatedTable).Single(button =>
                AutomationProperties.GetName(button) == "Show your destinations" &&
                button.DataContext is GameTableSeat tile && tile.Seat.DisplayName == "Solo test player");
            var computerStack = Descendants<Button>(animatedTable).First(button =>
                AutomationProperties.GetName(button) == "Show your train cards" &&
                button.DataContext is GameTableSeat tile && tile.Seat.Operator == "computer");
            var miniPanel = (Border)animatedTable.FindName("SoloCardPanel");
            var miniTrainCards = (ItemsControl)animatedTable.FindName("SoloTrainCards");
            var miniDestinations = (ItemsControl)animatedTable.FindName("SoloDestinationCards");
            var heldCityMarkers = (ItemsControl)animatedTable.FindName("DestinationCityMarkers");
            var heldDestinationLines = (ItemsControl)animatedTable.FindName("DestinationLines");
            if (!IsElementShown(humanTrainStack) || !IsElementShown(humanDestinationsStack) ||
                !humanTrainStack.IsHitTestVisible || !humanDestinationsStack.IsHitTestVisible ||
                !IsElementShown(computerStack) || computerStack.IsHitTestVisible || computerStack.Focusable ||
                IsElementShown(miniPanel) || solo.PrivateSeat is not null)
                throw new InvalidOperationException($"Only the solo human's T and D stacks should be clickable after opening setup. " +
                    $"Human shown/hit={IsElementShown(humanTrainStack)}/{humanTrainStack.IsHitTestVisible}, " +
                    $"destinations shown/hit={IsElementShown(humanDestinationsStack)}/{humanDestinationsStack.IsHitTestVisible}, " +
                    $"computer shown/hit/focus/command={IsElementShown(computerStack)}/{computerStack.IsHitTestVisible}/{computerStack.Focusable}/{computerStack.Command?.CanExecute(computerStack.CommandParameter)}, " +
                    $"panel={IsElementShown(miniPanel)}, private={solo.PrivateSeat is not null}, soloTurn={solo.IsSoloHumanTurn}.");
            if (humanTrainStack.Command is null ||
                !humanTrainStack.Command.CanExecute(humanTrainStack.CommandParameter))
                throw new InvalidOperationException($"The T stack must bind its toggle command and human seat; " +
                    $"command={humanTrainStack.Command is not null}, parameter={humanTrainStack.CommandParameter is GameTableSeat}.");

            await solo.ToggleSoloTrainCardsCommand.ExecuteAsync((GameTableSeat)computerStack.DataContext);
            if (solo.ShowSoloCardPanel || solo.PrivateSeat is not null)
                throw new InvalidOperationException("A direct computer-stack command must not reveal a private hand.");

            ((IInvokeProvider)new ButtonAutomationPeer(humanTrainStack).GetPattern(PatternInterface.Invoke)!).Invoke();
            for (var attempt = 0; attempt < 20 && !solo.ShowSoloTrainCards; attempt++) await Task.Delay(50);
            animatedTable.UpdateLayout();
            var groupedCards = miniTrainCards.Items.Cast<SoloTrainCardRow>().ToArray();
            if (!solo.ShowSoloTrainCards || !IsElementShown(miniPanel) || groupedCards.Sum(row => row.Count) != 4 ||
                groupedCards.Select(row => row.Kind).Distinct().Count() != groupedCards.Length ||
                !IsElementShown(heldCityMarkers) || !IsElementShown(heldDestinationLines) ||
                heldDestinationLines.Items.Count != 2 ||
                solo.PrivateSeat is not null || solo.Screen != Screen.Table)
                throw new InvalidOperationException($"The T stack must show one card per color accounting for all four held cards while the board remains visible. " +
                    $"Selected={solo.ShowSoloTrainCards}, panel={IsElementShown(miniPanel)}, " +
                    $"cards={miniTrainCards.Items.Count}, private={solo.PrivateSeat is not null}, screen={solo.Screen}, " +
                    $"enabled={humanTrainStack.IsEnabled}, opening={solo.ShowSoloOpeningTicketsOnBoard}, " +
                    $"reveal={solo.CanRevealPrivateSeat}.");
            solo.ShowDestinationsWhenViewingTrainCards = false;
            animatedTable.UpdateLayout();
            if (IsElementShown(heldCityMarkers) || IsElementShown(heldDestinationLines))
                throw new InvalidOperationException("Disabling the train-card destination setting must hide the map overlay.");
            solo.ShowDestinationsWhenViewingTrainCards = true;
            animatedTable.UpdateLayout();
            if (!IsElementShown(heldCityMarkers) || !IsElementShown(heldDestinationLines))
                throw new InvalidOperationException("Re-enabling the train-card destination setting must restore the map overlay.");
            var trainScroller = Descendants<ScrollViewer>(miniPanel).Single(IsElementShown);
            if (trainScroller.ScrollableHeight != 0 ||
                trainScroller.HorizontalScrollBarVisibility != ScrollBarVisibility.Auto ||
                (trainScroller.ScrollableWidth > 0 && !IsGameHorizontalScroller(trainScroller)))
                throw new InvalidOperationException("The solo train-card tray must scroll horizontally with the game-styled scrollbar.");
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent,
            };
            trainScroller.RaiseEvent(wheel);
            animatedTable.UpdateLayout();
            if (trainScroller.ScrollableWidth > 0 && (!wheel.Handled || trainScroller.HorizontalOffset <= 0))
                throw new InvalidOperationException("The mouse wheel must move the train-card tray sideways.");
            await RenderSizes("solo-train-cards-horizontal-synthetic",
                () => new GameTableView { DataContext = solo }, view =>
                {
                    var tray = (Border)view.FindName("SoloCardPanel");
                    var scroller = Descendants<ScrollViewer>(tray).Single(IsElementShown);
                    if (scroller.ScrollableHeight != 0 ||
                        scroller.HorizontalScrollBarVisibility != ScrollBarVisibility.Auto ||
                        (scroller.ScrollableWidth > 0 && !IsGameHorizontalScroller(scroller)))
                        throw new InvalidOperationException("The solo train-card tray must remain horizontally scrollable at both window sizes.");
                });

            ((IInvokeProvider)new ButtonAutomationPeer(humanDestinationsStack).GetPattern(PatternInterface.Invoke)!).Invoke();
            for (var attempt = 0; attempt < 20 && !solo.ShowSoloDestinations; attempt++) await Task.Delay(50);
            animatedTable.UpdateLayout();
            if (!solo.ShowSoloDestinations || miniDestinations.Items.Count != 2 ||
                !IsElementShown(heldCityMarkers) || heldCityMarkers.Items.Count != solo.SoloDestinationMarkers.Count ||
                !IsElementShown(heldDestinationLines) || heldDestinationLines.Items.Count != miniDestinations.Items.Count ||
                heldCityMarkers.Items.Count == 0 ||
                solo.SoloDestinationCards.Any(destination => destination.Description == dropped.Description) ||
                solo.PrivateSeat is not null || solo.Screen != Screen.Table)
                throw new InvalidOperationException("The D stack must show two retained mini destinations without reopening the private screen.");
            var destinationScroller = Descendants<ScrollViewer>(miniPanel).Single(IsElementShown);
            if (destinationScroller.ScrollableHeight != 0 ||
                destinationScroller.HorizontalScrollBarVisibility != ScrollBarVisibility.Auto ||
                (destinationScroller.ScrollableWidth > 0 && !IsGameHorizontalScroller(destinationScroller)))
                throw new InvalidOperationException("The solo destination tray must fit two narrow cards or use the horizontal game-styled scrollbar when needed.");
            await RenderSizes("solo-destinations-highlight-synthetic",
                () => new GameTableView { DataContext = solo }, view =>
                {
                    var markers = (ItemsControl)view.FindName("DestinationCityMarkers");
                    var lines = (ItemsControl)view.FindName("DestinationLines");
                    if (!IsShown(markers, view) || markers.Items.Count != solo.SoloDestinationMarkers.Count ||
                        !IsShown(lines, view) || lines.Items.Count != solo.SoloDestinationCards.Count)
                        throw new InvalidOperationException("Opening the solo D stack must show its held city rings and connections on the board.");
                });
            ((IInvokeProvider)new ButtonAutomationPeer(humanDestinationsStack).GetPattern(PatternInterface.Invoke)!).Invoke();
            for (var attempt = 0; attempt < 20 && solo.ShowSoloCardPanel; attempt++) await Task.Delay(50);
            if (solo.ShowSoloCardPanel || IsElementShown(miniPanel) || IsElementShown(heldCityMarkers) ||
                IsElementShown(heldDestinationLines))
                throw new InvalidOperationException("Clicking an open solo stack must collapse its mini-card panel.");
            checks.Add("Confirmed drop clears its card and track, centers the draw panels, and T/D stacks toggle mini cards over the visible table.");

            keepAll.Setup.ManualVerificationAccepted = true;
            await keepAll.StartMatchCommand.ExecuteAsync(null);
            var keepAllView = new PrivateSeatView { DataContext = keepAll };
            await Arrange(keepAllView, 1000, 620);
            ((Button)keepAllView.FindName("SoloKeepAllButton"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            for (var attempt = 0; attempt < 20 && keepAll.ShowSoloOpeningTicketsOnBoard; attempt++)
                await Task.Delay(50);
            if (keepAll.Table.Seats.Single(seat => seat.Operator == "human").TicketCount != 3 ||
                keepAll.ShowSoloOpeningTicketsOnBoard || keepAll.PrivateSeat is not null)
                throw new InvalidOperationException("Keep all three must commit every opening destination and return to the board.");
            checks.Add("Keep all three commits the full opening offer and returns to the board.");

            keepAll.Camera.GameTablePreview = solo.Camera.GameTablePreview;
            keepAll.Camera.GameTablePreviewStatus = "";
            var drawTable = new GameTableView { DataContext = keepAll };
            await Arrange(drawTable, 1280, 800);
            var trainPileButton = Descendants<Button>(drawTable).Single(button =>
                AutomationProperties.GetName(button) == "Draw a train card from the pile");
            var destinationPileButton = Descendants<Button>(drawTable).Single(button =>
                AutomationProperties.GetName(button) == "Draw destination tickets");
            var drawPiles = (Border)drawTable.FindName("DrawPilesPanel");
            var trainPileFrame = Descendants<Border>(trainPileButton).Single();
            var destinationPileFrame = Descendants<Border>(destinationPileButton).Single();
            var trainLabel = Descendants<TextBlock>(drawPiles).Single(label => label.Text == "TRAIN");
            var destinationLabel = Descendants<TextBlock>(drawPiles)
                .Single(label => label.Text == "DESTINATIONS");
            var trainButtonBottom = trainPileButton.TransformToAncestor(drawPiles)
                .TransformBounds(new Rect(trainPileButton.RenderSize)).Bottom;
            var destinationButtonBottom = destinationPileButton.TransformToAncestor(drawPiles)
                .TransformBounds(new Rect(destinationPileButton.RenderSize)).Bottom;
            var trainLabelTop = trainLabel.TransformToAncestor(drawPiles)
                .TransformBounds(new Rect(trainLabel.RenderSize)).Top;
            var destinationLabelTop = destinationLabel.TransformToAncestor(drawPiles)
                .TransformBounds(new Rect(destinationLabel.RenderSize)).Top;
            var faceUpButtons = Descendants<Button>(drawTable).Where(button =>
                AutomationProperties.GetName(button).StartsWith("Draw face-up ", StringComparison.Ordinal)).ToArray();
            if (!IsElementShown(trainPileButton) || !trainPileButton.IsEnabled ||
                !IsElementShown(destinationPileButton) || !destinationPileButton.IsEnabled ||
                trainPileButton.RenderSize != new Size(50, 54) ||
                destinationPileButton.RenderSize != new Size(50, 54) ||
                trainPileFrame.RenderSize != trainPileButton.RenderSize ||
                destinationPileFrame.RenderSize != destinationPileButton.RenderSize ||
                trainButtonBottom > trainLabelTop + .5 ||
                destinationButtonBottom > destinationLabelTop + .5 ||
                faceUpButtons.Length != 5 || faceUpButtons.All(button => !button.IsEnabled) ||
                faceUpButtons.Any(button => button.CommandParameter is not MarketSlotRow))
                throw new InvalidOperationException("The solo player's train pile, destination pile, and face-up market must expose legal draw buttons on the table.");
            ((IInvokeProvider)new ButtonAutomationPeer(destinationPileButton).GetPattern(PatternInterface.Invoke)!).Invoke();
            for (var attempt = 0; attempt < 30 && !keepAll.ShowSoloTicketOffer; attempt++) await Task.Delay(50);
            if (!keepAll.ShowSoloTicketOffer || keepAll.PrivateSeat is not null || keepAll.Screen != Screen.Table)
                throw new InvalidOperationException("Drawing destinations must open the choice on the public table.");
            await RenderSizes("solo-destination-draw-synthetic", () => new GameTableView { DataContext = keepAll }, view =>
            {
                var board = (Border)view.FindName("GameBoardFrame");
                var offer = (Border)view.FindName("SoloTicketOfferPanel");
                var piles = (Border)view.FindName("DrawPilesPanel");
                var market = (Border)view.FindName("FaceUpMarketPanel");
                var circles = (ItemsControl)view.FindName("DestinationCityMarkers");
                var lines = (ItemsControl)view.FindName("DestinationLines");
                var ticketChoices = Descendants<CheckBox>(offer).ToArray();
                var keepButton = Descendants<Button>(offer).Single(button => button.Content as string == "KEEP SELECTED");
                if (!IsShown(offer, view) || Canvas.GetTop(board) != 120 ||
                    Canvas.GetTop(board) + board.Height > Canvas.GetTop(offer) ||
                    !IsShown(circles, view) || circles.Items.Count == 0 ||
                    !IsShown(lines, view) || lines.Items.Count != keepAll.SoloTicketOffer.Count ||
                    ticketChoices.Length != keepAll.SoloTicketOffer.Count ||
                    !keepButton.IsEnabled || IsShown(piles, view) || IsShown(market, view))
                    throw new InvalidOperationException($"The in-game destination choice must fit below the board, show its routes, and hide the draw panels. " +
                        $"offer={IsShown(offer, view)}, boardTop={Canvas.GetTop(board)}, boardHeight={board.Height}, " +
                        $"offerTop={Canvas.GetTop(offer)}, circles={IsShown(circles, view)}/{circles.Items.Count}, " +
                        $"lines={IsShown(lines, view)}/{lines.Items.Count}/{keepAll.SoloTicketOffer.Count}, " +
                        $"choices={ticketChoices.Length}, keep={keepButton.IsEnabled}, piles={IsShown(piles, view)}, market={IsShown(market, view)}.");
            }, [(1280, 800), (1000, 620)]);
            checks.Add("Solo draw piles and market are actionable; drawing destinations opens a compact choice below the board.");

            if (solo.IsPrivateVisible || solo.PrivateSeat is not null)
                throw new InvalidOperationException("The solo table must remain visible after card-stack inspection.");
            await RenderSizes("solo-table-synthetic", () => new TableView { DataContext = solo }, view =>
            {
                var labels = VisibleButtons(view);
                if (!labels.Contains("Your cards") || labels.Contains("Reveal my private view") ||
                    VisibleText(view).Contains("Pass the laptop", StringComparison.Ordinal) ||
                    VisibleText(view).Contains("Shift+Esc", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The solo table must offer Your cards without a laptop handoff prompt.");
            });
            checks.Add("The solo table remains public while card stacks expand in place.");

            await solo.RevealPrivateSeatAsync();
            await solo.DrawBlindCardAsync();
            await solo.RevealPrivateSeatAsync();
            await solo.DrawBlindCardAsync();
            animatedTable.UpdateLayout();
            var computerTurnTrainStack = Descendants<Button>(animatedTable).Single(button =>
                AutomationProperties.GetName(button) == "Show your train cards" &&
                button.DataContext is GameTableSeat tile && tile.Seat.Operator == "human");
            var computerTurnDestinationStack = Descendants<Button>(animatedTable).Single(button =>
                AutomationProperties.GetName(button) == "Show your destinations" &&
                button.DataContext is GameTableSeat tile && tile.Seat.Operator == "human");
            if (solo.IsSoloHumanTurn || computerTurnTrainStack.IsHitTestVisible || computerTurnTrainStack.Focusable ||
                computerTurnDestinationStack.IsHitTestVisible || computerTurnDestinationStack.Focusable ||
                IsElementShown(miniPanel) ||
                IsElementShown(heldCityMarkers))
                throw new InvalidOperationException("The human's card stacks and preview must be unavailable during the computer's turn.");
            await solo.ToggleSoloDestinationsCommand.ExecuteAsync((GameTableSeat)computerTurnDestinationStack.DataContext);
            if (solo.ShowSoloCardPanel || solo.ShowDestinationMarkersOnBoard)
                throw new InvalidOperationException("Direct card commands must not reveal the human's hand during the computer's turn.");
            checks.Add("Solo card stacks and destination rings stay closed during computer turns.");

            shared.Setup.ManualVerificationAccepted = true;
            shared.Setup.Seats[0].DisplayName = "First human test player";
            shared.Setup.Seats[1].DisplayName = "Second human test player";
            shared.Setup.Seats[1].IsComputer = false;
            if (shared.IsSingleHumanGame || !shared.CanConnectPhone || !shared.ShowConnectionCommand.CanExecute(null))
                throw new InvalidOperationException("Multiple humans must retain the technical Connect phone command.");
            await RenderSizes("shared-setup-synthetic", () => new SetupView { DataContext = shared }, view =>
            {
                var text = VisibleText(view);
                if (!text.Contains("set up one shared phone from the QR over the table", StringComparison.Ordinal) ||
                    !text.Contains("Phone card handoff is planned for a later update", StringComparison.Ordinal) ||
                    text.Contains("No phone connection is needed.", StringComparison.Ordinal))
                    throw new InvalidOperationException("Multiple-human setup must explain shared-phone QR setup and the unfinished phone handoff.");
            });
            checks.Add("Multiple-human setup explains shared-phone QR setup and the forthcoming phone handoff.");

            await shared.StartMatchCommand.ExecuteAsync(null);
            if (!shared.ShowMultiHumanPhoneSetup || shared.Screen != Screen.Table)
                throw new InvalidOperationException("A multiple-human match must show shared-phone setup over the game table.");
            if (shared.IsPrivateVisible || shared.PrivateSeat is not null)
                throw new InvalidOperationException("A multiple-human match must start covered until explicit reveal.");
            await RenderSizes("shared-phone-setup-synthetic", () => new GameScreenView { DataContext = shared }, view =>
            {
                var panel = (Border)view.FindName("MultiHumanPhoneSetupPanel");
                var table = Descendants<GameTableView>(view).Single();
                var qr = Descendants<Image>(panel).Single(image =>
                    AutomationProperties.GetName(image) == "Shared phone installation QR code");
                var start = Descendants<Button>(panel).Single(button => button.Content as string == "Start hosting");
                if (!IsElementShown(panel) || !IsElementShown(table) ||
                    !VisibleText(panel).Contains("pass it to the active player", StringComparison.Ordinal) ||
                    qr.Source is not null || start.Command is null ||
                    !ReferenceEquals(start.Command, shared.Connection.StartCommand))
                    throw new InvalidOperationException("Multiple-human setup must cover the table, guide one shared phone, and bind the real local-host QR flow.");
            });
            shared.DismissMultiHumanPhoneSetupCommand.Execute(null);
            if (shared.ShowMultiHumanPhoneSetup)
                throw new InvalidOperationException("Continuing from phone setup must reveal the table.");
            shared.OpenMultiHumanPhoneSetupCommand.Execute(null);
            if (!shared.ShowMultiHumanPhoneSetup)
                throw new InvalidOperationException("The phone setup must be reopenable from the table.");
            shared.DismissMultiHumanPhoneSetupCommand.Execute(null);
            checks.Add("Multiple-human table presents a shared-phone QR setup, without starting a listener during UI smoke.");
            await RenderSizes("shared-table-covered-synthetic", () => new TableView { DataContext = shared }, view =>
            {
                var labels = VisibleButtons(view);
                if (!labels.Contains("Reveal my private view") || labels.Contains("Your cards") ||
                    !VisibleText(view).Contains("Pass the laptop to First human test player", StringComparison.Ordinal))
                    throw new InvalidOperationException("The multiple-human table must retain explicit private reveal and the current handoff prompt.");
            });
            checks.Add("Multiple-human match startup stays covered and retains the explicit reveal handoff.");

            await shared.RevealPrivateSeatCommand.ExecuteAsync(null);
            RequireHumanPrivateView(shared, "First human test player", mustChooseTickets: true);
            if (shared.ShowDestinationMarkersOnBoard || shared.BoardDestinationMarkers.Count != 0)
                throw new InvalidOperationException("Multiple-human private destinations must not mark the public board.");
            await RenderSizes("shared-first-private-synthetic", () => new PrivateSeatView { DataContext = shared },
                view => VerifyPrivateLabels(view, shared, "First human test player", singleHuman: false));
            await shared.CommitTicketsCommand.ExecuteAsync(null);
            if (shared.IsPrivateVisible || shared.PrivateSeat is not null)
                throw new InvalidOperationException("Moving to another human's opening tickets must discard the prior private view.");
            await shared.RevealPrivateSeatCommand.ExecuteAsync(null);
            RequireHumanPrivateView(shared, "Second human test player", mustChooseTickets: true);
            await RenderSizes("shared-second-private-synthetic", () => new PrivateSeatView { DataContext = shared },
                view => VerifyPrivateLabels(view, shared, "Second human test player", singleHuman: false));
            checks.Add("Each shared private view uses the explicitly revealed human's hand and ticket sources, with the prior view discarded at handoff.");

            await File.WriteAllTextAsync(Path.Combine(Output, "human-presentation-interactions.json"), JsonSerializer.Serialize(new
            {
                Fixture = "Synthetic in-memory matches on an isolated WPF dispatcher; no visible window, user save, network listener or camera.",
                Checks = checks,
                RenderSizes = new[] { "1280x800", "1000x620" }
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Human presentation: {checks.Count} synthetic flow checks passed.");
        }
        finally
        {
            await solo.DisposeToolsAsync();
            await keepAll.DisposeToolsAsync();
            await shared.DisposeToolsAsync();
        }
    }

    private static async Task VerifyCheckpointPhotoPresentation()
    {
        var model = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
        var checks = new List<string>();
        try
        {
            // Explicit visual fixtures only: no checkpoint is read or written, and no camera or
            // capture command is invoked. Persistence and capture guards have separate tests.
            var photo = model.CheckpointPhoto;
            photo.CheckpointName = "Synthetic empty-board checkpoint";
            photo.HasCheckpoint = true;
            photo.CaptureAllowed = true;
            photo.Status = "Synthetic photo-presentation fixture. No saved game or camera is opened.";
            model.Table.RebuildHeadline = "Synthetic empty-board checkpoint: 0 routes and 0 trains on the board.";
            model.Table.RebuildStock.Add(new RebuildStockRow("Synthetic player", PlayerColor.Blue, "◆", 0, 45));
            await RenderSizes("rebuild-empty-no-photo-synthetic", () => new RebuildView { DataContext = model }, view =>
            {
                if (!IsShown((FrameworkElement)view.FindName("MissingPhotoPanel"), view) ||
                    IsShown((FrameworkElement)view.FindName("SavedPhotoPanel"), view) ||
                    !IsShown((FrameworkElement)view.FindName("EmptySavedPosition"), view) ||
                    !VisibleText(view).Contains(photo.PhotoStateSummary, StringComparison.Ordinal) ||
                    !VisibleText(view).Contains("All route lanes should be empty.", StringComparison.Ordinal))
                    throw new InvalidOperationException("An empty checkpoint without a photo must explain both the absent image and the zero-route rebuild target.");
            });
            checks.Add("Zero-route rebuild clearly identifies the absent reference photo and says every route lane should be empty.");

            await RenderSizes("checkpoint-photo-missing-synthetic", () => new CheckpointPhotoView { DataContext = photo }, view =>
            {
                if (!VisibleText(view).Contains(photo.PhotoStateSummary, StringComparison.Ordinal) ||
                    !VisibleText(view).Contains(photo.CaptureGuidance, StringComparison.Ordinal) ||
                    Descendants<Image>(view).Any(image => IsShown(image, view) && image.Source is not null))
                    throw new InvalidOperationException("A missing-photo page must provide explicit photo state and camera setup guidance, with no stale image.");
            });
            checks.Add("Missing-photo page explains the missing image and camera setup instead of presenting an unexplained blank area.");

            var live = SyntheticCropFixture("SYNTHETIC LIVE CROP\nNOT SAVED");
            model.Camera.BoardPreview = live;
            model.Camera.IsRunning = true;
            model.Camera.HasBoardCrop = true;
            model.Camera.SafetyHeld = false;
            await RenderSizes("checkpoint-photo-live-crop-synthetic", () => new CheckpointPhotoView { DataContext = photo }, view =>
            {
                if (!photo.HasLivePreview || photo.HasPhoto || photo.PhotoImage is not null ||
                    !Descendants<Image>(view).Any(image => IsShown(image, view) && ReferenceEquals(image.Source, live)) ||
                    !VisibleText(view).Contains(photo.PhotoStateSummary, StringComparison.Ordinal) ||
                    !VisibleText(view).Contains("not saved", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The live crop must be visible and identified as not saved while the checkpoint has no reference photo.");
                var capture = Descendants<Button>(view).Single(button => ReferenceEquals(button.Command, photo.CaptureReferenceCommand));
                if (capture.IsEnabled)
                    throw new InvalidOperationException("A live crop alone must not enable capture before the operator confirmation.");
            });
            checks.Add("Live crop appears on the photo page, is labeled not saved, and does not bypass the operator confirmation.");

            var saved = SyntheticCropFixture("SYNTHETIC SAVED PHOTO\nLAYOUT FIXTURE ONLY");
            photo.PhotoImage = saved;
            photo.HasPhoto = true;
            photo.CaptureDetails = "Synthetic saved-image presentation; no photo was persisted by this diagnostic.";
            await RenderSizes("rebuild-saved-photo-synthetic", () => new RebuildView { DataContext = model }, view =>
            {
                var displayed = (Image)view.FindName("SavedBoardPhoto");
                if (!IsShown(displayed, view) || !ReferenceEquals(displayed.Source, saved) ||
                    IsShown((FrameworkElement)view.FindName("MissingPhotoPanel"), view) ||
                    !IsShown((FrameworkElement)view.FindName("EmptySavedPosition"), view) ||
                    !VisibleText(view).Contains("Saved board reference photo", StringComparison.Ordinal) ||
                    Descendants<Image>(view).Any(image => IsShown(image, view) && ReferenceEquals(image.Source, live)))
                    throw new InvalidOperationException("Rebuild must show the checkpoint's saved photo inline, retain the zero-route guidance, and exclude the live camera crop.");
            });
            checks.Add("Rebuild displays the exact checkpoint PhotoImage inline, retaining the saved route target and excluding the different live crop.");

            await RenderSizes("checkpoint-photo-saved-synthetic", () => new CheckpointPhotoView { DataContext = photo }, view =>
            {
                if (!Descendants<Image>(view).Any(image => IsShown(image, view) && ReferenceEquals(image.Source, saved)) ||
                    Descendants<Image>(view).Any(image => IsShown(image, view) && ReferenceEquals(image.Source, live)) ||
                    !VisibleText(view).Contains(photo.PhotoStateSummary, StringComparison.Ordinal))
                    throw new InvalidOperationException("A saved-photo page must display the saved reference and hide the different live crop.");
            });
            checks.Add("Saved-photo page displays the checkpoint reference instead of a currently available but different live crop.");

            await File.WriteAllTextAsync(Path.Combine(Output, "checkpoint-photo-presentation.json"), JsonSerializer.Serialize(new
            {
                Fixture = "Explicitly seeded synthetic visual states with labeled images; no storage, camera, OS input, or capture commands are used. Separate tests cover persistence and capture validation.",
                Checks = checks,
                RenderSizes = new[] { "1280x800", "1000x620" }
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Checkpoint photo presentation: {checks.Count} synthetic flow checks passed.");
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static void RequireHumanPrivateView(MainViewModel model, string humanName, bool mustChooseTickets)
    {
        var seat = model.Table.Seats.Single(row => row.DisplayName == humanName && row.Operator == "human");
        if (!model.IsPrivateVisible || model.PrivateSeat is not { } privateSeat ||
            privateSeat.SeatId != seat.SeatId || privateSeat.SeatName != humanName ||
            privateSeat.MustChooseTickets != mustChooseTickets || privateSeat.Hand.Sum(row => row.Count) != seat.CardCount)
            throw new InvalidOperationException($"The visible private view must belong only to the expected human: {humanName}.");
    }

    private static void VerifyPrivateLabels(UserControl view, MainViewModel model, string humanName, bool singleHuman)
    {
        var labels = VisibleButtons(view);
        var text = VisibleText(view);
        var privateSeat = model.PrivateSeat ?? throw new InvalidOperationException("The expected private view is missing.");
        if (singleHuman && privateSeat.IsSetupOffer)
        {
            var overlay = (Grid)view.FindName("SoloOpeningOverlay");
            if (!IsShown(overlay, view) || !labels.Contains("KEEP ALL THREE") ||
                labels.Contains("Back to table") || !text.Contains("Your Cards", StringComparison.Ordinal) ||
                Descendants<Button>(view).Count(button => IsShown(button, view) && button.DataContext is TicketChoiceRow) != 3 ||
                Descendants<ItemsControl>(view).Any(control => IsShown(control, view) &&
                    (ReferenceEquals(control.ItemsSource, privateSeat.Hand) ||
                     ReferenceEquals(control.ItemsSource, privateSeat.Tickets))))
                throw new InvalidOperationException("A solo opening offer must show only its three private destinations over the board.");
        }
        else if (singleHuman
            ? !labels.Contains("Back to table") || labels.Contains("Hide (pass the laptop on)") || !text.Contains("Your cards", StringComparison.Ordinal)
            : !labels.Contains("Hide (pass the laptop on)") || labels.Contains("Back to table") || !text.Contains(humanName + " - private view", StringComparison.Ordinal))
            throw new InvalidOperationException("The private-view title and return action must match the human count.");
        var privateContexts = Descendants<FrameworkElement>(view).Select(element => element.DataContext)
            .OfType<PrivateSeatViewModel>().Distinct().ToArray();
        if (privateContexts.Length != 1 || !ReferenceEquals(privateContexts[0], privateSeat))
            throw new InvalidOperationException("A private visual tree must bind to exactly the one revealed human seat.");
        foreach (var source in new System.Collections.IEnumerable[] { privateSeat.Hand, privateSeat.Tickets, privateSeat.Offer })
            if (!Descendants<ItemsControl>(view).Any(control => ReferenceEquals(control.ItemsSource, source)))
                throw new InvalidOperationException("Private hand and destination controls must use the revealed seat's own collections.");
    }

    private static string[] VisibleButtons(DependencyObject root) => Descendants<Button>(root)
        .Where(button => IsShown(button, root)).Select(Label).ToArray();

    private static string VisibleText(DependencyObject root) => string.Join("\n", Descendants<TextBlock>(root)
        .Where(block => IsShown(block, root)).Select(block => block.Text));

    private static bool IsShown(DependencyObject element, DependencyObject root)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
            if (ReferenceEquals(current, root)) return true;
        }
        return false;
    }

    private static bool IsGameHorizontalScroller(ScrollViewer viewer)
    {
        var bar = Descendants<ScrollBar>(viewer).SingleOrDefault(scrollBar =>
            scrollBar.Orientation == Orientation.Horizontal && IsElementShown(scrollBar));
        return viewer.ScrollableWidth > 0 && viewer.ScrollableHeight == 0 &&
               viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled &&
               bar?.Background is SolidColorBrush { Color: { R: 0x26, G: 0x34, B: 0x41 } } &&
               bar.Template.FindName("PART_Track", bar) is Track { Thumb: not null };
    }

    private static async Task VerifyProcessingPresentation()
    {
        await using var camera = new CameraViewModel();
        if (camera.SelectedPreference.Value != CameraCapturePreference.Balanced1080p ||
            !camera.Preferences.Any(option => option.Value == CameraCapturePreference.HighDetail2160p) ||
            camera.SelectedProcessor.Value != FrameComputeMode.Auto)
            throw new InvalidOperationException("Camera defaults must request the best native 1080p mode, retain the 4K option, and use automatic hardware processing.");
        camera.Preview = SyntheticCropFixture();
        camera.IsRunning = true;
        camera.FormatText = "Camera delivered 1920 × 1080 · synthetic presentation fixture";
        camera.ProcessingText = "Processing 3840 × 2160 · upscaled from 1920 × 1080; not native 4K";
        camera.ComputeBadge = "▣ CPU";
        camera.ComputeStatus = "▣ CPU · synthetic status fixture";
        camera.DetectionText = "One train candidate and one player marker. Synthetic overlay geometry; no camera opened.";
        PreviewPieceOutline[] syntheticOutlines =
        [
            new(false, [new(.2,.25), new(.33,.31), new(.31,.35), new(.18,.29)]),
            new(true, [new(.6,.6), new(.65,.6), new(.65,.67), new(.6,.67)])
        ];
        await RenderSizes("camera-processing-outlines-synthetic", () =>
        {
            camera.PieceOutlines = syntheticOutlines;
            return new CameraView { DataContext = camera };
        }, view =>
        {
            var overlay = (Canvas)view.FindName("DetectionOverlay");
            var whites = overlay.Children.OfType<System.Windows.Shapes.Polygon>()
                .Where(shape => shape.Stroke == Brushes.White).ToArray();
            if (whites.Length != 2 || overlay.IsHitTestVisible)
                throw new InvalidOperationException("The preview must show two white candidate outlines that cannot intercept corner editing.");
            var marker = whites[1].Points;
            if (Math.Abs((marker[1].X - marker[0].X) - (marker[2].Y - marker[1].Y)) > .01)
                throw new InvalidOperationException("The player-marker outline must remain square after image scaling and letterboxing.");
            camera.ShowPieceOutlines = false;
            if (overlay.Children.Count != 0) throw new InvalidOperationException("The outline toggle must hide all candidate geometry.");
            camera.ShowPieceOutlines = true;
            if (overlay.Children.Count != 0) throw new InvalidOperationException("Re-enabling ML outlines must wait for a fresh result instead of restoring old detections.");
        });
        camera.ClearPieceReferenceCommand.Execute(null);
        if (camera.PieceOutlines.Count != 0 || camera.HasPieceReference)
            throw new InvalidOperationException("Clearing the piece reference must immediately remove all outlines.");
        Console.WriteLine("Processing presentation:1080p/Auto defaults with a 4K option, white train/player geometry, square marker, toggle and reference clearing passed.");
    }

    private static async Task VerifyKeyboardCornerHandler()
    {
        await using var camera = new CameraViewModel();
        var view = new CameraView { DataContext = camera };
        var image = (Image)view.FindName("PreviewImage");
        camera.Preview = SyntheticCropFixture();
        camera.IsRunning = true;
        camera.Status = "Synthetic crop editing fixture. No camera is opened or connected by this diagnostic.";
        camera.FormatText = "Synthetic 640 × 360 image · no camera input";
        camera.SelectingCorners = true;
        await Arrange(view, 1000, 620);
        var source = new FixturePresentationSource { RootVisual = view };
        void KeyPress(Key key) => image.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            { RoutedEvent = Keyboard.KeyDownEvent });
        var checks = new List<string>();
        var overlay = (Canvas)view.FindName("CornerOverlay");
        int PlacementCueLines() => overlay.Children.OfType<System.Windows.Shapes.Line>().Count();
        if (PlacementCueLines() != 0)
            throw new InvalidOperationException("Starting corner selection must not put a white plus on the preview before pointer input.");
        KeyPress(Key.Right);
        if (PlacementCueLines() != 2)
            throw new InvalidOperationException("Explicit keyboard positioning needs a visible placement cue.");
        KeyPress(Key.Enter);
        if (camera.SelectedCorners.Count != 1 || camera.SelectedCorners[0].X <= .05 || camera.SelectedCorners[0].Y != .05)
            throw new InvalidOperationException("The camera keyboard corner handler did not move and place its first corner.");
        checks.Add("Arrow and Enter move the crosshair and place the first corner.");

        image.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseMoveEvent });
        if (PlacementCueLines() != 0 || overlay.Children.OfType<Border>().Count() != 1)
            throw new InvalidOperationException("Returning to the mouse must hide the keyboard plus and preserve the placed corner marker.");
        checks.Add("Pointer selection has no speculative plus; keyboard input shows its cue, and mouse movement hides it without removing placed corners.");

        var firstCorner = camera.SelectedCorners[0];
        KeyPress(Key.D1);
        KeyPress(Key.Right);
        if (camera.SelectedCorners.Count != 1 || camera.SelectedCorners[0].X <= firstCorner.X ||
            camera.SelectedCorners[0].Y != firstCorner.Y)
            throw new InvalidOperationException("Selecting corner 1 and pressing Right must edit that corner during initial selection without adding another.");
        checks.Add("Number 1 and Right adjust an existing corner during initial selection without adding one.");

        KeyPress(Key.Enter);
        if (camera.SelectedCorners.Count != 1)
            throw new InvalidOperationException("Enter while editing an existing corner must return to placement without adding a corner.");
        KeyPress(Key.Enter);
        if (camera.SelectedCorners.Count != 2 || camera.SelectedCorners[1] != new NormalizedPoint(.95, .05))
            throw new InvalidOperationException("Returning to placement must preserve the expected next-corner crosshair.");
        checks.Add("Enter leaves corner editing, and the following Enter places the next corner.");

        camera.AddBoardCorner(new(.95, .95));
        if (PlacementCueLines() != 0)
            throw new InvalidOperationException("A pointer-placed corner must not draw a plus at the next unplaced corner.");
        camera.AddBoardCorner(new(.05, .95));
        if (camera.SelectedCorners.Count != 4)
            throw new InvalidOperationException("All four corner handles must survive a failed crop attempt so the user can correct them.");
        camera.SelectingCorners = false;
        var completedFirstCorner = camera.SelectedCorners[0];
        KeyPress(Key.NumPad1);
        KeyPress(Key.Right);
        if (camera.SelectedCorners.Count != 4 || camera.SelectedCorners[0].X <= completedFirstCorner.X)
            throw new InvalidOperationException("The keyboard must adjust corner 1 after initial corner selection is complete.");
        if (camera.HasBoardCrop || camera.CanCapturePhoto)
            throw new InvalidOperationException("Editing a synthetic preview without a fresh camera frame must not enable photo capture.");
        checks.Add("Numpad 1 and Right adjust a corner after initial selection; a missing camera keeps photo capture disabled.");

        var fourthCorner = camera.SelectedCorners[3];
        KeyPress(Key.D4);
        KeyPress(Key.Left);
        if (camera.SelectedCorners[3].X >= fourthCorner.X || camera.SelectedCorners.Count != 4)
            throw new InvalidOperationException("Number 4 must select and adjust the fourth corner rather than the previously selected corner.");
        for (var i = 0; i < 50; i++) KeyPress(Key.Left);
        if (camera.SelectedCorners[3].X != 0)
            throw new InvalidOperationException("Keyboard corner adjustment must clamp at the image boundary.");
        checks.Add("Number 4 selects another existing handle, and repeated Left clamps at the image boundary.");

        VerifyCornerPointerMapping(view);
        checks.Add("Pointer mapping ignores letterbox clicks and clamps a drag at the actual image boundary.");
        camera.CropText = "Synthetic editing fixture: all four numbered handles remain adjustable. No live camera frame or valid photo crop is supplied.";
        camera.Problem = null;
        await File.WriteAllTextAsync(Path.Combine(Output, "camera-corner-interactions.json"), JsonSerializer.Serialize(new
        {
            Fixture = "Synthetic camera preview; no camera, operating-system input, or capture device was used.",
            Checks = checks,
            FinalCorners = camera.SelectedCorners.ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        view.DataContext = null;
        await RenderSizes("camera-corners-synthetic", () => new CameraView { DataContext = camera });
        // Seed only presentation state here; the export tests supply owned frames and exercise
        // actual PNG encoding. This fixture never opens a Save dialog or a camera.
        camera.HasBoardCrop = true;
        camera.BoardPreview = SyntheticCropFixture();
        camera.SafetyHeld = true;
        camera.ComparisonText = "The scene changed. Synthetic export-availability fixture.";
        camera.CropText = "Synthetic valid crop for export-button layout testing.";
        await RenderSizes("camera-export-scene-changed-synthetic", () => new CameraView { DataContext = camera }, view =>
        {
            var export = Descendants<Button>(view).Single(button => Label(button) == "Export board photo…");
            if (!export.IsEnabled || !camera.CanExportPhoto || camera.CanCapturePhoto)
                throw new InvalidOperationException("Manual export must stay enabled during a scene hold while checkpoint capture remains held.");
        });
        Console.WriteLine($"Camera corner editing: {checks.Count} synthetic interaction checks passed; no OS input injected.");
    }

    private static void VerifyCornerPointerMapping(CameraView view)
    {
        var map = typeof(CameraView).GetMethod("PointInImage", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("The camera image coordinate mapper was not found.");
        NormalizedPoint? Map(Point position, bool clamp) => (NormalizedPoint?)map.Invoke(view, [position, clamp]);
        var area = (Grid)view.FindName("PreviewArea");
        var image = (Image)view.FindName("PreviewImage");
        var source = image.Source;
        var scale = Math.Min(area.ActualWidth / source.Width, area.ActualHeight / source.Height);
        var width = source.Width * scale;
        var height = source.Height * scale;
        var left = (area.ActualWidth - width) / 2;
        var top = (area.ActualHeight - height) / 2;
        var middle = Map(new Point(area.ActualWidth / 2, area.ActualHeight / 2), false);
        if (middle is not { } point || Math.Abs(point.X - .5) > 1e-8 || Math.Abs(point.Y - .5) > 1e-8)
            throw new InvalidOperationException("The center of the displayed image must map to normalized center coordinates.");
        var outside = left > .1 ? new Point(left / 2, area.ActualHeight / 2) : new Point(area.ActualWidth / 2, top / 2);
        if (left <= .1 && top <= .1)
            throw new InvalidOperationException("The pointer fixture must provide a letterboxed image to check coordinate mapping.");
        if (Map(outside, false) is not null)
            throw new InvalidOperationException("A click in the letterbox must not place a corner.");
        var clamped = Map(outside, true);
        if (clamped is not { } edge || (left > .1 ? edge.X != 0 || Math.Abs(edge.Y - .5) > 1e-8 : edge.Y != 0 || Math.Abs(edge.X - .5) > 1e-8))
            throw new InvalidOperationException("Dragging into the letterbox must clamp to the actual camera-image edge.");
    }

    private static BitmapSource SyntheticCropFixture(string label = "SYNTHETIC CROP FIXTURE\nNO CAMERA INPUT")
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.Bisque, null, new Rect(0, 0, 640, 360));
            for (var row = 0; row < 9; row++)
            for (var column = 0; column < 16; column++)
                if ((row + column) % 2 == 0)
                    drawing.DrawRectangle(Brushes.SlateGray, null, new Rect(column * 40, row * 40, 40, 40));
            drawing.DrawRectangle(Brushes.Black, null, new Rect(45, 145, 550, 65));
            drawing.DrawText(new FormattedText(label, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 19, Brushes.White, 1), new Point(65, 152));
        }
        var bitmap = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
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

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null;
             current = VisualTreeHelper.GetParent(current))
            yield return current;
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
