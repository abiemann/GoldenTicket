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
            if (!PlacementBoardOverlay.TryGetTarget(manifest, placement.RouteId, out var targetX, out var targetY))
                throw new InvalidOperationException("A pending classic-US route must have a board target.");

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
            if (!model.Game.ShowPlacementTarget ||
                Math.Abs(model.Game.PlacementTargetX - targetX) > 0.01 ||
                Math.Abs(model.Game.PlacementTargetY - targetY) > 0.01)
                throw new InvalidOperationException("The public target must follow the pending route's canonical lane center.");

            void VerifyVisible(UserControl view)
            {
                var marker = (FrameworkElement)view.FindName("PlacementTargetMarker");
                var pulseRing = (Ellipse)view.FindName("PlacementPulseRing");
                var board = (Image)view.FindName("LiveBoardImage");
                if (!IsElementShown(marker) || marker.ActualWidth != 46 || marker.ActualHeight != 46 ||
                    !IsElementShown(pulseRing))
                    throw new InvalidOperationException("The live board must show a visible placement dot and pulse ring.");
                var center = marker.TranslatePoint(new Point(marker.ActualWidth / 2, marker.ActualHeight / 2), board);
                var expected = new Point(targetX / 960 * board.ActualWidth,
                    targetY / 600 * board.ActualHeight);
                if (Math.Abs(center.X - expected.X) > 3 || Math.Abs(center.Y - expected.Y) > 3)
                    throw new InvalidOperationException($"The placement dot missed the route center: {center} versus {expected}.");
            }

            await RenderSizes("game-placement-computer", () => new GameTableView { DataContext = model },
                VerifyVisible, [(1000, 620), (1280, 800)]);

            model.Camera.IsGameTablePreviewUpright = false;
            if (model.Game.ShowPlacementTarget)
                throw new InvalidOperationException("An unregistered board orientation must hide the route cue.");
            model.Camera.IsGameTablePreviewUpright = true;
            model.Table.Placement = null;
            if (model.Game.ShowPlacementTarget)
                throw new InvalidOperationException("The route cue must clear when no placement awaits confirmation.");
            await RenderSizes("game-placement-cleared", () => new GameTableView { DataContext = model }, view =>
            {
                var marker = (FrameworkElement)view.FindName("PlacementTargetMarker");
                if (IsElementShown(marker))
                    throw new InvalidOperationException("The board must not retain a stale target after placement clears.");
            }, [(1280, 800)]);

            model.Table.Placement = placement;
            model.Table.WholeBoardAcknowledged = true;
            await model.ConfirmPlacementCommand.ExecuteAsync(null);
            if (model.Table.Placement?.OperationId == placement.OperationId ||
                model.Game.ShowPlacementTarget != (model.Table.Placement is not null))
                throw new InvalidOperationException("Confirming placement must retire the old target, even if the computer immediately plans another claim.");
            Console.WriteLine("Computer placement: canonical board target, scaled marker, orientation guard, and confirmation lifecycle passed.");
        }
        finally { await model.DisposeToolsAsync(); }
    }
}
