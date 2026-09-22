using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Desktop.Views;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Vision;

internal static partial class Program
{
    private static async Task VerifyInventoryProblemMarkers()
    {
        var model = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
        var update = typeof(GameScreenViewModel).GetMethod("UpdateInventoryProblemMarkers",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Inventory failures need a shared board marker path.");
        void Observe(string source, BoardInventoryObservation? observation) =>
            update.Invoke(model.Game, [source, observation, null]);
        var known = ClassicUsRouteGeometry.TryGetSlots("kansas-city--oklahoma-city--a", out var slots)
            ? slots[0] : throw new InvalidOperationException("The fixture route needs measured train spaces.");
        var detections = new BoardInventoryDetection[]
        {
            new(known.X - .014, known.Y - .014, .028, .028, .93, MarkerColor.Red,
                "kansas-city--oklahoma-city--a"),
            new(.80, .79, .05, .035, .86, null, null)
        };
        var problem = new BoardInventoryObservation(BoardInventoryState.UnexpectedTrain,
            new Dictionary<MarkerColor, int>()) { UnexpectedDetections = detections };
        try
        {
            var rows = detections.Select((detection, index) => new BoardCheckDetectionRow(index + 1,
                detection.X * 960, detection.Y * 600, detection.Width * 960,
                detection.Height * 600, "Synthetic problematic train")).ToArray();
            model.Camera.GameTablePreview = GameExitBoardFixture(rows);
            model.Camera.IsGameTablePreviewUpright = true;
            model.Camera.GameTablePreviewStatus = "";
            typeof(GameScreenViewModel).GetMethod("ShowGuidance", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(model.Game, ["Turn 10", "Checking...",
                    "Check the trains marked by yellow spheres. The camera detected pieces outside the saved routes."]);

            // A partly placed route has a more specific missing-slot cue than the card
            // monitor, which still counts the already placed, unpaid pieces as extras.
            var manifest = ManifestLoader.LoadClassicUs();
            var route = manifest.Routes.Single(item => item.RouteId.Value == "kansas-city--oklahoma-city--a");
            Observe("card", problem);
            typeof(GameScreenViewModel).GetMethod("ShowUnverifiedTrainSpaces", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(model.Game, [route.RouteId, route.Length, 2]);
            if (!PlacementBoardOverlay.TryGetTargets(manifest, route.RouteId, route.Length, out var routeSlots) ||
                model.Game.PlacementTargets.Count != 1 || model.Game.PlacementTargets[0].X != routeSlots[1].X ||
                model.Game.PlacementTargets[0].Y != routeSlots[1].Y)
                throw new InvalidOperationException("A card monitor must not replace the precise missing-space cue for an incomplete route.");
            Observe("card", null);
            typeof(GameScreenViewModel).GetMethod("ClearUnverifiedTrainSpaces", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(model.Game, null);

            foreach (var (width, height) in new[] { (1280, 800), (1000, 620) })
            {
                BindingLog.Context = $"game-inventory-problem-markers-{width}x{height}";
                Observe("restore", problem);
                // A later inactive observer or repeated frame cannot erase the current blocker.
                Observe("card", null);
                Observe("restore", new(BoardInventoryState.WaitingForFreshFrame, new Dictionary<MarkerColor, int>()));
                var view = new GameTableView { DataContext = model };
                await Arrange(view, width, height);
                var markers = (ItemsControl)view.FindName("PlacementTargetMarkers");
                var board = (Image)view.FindName("LiveBoardImage");
                if (!model.Game.ShowPlacementTarget || !IsElementShown(markers) || markers.Items.Count != detections.Length)
                    throw new InvalidOperationException("Every blocking detection must show a yellow sphere on the live game board, even without a route name.");
                for (var index = 0; index < detections.Length; index++)
                {
                    var marker = (ContentPresenter)markers.ItemContainerGenerator.ContainerFromIndex(index);
                    var pulse = FindNamedDescendant<Ellipse>(marker, "PlacementPulseRing");
                    var sphere = Descendants<Ellipse>(marker).SingleOrDefault(ellipse =>
                        ellipse.Fill is RadialGradientBrush);
                    if (!IsElementShown(marker) || pulse is null || !IsElementShown(pulse) ||
                        sphere is null || !IsElementShown(sphere))
                        throw new InvalidOperationException("A blocking train must use the existing pulsing gold sphere, not a text-only warning.");
                    var markerVisual = Descendants<Grid>(marker).Single(grid => grid.ActualWidth == 32 && grid.ActualHeight == 32);
                    if (markerVisual.Triggers.Count == 0 ||
                        AutomationProperties.GetName(markerVisual) != model.Game.PlacementTargets[index].Description)
                        throw new InvalidOperationException("Problem spheres must retain the pulse animation and a description of the detection.");
                    var detection = detections[index];
                    var center = marker.TranslatePoint(new Point(marker.ActualWidth / 2, marker.ActualHeight / 2), board);
                    var expected = new Point((detection.X + detection.Width / 2) * board.ActualWidth,
                        (detection.Y + detection.Height / 2) * board.ActualHeight);
                    if (Math.Abs(center.X - expected.X) > 1 || Math.Abs(center.Y - expected.Y) > 1)
                        throw new InvalidOperationException($"Blocking train {index + 1} sphere missed the detected train: {center} versus {expected}.");
                }
                if (!model.Game.PlacementTargets[0].Description.Contains("Kansas City", StringComparison.Ordinal) ||
                    !model.Game.PlacementTargets[1].Description.Contains("route uncertain", StringComparison.Ordinal))
                    throw new InvalidOperationException("Both named-route and unknown-route detections must remain individually identifiable.");
                Save(view, $"game-inventory-problem-markers-{width}x{height}.png", width, height);
                Results.Add(new { View = "game-inventory-problem-markers", Width = width, Height = height,
                    Spheres = detections.Length, UnknownRouteMarked = true, Passed = true });

                Observe("restore", new(BoardInventoryState.Stabilizing, new Dictionary<MarkerColor, int>()));
                await Arrange(view, width, height);
                if (model.Game.ShowPlacementTarget || markers.Items.Count != 0 || IsElementShown(markers))
                    throw new InvalidOperationException("The first fresh matching observation must remove the blocking spheres without waiting for a second match.");
                Save(view, $"game-inventory-problem-cleared-{width}x{height}.png", width, height);
                Results.Add(new { View = "game-inventory-problem-cleared", Width = width, Height = height,
                    FirstFreshMatchCleared = true, Passed = true });
            }
            Console.WriteLine("Inventory blockers: named and unknown train detections have aligned gold spheres on the actual board, stale frames retain them, and the first fresh matching frame clears them at both sizes.");
        }
        finally { await model.DisposeToolsAsync(); }
    }
}
