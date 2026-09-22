using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class ScoreMarkerMoveVerifierTests
{
    private static readonly ScoreMarkerReading BlueAtTwo =
        new(0, MarkerColor.Blue, 2, ScoreMarkerReadingStatus.Read, "Printed score track position read.");

    [Fact]
    public void Correct_marker_requires_two_distinct_fresh_observations_one_second_apart()
    {
        var verifier = new ScoreMarkerMoveVerifier();
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(ScoreMarkerMoveState.Stabilizing,
            verifier.Observe(Frame(1, 1, now), [BlueAtTwo], MarkerColor.Blue, 2, "op-1", 1, 1).State);
        Assert.Equal(ScoreMarkerMoveState.Stabilizing,
            verifier.Observe(Frame(2, 1, now.AddMilliseconds(800)), [BlueAtTwo],
                MarkerColor.Blue, 2, "op-1", 1, 1).State);
        Assert.True(verifier.Observe(Frame(3, 1, now.AddSeconds(1.2)), [BlueAtTwo],
            MarkerColor.Blue, 2, "op-1", 1, 1).Confirmed);
        Assert.False(verifier.Observe(Frame(4, 1, now.AddSeconds(2.4)), [BlueAtTwo],
            MarkerColor.Blue, 2, "op-1", 1, 1).Confirmed);
    }

    [Fact]
    public void Wrong_score_and_uncertain_or_duplicate_markers_reset_stability()
    {
        var verifier = new ScoreMarkerMoveVerifier();
        var now = DateTimeOffset.UtcNow;
        verifier.Observe(Frame(1, 1, now), [BlueAtTwo], MarkerColor.Blue, 2, "op-1", 1, 1);
        Assert.Equal(ScoreMarkerMoveState.WrongPosition,
            verifier.Observe(Frame(2, 1, now.AddMilliseconds(400)),
                [BlueAtTwo with { Score = 1 }], MarkerColor.Blue, 2, "op-1", 1, 1).State);
        Assert.Equal(ScoreMarkerMoveState.Stabilizing,
            verifier.Observe(Frame(3, 1, now.AddSeconds(1.3)), [BlueAtTwo],
                MarkerColor.Blue, 2, "op-1", 1, 1).State);
        Assert.Equal(ScoreMarkerMoveState.Ambiguous,
            verifier.Observe(Frame(4, 1, now.AddSeconds(1.5)),
                [BlueAtTwo, BlueAtTwo with { CandidateIndex = 1 }],
                MarkerColor.Blue, 2, "op-1", 1, 1).State);
        Assert.Equal(ScoreMarkerMoveState.Ambiguous,
            verifier.Observe(Frame(5, 1, now.AddSeconds(1.7)),
                [BlueAtTwo with { Status = ScoreMarkerReadingStatus.AmbiguousPosition }],
                MarkerColor.Blue, 2, "op-1", 1, 1).State);
        Assert.Equal(ScoreMarkerMoveState.Missing,
            verifier.Observe(Frame(6, 1, now.AddSeconds(1.9)), [],
                MarkerColor.Blue, 2, "op-1", 1, 1).State);
    }

    [Theory]
    [InlineData(ScoreMarkerReadingStatus.OffTrack)]
    [InlineData(ScoreMarkerReadingStatus.UnknownColor)]
    [InlineData(ScoreMarkerReadingStatus.AmbiguousPosition)]
    public void Unrelated_unknown_detection_does_not_veto_a_correctly_identified_target(ScoreMarkerReadingStatus status)
    {
        var verifier = new ScoreMarkerMoveVerifier();
        var now = DateTimeOffset.UtcNow;
        ScoreMarkerReading[] readings = [BlueAtTwo, new(1, null, null, status, "unrelated object")];
        PieceCandidate[] candidates = [Marker(.017, .8792), Marker(.16, .82)];

        var first = verifier.Observe(Frame(1, 1, now), readings, MarkerColor.Blue, 2, "op", 1, 1, candidates);
        Assert.Equal(ScoreMarkerMoveState.Stabilizing, first.State);
        Assert.Empty(first.ProblemCandidateIndices);
        Assert.True(verifier.Observe(Frame(2, 1, now.AddSeconds(1.1)), readings,
            MarkerColor.Blue, 2, "op", 1, 1, candidates).Confirmed);
    }

    [Theory]
    [InlineData(.017, .8792)] // Same body.
    [InlineData(.028, .8792)] // Overlapping outline.
    [InlineData(.0325, .8792)] // Adjacent narrow outlines remain within one marker body.
    public void Unknown_detection_that_could_be_the_same_target_preserves_ambiguity(double x, double y)
    {
        var verifier = new ScoreMarkerMoveVerifier();
        var now = DateTimeOffset.UtcNow;
        ScoreMarkerReading[] readings = [BlueAtTwo,
            new(1, null, null, ScoreMarkerReadingStatus.OffTrack, "unclear body")];
        PieceCandidate[] candidates = [Marker(.017, .8792), Marker(x, y)];

        for (var sequence = 1; sequence <= 2; sequence++)
        {
            var result = verifier.Observe(Frame(sequence, 1, now.AddSeconds(sequence * 1.1)), readings,
                MarkerColor.Blue, 2, "op", 1, 1, candidates);
            Assert.Equal(ScoreMarkerMoveState.Ambiguous, result.State);
            Assert.Equal([0, 1], result.ProblemCandidateIndices);
        }
    }

    [Fact]
    public void Readings_without_geometry_do_not_invent_a_connection_to_an_unidentified_object()
    {
        var verifier = new ScoreMarkerMoveVerifier();
        var now = DateTimeOffset.UtcNow;
        ScoreMarkerReading[] readings = [BlueAtTwo,
            new(1, null, null, ScoreMarkerReadingStatus.OffTrack, "unknown location")];
        Assert.Equal(ScoreMarkerMoveState.Stabilizing,
            verifier.Observe(Frame(1, 1, now), readings, MarkerColor.Blue, 2, "op", 1, 1).State);
        Assert.True(verifier.Observe(Frame(2, 1, now.AddSeconds(1.1)), readings,
            MarkerColor.Blue, 2, "op", 1, 1).Confirmed);
    }

    [Fact]
    public void Unknown_target_or_duplicate_target_color_still_blocks_even_with_other_remote_objects()
    {
        var now = DateTimeOffset.UtcNow;
        var unknown = new ScoreMarkerReading(1, null, null, ScoreMarkerReadingStatus.OffTrack, "coin");
        PieceCandidate[] candidates = [Marker(.017, .8792), Marker(.16, .82), Marker(.9, .6)];
        var missing = new ScoreMarkerMoveVerifier().Observe(Frame(1, 1, now), [unknown], MarkerColor.Blue, 2,
                "op", 1, 1, candidates);
        Assert.Equal(ScoreMarkerMoveState.Missing, missing.State);
        Assert.Empty(missing.ProblemCandidateIndices);
        var duplicate = new ScoreMarkerMoveVerifier().Observe(Frame(1, 1, now),
                [BlueAtTwo, unknown, BlueAtTwo with { CandidateIndex = 2 }], MarkerColor.Blue, 2,
                "op", 1, 1, candidates);
        Assert.Equal(ScoreMarkerMoveState.Ambiguous, duplicate.State);
        Assert.Equal([0, 2], duplicate.ProblemCandidateIndices);
        var wrong = new ScoreMarkerMoveVerifier().Observe(Frame(1, 1, now), [BlueAtTwo, unknown], MarkerColor.Blue, 3,
                "op", 1, 1, candidates);
        Assert.Equal(ScoreMarkerMoveState.WrongPosition, wrong.State);
        Assert.Equal([0], wrong.ProblemCandidateIndices);
    }

    [Fact]
    public void Repeated_frame_never_completes_confirmation()
    {
        var verifier = new ScoreMarkerMoveVerifier();
        var now = DateTimeOffset.UtcNow;
        var frame = Frame(1, 1, now);
        verifier.Observe(frame, [BlueAtTwo], MarkerColor.Blue, 2, "op-1", 1, 1);
        Assert.Equal(ScoreMarkerMoveState.WaitingForFreshFrame,
            verifier.Observe(frame, [BlueAtTwo], MarkerColor.Blue, 2, "op-1", 1, 1).State);
        Assert.Equal(ScoreMarkerMoveState.Stabilizing,
            verifier.Observe(Frame(2, 1, now.AddSeconds(1.2)), [BlueAtTwo],
                MarkerColor.Blue, 2, "op-1", 1, 1).State);
    }

    [Theory]
    [InlineData("op-2", 1, 1, 1)]
    [InlineData("op-1", 2, 1, 1)]
    [InlineData("op-1", 1, 2, 1)]
    [InlineData("op-1", 1, 1, 2)]
    public void Identity_change_requires_new_stability_window(string operation,
        long cropRevision, long modelRevision, long epoch)
    {
        var verifier = new ScoreMarkerMoveVerifier();
        var now = DateTimeOffset.UtcNow;
        verifier.Observe(Frame(1, 1, now), [BlueAtTwo], MarkerColor.Blue, 2, "op-1", 1, 1);
        Assert.Equal(ScoreMarkerMoveState.Stabilizing,
            verifier.Observe(Frame(2, epoch, now.AddSeconds(1.2)), [BlueAtTwo],
                MarkerColor.Blue, 2, operation, cropRevision, modelRevision).State);
    }

    private static CameraFrame Frame(long sequence, long epoch, DateTimeOffset capturedAt)
    {
        const int width = 320;
        const int height = 200;
        return CameraFrame.CopyFromBgra32(width, height,
            new byte[width * height * 4], sequence, epoch, capturedAt);
    }

    private static PieceCandidate Marker(double x, double y) => new(PieceCandidateKind.PlayerMarker,
        [new(x - .0075, y - .016), new(x + .0075, y - .016),
            new(x + .0075, y + .016), new(x - .0075, y + .016)], .9);
}
