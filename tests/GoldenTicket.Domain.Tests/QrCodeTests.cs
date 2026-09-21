using System.Text;
using GoldenTicket.CompanionHost.Connect;

namespace GoldenTicket.Domain.Tests;

/// <summary>
/// The connection QR is generated on the laptop with no third-party encoder (DESIGN 18.3), so the
/// standard's structure has to be checked here rather than assumed.
///
/// The checks are deliberately independent of each other. Capacity is counted from the symbol's own
/// function patterns and compared with the standard's published data-codeword table; the format and
/// version bit strings are computed from their BCH generators and compared with the standard's
/// published constants; the alignment centres are derived and compared with the published table; and
/// every finished symbol is read back, block by block, with a Reed-Solomon syndrome check.
///
/// None of this replaces a scan by a real phone, which is what DESIGN 22.7 asks for.
/// </summary>
public sealed class QrCodeTests
{
    // ---- Capacity: geometry against the standard's table -------------------------------------------

    // Data codewords per version at L, M, Q, H (ISO/IEC 18004 table 7). Nothing in the encoder reads
    // this; it counts modules and subtracts what the block table spends on error correction. The two
    // agreeing means neither the counted geometry nor the block table is wrong.
    private static readonly int[,] PublishedDataCodewords =
    {
        //  L     M     Q     H
        {  19,   16,   13,    9 },   // version 1
        {  34,   28,   22,   16 },   // 2
        {  55,   44,   34,   26 },   // 3
        {  80,   64,   48,   36 },   // 4
        { 108,   86,   62,   46 },   // 5
        { 136,  108,   76,   60 },   // 6
        { 156,  124,   88,   66 },   // 7
        { 194,  154,  110,   86 },   // 8
        { 232,  182,  132,  100 },   // 9
        { 274,  216,  154,  122 },   // 10
    };

    private static readonly int[] PublishedTotalCodewords =
        [26, 44, 70, 100, 134, 172, 196, 242, 292, 346];

    [Fact]
    public void CountedGeometryMatchesThePublishedCodewordTotals()
    {
        for (var version = 1; version <= QrCode.MaximumSupportedVersion; version++)
        {
            Assert.Equal(PublishedTotalCodewords[version - 1], QrCode.TotalCodewords(version));
        }
    }

    [Fact]
    public void DerivedCapacityMatchesThePublishedDataCodewords()
    {
        foreach (var level in Enum.GetValues<QrErrorCorrection>())
        {
            for (var version = 1; version <= QrCode.MaximumSupportedVersion; version++)
            {
                Assert.Equal(
                    PublishedDataCodewords[version - 1, (int)level],
                    QrCode.DataCodewords(version, level));
            }
        }
    }

    [Fact]
    public void EveryBlockCarriesWholeCodewordsAndDiffersByAtMostOne()
    {
        foreach (var level in Enum.GetValues<QrErrorCorrection>())
        {
            for (var version = 1; version <= QrCode.MaximumSupportedVersion; version++)
            {
                var data = QrCode.DataCodewords(version, level);
                var blocks = QrCode.BlockCount(version, level);

                var shortLength = data / blocks;
                var longBlocks = data % blocks;

                Assert.True(shortLength > 0, $"version {version} {level} has an empty block");
                Assert.Equal(data, ((blocks - longBlocks) * shortLength) + (longBlocks * (shortLength + 1)));
            }
        }
    }

    // ---- Format and version bit strings against the standard's constants ---------------------------

    [Fact]
    public void FormatBitsMatchThePublishedConstants()
    {
        int[,] published =
        {
            { 0x77C4, 0x72F3, 0x7DAA, 0x789D, 0x662F, 0x6318, 0x6C41, 0x6976 },   // L
            { 0x5412, 0x5125, 0x5E7C, 0x5B4B, 0x45F9, 0x40CE, 0x4F97, 0x4AA0 },   // M
            { 0x355F, 0x3068, 0x3F31, 0x3A06, 0x24B4, 0x2183, 0x2EDA, 0x2BED },   // Q
            { 0x1689, 0x13BE, 0x1CE7, 0x19D0, 0x0762, 0x0255, 0x0D0C, 0x083B },   // H
        };

        foreach (var level in Enum.GetValues<QrErrorCorrection>())
        {
            for (var mask = 0; mask < 8; mask++)
            {
                Assert.Equal(published[(int)level, mask], QrCode.FormatBits(level, mask));
            }
        }
    }

