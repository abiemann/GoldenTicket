using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
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
using GoldenTicket.Domain;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Vision;

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
                await VerifyWindowShutdown();
                await VerifyWindowExitConfirmation();
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

    private static async Task RenderSizes(string name, Func<UserControl> make, Action<UserControl>? verify = null)
    {
        foreach (var (width, height) in new[] { (1280, 800), (1000, 620) })
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

    private static async Task VerifyHumanPresentation()
    {
        var checks = new List<string>();
        var solo = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
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
            RequireHumanPrivateView(solo, "Solo test player", mustChooseTickets: true);
            await RenderSizes("solo-opening-tickets-synthetic", () => new PrivateSeatView { DataContext = solo },
                view => VerifyPrivateLabels(view, solo, "Solo test player", singleHuman: true));
            checks.Add("Starting a solo match automatically presents only that human's opening cards and destination choices.");

            await solo.CommitTicketsCommand.ExecuteAsync(null);
            RequireHumanPrivateView(solo, "Solo test player", mustChooseTickets: false);
            await RenderSizes("solo-turn-cards-synthetic", () => new PrivateSeatView { DataContext = solo },
                view => VerifyPrivateLabels(view, solo, "Solo test player", singleHuman: true));
            checks.Add("Committing solo opening destinations automatically presents the human turn with Your cards and Back to table.");

            solo.HidePrivateSeatCommand.Execute(null);
            if (solo.IsPrivateVisible || solo.PrivateSeat is not null)
                throw new InvalidOperationException("Back to table must discard the solo private view.");
            await RenderSizes("solo-table-synthetic", () => new TableView { DataContext = solo }, view =>
            {
                var labels = VisibleButtons(view);
                if (!labels.Contains("Your cards") || labels.Contains("Reveal my private view") ||
                    VisibleText(view).Contains("Pass the laptop", StringComparison.Ordinal))
                    throw new InvalidOperationException("The solo table must offer Your cards without a laptop handoff prompt.");
            });
            checks.Add("Back to table discards the private view and offers Your cards without handoff wording.");

            shared.Setup.ManualVerificationAccepted = true;
            shared.Setup.Seats[0].DisplayName = "First human test player";
            shared.Setup.Seats[1].DisplayName = "Second human test player";
            shared.Setup.Seats[1].IsComputer = false;
            if (shared.IsSingleHumanGame || !shared.CanConnectPhone || !shared.ShowConnectionCommand.CanExecute(null))
                throw new InvalidOperationException("Multiple humans must retain the optional Connect phone command.");
            await RenderSizes("shared-setup-synthetic", () => new SetupView { DataContext = shared }, view =>
            {
                var text = VisibleText(view);
                if (!text.Contains("Multiple humans can pass", StringComparison.Ordinal) ||
                    text.Contains("No phone connection is needed.", StringComparison.Ordinal))
                    throw new InvalidOperationException("Multiple-human setup must show the choice of laptop handoff or shared companion.");
            });
            checks.Add("Multiple-human setup offers laptop pass-and-hide or an optional companion.");

            await shared.StartMatchCommand.ExecuteAsync(null);
            if (shared.IsPrivateVisible || shared.PrivateSeat is not null)
                throw new InvalidOperationException("A multiple-human match must start covered until explicit reveal.");
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
        if (singleHuman
            ? !labels.Contains("Back to table") || labels.Contains("Hide (pass the laptop on)") || !text.Contains("Your cards", StringComparison.Ordinal)
            : !labels.Contains("Hide (pass the laptop on)") || labels.Contains("Back to table") || !text.Contains(humanName + " - private view", StringComparison.Ordinal))
            throw new InvalidOperationException("The private-view title and return action must match the human count.");
        var privateSeat = model.PrivateSeat ?? throw new InvalidOperationException("The expected private view is missing.");
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
