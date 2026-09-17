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
                [BlueAtTwo, BlueAtTwo with { CandidateIndex = 2, Color = null }],
                MarkerColor.Blue, 2, "op-1", 1, 1).State);
        Assert.Equal(ScoreMarkerMoveState.Missing,
            verifier.Observe(Frame(6, 1, now.AddSeconds(1.9)), [],
                MarkerColor.Blue, 2, "op-1", 1, 1).State);
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
}
