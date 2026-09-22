using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Testing;
using Rectangle = System.Windows.Shapes.Rectangle;

internal static partial class Program
{
    private static async Task VerifyGameExitEvidence()
    {
        var model = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
        // Construct and arrange the production window without Show/Loaded, physical camera,
        // input injection, network hosting, or reading the user's saved session.
        var window = new GoldenTicket.Desktop.MainWindow(model, _ => true);
        var root = (Grid)window.FindName("Root");
        var overlay = (Grid)window.FindName("GameExitMenuOverlay");
        var panel = (Border)window.FindName("GameExitEvidencePanel");
        var checks = new List<object>();
        var rows = new BoardCheckDetectionRow[]
        {
            new(1, 250, 210, 60, 32, "1. Red train near Kansas City – Oklahoma City (lane A)."),
            new(2, 795, 488, 44, 72, "2. Possible train in the lower right of the board (route uncertain).")
        };
        var evidence = new BoardCheckEvidence(GameExitBoardFixture(rows),
            "Camera frame used for this check. Numbered boxes show the detections that need attention.", rows);
        try
        {
            model.IsGameExitMenuOpen = true;
            model.GameExitInventorySummary = "Expected from confirmed routes: Player 1 (Red): 4 on board, 41 remaining · " +
                "Computer 1 (Blue): 5 on board, 40 remaining · Computer 2 (Green): 6 on board, 39 remaining";
            model.GameExitStatus = "The camera sees extra or misplaced trains. Check the numbered areas beside this message.";
            foreach (var (width, height) in new[] { (1280, 800), (1000, 620) })
            {
                BindingLog.Context = $"game-exit-evidence-{width}x{height}";
                model.GameExitEvidence = evidence;
                await Arrange(root, width, height);
                if (!IsElementShown(overlay) || !IsElementShown(panel))
                    throw new InvalidOperationException("A failed board check must display its evidence alongside the exit menu.");
                var image = Descendants<Image>(panel).Single();
                if (!ReferenceEquals(image.Source, evidence.Image))
                    throw new InvalidOperationException("The evidence image must be the same captured frame as its numbered boxes.");
                var board = (Grid)image.Parent;
                var imageBounds = BoundsIn(image, root);
                var boxes = Descendants<Rectangle>(board).ToArray();
                Save(root, $"game-exit-evidence-{width}x{height}.png", width, height);
                if (boxes.Length != rows.Length || Math.Abs(board.ActualWidth - 960) > .01 ||
                    Math.Abs(board.ActualHeight - 600) > .01)
                    throw new InvalidOperationException($"Every detection must have one box on the 960 by 600 board canvas. " +
                        $"Found {boxes.Length} boxes for {rows.Length} rows; board={board.ActualWidth}x{board.ActualHeight}.");
                for (var index = 0; index < rows.Length; index++)
                {
                    var row = rows[index];
                    var box = boxes.Single(candidate => ReferenceEquals(candidate.DataContext, row));
                    var bounds = BoundsIn(box, root);
                    var scaleX = imageBounds.Width / 960;
                    var scaleY = imageBounds.Height / 600;
                    var expected = new Rect(imageBounds.Left + row.Left * scaleX,
                        imageBounds.Top + row.Top * scaleY, row.Width * scaleX, row.Height * scaleY);
                    if (!Near(bounds, expected) || !ContainsWithTolerance(imageBounds, bounds))
                        throw new InvalidOperationException("Detection boxes must scale and stay aligned with the captured board image.");
                    var number = Descendants<TextBlock>(board).Single(text =>
                        ReferenceEquals(text.DataContext, row) && text.Text == row.Number.ToString(CultureInfo.InvariantCulture));
                    if (!ContainsWithTolerance(imageBounds, BoundsIn(number, root)))
                        throw new InvalidOperationException("Detection numbers must remain visible on the board.");
                    var legend = Descendants<TextBlock>(panel).Single(text => text.Text == row.Description);
                    if (!IsElementShown(legend) || BoundsIn(legend, root).Top < imageBounds.Bottom)
                        throw new InvalidOperationException("Each numbered box must have its corresponding readable legend below the board.");
                }
                var actions = Descendants<Button>(overlay).Where(button =>
                    button.Content is "Save Game" or "Quit to Menu" or "Return to Game").ToArray();
                if (actions.Length != 3 || actions.Any(button => !IsElementShown(button) ||
                    !ContainsWithTolerance(new Rect(0, 0, width, height), BoundsIn(button, root)) ||
                    BoundsIn(button, root).Height < 28))
                    throw new InvalidOperationException("All three exit actions must stay visible and usable with board evidence open.");
                Save(root, $"game-exit-evidence-{width}x{height}.png", width, height);
                Results.Add(new { View = "game-exit-evidence", Width = width, Height = height,
                    Boxes = boxes.Length, ExitActions = actions.Length, Passed = true });
                checks.Add(new { Width = width, Height = height, ImageBounds = imageBounds.ToString(),
                    BoxCount = boxes.Length, TotalPanelRectangles = Descendants<Rectangle>(panel).Count(),
                    BoardWidth = board.ActualWidth, BoardHeight = board.ActualHeight,
                    NumberedLegendCount = rows.Length, ExitActionsVisible = true });

                model.GameExitEvidence = null;
                await Arrange(root, width, height);
                var rowPanel = (StackPanel)panel.Parent;
                var menu = rowPanel.Children.OfType<Border>().Single(border => !ReferenceEquals(border, panel));
                var menuBounds = BoundsIn(menu, root);
                if (panel.Visibility != Visibility.Collapsed || panel.DesiredSize.Width != 0 ||
                    Math.Abs(rowPanel.ActualWidth - menu.ActualWidth) > .5 ||
                    Math.Abs(menuBounds.Left + menuBounds.Width / 2 - width / 2d) > 1)
                    throw new InvalidOperationException("Without evidence the exit menu must recenter, with no reserved side-panel space.");
                Save(root, $"game-exit-no-evidence-{width}x{height}.png", width, height);
                Results.Add(new { View = "game-exit-no-evidence", Width = width, Height = height,
                    EvidenceCollapsed = true, EmptySideSpace = false, Passed = true });
            }
            await File.WriteAllTextAsync(Path.Combine(Output, "game-exit-evidence-layout.json"),
                JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Save error evidence: two numbered detections align with the captured board, the unknown route has a legend, actions remain visible, and cleared evidence leaves no empty panel at both sizes.");
        }
        finally
        {
            window.Close();
            await model.DisposeToolsAsync();
        }

        static Rect BoundsIn(FrameworkElement element, Visual ancestor) =>
            element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
        static bool Near(Rect actual, Rect expected) =>
            Math.Abs(actual.X - expected.X) < 1 && Math.Abs(actual.Y - expected.Y) < 1 &&
            Math.Abs(actual.Width - expected.Width) < 1 && Math.Abs(actual.Height - expected.Height) < 1;
        static bool ContainsWithTolerance(Rect outer, Rect inner)
        {
            outer.Inflate(1, 1);
            return outer.Contains(inner);
        }
    }

    private static BitmapSource GameExitBoardFixture(IReadOnlyList<BoardCheckDetectionRow> rows)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.Wheat, new Pen(Brushes.SaddleBrown, 8), new Rect(4, 4, 952, 592));
            var routePen = new Pen(Brushes.SlateGray, 6) { DashStyle = DashStyles.Dash };
            var stations = new[] { new Point(130, 140), new Point(380, 290), new Point(620, 130),
                new Point(840, 390), new Point(580, 505), new Point(190, 460) };
            for (var index = 0; index < stations.Length; index++)
            {
                drawing.DrawLine(routePen, stations[index], stations[(index + 1) % stations.Length]);
                drawing.DrawEllipse(Brushes.DarkGoldenrod, new Pen(Brushes.SaddleBrown, 2), stations[index], 9, 9);
            }
            drawing.DrawText(new FormattedText("SYNTHETIC BOARD · NO CAMERA INPUT", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 25, Brushes.SaddleBrown, 1), new Point(195, 45));
            foreach (var row in rows)
                drawing.DrawRoundedRectangle(row.Number == 1 ? Brushes.Firebrick : Brushes.DarkSlateGray,
                    new Pen(Brushes.Black, 2), new Rect(row.Left + 5, row.Top + 5, row.Width - 10, row.Height - 10), 5, 5);
        }
        var bitmap = new RenderTargetBitmap(960, 600, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
