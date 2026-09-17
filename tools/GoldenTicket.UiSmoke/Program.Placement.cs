using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Desktop.Views;
using GoldenTicket.Domain.Manifest;

internal static partial class Program
{
    private static async Task VerifyPlacementTarget()
    {
        var manifest = ManifestLoader.LoadClassicUs();
        var model = new MainViewModel(manifest, new InMemorySessionStore());
        try
        {
            model.Setup.ManualVerificationAccepted = true;
            foreach (var seat in model.Setup.Seats) seat.IsComputer = true;
            await model.StartMatchCommand.ExecuteAsync(null);
            var placement = model.Table.Placement ?? throw new InvalidOperationException(
                "The computer fixture must stop at a physical route placement.");
            if (!PlacementBoardOverlay.TryGetTargets(manifest, placement.RouteId,
                    placement.TrainCount, out var targets))
                throw new InvalidOperationException("A pending classic-US route must have a target for every train.");

            var pixels = new byte[960 * 600 * 4];
            for (var index = 0; index < pixels.Length; index += 4)
            {
                pixels[index] = 95;
                pixels[index + 1] = 124;
                pixels[index + 2] = 155;
                pixels[index + 3] = 255;
            }
            var preview = BitmapSource.Create(960, 600, 96, 96,
                PixelFormats.Bgra32, null, pixels, 960 * 4);
            preview.Freeze();
            model.Camera.GameTablePreview = preview;
            model.Camera.IsGameTablePreviewUpright = true;
            model.Camera.GameTablePreviewStatus = "";
            if (!model.Game.ShowPlacementTarget || model.Game.PlacementTargets.Count != placement.TrainCount ||
                model.Game.PlacementTargets.Where((marker, index) =>
                    Math.Abs(marker.X - targets[index].X) > 0.01 ||
                    Math.Abs(marker.Y - targets[index].Y) > 0.01).Any())
                throw new InvalidOperationException("The public board must mark every requested train space.");

            void VerifyVisible(UserControl view)
            {
                var activePlacement = model.Table.Placement ?? throw new InvalidOperationException(
                    "A placement render needs a pending route.");
                if (!PlacementBoardOverlay.TryGetTargets(manifest, activePlacement.RouteId,
                        activePlacement.TrainCount, out var visibleTargets))
                    throw new InvalidOperationException("The rendered route has no measured train spaces.");
                var markers = (ItemsControl)view.FindName("PlacementTargetMarkers");
                var board = (Image)view.FindName("LiveBoardImage");
                if (!IsElementShown(markers) || markers.Items.Count != activePlacement.TrainCount)
                    throw new InvalidOperationException("The live board must show one cue per requested train.");
                for (var index = 0; index < visibleTargets.Count; index++)
                {
                    var marker = (ContentPresenter)markers.ItemContainerGenerator.ContainerFromIndex(index);
                    var pulseRing = FindNamedDescendant<Ellipse>(marker, "PlacementPulseRing");
                    if (!IsElementShown(marker) || marker.ActualWidth != 32 || marker.ActualHeight != 32 ||
                        pulseRing is null || !IsElementShown(pulseRing))
                        throw new InvalidOperationException($"Train space {index + 1} must show a pulsing yellow sphere.");
                    var center = marker.TranslatePoint(new Point(marker.ActualWidth / 2, marker.ActualHeight / 2), board);
                    var expected = new Point(visibleTargets[index].X / 960 * board.ActualWidth,
                        visibleTargets[index].Y / 600 * board.ActualHeight);
                    if (Math.Abs(center.X - expected.X) > 3 || Math.Abs(center.Y - expected.Y) > 3)
                        throw new InvalidOperationException($"Train space {index + 1} missed its route slot: {center} versus {expected}.");
                }
            }

            await RenderSizes("game-placement-computer", () => new GameTableView { DataContext = model },
                VerifyVisible, [(1000, 620), (1280, 800)]);

            // Render sparse and long routes too: one, two, and six individual spheres must
            // remain aligned with the same board crop at different window sizes.
            foreach (var (routeId, sceneName) in new[]
                     {
                         ("kansas-city--omaha--a", "game-placement-one-train"),
                         ("atlanta--raleigh--a", "game-placement-two-trains"),
                         ("helena--seattle", "game-placement-six-trains")
                     })
            {
                var route = manifest.Routes.Single(row => row.RouteId.Value == routeId);
                model.Table.Placement = placement with
                {
                    RouteId = route.RouteId,
                    TrainCount = route.Length,
                    RouteText = routeId
                };
                await RenderSizes(sceneName, () => new GameTableView { DataContext = model },
                    VerifyVisible, [(1000, 620), (1280, 800)]);
            }
            model.Table.Placement = placement;

            model.Camera.IsGameTablePreviewUpright = false;
            if (model.Game.ShowPlacementTarget)
                throw new InvalidOperationException("An unregistered board orientation must hide the route cue.");
            model.Camera.IsGameTablePreviewUpright = true;
            model.Table.Placement = null;
            if (model.Game.ShowPlacementTarget)
                throw new InvalidOperationException("The route cue must clear when no placement awaits confirmation.");
            await RenderSizes("game-placement-cleared", () => new GameTableView { DataContext = model }, view =>
            {
                var markers = (ItemsControl)view.FindName("PlacementTargetMarkers");
                if (markers.Items.Count != 0 || IsElementShown(markers))
                    throw new InvalidOperationException("The board must not retain stale targets after placement clears.");
            }, [(1280, 800)]);

            model.Table.Placement = placement;
            model.Table.WholeBoardAcknowledged = true;
            await model.ConfirmPlacementCommand.ExecuteAsync(null);
            if (model.Table.Placement?.OperationId == placement.OperationId ||
                model.Game.ShowPlacementTarget != (model.Table.Placement is not null))
                throw new InvalidOperationException("Confirming placement must retire the old target, even if the computer immediately plans another claim.");
            Console.WriteLine("Computer placement: per-train board targets, scaled spheres, orientation guard, and confirmation lifecycle passed.");
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static T? FindNamedDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T element && element.Name == name) return element;
            var nested = FindNamedDescendant<T>(child, name);
            if (nested is not null) return nested;
        }
        return null;
    }
}
