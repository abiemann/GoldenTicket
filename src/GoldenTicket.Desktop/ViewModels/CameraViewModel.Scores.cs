using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GoldenTicket.Domain;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

public sealed record PreviewMarkerScore(PlayerColor Color, string ValueText, string StatusText)
{
    public string AccessibleText => $"{Color}: {ValueText}, {StatusText}";
}

public sealed partial class CameraViewModel
{
    public IReadOnlyList<ScoreMarkerReading> ScoreMarkerReadings { get; private set; } = [];
    [ObservableProperty] private IReadOnlyList<PreviewMarkerScore> _markerScores = WaitingMarkerScores();
    [ObservableProperty] private string _markerScoreStatus = "Waiting for fresh piece outlines.";

    private void PublishMarkerScores(IReadOnlyList<ScoreMarkerReading> value)
    {
        ScoreMarkerReadings = value;
        OnPropertyChanged(nameof(ScoreMarkerReadings));
        // A color may be missing or duplicated; never infer its score from another player.
        MarkerScores = Enum.GetValues<PlayerColor>().Select(color =>
        {
            var matches = value.Where(reading => reading.Color?.ToString() == color.ToString()).ToArray();
            if (matches.Length == 0) return new PreviewMarkerScore(color, "—", "Not detected");
            if (matches.Length > 1) return new PreviewMarkerScore(color, "?", "Multiple markers");
            var reading = matches[0];
            return reading.Status == ScoreMarkerReadingStatus.Read && reading.Score is { } score
                ? new PreviewMarkerScore(color, score.ToString(CultureInfo.InvariantCulture), "points")
                : new PreviewMarkerScore(color, "?", reading.Status == ScoreMarkerReadingStatus.OffTrack
                    ? "Off track" : "Check position");
        }).ToArray();
        var unknown = value.Count(reading => reading.Color is null);
        MarkerScoreStatus = value.Count == 0 ? "No score markers detected in this image."
            : unknown > 0 ? $"Color could not be read for {unknown} score marker(s). Check lighting and overlapping pieces."
            : "Markers beside the same row or column share its score.";
    }

    private void ClearMarkerScores()
    {
        ScoreMarkerReadings = [];
        OnPropertyChanged(nameof(ScoreMarkerReadings));
        MarkerScores = WaitingMarkerScores();
        MarkerScoreStatus = "Waiting for fresh piece outlines.";
    }

    private static PreviewMarkerScore[] WaitingMarkerScores() => Enum.GetValues<PlayerColor>()
        .Select(color => new PreviewMarkerScore(color, "—", "Waiting")).ToArray();
}