    [Fact]
    public void VersionBitsMatchThePublishedConstants()
    {
        int[] published = [0x07C94, 0x085BC, 0x09A99, 0x0A4D3];

        for (var version = 7; version <= 10; version++)
        {
            Assert.Equal(published[version - 7], QrCode.VersionBits(version));
        }
    }

    [Fact]
    public void AlignmentCentresMatchThePublishedTable()
    {
        int[][] published =
        [
            [],                 // version 1
            [6, 18],            // 2
            [6, 22],            // 3
            [6, 26],            // 4
            [6, 30],            // 5
            [6, 34],            // 6
            [6, 22, 38],        // 7
            [6, 24, 42],        // 8
            [6, 26, 46],        // 9
            [6, 28, 50],        // 10
        ];

        for (var version = 1; version <= QrCode.MaximumSupportedVersion; version++)
        {
            Assert.Equal(published[version - 1], QrCode.AlignmentCentres(version));
        }
    }

    // ---- Structure of a finished symbol --------------------------------------------------------------

    [Fact]
    public void TheThreeFinderPatternsAreDrawnWithTheirSeparators()
    {
        var code = QrCode.Encode("https://gt-1a2b3c4d.local:8443/");

        foreach (var (originX, originY) in new[] { (0, 0), (code.Size - 7, 0), (0, code.Size - 7) })
        {
            for (var dy = 0; dy < 7; dy++)
            {
                for (var dx = 0; dx < 7; dx++)
                {
                    var ring = Math.Max(Math.Abs(dx - 3), Math.Abs(dy - 3));
                    Assert.Equal(ring != 2, code[originX + dx, originY + dy]);
                }
            }
        }
    }

    [Fact]
    public void TheTimingPatternsAlternate()
    {
        var code = QrCode.Encode("https://gt-1a2b3c4d.local:8443/");

        for (var index = 8; index < code.Size - 8; index++)
        {
            Assert.Equal(index % 2 == 0, code[index, 6]);
            Assert.Equal(index % 2 == 0, code[6, index]);
        }
    }

    [Fact]
    public void ModulesOutsideTheSymbolReadLight()
    {
        var code = QrCode.Encode("https://192.168.1.11:8443/");

        Assert.False(code[-1, 0]);
        Assert.False(code[0, -1]);
        Assert.False(code[code.Size, 0]);
        Assert.False(code[0, code.Size]);
    }

    // ---- Reading the symbol back -----------------------------------------------------------------------

    [Theory]
    [InlineData("https://gt-1a2b3c4d.local:8443/")]
    [InlineData("http://192.168.1.11:8080/")]
    [InlineData("https://192.168.1.11:8443/")]
    [InlineData("http://10.0.0.2:8080/")]
    [InlineData("https://gt-ffffffff.local:65535/")]
    [InlineData("A")]
    public void ASymbolReadsBackAsTheTextItEncodes(string text)
    {
        foreach (var level in Enum.GetValues<QrErrorCorrection>())
        {
            var code = QrCode.Encode(text, level);
            var read = QrReader.Read(code);

            Assert.Equal(text, read.Text);
            Assert.Equal(level, read.ErrorCorrection);
            Assert.Equal(code.Mask, read.Mask);
            Assert.Equal(code.Version, read.Version);
        }
    }

    [Fact]
    public void EveryLengthUpToTheChosenVersionReadsBack()
    {
        // Walks the padding, terminator and block-split boundaries, which are where an off-by-one in
        // the codeword assembly hides.
        for (var length = 1; length <= 120; length++)
        {
            var text = new string('7', length);
            var code = QrCode.Encode(text, QrErrorCorrection.Medium);

            Assert.Equal(text, QrReader.Read(code).Text);
        }
    }

    [Fact]
    public void TheSmallestSymbolThatFitsIsChosen()
    {
        // Version 1 at M holds 16 data codewords: 1 for the mode and count, leaving 14 characters.
        Assert.Equal(1, QrCode.Encode(new string('7', 14), QrErrorCorrection.Medium).Version);
        Assert.Equal(2, QrCode.Encode(new string('7', 15), QrErrorCorrection.Medium).Version);
    }

