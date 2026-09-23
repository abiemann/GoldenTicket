using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Desktop.Views;
using GoldenTicket.Domain;
using GoldenTicket.Vision;

internal static partial class Program
{
    private sealed record ExpectedMarkerReading(PlayerColor Color, string Value, string Status);

    private static async Task RunMarkerScoresSmoke()
    {
        // Use a never-created isolated settings path, not the user's saved processor preference.
        // No camera/model initialization, native window, or session store is involved.
        var settings = Path.Combine(Output, $"unused-synthetic-settings-{Guid.NewGuid():N}.json");
        await using var camera = new CameraViewModel(processingSettingsPath: settings);
        camera.Preview = SyntheticCropFixture("SYNTHETIC SCORE FIXTURE\nNO CAMERA OR MODEL INPUT");
        camera.Status = "Synthetic internal score readings. No camera, model, or user game state was loaded.";
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
        VerifyMarkerScores(camera, [
            new(PlayerColor.Blue, "15", "points"), new(PlayerColor.Red, "11", "points"),
            new(PlayerColor.Green, "50", "points"), new(PlayerColor.Yellow, "20", "points"),
            new(PlayerColor.Black, "11", "points")
        ], checks);
        if (camera.ScoreMarkerReadings.Count != 5 ||
            !camera.MarkerScoreStatus.Contains("same row or column", StringComparison.Ordinal))
            throw new InvalidOperationException("Five synthetic readings must publish shared-score guidance internally.");
        await RenderSizes("camera-marker-diagnostics-hidden", () => new CameraView { DataContext = camera }, view =>
        {
            if (view.FindName("MarkerScoresList") is not null ||
                view.FindName("DetectionOverlay") is not null ||
                VisibleText(view).Contains("Score track", StringComparison.Ordinal) ||
                Descendants<Button>(view).Any(button => button.Content as string is "Reload ML model" or "Save detection example…"))
                throw new InvalidOperationException("The technical camera must not display score cards or piece diagnostics.");
        });

        // Replacing a good frame must not retain old scores for ambiguous or missing colors.
        Publish([
            new(0, MarkerColor.Blue, null, ScoreMarkerReadingStatus.OffTrack, "Synthetic off-track marker"),
            new(1, MarkerColor.Red, 11, ScoreMarkerReadingStatus.Read, "Synthetic first red marker"),
            new(2, MarkerColor.Red, 12, ScoreMarkerReadingStatus.Read, "Synthetic second red marker"),
            new(3, MarkerColor.Yellow, null, ScoreMarkerReadingStatus.AmbiguousPosition, "Synthetic boundary position"),
            new(4, null, null, ScoreMarkerReadingStatus.UnknownColor, "Synthetic unreadable color")
        ]);
        VerifyMarkerScores(camera, [
            new(PlayerColor.Blue, "?", "Off track"), new(PlayerColor.Red, "?", "Multiple markers"),
            new(PlayerColor.Green, "—", "Not detected"), new(PlayerColor.Yellow, "?", "Check position"),
            new(PlayerColor.Black, "—", "Not detected")
        ], checks);
        if (!camera.MarkerScoreStatus.Contains("Color could not be read for 1 score marker(s).", StringComparison.Ordinal))
            throw new InvalidOperationException("An unknown color must not invent a player's score.");
        if (File.Exists(settings))
            throw new InvalidOperationException("The synthetic fixture must not persist processor settings.");
        await File.WriteAllTextAsync(Path.Combine(Output, "marker-score-checks.json"), JsonSerializer.Serialize(new
        {
            Fixture = "Synthetic internal score publication and production CameraView without the removed diagnostic controls; no camera/model inference or native window.",
            Checks = checks,
            SharedRedAndBlackScore = 11,
            MissingDuplicateUnknownAndOffTrackStatesVerified = true,
            BindingErrors = BindingLog.ErrorCount
        }, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(Path.Combine(Output, "layout-report.json"),
            JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(Path.Combine(Output, "binding-errors.log"), BindingLog.Text.ToString());
        Console.WriteLine($"Internal marker scores: {checks.Count} synthetic states; diagnostics absent from the camera view. Binding errors/warnings: {BindingLog.ErrorCount}.");
        if (BindingLog.ErrorCount > 0) Environment.ExitCode = 2;
    }

    private static void VerifyMarkerScores(CameraViewModel camera, IReadOnlyList<ExpectedMarkerReading> expected,
        List<object> checks)
    {
        if (camera.MarkerScores.Count != 5)
            throw new InvalidOperationException("Internal score publication must retain all five player colors.");
        foreach (var want in expected)
        {
            var actual = camera.MarkerScores.Single(score => score.Color == want.Color);
            if (actual.ValueText != want.Value || actual.StatusText != want.Status ||
                actual.AccessibleText != $"{want.Color}: {want.Value}, {want.Status}")
                throw new InvalidOperationException($"The internal {want.Color} score reading is incorrect.");
        }
        checks.Add(new { Readings = expected, InternalPublicationVerified = true });
    }
}
