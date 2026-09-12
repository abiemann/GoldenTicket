using System.Diagnostics;

namespace GoldenTicket.Vision;

public enum SceneReferenceState { NoReference, Stabilizing, SimilarToReference, SceneChanged, CameraRestarted, Stale, InsufficientDetail }
public sealed record SceneComparison(SceneReferenceState State, double Difference, double ChangedFraction,
    double Motion, int StableFrames, long EvidenceRevision)
{
    public bool SafetyHeld => State != SceneReferenceState.SimilarToReference;
}

/// <summary>
/// Conservative scene-change signal only. Similarity NEVER means trains were recognized, a route
/// was verified, or a hand is absent. A marker/landmark and occupancy pipeline is still required.
/// </summary>
public sealed class SceneReferenceMonitor
{
    public const int SampleWidth = 96;
    public const int SampleHeight = 60;
    private byte[]? _reference;
    private byte[]? _previous;
    private long _epoch;
    private int _width;
    private int _height;
    private long _sequence;
    private TimeSpan? _stableSince;
    private int _stableFrames;
    private long _revision;

    public bool HasReference => _reference is not null;
    public SceneComparison Current { get; private set; } = new(SceneReferenceState.NoReference, 0, 0, 0, 0, 0);

    public void SetReference(CameraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var signature = Sample(frame);
        if (Detail(signature) < 0.012)
            throw new InvalidOperationException("The image has too little detail for scene comparison. Point the camera at the whole board and improve focus or lighting.");
        _reference = signature;
        _previous = null;
        _epoch = frame.Epoch;
        _width = frame.Width;
        _height = frame.Height;
        _sequence = frame.Sequence;
        _stableFrames = 0;
        _stableSince = null;
        Current = new(SceneReferenceState.Stabilizing, 0, 0, 0, 0, ++_revision);
    }

    public void Clear()
    {
        _reference = null;
        _previous = null;
        _stableFrames = 0;
        _stableSince = null;
        Current = new(SceneReferenceState.NoReference, 0, 0, 0, 0, ++_revision);
    }

    public SceneComparison Observe(CameraFrame frame) => ObserveSample(Sample(frame), frame.Epoch, frame.Sequence,
        frame.Width, frame.Height, TimeSpan.FromSeconds((double)frame.MonotonicTimestamp / Stopwatch.Frequency));

    /// <summary>Public for deterministic recording replay; samples have an exact versioned 96×60 luminance shape.</summary>
    public SceneComparison ObserveSample(ReadOnlySpan<byte> sample, long epoch, long sequence, int width, int height, TimeSpan timestamp)
    {
        if (sample.Length != SampleWidth * SampleHeight) throw new ArgumentException("Unexpected scene sample size.", nameof(sample));
        if (_reference is null) return Hold(SceneReferenceState.NoReference);
        if (epoch != _epoch || width != _width || height != _height) return Hold(SceneReferenceState.CameraRestarted);
        // A frozen driver frame or duplicated callback cannot satisfy the distinct-frame window.
        if (sequence <= _sequence) return Current;
        _sequence = sequence;
        var (difference, changed) = Compare(_reference, sample);
        var motion = _previous is null ? 0 : Compare(_previous, sample).Mean;
        _previous = sample.ToArray();
        if (Detail(sample) < 0.012) return Hold(SceneReferenceState.InsufficientDetail, difference, changed, motion);
        if (difference > 0.07 || changed > 0.20)
            return Hold(SceneReferenceState.SceneChanged, difference, changed, motion);
        if (motion > 0.012) return Hold(SceneReferenceState.Stabilizing, difference, changed, motion);
        if (_stableSince is { } since && timestamp <= since)
            return Hold(SceneReferenceState.Stabilizing, difference, changed, motion);
        _stableSince ??= timestamp;
        _stableFrames++;
        var state = _stableFrames >= 5 && timestamp - _stableSince >= TimeSpan.FromMilliseconds(800)
            ? SceneReferenceState.SimilarToReference : SceneReferenceState.Stabilizing;
        Current = new(state, difference, changed, motion, _stableFrames, _revision);
        return Current;
    }

    public SceneComparison MarkStale() => Hold(SceneReferenceState.Stale);

    private SceneComparison Hold(SceneReferenceState state, double difference = 0, double changed = 0, double motion = 0)
    {
        if (!Current.SafetyHeld) _revision++;
        _stableFrames = 0;
        _stableSince = null;
        _previous = null;
        Current = new(state, difference, changed, motion, 0, _revision);
        return Current;
    }

    public static byte[] Sample(CameraFrame frame)
    {
        var sample = new byte[SampleWidth * SampleHeight];
        var pixels = frame.Bgra32.Span;
        for (var y = 0; y < SampleHeight; y++)
        for (var x = 0; x < SampleWidth; x++)
        {
            var sx = Math.Min(frame.Width - 1, (int)((x + .5) * frame.Width / SampleWidth));
            var sy = Math.Min(frame.Height - 1, (int)((y + .5) * frame.Height / SampleHeight));
            var i = (sy * frame.Width + sx) * 4;
            sample[y * SampleWidth + x] = (byte)((pixels[i] * 29 + pixels[i + 1] * 150 + pixels[i + 2] * 77) >> 8);
        }
        return sample;
    }

    private static (double Mean, double Changed) Compare(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        long difference = 0;
        var changed = 0;
        for (var i = 0; i < before.Length; i++)
        {
            var delta = Math.Abs(before[i] - after[i]);
            difference += delta;
            if (delta > 33) changed++;
        }
        return ((double)difference / before.Length / 255, (double)changed / before.Length);
    }

    private static double Detail(ReadOnlySpan<byte> sample)
    {
        long difference = 0;
        for (var y = 1; y < SampleHeight; y++)
        for (var x = 1; x < SampleWidth; x++)
        {
            var i = y * SampleWidth + x;
            difference += Math.Abs(sample[i] - sample[i - 1]) + Math.Abs(sample[i] - sample[i - SampleWidth]);
        }
        return (double)difference / ((SampleWidth - 1) * (SampleHeight - 1)) / 2 / 255;
    }
}
