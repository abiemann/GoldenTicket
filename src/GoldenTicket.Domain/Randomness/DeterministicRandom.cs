using System.Security.Cryptography;

namespace GoldenTicket.Domain.Randomness;

/// <summary>
/// The referee's serialisable random state. DESIGN 5.4: the exact generator state is recorded in
/// referee-only storage so a resumed match preserves the remaining order and a reopened match
/// cannot reroll a draw that was already committed.
/// </summary>
public readonly record struct RandomState(ulong S0, ulong S1, ulong S2, ulong S3)
{
    public string ToWire() => $"{S0:x16}{S1:x16}{S2:x16}{S3:x16}";

    public static RandomState FromWire(string wire)
    {
        if (wire.Length != 64)
            throw new ArgumentException("A random state is 64 hexadecimal characters.", nameof(wire));

        return new RandomState(
            ulong.Parse(wire.AsSpan(0, 16), System.Globalization.NumberStyles.HexNumber),
            ulong.Parse(wire.AsSpan(16, 16), System.Globalization.NumberStyles.HexNumber),
            ulong.Parse(wire.AsSpan(32, 16), System.Globalization.NumberStyles.HexNumber),
            ulong.Parse(wire.AsSpan(48, 16), System.Globalization.NumberStyles.HexNumber));
    }
}

/// <summary>
/// xoshiro256** with an explicit, inspectable state. DESIGN 5.4 requires a versioned unbiased
/// shuffle whose continuation can be persisted; production seeds come from the operating system
/// and simulation seeds are set explicitly for reproducible tests.
/// </summary>
public sealed class DeterministicRandom
{
    public const int ShuffleVersion = 1;

    private ulong _s0, _s1, _s2, _s3;

    public DeterministicRandom(RandomState state)
    {
        if ((state.S0 | state.S1 | state.S2 | state.S3) == 0)
            throw new ArgumentException("The random state must not be all zero.", nameof(state));

        (_s0, _s1, _s2, _s3) = (state.S0, state.S1, state.S2, state.S3);
    }

    /// <summary>Derives a state from a 64-bit seed. Use for reproducible simulation only.</summary>
    public static RandomState SeedFrom(ulong seed)
    {
        // SplitMix64 expansion, the reference seeding procedure for xoshiro.
        ulong Next()
        {
            seed += 0x9E3779B97F4A7C15UL;
            var z = seed;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        RandomState state;
        do
        {
            state = new RandomState(Next(), Next(), Next(), Next());
        }
        while ((state.S0 | state.S1 | state.S2 | state.S3) == 0);

        return state;
    }

    /// <summary>DESIGN 5.4: production seeds originate from the operating system random source.</summary>
    public static RandomState SeedFromOperatingSystem()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomState state;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            state = new RandomState(
                BitConverter.ToUInt64(bytes[..8]),
                BitConverter.ToUInt64(bytes[8..16]),
                BitConverter.ToUInt64(bytes[16..24]),
                BitConverter.ToUInt64(bytes[24..32]));
        }
        while ((state.S0 | state.S1 | state.S2 | state.S3) == 0);

        return state;
    }

    public RandomState State => new(_s0, _s1, _s2, _s3);

    public ulong NextUInt64()
    {
        var result = System.Numerics.BitOperations.RotateLeft(_s1 * 5, 7) * 9;

        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = System.Numerics.BitOperations.RotateLeft(_s3, 45);

        return result;
    }

    /// <summary>Uniform value in <c>[0, exclusiveUpperBound)</c> with rejection of the biased tail.</summary>
    public int NextInt(int exclusiveUpperBound)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveUpperBound);

        var bound = (ulong)exclusiveUpperBound;
        var limit = ulong.MaxValue - (ulong.MaxValue % bound) - 1;

        ulong value;
        do
        {
            value = NextUInt64();
        }
        while (value > limit);

        return (int)(value % bound);
    }

    /// <summary>In-place unbiased Fisher-Yates shuffle.</summary>
    public void Shuffle<T>(IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = NextInt(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
