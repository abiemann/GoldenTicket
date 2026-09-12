namespace GoldenTicket.CompanionHost.Connect;

/// <summary>
/// How much of the symbol is redundancy. Higher survives more damage but needs a larger symbol.
/// </summary>
public enum QrErrorCorrection
{
    Low,
    Medium,
    Quartile,
    High,
}

/// <summary>
/// A QR symbol generated on the laptop, with no network call and no third-party encoder
/// (DESIGN 18.3: the QR-generation code is bundled with the Windows installation, and no external
/// website or redirect service may be required to complete setup).
///
/// Only byte mode is implemented, because the only thing this ever encodes is the local landing
/// address (DESIGN 18.5: "The connection QR contains only the local landing address"). It carries no
/// pairing code, no credential and no game state.
///
/// Two structural facts are checked rather than trusted. The number of codewords a version holds is
/// counted from the finished function-pattern map instead of read from a capacity table, and it must
/// agree with the block table below; and the format and version bit strings are computed with their
/// BCH generators rather than copied. A scan by a real phone is still the acceptance test.
/// </summary>
public sealed class QrCode
{
    /// <summary>Modules per side, including no quiet zone.</summary>
    public int Size { get; }

    public int Version { get; }

    public QrErrorCorrection ErrorCorrection { get; }

    /// <summary>Which of the eight data masks scored best.</summary>
    public int Mask { get; }

    private readonly bool[,] modules;

    /// <summary>Modules belonging to a function pattern, which data never overwrites.</summary>
    private readonly bool[,] function;

    /// <summary>True where the module is dark. Outside the symbol it reads light, so a caller can
    /// draw the quiet zone by indexing past the edge.</summary>
    public bool this[int x, int y] =>
        x >= 0 && y >= 0 && x < Size && y < Size && modules[y, x];

    private QrCode(int version, QrErrorCorrection errorCorrection, byte[] codewords)
    {
        Version = version;
        ErrorCorrection = errorCorrection;
        Size = 17 + (4 * version);

        modules = new bool[Size, Size];
        function = new bool[Size, Size];

        DrawFunctionPatterns();
        DrawCodewords(codewords);
        Mask = ApplyBestMask();
    }

