using System.Diagnostics;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class CameraFrameClockTests
{
    private static readonly NormalizedPoint[] UnitCorners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];

    [Fact]
    public async Task Processing_and_rectification_preserve_capture_age_and_advance_together()
    {
        var clock = new ManualFrameTimeProvider();
        clock.Advance(TimeSpan.FromSeconds(3));
        // A narrow frame exercises the real CPU processing path without an expensive 4K-area allocation.
        var frame = CameraFrame.CopyFromBgra32(1920, 1, new byte[1920 * 4],
            sequence: 7, epoch: 4, clock: clock);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await using var processor = new FrameProcessor();
        await processor.InitializeAsync(FrameComputeMode.Cpu, TestContext.Current.CancellationToken);
        var processed = (await processor.ProcessAsync(frame, TestContext.Current.CancellationToken)).Frame;
        Assert.Equal(TimeSpan.FromMilliseconds(100), processed.Age);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        var rectified = BoardRegistration.Create(processed, UnitCorners).Rectify(processed, 96, 60);
        Assert.Equal(TimeSpan.FromMilliseconds(300), rectified.Age);
        clock.Advance(TimeSpan.FromMilliseconds(700));
        foreach (var derived in new[] { frame, processed, rectified })
        {
            Assert.Equal(TimeSpan.FromSeconds(1), derived.Age);
            Assert.Equal(frame.MonotonicTimestamp, derived.MonotonicTimestamp);
            Assert.Equal(frame.CapturedAt, derived.CapturedAt);
            Assert.Equal(frame.Sequence, derived.Sequence);
            Assert.Equal(frame.Epoch, derived.Epoch);
        }
    }

    [Fact]
    public void Scene_stability_uses_the_frame_clock_frequency_for_its_full_window()
    {
        var clock = new ManualFrameTimeProvider(Stopwatch.Frequency == 1_000 ? 100 : 1_000);
        Assert.NotEqual(Stopwatch.Frequency, clock.TimestampFrequency);
        var monitor = new SceneReferenceMonitor();
        monitor.SetReference(PatternFrame(clock, 1));
        for (var index = 0; index < 5; index++)
        {
            if (index > 0) clock.Advance(TimeSpan.FromMilliseconds(200));
            var result = monitor.Observe(PatternFrame(clock, index + 2));
            Assert.Equal(index + 1, result.StableFrames);
            Assert.Equal(index == 4 ? SceneReferenceState.SimilarToReference : SceneReferenceState.Stabilizing,
                result.State);
        }
    }

    [Fact]
    public void Derived_frames_still_expire_after_two_seconds()
    {
        var clock = new ManualFrameTimeProvider();
        var reference = PatternFrame(clock, 1);
        var registration = BoardRegistration.Create(reference, UnitCorners);
        var detector = new PieceCandidateDetector();
        detector.SetReference(registration.Rectify(reference, 320, 200));
        var next = PatternFrame(clock, 2);
        clock.Advance(TimeSpan.FromMilliseconds(2001));
        // Rectifying an expired input must not make it fresh again.
        var expired = registration.Rectify(next, 320, 200);
        Assert.Equal(TimeSpan.FromMilliseconds(2001), expired.Age);
        Assert.Equal(PieceDetectionState.Stale,
            detector.Detect(expired, TestContext.Current.CancellationToken).State);
        Assert.Throws<InvalidOperationException>(() => detector.SetReference(expired));
    }

    private static CameraFrame PatternFrame(TimeProvider clock, long sequence)
    {
        const int width = 96, height = 60;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (y * width + x) * 4;
            var value = (byte)(40 + ((x / 4 + y / 4) % 2) * 180);
            pixels[index] = pixels[index + 1] = pixels[index + 2] = value;
            pixels[index + 3] = 255;
        }
        return CameraFrame.CopyFromBgra32(width, height, pixels, sequence, clock: clock);
    }
}