    [Fact]
    public void TextThatCannotFitIsRefusedRatherThanTruncated()
    {
        var tooLong = new string('7', 300);

        var error = Assert.Throws<ArgumentException>(() => QrCode.Encode(tooLong));
        Assert.Contains("do not fit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonAsciiIsRefusedRatherThanMangled()
    {
        var error = Assert.Throws<ArgumentException>(() => QrCode.Encode("https://café.local/"));
        Assert.Contains("ASCII", error.Message, StringComparison.Ordinal);
    }

    // ---- The rendered forms ------------------------------------------------------------------------------

    [Fact]
    public void TheSvgCoversEveryDarkModuleAndCarriesAQuietZone()
    {
        var code = QrCode.Encode("https://gt-1a2b3c4d.local:8443/");
        var svg = QrRenderer.ToSvg(code, "https://gt-1a2b3c4d.local:8443/");

        var dark = 0;
        for (var y = 0; y < code.Size; y++)
            for (var x = 0; x < code.Size; x++)
                if (code[x, y]) dark++;

        Assert.Equal(dark, svg.Split("h1v1h-1z").Length - 1);

        // Four modules of margin on every side, which is the standard's minimum.
        Assert.Contains($"viewBox=\"0 0 {code.Size + 8} {code.Size + 8}\"", svg, StringComparison.Ordinal);
        Assert.Contains("fill=\"#FFFFFF\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSvgEscapesTheAddressItLabelsItselfWith()
    {
        var code = QrCode.Encode("https://192.168.1.11:8443/");
        var svg = QrRenderer.ToSvg(code, "https://192.168.1.11:8443/?a=1&b=<2>");

        Assert.Contains("&amp;", svg, StringComparison.Ordinal);
        Assert.Contains("&lt;2&gt;", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<2>", svg, StringComparison.Ordinal);
    }
}

/// <summary>
/// Reads a symbol back the way a scanner would: format information, mask removal, the zigzag module
/// order, de-interleaving, and a Reed-Solomon syndrome check on every block. It exists only to test
/// the encoder, so it corrects nothing and gives up loudly on anything unexpected.
/// </summary>
internal static class QrReader
{
    internal sealed record Result(string Text, int Version, QrErrorCorrection ErrorCorrection, int Mask);

    internal static Result Read(QrCode code)
    {
        var version = (code.Size - 17) / 4;
        var (level, mask) = ReadFormat(code);

        var codewords = ReadCodewords(code, mask);
        var data = Deinterleave(codewords, version, level);

        return new Result(ReadPayload(data, version), version, level, mask);
    }

    private static (QrErrorCorrection Level, int Mask) ReadFormat(QrCode code)
    {
        var first = 0;
        for (var index = 0; index <= 5; index++) first |= Bit(code, 8, index) << index;
        first |= Bit(code, 8, 7) << 6;
        first |= Bit(code, 8, 8) << 7;
        first |= Bit(code, 7, 8) << 8;
        for (var index = 9; index < 15; index++) first |= Bit(code, 14 - index, 8) << index;

        var second = 0;
        for (var index = 0; index < 8; index++) second |= Bit(code, code.Size - 1 - index, 8) << index;
        for (var index = 8; index < 15; index++) second |= Bit(code, 8, code.Size - 15 + index) << index;

        if (first != second)
            throw new InvalidOperationException($"The two format copies disagree: {first:X4} and {second:X4}.");

        foreach (var level in Enum.GetValues<QrErrorCorrection>())
            for (var mask = 0; mask < 8; mask++)
                if (QrCode.FormatBits(level, mask) == first) return (level, mask);

        throw new InvalidOperationException($"{first:X4} is not a valid format string.");
    }

    private static byte[] ReadCodewords(QrCode code, int mask)
    {
        var version = (code.Size - 17) / 4;
        var total = QrCode.TotalCodewords(version);
        var bits = new bool[total * 8];
        var read = 0;

        for (var right = code.Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;

            for (var step = 0; step < code.Size; step++)
            {
                for (var column = 0; column < 2; column++)
                {
                    var x = right - column;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? code.Size - 1 - step : step;

                    if (code.IsFunction(x, y) || read >= bits.Length) continue;

                    bits[read++] = code[x, y] ^ Masked(mask, x, y);
                }
            }
        }

        if (read != bits.Length)
            throw new InvalidOperationException($"Read {read} of {bits.Length} data bits.");

        var codewords = new byte[total];
        for (var index = 0; index < bits.Length; index++)
            if (bits[index]) codewords[index >> 3] |= (byte)(1 << (7 - (index & 7)));

        return codewords;
    }

    /// <summary>The eight mask predicates, written out again from the standard rather than shared
    /// with the encoder, so a transcription slip in one shows up as a failed round trip.</summary>
    private static bool Masked(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => ((y / 2) + (x / 3)) % 2 == 0,
        5 => (x * y % 2) + (x * y % 3) == 0,
        6 => ((x * y % 2) + (x * y % 3)) % 2 == 0,
        7 => (((x + y) % 2) + (x * y % 3)) % 2 == 0,
        _ => throw new ArgumentOutOfRangeException(nameof(mask)),
    };

    private static byte[] Deinterleave(byte[] codewords, int version, QrErrorCorrection level)
    {
        var blocks = QrCode.BlockCount(version, level);
        var eccPerBlock = QrCode.EccCodewordsPerBlock(version, level);
        var dataTotal = QrCode.DataCodewords(version, level);

        var shortLength = dataTotal / blocks;
        var longBlocks = dataTotal % blocks;

        var lengths = new int[blocks];
        for (var index = 0; index < blocks; index++)
            lengths[index] = shortLength + (index >= blocks - longBlocks ? 1 : 0);

        var assembled = new byte[blocks][];
        for (var index = 0; index < blocks; index++) assembled[index] = new byte[lengths[index] + eccPerBlock];

        var position = 0;
        for (var column = 0; column <= shortLength; column++)
            for (var index = 0; index < blocks; index++)
                if (column < lengths[index]) assembled[index][column] = codewords[position++];

        for (var column = 0; column < eccPerBlock; column++)
            for (var index = 0; index < blocks; index++)
                assembled[index][lengths[index] + column] = codewords[position++];

        if (position != codewords.Length)
            throw new InvalidOperationException($"De-interleaved {position} of {codewords.Length} codewords.");

        // A block that does not divide by its generator was assembled wrongly, which a scanner would
        // either silently correct or refuse. Neither is acceptable from our own encoder.
        var data = new byte[dataTotal];
        var written = 0;

        for (var index = 0; index < blocks; index++)
        {
            if (!ReedSolomonCheck(assembled[index], eccPerBlock))
                throw new InvalidOperationException($"Block {index} failed its Reed-Solomon check.");

            Array.Copy(assembled[index], 0, data, written, lengths[index]);
            written += lengths[index];
        }

        return data;
    }

    private static bool ReedSolomonCheck(byte[] block, int eccLength)
    {
        // Evaluate the block as a polynomial at the generator's roots. A correct codeword is a
        // multiple of the generator, so every syndrome is zero. This is computed from first
        // principles here, not with the encoder's division routine.
        byte root = 1;
        for (var index = 0; index < eccLength; index++)
        {
            byte syndrome = 0;
            foreach (var value in block) syndrome = (byte)(GaloisMultiply(syndrome, root) ^ value);

            if (syndrome != 0) return false;
            root = GaloisMultiply(root, 2);
        }

        return true;
    }

    private static byte GaloisMultiply(byte left, byte right)
    {
        var result = 0;
        for (var bit = 7; bit >= 0; bit--)
        {
            result = (result << 1) ^ ((result >> 7) * 0x11D);
            result ^= ((right >> bit) & 1) * left;
        }

        return (byte)result;
    }

    private static string ReadPayload(byte[] data, int version)
    {
        var position = 0;

        int Take(int count)
        {
            var value = 0;
            for (var index = 0; index < count; index++, position++)
                value = (value << 1) | ((data[position >> 3] >> (7 - (position & 7))) & 1);

            return value;
        }

        var mode = Take(4);
        if (mode != 0b0100) throw new InvalidOperationException($"Expected byte mode, read {mode:X}.");

        var length = Take(QrCode.CharacterCountBits(version));
        var bytes = new byte[length];
        for (var index = 0; index < length; index++) bytes[index] = (byte)Take(8);

        var terminator = Math.Min(4, (data.Length * 8) - position);
        if (Take(terminator) != 0) throw new InvalidOperationException("The terminator is not zero.");

        return Encoding.ASCII.GetString(bytes);
    }

    private static int Bit(QrCode code, int x, int y) => code[x, y] ? 1 : 0;
}
