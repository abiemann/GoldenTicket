namespace GoldenTicket.ConnectivitySpike.Connect;

/// <summary>
/// Reed-Solomon remainders over GF(256) with the QR standard's primitive polynomial
/// x^8 + x^4 + x^3 + x^2 + 1 (0x11D), used only to produce the error-correction codewords of a
/// locally generated connection QR.
/// </summary>
internal static class ReedSolomon
{
    private const int Primitive = 0x11D;

    /// <summary>
    /// The generator polynomial for <paramref name="degree"/> error-correction codewords, which is
    /// the product of (x - a^i) for i below the degree. Its leading coefficient is implicit.
    /// </summary>
    internal static byte[] Divisor(int degree)
    {
        if (degree is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(degree));

        var result = new byte[degree];
        result[degree - 1] = 1;

        byte root = 1;
        for (var round = 0; round < degree; round++)
        {
            for (var index = 0; index < degree; index++)
            {
                result[index] = Multiply(result[index], root);
                if (index + 1 < degree) result[index] ^= result[index + 1];
            }

            root = Multiply(root, 2);
        }

        return result;
    }

    /// <summary>Writes the remainder of <paramref name="data"/> divided by the generator.</summary>
    internal static void Remainder(ReadOnlySpan<byte> data, ReadOnlySpan<byte> divisor, Span<byte> destination)
    {
        if (destination.Length != divisor.Length)
            throw new ArgumentException("The remainder is as long as the generator polynomial.");

        destination.Clear();

        foreach (var value in data)
        {
            var factor = (byte)(value ^ destination[0]);
            destination[1..].CopyTo(destination);
            destination[^1] = 0;

            for (var index = 0; index < destination.Length; index++)
                destination[index] ^= Multiply(divisor[index], factor);
        }
    }

    /// <summary>
    /// Whether a codeword block still divides cleanly by its generator. Recomputing the remainder of
    /// data-plus-remainder must give zero; anything else means the block was assembled wrongly.
    /// </summary>
    internal static bool IsIntact(ReadOnlySpan<byte> block, int eccLength)
    {
        var divisor = Divisor(eccLength);
        Span<byte> remainder = stackalloc byte[eccLength];
        Remainder(block, divisor, remainder);

        foreach (var value in remainder) if (value != 0) return false;
        return true;
    }

    /// <summary>Carry-less multiplication reduced by the primitive polynomial.</summary>
    internal static byte Multiply(byte left, byte right)
    {
        var result = 0;
        for (var bit = 7; bit >= 0; bit--)
        {
            result = (result << 1) ^ ((result >> 7) * Primitive);
            result ^= ((right >> bit) & 1) * left;
        }

        return (byte)result;
    }
}
