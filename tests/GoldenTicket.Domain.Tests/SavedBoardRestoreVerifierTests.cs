using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class SavedBoardRestoreVerifierTests
{
    private static readonly SavedScoreMarker[] SavedMarkers =
    [
        new(MarkerColor.Blue, 3),
        new(MarkerColor.Yellow, 5)
    ];

    [Fact]
    public void Reload_confirms_each_marker_then_the_whole_board_on_fresh_frames()
    {
        var verifier = new SavedBoardRestoreVerifier(SavedMarkers, []);
        var at = DateTimeOffset.UtcNow;
        var scores = Scores(3, 5);

        var first = Observe(verifier, 1, at, scores);
        Assert.Equal(SavedBoardRestoreStage.CheckingMarker, first.Stage);
        Assert.Equal(MarkerColor.Blue, first.Marker?.Color);
        Assert.Equal(MarkerColor.Yellow,
            Observe(verifier, 2, at.AddSeconds(1.1), scores).Marker?.Color);
        Assert.Equal(SavedBoardRestoreStage.CheckingMarker,
            Observe(verifier, 3, at.AddSeconds(2.2), scores).Stage);
        Assert.Equal(SavedBoardRestoreStage.CheckingTrains,
            Observe(verifier, 4, at.AddSeconds(3.3), scores).Stage);
        Assert.Equal(SavedBoardRestoreStage.CheckingTrains,
            Observe(verifier, 5, at.AddSeconds(4.4), scores).Stage);
        Assert.Equal(SavedBoardRestoreStage.Confirmed,
            Observe(verifier, 6, at.AddSeconds(5.5), scores).Stage);
    }

    [Fact]
    public void Earlier_marker_moved_while_checking_later_marker_must_be_reverified()
    {
        var verifier = new SavedBoardRestoreVerifier(SavedMarkers, []);
        var at = DateTimeOffset.UtcNow;
        Observe(verifier, 1, at, Scores(3, 5));
        Assert.Equal(MarkerColor.Yellow,
            Observe(verifier, 2, at.AddSeconds(1.1), Scores(3, 5)).Marker?.Color);

        // One bad camera reading holds the prompt. A second fresh reading proves the blue
        // marker has moved, so the verifier starts with blue again.
        Assert.Equal(MarkerColor.Yellow,
            Observe(verifier, 3, at.AddSeconds(2.2), Scores(4, 5)).Marker?.Color);
        Assert.Equal(MarkerColor.Blue,
            Observe(verifier, 4, at.AddSeconds(3.3), Scores(4, 5)).Marker?.Color);
        Assert.NotEqual(SavedBoardRestoreStage.Confirmed,
            Observe(verifier, 5, at.AddSeconds(4.4), Scores(4, 5)).Stage);
    }

    [Fact]
    public void Camera_epoch_change_invalidates_all_previous_confirmations()
    {
        var verifier = new SavedBoardRestoreVerifier(SavedMarkers, []);
        var at = DateTimeOffset.UtcNow;
        Observe(verifier, 1, at, Scores(3, 5));
        Assert.Equal(MarkerColor.Yellow,
            Observe(verifier, 2, at.AddSeconds(1.1), Scores(3, 5)).Marker?.Color);

        var afterReconnect = verifier.Observe(Frame(1, 2, at.AddSeconds(2.2)), Scores(3, 5), [], 1, 1);
        Assert.Equal(MarkerColor.Blue, afterReconnect.Marker?.Color);
    }

    [Fact]
    public void Missing_saved_train_keeps_reload_suspended_after_markers_match()
    {
        var verifier = new SavedBoardRestoreVerifier(SavedMarkers,
            [new BoardInventoryRoute("atlanta--raleigh--a", MarkerColor.Blue, 2)]);
        var at = DateTimeOffset.UtcNow;
        var scores = Scores(3, 5);
        for (var sequence = 1; sequence <= 5; sequence++)
            Observe(verifier, sequence, at.AddSeconds(sequence * 1.1), scores);

        var missing = Observe(verifier, 6, at.AddSeconds(6.6), scores);
        Assert.Equal(SavedBoardRestoreStage.CheckingTrains, missing.Stage);
        Assert.Equal(BoardInventoryState.MissingTrains, missing.Inventory?.State);
        Assert.Equal("atlanta--raleigh--a", missing.Inventory?.RouteId);
    }

    [Fact]
    public void Marker_changed_during_train_check_prevents_resume()
    {
        var verifier = new SavedBoardRestoreVerifier(SavedMarkers, []);
        var at = DateTimeOffset.UtcNow;
        for (var sequence = 1; sequence <= 5; sequence++)
            Observe(verifier, sequence, at.AddSeconds(sequence * 1.1), Scores(3, 5));

        var firstMiss = Observe(verifier, 6, at.AddSeconds(6.6), Scores(4, 5));
        Assert.Equal(SavedBoardRestoreStage.CheckingTrains, firstMiss.Stage);
        Assert.Null(firstMiss.Inventory);
        var secondMiss = Observe(verifier, 7, at.AddSeconds(7.7), Scores(4, 5));
        Assert.Equal(MarkerColor.Blue, secondMiss.Marker?.Color);
        Assert.NotEqual(SavedBoardRestoreStage.Confirmed, secondMiss.Stage);
    }

    [Fact]
    public void Unrelated_unknown_body_does_not_block_marker_confirmation_or_invalidate_verified_markers()
    {
        var verifier = new SavedBoardRestoreVerifier(SavedMarkers, []);
        var at = DateTimeOffset.UtcNow;
        ScoreMarkerReading[] scores = [.. Scores(3, 5),
            new(2, null, null, ScoreMarkerReadingStatus.OffTrack, "unrelated coin")];
        PieceCandidate[] candidates = [Marker(.017, .8318), Marker(.017, .737), Marker(.16, .82)];
        SavedBoardRestoreObservation? result = null;
        for (var sequence = 1; sequence <= 6; sequence++)
        {
            result = verifier.Observe(Frame(sequence, 1, at.AddSeconds(sequence * 1.1)), scores,
                candidates, 1, 1);
            Assert.Empty(result.ProblemCandidateIndices);
            if (sequence is 2 or 3) Assert.Equal(MarkerColor.Yellow, result.Marker?.Color);
        }
        Assert.Equal(SavedBoardRestoreStage.Confirmed, result!.Stage);
    }

    [Fact]
    public void Unknown_body_over_verified_marker_requires_that_marker_to_be_rechecked()
    {
        var verifier = new SavedBoardRestoreVerifier(SavedMarkers, []);
        var at = DateTimeOffset.UtcNow;
        PieceCandidate[] candidates = [Marker(.017, .8318), Marker(.017, .737), Marker(.02, .8318)];
        verifier.Observe(Frame(1, 1, at), Scores(3, 5), candidates, 1, 1);
        Assert.Equal(MarkerColor.Yellow,
            verifier.Observe(Frame(2, 1, at.AddSeconds(1.1)), Scores(3, 5), candidates, 1, 1).Marker?.Color);
        ScoreMarkerReading[] ambiguous = [.. Scores(3, 5),
            new(2, null, null, ScoreMarkerReadingStatus.UnknownColor, "overlapping body")];
        Assert.Equal(MarkerColor.Yellow,
            verifier.Observe(Frame(3, 1, at.AddSeconds(2.2)), ambiguous, candidates, 1, 1).Marker?.Color);
        var recheck = verifier.Observe(Frame(4, 1, at.AddSeconds(3.3)), ambiguous, candidates, 1, 1);
        Assert.Equal(MarkerColor.Blue, recheck.Marker?.Color);
        Assert.Equal(ScoreMarkerMoveState.Ambiguous, recheck.MarkerState);
        Assert.Equal([0, 2], recheck.ProblemCandidateIndices);
        var blocked = verifier.Observe(Frame(5, 1, at.AddSeconds(4.4)), ambiguous, candidates, 1, 1);
        Assert.Equal(MarkerColor.Blue, blocked.Marker?.Color);
        Assert.Equal(ScoreMarkerMoveState.Ambiguous, blocked.MarkerState);
        Assert.Equal([0, 2], blocked.ProblemCandidateIndices);
    }

    private static SavedBoardRestoreObservation Observe(SavedBoardRestoreVerifier verifier,
        long sequence, DateTimeOffset at, IReadOnlyList<ScoreMarkerReading> scores) =>
        verifier.Observe(Frame(sequence, 1, at), scores, [], 1, 1);

    private static ScoreMarkerReading[] Scores(int blue, int yellow) =>
    [
        new(0, MarkerColor.Blue, blue, ScoreMarkerReadingStatus.Read, "score"),
        new(1, MarkerColor.Yellow, yellow, ScoreMarkerReadingStatus.Read, "score")
    ];

    private static CameraFrame Frame(long sequence, long epoch, DateTimeOffset at) =>
        CameraFrame.CopyFromBgra32(320, 200, new byte[320 * 200 * 4], sequence, epoch, at);

    private static PieceCandidate Marker(double x, double y) => new(PieceCandidateKind.PlayerMarker,
        [new(x - .0075, y - .016), new(x + .0075, y - .016),
            new(x + .0075, y + .016), new(x - .0075, y + .016)], .9);
}
