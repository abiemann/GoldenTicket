using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class VisionFoundationTests
{
    private static readonly NormalizedPoint[] UnitCorners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];

    [Fact]
    public void Frame_copy_does_not_retain_the_capture_buffer()
    {
        var bytes = new byte[] { 10, 20, 30, 255 };
        var frame = CameraFrame.CopyFromBgra32(1, 1, bytes);
        bytes[0] = 200;
        Assert.Equal(10, frame.Bgra32.Span[0]);
        Assert.Equal(4, frame.Stride);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(-1, 1, 0)]
    [InlineData(3841, 1, 15364)]
    [InlineData(1, 2161, 8644)]
    [InlineData(2, 2, 15)]
    public void Frame_rejects_invalid_or_unbounded_images(int width, int height, int bytes) =>
        Assert.Throws<ArgumentException>(() => CameraFrame.CopyFromBgra32(width, height, new byte[bytes]));

    [Fact]
    public void Identity_crop_preserves_pixels_and_frame_identity()
    {
        var frame = PatternFrame(sequence: 9, epoch: 3);
        var registration = BoardRegistration.Create(frame, UnitCorners);
        var output = registration.Rectify(frame, frame.Width, frame.Height);
        Assert.Equal(frame.Bgra32.ToArray(), output.Bgra32.ToArray());
        Assert.Equal(9, output.Sequence);
        Assert.Equal(3, output.Epoch);
        Assert.Equal(frame.CapturedAt, output.CapturedAt);
        Assert.Equal(frame.MonotonicTimestamp, output.MonotonicTimestamp);
    }

    [Fact]
    public void Perspective_crop_maps_each_corner_and_centre_to_sensor_space()
    {
        var frame = PatternFrame();
        NormalizedPoint[] corners = [new(.1, .2), new(.9, .1), new(.8, .9), new(.2, .8)];
        var registration = BoardRegistration.Create(frame, corners);
        for (var i = 0; i < 4; i++)
        {
            var actual = registration.MapToSensor(UnitCorners[i].X, UnitCorners[i].Y);
            Assert.Equal(corners[i].X, actual.X, 8);
            Assert.Equal(corners[i].Y, actual.Y, 8);
        }
        var middle = registration.MapToSensor(.5, .5);
        Assert.InRange(middle.X, .3, .7);
        Assert.InRange(middle.Y, .3, .7);
    }

    [Fact]
    public void Registration_copies_corners_and_rejects_epoch_or_format_changes()
    {
        var frame = PatternFrame();
        var corners = UnitCorners.ToArray();
        var registration = BoardRegistration.Create(frame, corners);
        corners[0] = new(.4, .4);
        Assert.Equal(new NormalizedPoint(0, 0), registration.Corners[0]);
        Assert.Throws<InvalidOperationException>(() => registration.Rectify(PatternFrame(epoch: 2)));
        var otherSize = CameraFrame.CopyFromBgra32(4, 4, new byte[4 * 4 * 4]);
        Assert.Throws<InvalidOperationException>(() => registration.Rectify(otherSize));
    }

    [Fact]
    public void Registration_rejects_bowtie_counterclockwise_tiny_and_nonfinite_corners()
    {
        var frame = PatternFrame();
        Assert.Throws<ArgumentException>(() => BoardRegistration.Create(frame, [new(0, 0), new(1, 1), new(1, 0), new(0, 1)]));
        Assert.Throws<ArgumentException>(() => BoardRegistration.Create(frame, [new(0, 0), new(0, 1), new(1, 1), new(1, 0)]));
        Assert.Throws<ArgumentException>(() => BoardRegistration.Create(frame, [new(0, 0), new(.1, 0), new(.1, .1), new(0, .1)]));
        Assert.Throws<ArgumentException>(() => BoardRegistration.Create(frame, [new(double.NaN, 0), new(1, 0), new(1, 1), new(0, 1)]));
        Assert.Throws<ArgumentException>(() => BoardRegistration.Create(frame, [new(-.1, 0), new(1, 0), new(1, 1), new(0, 1)]));
    }

    [Fact]
    public void Flat_or_covered_frame_cannot_be_a_healthy_reference()
    {
        var flat = CameraFrame.CopyFromBgra32(96, 60, new byte[96 * 60 * 4]);
        var monitor = new SceneReferenceMonitor();
        Assert.Throws<InvalidOperationException>(() => monitor.SetReference(flat));
        monitor.SetReference(PatternFrame());
        var result = monitor.ObserveSample(SceneReferenceMonitor.Sample(flat), 1, 2, 96, 60, TimeSpan.FromSeconds(1));
        Assert.Equal(SceneReferenceState.InsufficientDetail, result.State);
        Assert.True(result.SafetyHeld);
    }

    [Fact]
    public void Similarity_requires_five_distinct_frames_and_a_full_stable_window()
    {
        var frame = PatternFrame();
        var sample = SceneReferenceMonitor.Sample(frame);
        var monitor = new SceneReferenceMonitor();
        monitor.SetReference(frame);
        for (var i = 0; i < 4; i++)
        {
            var result = monitor.ObserveSample(sample, 1, i + 2, 96, 60, TimeSpan.FromMilliseconds(i * 200));
            Assert.True(result.SafetyHeld);
        }
        var ready = monitor.ObserveSample(sample, 1, 6, 96, 60, TimeSpan.FromMilliseconds(800));
        Assert.False(ready.SafetyHeld);
        Assert.Equal(SceneReferenceState.SimilarToReference, ready.State);
    }

    [Fact]
    public void Duplicate_or_old_frames_cannot_unlock_reference()
    {
        var frame = PatternFrame();
        var sample = SceneReferenceMonitor.Sample(frame);
        var monitor = new SceneReferenceMonitor();
        monitor.SetReference(frame);
        for (var i = 0; i < 20; i++)
            Assert.True(monitor.ObserveSample(sample, 1, 1, 96, 60, TimeSpan.FromSeconds(i)).SafetyHeld);
        Assert.Equal(0, monitor.Current.StableFrames);
    }

    [Fact]
    public void Jog_holds_immediately_and_return_requires_new_stable_frames()
    {
        var frame = PatternFrame();
        var sample = SceneReferenceMonitor.Sample(frame);
        var monitor = new SceneReferenceMonitor();
        monitor.SetReference(frame);
        MakeStable(monitor, sample);
        var revision = monitor.Current.EvidenceRevision;
        var changed = sample.Select(b => (byte)(255 - b)).ToArray();
        var held = monitor.ObserveSample(changed, 1, 7, 96, 60, TimeSpan.FromSeconds(1));
        Assert.Equal(SceneReferenceState.SceneChanged, held.State);
        Assert.True(held.SafetyHeld);
        Assert.True(held.EvidenceRevision > revision);
        for (var i = 0; i < 4; i++)
            Assert.True(monitor.ObserveSample(sample, 1, i + 8, 96, 60, TimeSpan.FromMilliseconds(1200 + i * 200)).SafetyHeld);
        Assert.False(monitor.ObserveSample(sample, 1, 12, 96, 60, TimeSpan.FromSeconds(2)).SafetyHeld);
    }

    [Fact]
    public void Device_restart_and_staleness_invalidate_old_similarity_evidence()
    {
        var frame = PatternFrame();
        var sample = SceneReferenceMonitor.Sample(frame);
        var monitor = new SceneReferenceMonitor();
        monitor.SetReference(frame);
        MakeStable(monitor, sample);
        Assert.Equal(SceneReferenceState.CameraRestarted,
            monitor.ObserveSample(sample, 2, 10, 96, 60, TimeSpan.FromSeconds(2)).State);
        Assert.True(monitor.Current.SafetyHeld);
        Assert.Equal(SceneReferenceState.Stale, monitor.MarkStale().State);
        monitor.Clear();
        Assert.False(monitor.HasReference);
        Assert.Equal(SceneReferenceState.NoReference, monitor.Current.State);
    }

    [Fact]
    public void Small_local_difference_is_not_misreported_as_recognized_route()
    {
        var frame = PatternFrame();
        var monitor = new SceneReferenceMonitor();
        monitor.SetReference(frame);
        var sample = SceneReferenceMonitor.Sample(frame);
        sample[100] = 255;
        MakeStable(monitor, sample);
        // The only positive enum is image similarity. It deliberately carries no owner/route/claim.
        Assert.Equal(SceneReferenceState.SimilarToReference, monitor.Current.State);
        Assert.InRange(monitor.Current.ChangedFraction, 0, .01);
    }

    private static void MakeStable(SceneReferenceMonitor monitor, byte[] sample)
    {
        for (var i = 0; i < 5; i++)
            monitor.ObserveSample(sample, 1, i + 2, 96, 60, TimeSpan.FromMilliseconds(i * 200));
        Assert.False(monitor.Current.SafetyHeld);
    }

    private static CameraFrame PatternFrame(long sequence = 1, long epoch = 1)
    {
        var bytes = new byte[96 * 60 * 4];
        for (var y = 0; y < 60; y++)
        for (var x = 0; x < 96; x++)
        {
            var i = (y * 96 + x) * 4;
            bytes[i] = bytes[i + 1] = bytes[i + 2] = (byte)(((x / 4 + y / 4) % 2) * 160 + 40);
            bytes[i + 3] = 255;
        }
        return CameraFrame.CopyFromBgra32(96, 60, bytes, sequence, epoch);
    }
}