    /// <summary>
    /// Encodes one ASCII string, choosing the smallest symbol that holds it.
    /// </summary>
    /// <exception cref="ArgumentException">The text is not ASCII, or does not fit.</exception>
    public static QrCode Encode(
        string text,
        QrErrorCorrection errorCorrection = QrErrorCorrection.Medium,
        int maximumVersion = MaximumSupportedVersion)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Byte mode defaults to ISO-8859-1, and a local landing address is always ASCII. Refusing
        // anything else is better than emitting a symbol that decodes to different text.
        var payload = new byte[text.Length];
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] > 0x7E || text[index] < 0x20)
                throw new ArgumentException(
                    $"Only printable ASCII can be encoded; found U+{(int)text[index]:X4} at {index}.",
                    nameof(text));

            payload[index] = (byte)text[index];
        }

        if (maximumVersion is < 1 or > MaximumSupportedVersion)
            throw new ArgumentOutOfRangeException(nameof(maximumVersion));

        for (var version = 1; version <= maximumVersion; version++)
        {
            var capacityBits = DataCodewords(version, errorCorrection) * 8;
            var neededBits = 4 + CharacterCountBits(version) + (payload.Length * 8);

            if (neededBits <= capacityBits)
                return new QrCode(version, errorCorrection, BuildCodewords(version, errorCorrection, payload));
        }

        throw new ArgumentException(
            $"{payload.Length} bytes do not fit in a version {maximumVersion} symbol at " +
            $"{errorCorrection} error correction.",
            nameof(text));
    }

    // ---- Codeword construction -------------------------------------------------------------------

    private static byte[] BuildCodewords(int version, QrErrorCorrection errorCorrection, byte[] payload)
    {
        var dataCodewords = DataCodewords(version, errorCorrection);
        var bits = new BitBuffer(dataCodewords * 8);

        bits.Append(0b0100, 4);                                  // byte mode
        bits.Append(payload.Length, CharacterCountBits(version));
        foreach (var value in payload) bits.Append(value, 8);

        // Terminator, then zero-fill to a codeword boundary, then the two alternating pad bytes.
        bits.Append(0, Math.Min(4, (dataCodewords * 8) - bits.Length));
        bits.Append(0, (8 - (bits.Length % 8)) % 8);

        for (var pad = 0xEC; bits.Length < dataCodewords * 8; pad ^= 0xEC ^ 0x11)
            bits.Append(pad, 8);

        return Interleave(version, errorCorrection, bits.ToBytes());
    }

    /// <summary>
    /// Splits the data into blocks, appends each block's Reed-Solomon remainder, and interleaves
    /// them so a burst of damage is spread across blocks rather than destroying one.
    /// </summary>
    private static byte[] Interleave(int version, QrErrorCorrection errorCorrection, byte[] data)
    {
        var blockCount = BlockCount(version, errorCorrection);
        var eccPerBlock = EccCodewordsPerBlock(version, errorCorrection);
        var total = TotalCodewords(version);

        // The spec's two block groups are derivable: the short blocks come first, and the remainder
        // of the division is exactly how many blocks carry one extra data codeword.
        var shortLength = data.Length / blockCount;
        var longBlocks = data.Length % blockCount;

        var divisor = ReedSolomon.Divisor(eccPerBlock);
        var blocks = new byte[blockCount][];

        var offset = 0;
        for (var index = 0; index < blockCount; index++)
        {
            var length = shortLength + (index >= blockCount - longBlocks ? 1 : 0);
            var block = new byte[length + eccPerBlock];

            Array.Copy(data, offset, block, 0, length);
            offset += length;

            ReedSolomon.Remainder(block.AsSpan(0, length), divisor, block.AsSpan(length));
            blocks[index] = block;
        }

        var result = new byte[total];
        var written = 0;

        // Data first. The shorter blocks simply have nothing to contribute in the final column.
        for (var column = 0; column <= shortLength; column++)
        {
            for (var index = 0; index < blockCount; index++)
            {
                var dataLength = blocks[index].Length - eccPerBlock;
                if (column < dataLength) result[written++] = blocks[index][column];
            }
        }

        // Then the remainders, which are the same length in every block.
        for (var column = 0; column < eccPerBlock; column++)
        {
            for (var index = 0; index < blockCount; index++)
            {
                var dataLength = blocks[index].Length - eccPerBlock;
                result[written++] = blocks[index][dataLength + column];
            }
        }

        if (written != total)
            throw new InvalidOperationException(
                $"Interleaving produced {written} codewords where version {version} holds {total}.");

        return result;
    }

    // ---- Function patterns ---------------------------------------------------------------------

    private void DrawFunctionPatterns()
    {
        for (var index = 0; index < Size; index++)
        {
            // Timing patterns run the full width and height; the finder blocks overwrite their ends.
            SetFunction(6, index, index % 2 == 0);
            SetFunction(index, 6, index % 2 == 0);
        }

        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        var centres = AlignmentCentres(Version);
        for (var row = 0; row < centres.Length; row++)
        {
            for (var column = 0; column < centres.Length; column++)
            {
                // The three corners are already occupied by finder patterns.
                var corner = (row == 0 && column == 0) ||
                             (row == 0 && column == centres.Length - 1) ||
                             (row == centres.Length - 1 && column == 0);

                if (!corner) DrawAlignment(centres[column], centres[row]);
            }
        }

        // Reserve the format area now so data placement skips it; the real bits need the mask.
        DrawFormatBits(0);
        DrawVersionBits();
    }

    private void DrawFinder(int centreX, int centreY)
    {
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                var x = centreX + dx;
                var y = centreY + dy;
                if (x < 0 || y < 0 || x >= Size || y >= Size) continue;

                // Rings at Chebyshev distance 0-2 are dark, 3 is the light ring, 4 is the separator.
                var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetFunction(x, y, distance != 2 && distance != 4);
            }
        }
    }

    private void DrawAlignment(int centreX, int centreY)
    {
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                SetFunction(centreX + dx, centreY + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        }
    }

    /// <summary>
    /// The alignment-pattern centres, derived rather than tabulated: the count grows every seventh
    /// version, the first is always at 6 and the last at size minus 7, and the intervening spacing is
    /// the even number that closes the gap.
    /// </summary>
    internal static int[] AlignmentCentres(int version)
    {
        if (version == 1) return [];

        var count = (version / 7) + 2;
        var step = ((version * 4) + (count * 2) + 1) / ((count * 2) - 2) * 2;

        var centres = new int[count];
        centres[0] = 6;

        var position = (17 + (4 * version)) - 7;
        for (var index = count - 1; index >= 1; index--, position -= step) centres[index] = position;

        return centres;
    }

    /// <summary>Writes the 15 format bits for a mask into both copies, plus the always-dark module.</summary>
    private void DrawFormatBits(int mask)
    {
        var bits = FormatBits(ErrorCorrection, mask);

        for (var index = 0; index <= 5; index++) SetFunction(8, index, GetBit(bits, index));
        SetFunction(8, 7, GetBit(bits, 6));
        SetFunction(8, 8, GetBit(bits, 7));
        SetFunction(7, 8, GetBit(bits, 8));
        for (var index = 9; index < 15; index++) SetFunction(14 - index, 8, GetBit(bits, index));

        for (var index = 0; index < 8; index++) SetFunction(Size - 1 - index, 8, GetBit(bits, index));
        for (var index = 8; index < 15; index++) SetFunction(8, Size - 15 + index, GetBit(bits, index));

        SetFunction(8, Size - 8, true);
    }

    private void DrawVersionBits()
    {
        if (Version < 7) return;

        var bits = VersionBits(Version);
        for (var index = 0; index < 18; index++)
        {
            var bit = GetBit(bits, index);
            var far = Size - 11 + (index % 3);
            var near = index / 3;

            SetFunction(far, near, bit);
            SetFunction(near, far, bit);
        }
    }

    /// <summary>
    /// The 15-bit format string: two level bits and three mask bits, extended by a BCH(15, 5) code
    /// and masked so an all-zero format cannot be mistaken for a blank symbol.
    /// </summary>
    internal static int FormatBits(QrErrorCorrection errorCorrection, int mask)
    {
        var level = errorCorrection switch
        {
            QrErrorCorrection.Low => 0b01,
            QrErrorCorrection.Medium => 0b00,
            QrErrorCorrection.Quartile => 0b11,
            QrErrorCorrection.High => 0b10,
            _ => throw new ArgumentOutOfRangeException(nameof(errorCorrection)),
        };

        var data = (level << 3) | mask;
        var remainder = data;
        for (var round = 0; round < 10; round++)
            remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);

        return (((data << 10) | remainder) ^ 0x5412) & 0x7FFF;
    }

    /// <summary>The 18-bit version string: six version bits extended by a BCH(18, 6) code.</summary>
    internal static int VersionBits(int version)
    {
        var remainder = version;
        for (var round = 0; round < 12; round++)
            remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);

        return ((version << 12) | remainder) & 0x3FFFF;
    }

    // ---- Data placement ---------------------------------------------------------------------------

    private void DrawCodewords(byte[] codewords)
    {
        var expected = TotalCodewords(Version);
        if (codewords.Length != expected)
            throw new ArgumentException($"Expected {expected} codewords, received {codewords.Length}.");

        var bit = 0;

        // Two columns at a time, right to left, alternating upward and downward, skipping the column
        // that carries the vertical timing pattern.
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;

            for (var step = 0; step < Size; step++)
            {
                for (var column = 0; column < 2; column++)
                {
                    var x = right - column;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? Size - 1 - step : step;

                    if (function[y, x]) continue;
                    if (bit >= codewords.Length * 8) continue;   // the trailing remainder bits stay light

                    modules[y, x] = GetBit(codewords[bit >> 3], 7 - (bit & 7));
                    bit++;
                }
            }
        }

        if (bit != codewords.Length * 8)
            throw new InvalidOperationException(
                $"Placed {bit} of {codewords.Length * 8} data bits in a version {Version} symbol.");
    }

    // ---- Masking -------------------------------------------------------------------------------------

    private int ApplyBestMask()
    {
        var best = 0;
        var bestPenalty = int.MaxValue;

        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask);
            DrawFormatBits(mask);

            var penalty = Penalty();
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                best = mask;
            }

            ApplyMask(mask);   // XOR is its own inverse, so this restores the unmasked modules
        }

        ApplyMask(best);
        DrawFormatBits(best);
        return best;
    }

    private void ApplyMask(int mask)
    {
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (function[y, x]) continue;

                var invert = mask switch
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

                if (invert) modules[y, x] ^= true;
            }
        }
    }

    /// <summary>
    /// The four penalty rules. A lower total is a symbol a scanner is less likely to misread: long
    /// same-colour runs, solid blocks, sequences that imitate a finder pattern, and an unbalanced
    /// ratio of dark to light.
    /// </summary>
    private int Penalty()
    {
        var penalty = 0;

        for (var line = 0; line < Size; line++)
        {
            penalty += RunPenalty(line, horizontal: true);
            penalty += RunPenalty(line, horizontal: false);
        }

        for (var y = 0; y < Size - 1; y++)
        {
            for (var x = 0; x < Size - 1; x++)
            {
                var colour = modules[y, x];
                if (colour == modules[y, x + 1] && colour == modules[y + 1, x] && colour == modules[y + 1, x + 1])
                    penalty += 3;
            }
        }

        var dark = 0;
        foreach (var module in modules) if (module) dark++;

        var percent = dark * 100 / (Size * Size);
        penalty += Math.Abs(percent - 50) / 5 * 10;

        return penalty;
    }

    private int RunPenalty(int line, bool horizontal)
    {
        var penalty = 0;
        var runLength = 0;
        var runColour = false;

        // A finder-like sequence is 1:1:3:1:1 dark-light-dark-light-dark with four light modules on
        // one side, so eleven consecutive modules are enough to recognise it.
        var window = 0;

        for (var index = 0; index < Size; index++)
        {
            var colour = horizontal ? modules[line, index] : modules[index, line];

            if (index > 0 && colour == runColour)
            {
                runLength++;
                if (runLength == 5) penalty += 3;
                else if (runLength > 5) penalty += 1;
            }
            else
            {
                runColour = colour;
                runLength = 1;
            }

            window = ((window << 1) | (colour ? 1 : 0)) & 0x7FF;
            if (index >= 10 && (window == 0b10111010000 || window == 0b00001011101)) penalty += 40;
        }

        return penalty;
    }

    // ---- Geometry ---------------------------------------------------------------------------------

    /// <summary>
    /// Data and error-correction codewords a version holds, counted from a symbol's actual function
    /// patterns rather than copied from a capacity table.
    /// </summary>
    internal static int TotalCodewords(int version)
    {
        if (version is < 1 or > MaximumSupportedVersion)
            throw new ArgumentOutOfRangeException(nameof(version));

        return FunctionModuleCounts.GetOrAdd(version, static key =>
        {
            var probe = new QrCode(key);
            var size = probe.Size;
            var reserved = 0;

            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                    if (probe.function[y, x]) reserved++;

            return ((size * size) - reserved) / 8;
        });
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> FunctionModuleCounts = new();

    /// <summary>Builds only the function patterns, to count how many modules data may use.</summary>
    private QrCode(int version)
    {
        Version = version;
        ErrorCorrection = QrErrorCorrection.Medium;
        Size = 17 + (4 * version);
        modules = new bool[Size, Size];
        function = new bool[Size, Size];
        DrawFunctionPatterns();
    }

    internal static int DataCodewords(int version, QrErrorCorrection errorCorrection)
    {
        var total = TotalCodewords(version);
        var data = total - (BlockCount(version, errorCorrection) * EccCodewordsPerBlock(version, errorCorrection));

        if (data <= 0)
            throw new InvalidOperationException(
                $"Version {version} at {errorCorrection} leaves no room for data.");

        return data;
    }

    internal static int CharacterCountBits(int version) => version <= 9 ? 8 : 16;

    /// <summary>Whether a module belongs to a function pattern, so a reader knows to skip it.</summary>
    internal bool IsFunction(int x, int y) => function[y, x];

    private void SetFunction(int x, int y, bool dark)
    {
        if (x < 0 || y < 0 || x >= Size || y >= Size) return;

        modules[y, x] = dark;
        function[y, x] = true;
    }

    private static bool GetBit(int value, int index) => ((value >> index) & 1) != 0;

    // ---- The one table that cannot be derived ------------------------------------------------------
    //
    // Everything else here is computed. These two come from the standard's block-structure table, so
    // they are cross-checked against the geometry above: the codewords a version holds must equal its
    // blocks times their error-correction codewords, plus the data the blocks carry. A typo in either
    // column fails that check rather than producing a symbol that silently will not scan.

    public const int MaximumSupportedVersion = 10;

    private static readonly byte[,] EccPerBlockTable =
    {
        //  L   M   Q   H
        {   7, 10, 13, 17 },   // version 1
        {  10, 16, 22, 28 },   // 2
        {  15, 26, 18, 22 },   // 3
        {  20, 18, 26, 16 },   // 4
        {  26, 24, 18, 22 },   // 5
        {  18, 16, 24, 28 },   // 6
        {  20, 18, 18, 26 },   // 7
        {  24, 22, 22, 26 },   // 8
        {  30, 22, 20, 24 },   // 9
        {  18, 26, 24, 28 },   // 10
    };

    private static readonly byte[,] BlockCountTable =
    {
        //  L   M   Q   H
        {   1,  1,  1,  1 },   // version 1
        {   1,  1,  1,  1 },   // 2
        {   1,  1,  2,  2 },   // 3
        {   1,  2,  2,  4 },   // 4
        {   1,  2,  4,  4 },   // 5
        {   2,  4,  4,  4 },   // 6
        {   2,  4,  6,  5 },   // 7
        {   2,  4,  6,  6 },   // 8
        {   2,  5,  8,  8 },   // 9
        {   4,  5,  8,  8 },   // 10
    };

    internal static int EccCodewordsPerBlock(int version, QrErrorCorrection errorCorrection) =>
        EccPerBlockTable[version - 1, (int)errorCorrection];

    internal static int BlockCount(int version, QrErrorCorrection errorCorrection) =>
        BlockCountTable[version - 1, (int)errorCorrection];

    // ---- Bit assembly --------------------------------------------------------------------------------

    private sealed class BitBuffer(int expectedBits)
    {
        private readonly List<bool> bits = new(expectedBits);

        public int Length => bits.Count;

        public void Append(int value, int count)
        {
            for (var index = count - 1; index >= 0; index--) bits.Add(GetBit(value, index));
        }

        public byte[] ToBytes()
        {
            if (bits.Count % 8 != 0)
                throw new InvalidOperationException($"{bits.Count} bits do not fill whole codewords.");

            var bytes = new byte[bits.Count / 8];
            for (var index = 0; index < bits.Count; index++)
                if (bits[index]) bytes[index >> 3] |= (byte)(1 << (7 - (index & 7)));

            return bytes;
        }
    }
}
