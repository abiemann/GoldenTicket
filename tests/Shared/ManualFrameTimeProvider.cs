namespace GoldenTicket.Domain.Tests;

/// <summary>Explicit monotonic time for timing and camera tests; asynchronous processing never advances it.</summary>
internal sealed class ManualFrameTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
    private long _elapsedTicks;

    public ManualFrameTimeProvider(long timestampFrequency = TimeSpan.TicksPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        TimestampFrequency = timestampFrequency;
    }

    public override long TimestampFrequency { get; }
    public override DateTimeOffset GetUtcNow() => Origin.AddTicks(Interlocked.Read(ref _elapsedTicks));
    public override long GetTimestamp() => checked((long)(Interlocked.Read(ref _elapsedTicks) *
        ((double)TimestampFrequency / TimeSpan.TicksPerSecond)));

    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        Interlocked.Add(ref _elapsedTicks, elapsed.Ticks);
    }
}
