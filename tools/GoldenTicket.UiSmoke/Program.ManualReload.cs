using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Desktop.Views;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Testing;

internal static partial class Program
{
    private static async Task VerifyManualSavedBoardCheck()
    {
        var model = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore(),
            camera: new CameraViewModel(capture: new FakeCameraCapture()));
        try
        {
            // Exercise production views with an isolated in-memory game. No native window,
            // user save, camera capture, or operating-system input is involved.
            model.Setup.ManualVerificationAccepted = true;
            foreach (var seat in model.Setup.Seats) seat.IsComputer = false;
            await model.StartMatchAsync();
            for (var index = 0; index < model.Setup.Seats.Count; index++)
            {
                await model.RevealPrivateSeatAsync();
                await model.CommitTicketsAsync();
            }
            model.HidePrivateSeat();
            model.Camera.GameTablePreview = GameExitBoardFixture([]);
            model.Camera.IsGameTablePreviewUpright = true;
            model.Camera.GameTablePreviewStatus = "";

            await RenderSizes("game-manual-reload-absent", () => new GameTableView { DataContext = model }, view =>
            {
                var panel = (Border)view.FindName("ManualSavedBoardCheckPanel");
                if (model.ShowManualSavedBoardCheck || IsElementShown(panel))
                    throw new InvalidOperationException("Ordinary gameplay must not offer the saved-board recovery action.");
            });

            model.Table.SaveName = "Synthetic manual reload";
            await model.SaveAndPackAwayAsync();
            model.IsCheckingResumedGame = true;
            typeof(MainViewModel).GetMethod("StartSavedBoardRestore", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(model, null);
            typeof(GameScreenViewModel).GetMethod("ShowGuidance", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(model.Game, ["Turn 10", "Checking...",
                    "The camera cannot distinguish the BLUE scoring marker. Check its position on 5."]);

            await RenderSizes("game-manual-reload-available", () => new GameTableView { DataContext = model }, view =>
            {
                var panel = (Border)view.FindName("ManualSavedBoardCheckPanel");
                var guidance = (Border)view.FindName("HumanGuidancePanel");
                var button = Descendants<Button>(panel).Single();
                if (!IsElementShown(panel) || !button.IsEnabled ||
                    !ReferenceEquals(button.Command, model.CheckSavedBoardMyselfCommand) ||
                    Label(button) != "Check board myself")
                    throw new InvalidOperationException("An eligible reload needs its visible, enabled, bound manual-check action.");
                var panelBounds = ReloadBounds(panel, view);
                var guidanceBounds = ReloadBounds(guidance, view);
                if (!ReloadContains(new Rect(view.RenderSize), panelBounds) ||
                    panelBounds.IntersectsWith(guidanceBounds) ||
                    !ReloadContains(panelBounds, ReloadBounds(button, view)))
                    throw new InvalidOperationException("The manual reload panel must fit beside the central guidance without overlap.");
                foreach (var text in Descendants<TextBlock>(panel))
                    if (!ReloadContains(panelBounds, ReloadBounds(text, view)))
                        throw new InvalidOperationException("Manual reload panel text extends outside its card.");
            });

            model.Busy = "Synthetic busy operation";
            await RenderSizes("game-manual-reload-busy", () => new GameTableView { DataContext = model }, view =>
            {
                var panel = (Border)view.FindName("ManualSavedBoardCheckPanel");
                if (!IsElementShown(panel) || Descendants<Button>(panel).Single().IsEnabled)
                    throw new InvalidOperationException("A busy reload must disable its manual check button.");
            }, [(1000, 620)]);
            model.Busy = null;

            await model.CheckSavedBoardMyselfAsync();
            if (model.GameplayScreen != Screen.Rebuild || model.Table.RebuildAttested)
                throw new InvalidOperationException("The recovery action must enter inspection without attesting to the board.");
            model.CheckpointPhoto.PhotoImage = GameExitBoardFixture([]);
            foreach (var (width, height) in new[] { (1280, 800), (1000, 620) })
            {
                BindingLog.Context = $"manual-reload-inspection-{width}x{height}";
                var view = new RebuildView { DataContext = model };
                var root = new Border { Width = width, Height = height, Child = view,
                    Background = (Brush)System.Windows.Application.Current.Resources["Surface.Window"] };
                TextElement.SetForeground(root, (Brush)System.Windows.Application.Current.Resources["Text.Primary"]);
                await Arrange(root, width, height);
                var markers = Descendants<TextBlock>(view).Single(text =>
                    text.Text == model.ManualReloadMarkerInstructions);
                var caption = Descendants<TextBlock>(view).Single(text =>
                    text.Text.Contains("recorded as a manual board check", StringComparison.Ordinal));
                var acknowledgment = Descendants<CheckBox>(view).Single();
                var confirm = Descendants<Button>(view).Single(button =>
                    ReferenceEquals(button.Command, model.AttestRebuildCommand));
                var scroller = Ancestors(confirm).OfType<ScrollViewer>().First();
                if (!markers.Text.Contains("Blue scoring marker: 1", StringComparison.Ordinal) ||
                    !Label(acknowledgment).Contains("scoring markers", StringComparison.Ordinal) ||
                    acknowledgment.IsChecked == true || !IsElementShown(confirm))
                    throw new InvalidOperationException("Manual inspection must show marker positions and require explicit whole-board acknowledgment.");
                foreach (var element in new FrameworkElement[] { markers, caption, acknowledgment, confirm })
                {
                    var bounds = ReloadBounds(element, root);
                    if (bounds.Left < -1 || bounds.Right > width + 1 ||
                        element.ActualHeight + 1 < element.DesiredSize.Height - element.Margin.Top - element.Margin.Bottom)
                        throw new InvalidOperationException("Marker guidance or manual confirmation is clipped in the rebuild view.");
                }
                Save(root, $"manual-reload-inspection-{width}x{height}-top.png", width, height);
                scroller.ScrollToBottom();
                await Arrange(root, width, height);
                if (scroller.ScrollableWidth > 1 ||
                    !ReloadContains(ReloadBounds(scroller, root), ReloadBounds(confirm, root)))
                    throw new InvalidOperationException("The manual confirmation action must be reachable within its scroll viewport.");
                Save(root, $"manual-reload-inspection-{width}x{height}-bottom.png", width, height);
                Results.Add(new { View = "manual-reload-inspection", Width = width, Height = height,
                    MarkerPositionsShown = true, ManualEvidenceExplained = true,
                    ConfirmationReachable = true, HorizontalOverflow = false, Passed = true });
                root.Child = null;
                view.DataContext = null;
            }
            Console.WriteLine("Manual reload: eligible, ordinary-game and busy states passed; panel and whole-board inspection fit at both sizes.");
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static Rect ReloadBounds(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static bool ReloadContains(Rect outer, Rect inner)
    {
        outer.Inflate(1, 1);
        return outer.Contains(inner);
    }
}
