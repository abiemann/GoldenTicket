using System.Reflection;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class CameraMarkerScoreTests
{
    [Fact]
    public async Task Colors_share_a_score_and_missing_colors_are_not_assigned_zero()
    {
        await using var camera = new CameraViewModel();
        Publish(camera,
            new(0, MarkerColor.Red, 11, ScoreMarkerReadingStatus.Read, ""),
            new(1, MarkerColor.Black, 11, ScoreMarkerReadingStatus.Read, ""),
            new(2, MarkerColor.Yellow, 20, ScoreMarkerReadingStatus.Read, ""));
        Assert.Equal("11", Row(camera, PlayerColor.Red).ValueText);
        Assert.Equal("11", Row(camera, PlayerColor.Black).ValueText);
        Assert.Equal("20", Row(camera, PlayerColor.Yellow).ValueText);
        Assert.Equal("—", Row(camera, PlayerColor.Green).ValueText);
        Assert.Equal("Not detected", Row(camera, PlayerColor.Green).StatusText);
    }

    [Fact]
    public async Task Duplicated_color_and_uncertain_position_do_not_publish_a_numeric_score()
    {
        await using var camera = new CameraViewModel();
        Publish(camera,
            new(0, MarkerColor.Blue, 15, ScoreMarkerReadingStatus.Read, ""),
            new(1, MarkerColor.Blue, 16, ScoreMarkerReadingStatus.Read, ""),
            new(2, MarkerColor.Red, null, ScoreMarkerReadingStatus.OffTrack, ""),
            new(3, null, null, ScoreMarkerReadingStatus.UnknownColor, ""));
        Assert.Equal("?", Row(camera, PlayerColor.Blue).ValueText);
        Assert.Equal("Multiple markers", Row(camera, PlayerColor.Blue).StatusText);
        Assert.Equal("?", Row(camera, PlayerColor.Red).ValueText);
        Assert.Equal("Off track", Row(camera, PlayerColor.Red).StatusText);
        Assert.Contains("1 score marker", camera.MarkerScoreStatus);
    }

    [Fact]
    public async Task Empty_detection_and_disabled_outlines_are_distinct_states()
    {
        await using var camera = new CameraViewModel();
        Publish(camera);
        Assert.All(camera.MarkerScores, row => Assert.Equal("Not detected", row.StatusText));
        Publish(camera, new ScoreMarkerReading(0, MarkerColor.Green, 50, ScoreMarkerReadingStatus.Read, ""));
        camera.ShowPieceOutlines = false;
        Assert.Empty(camera.ScoreMarkerReadings);
        Assert.All(camera.MarkerScores, row =>
        {
            Assert.Equal("—", row.ValueText);
            Assert.Equal("Waiting", row.StatusText);
        });
        camera.ShowPieceOutlines = true;
        Assert.Empty(camera.ScoreMarkerReadings);
        Assert.DoesNotContain(camera.MarkerScores, row => row.ValueText == "50");
    }

    private static PreviewMarkerScore Row(CameraViewModel camera, PlayerColor color) =>
        Assert.Single(camera.MarkerScores, row => row.Color == color);

    private static void Publish(CameraViewModel camera, params ScoreMarkerReading[] readings) =>
        typeof(CameraViewModel).GetMethod("PublishMarkerScores", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(camera, [readings]);
}
