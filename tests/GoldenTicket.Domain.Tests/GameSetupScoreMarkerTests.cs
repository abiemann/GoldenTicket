using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class GameSetupScoreMarkerTests
{
    private static readonly NormalizedPoint[] ImageCorners =
    [
        new(.1, .1), new(.9, .1), new(.9, .9), new(.1, .9)
    ];

    [Fact]
    public void All_four_board_orientations_are_checked_without_a_click()
    {
        var rotations = GameBoardOrientations.Enumerate(ImageCorners);
        Assert.Equal(4, rotations.Count);
        for (var scoreOneCorner = 0; scoreOneCorner < 4; scoreOneCorner++)
            Assert.Equal(ImageCorners[(scoreOneCorner + 1) % 4], rotations[scoreOneCorner][0]);
    }

    [Fact]
    public void Selected_colors_near_one_enable_play_and_clear_the_notice()
    {
        var views = Views(Observation((MarkerColor.Red, .017, .9266),
            (MarkerColor.Blue, .055, .927)));
        var check = GameSetupBoardValidator.Check([MarkerColor.Red, MarkerColor.Blue], views);
        Assert.True(check.Ready);
        Assert.Equal("", check.Message);
        Assert.Equal(3, check.OrientationIndex);
    }

    [Fact]
    public void Missing_color_is_named_even_when_no_markers_are_detected()
    {
        var missingBoth = GameSetupBoardValidator.Check([MarkerColor.Red, MarkerColor.Blue], Views(Observation()));
        Assert.False(missingBoth.Ready);
        Assert.Contains("red", missingBoth.Message);
        Assert.Contains("blue", missingBoth.Message);

        var missingBlue = GameSetupBoardValidator.Check([MarkerColor.Red, MarkerColor.Blue],
            Views(Observation((MarkerColor.Red, .017, .9266))));
        Assert.False(missingBlue.Ready);
        Assert.Contains("blue", missingBlue.Message);

        var train = Candidate(PieceCandidateKind.Train, .5, .5);
        var withTrain = Views(Observation((MarkerColor.Red, .017, .9266))).ToArray();
        withTrain[0] = new([train], []);
        var combined = GameSetupBoardValidator.Check([MarkerColor.Red, MarkerColor.Blue], withTrain);
        Assert.Contains("Remove all trains", combined.Message);
        Assert.Contains("blue", combined.Message);
    }

    [Fact]
    public void A_train_blocks_play_even_when_every_marker_is_present()
    {
        var good = Observation((MarkerColor.Red, .017, .9266), (MarkerColor.Blue, .055, .927));
        var train = Candidate(PieceCandidateKind.Train, .5, .5);
        var views = Views(good).ToArray();
        views[3] = new([..good.Candidates, train], good.Markers);
        var check = GameSetupBoardValidator.Check([MarkerColor.Red, MarkerColor.Blue], views);
        Assert.False(check.Ready);
        Assert.Contains("Remove all trains", check.Message);
    }

    [Theory]
    [InlineData(.017, .8792)] // neighboring printed 2
    [InlineData(.017, .974)]  // neighboring printed 100
    [InlineData(.5, .5)]      // elsewhere on the map
    public void Markers_outside_the_one_area_cannot_start_the_game(double x, double y)
    {
        var check = GameSetupBoardValidator.Check([MarkerColor.Red, MarkerColor.Blue],
            Views(Observation((MarkerColor.Red, x, y), (MarkerColor.Blue, .055, .927))));
        Assert.False(check.Ready);
        Assert.Contains("red", check.Message);
    }

    private static IReadOnlyList<GameSetupBoardObservation> Views(GameSetupBoardObservation active) =>
        [new([], []), new([], []), new([], []), active];

    private static GameSetupBoardObservation Observation(params (MarkerColor Color, double X, double Y)[] markers)
    {
        var candidates = markers.Select(marker => Candidate(PieceCandidateKind.PlayerMarker, marker.X, marker.Y)).ToArray();
        var readings = markers.Select((marker, index) => new ScoreMarkerReading(index, marker.Color, 1,
            ScoreMarkerReadingStatus.Read, "Read")).ToArray();
        return new(candidates, readings);
    }

    private static PieceCandidate Candidate(PieceCandidateKind kind, double x, double y) =>
        new(kind, [new(x - .01, y - .01), new(x + .01, y - .01),
            new(x + .01, y + .01), new(x - .01, y + .01)], .95);
}
