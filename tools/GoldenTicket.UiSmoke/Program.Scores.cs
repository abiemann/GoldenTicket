using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Desktop.Views;
using GoldenTicket.Domain;
using GoldenTicket.Vision;

internal static partial class Program
{
    private sealed record ExpectedMarkerCard(PlayerColor Color, string Value, string Status);

    private static async Task RunMarkerScoresSmoke()
    {
        // Use a never-created isolated settings path, not the user's saved processor preference.
        // No camera/model initialization, native window, or session store is involved.
        var settings = Path.Combine(Output, $"unused-synthetic-settings-{Guid.NewGuid():N}.json");
        await using var camera = new CameraViewModel(processingSettingsPath: settings);
        camera.Preview = SyntheticCropFixture("SYNTHETIC SCORE FIXTURE\nNO CAMERA OR MODEL INPUT");
        camera.Status = "Synthetic score-card presentation. No camera, model, or user game state was loaded.";
        camera.FormatText = "Synthetic 640 × 360 preview";
        camera.DetectionText = "Five synthetic marker readings for layout and binding checks.";
        var publish = typeof(CameraViewModel).GetMethod("PublishMarkerScores", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The camera score publication method was not found.");
        void Publish(ScoreMarkerReading[] readings) => publish.Invoke(camera, [readings]);
        var checks = new List<object>();

        Publish([
            new(0, MarkerColor.Blue, 15, ScoreMarkerReadingStatus.Read, "Synthetic blue reading"),
            new(1, MarkerColor.Red, 11, ScoreMarkerReadingStatus.Read, "Synthetic red reading"),
            new(2, MarkerColor.Green, 50, ScoreMarkerReadingStatus.Read, "Synthetic green reading"),
            new(3, MarkerColor.Yellow, 20, ScoreMarkerReadingStatus.Read, "Synthetic yellow reading"),
            new(4, MarkerColor.Black, 11, ScoreMarkerReadingStatus.Read, "Synthetic black reading")
        ]);
        ExpectedMarkerCard[] values = [
            new(PlayerColor.Blue, "15", "points"), new(PlayerColor.Red, "11", "points"),
            new(PlayerColor.Green, "50", "points"), new(PlayerColor.Yellow, "20", "points"),
            new(PlayerColor.Black, "11", "points")
        ];
        await RenderSizes("camera-marker-scores-synthetic", () => new CameraView { DataContext = camera }, view =>
        {
            VerifyMarkerCards(view, values, checks);
            if (camera.ScoreMarkerReadings.Count != 5 ||
                !VisibleText(view).Contains("Markers beside the same row or column share its score.", StringComparison.Ordinal))
                throw new InvalidOperationException("Five readings and shared-score guidance must be published to the real camera view.");
            SaveMarkerCards(view, "scores");
        });

        // Replacing a good frame must not leave old scores visible for ambiguous or missing colors.
        Publish([
            new(0, MarkerColor.Blue, null, ScoreMarkerReadingStatus.OffTrack, "Synthetic off-track marker"),
            new(1, MarkerColor.Red, 11, ScoreMarkerReadingStatus.Read, "Synthetic first red marker"),
            new(2, MarkerColor.Red, 12, ScoreMarkerReadingStatus.Read, "Synthetic second red marker"),
            new(3, MarkerColor.Yellow, null, ScoreMarkerReadingStatus.AmbiguousPosition, "Synthetic boundary position"),
            new(4, null, null, ScoreMarkerReadingStatus.UnknownColor, "Synthetic unreadable color")
        ]);
        ExpectedMarkerCard[] uncertain = [
            new(PlayerColor.Blue, "?", "Off track"), new(PlayerColor.Red, "?", "Multiple markers"),
            new(PlayerColor.Green, "—", "Not detected"), new(PlayerColor.Yellow, "?", "Check position"),
            new(PlayerColor.Black, "—", "Not detected")
        ];
        await RenderSizes("camera-marker-scores-uncertain-synthetic", () => new CameraView { DataContext = camera }, view =>
        {
            VerifyMarkerCards(view, uncertain, checks);
            if (!VisibleText(view).Contains("Color could not be read for 1 score marker(s).", StringComparison.Ordinal))
                throw new InvalidOperationException("An unknown color must have visible guidance without inventing a player's score.");
            SaveMarkerCards(view, "uncertain");
        });

        if (File.Exists(settings))
            throw new InvalidOperationException("The presentation fixture must not persist processor settings.");
        await File.WriteAllTextAsync(Path.Combine(Output, "marker-score-checks.json"), JsonSerializer.Serialize(new
        {
            Fixture = "Explicit synthetic readings rendered through the production CameraView and PublishMarkerScores; no camera/model inference or native window.",
            Checks = checks,
            SharedRedAndBlackScore = 11,
            MissingDuplicateUnknownAndOffTrackStatesVerified = true,
            BindingErrors = BindingLog.ErrorCount
        }, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(Path.Combine(Output, "layout-report.json"),
            JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(Path.Combine(Output, "binding-errors.log"), BindingLog.Text.ToString());
        Console.WriteLine($"Score cards: {checks.Count} real-view cases; five color/value bindings and uncertainty states checked. Binding errors/warnings: {BindingLog.ErrorCount}.");
        if (BindingLog.ErrorCount > 0) Environment.ExitCode = 2;
    }

    private static void VerifyMarkerCards(UserControl view, IReadOnlyList<ExpectedMarkerCard> expected, List<object> checks)
    {
        var list = (ItemsControl)view.FindName("MarkerScoresList");
        if (list.Items.Count != 5 || !BindingOperations.IsDataBound(list, ItemsControl.ItemsSourceProperty))
            throw new InvalidOperationException("The score card list must bind all five player colors.");
        var cards = Descendants<Border>(list).Where(border => border.DataContext is PreviewMarkerScore &&
            !string.IsNullOrEmpty(AutomationProperties.GetName(border))).ToArray();
        if (cards.Length != 5) throw new InvalidOperationException("Every score needs a realized, accessible card.");
        foreach (var want in expected)
        {
            var card = cards.Single(border => ((PreviewMarkerScore)border.DataContext).Color == want.Color);
            var texts = Descendants<TextBlock>(card).ToArray();
            var value = texts.Single(text => Math.Abs(text.FontSize - 24) < .01);
            if (value.Text != want.Value || !texts.Any(text => text.Text == want.Color.ToString()) ||
                !texts.Any(text => text.Text == want.Status) || !BindingOperations.IsDataBound(value, TextBlock.TextProperty) ||
                AutomationProperties.GetName(card) != $"{want.Color}: {want.Value}, {want.Status}")
                throw new InvalidOperationException($"The real {want.Color} score card has incorrect text or accessibility bindings.");
            var bounds = card.TransformToAncestor(view).TransformBounds(new Rect(card.RenderSize));
            if (bounds.Left < -1 || bounds.Right > view.ActualWidth + 1 || bounds.Width < 1 || bounds.Height < 1)
                throw new InvalidOperationException($"The {want.Color} score card extends outside the horizontal viewport.");
            foreach (var text in texts)
            {
                var textBounds = text.TransformToAncestor(card).TransformBounds(new Rect(text.RenderSize));
                if (textBounds.Left < -1 || textBounds.Right > card.ActualWidth + 1)
                    throw new InvalidOperationException($"The {want.Color} score text extends outside its card.");
            }
        }
        if (Descendants<ScrollViewer>(view).Any(scroll => scroll.ScrollableWidth > 1))
            throw new InvalidOperationException("Score cards must wrap within the viewport without horizontal scrolling.");
        checks.Add(new { Width = view.ActualWidth, Height = view.ActualHeight,
            Cards = expected, NoHorizontalOverflow = true, RealBindingsVerified = true });
    }

    private static void SaveMarkerCards(UserControl view, string name)
    {
        var list = (ItemsControl)view.FindName("MarkerScoresList");
        var width = (int)Math.Ceiling(list.ActualWidth);
        var height = (int)Math.Ceiling(list.ActualHeight) + 32;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle((Brush)System.Windows.Application.Current.Resources["Surface.Window"], null, new Rect(0, 0, width, height));
            drawing.DrawText(new FormattedText("SYNTHETIC READINGS · REAL CAMERA VIEW CARDS", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.Black, 1), new Point(8, 6));
            drawing.DrawRectangle(new VisualBrush(list) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 32, width, height - 32));
        }
        Save(visual, $"marker-{name}-{(int)view.ActualWidth}x{(int)view.ActualHeight}-cards.png", width, height);
    }
}
