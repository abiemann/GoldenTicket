namespace GoldenTicket.Desktop.ViewModels;

/// <summary>
/// Makes a small, conservative correction to a known city location in an upright board crop.
/// The orange center of a printed city is approximately circular; a train route is elongated.
/// If that distinction is unclear, callers retain their calibrated reference coordinate.
/// </summary>
public static class CityDotLocator
{
    private const int SearchRadius = 8;
    private const int SampleRadius = 11;
    private const int PatchRadius = SearchRadius + SampleRadius;
    private const int PatchWidth = PatchRadius * 2 + 1;

    private static readonly (int X, int Y)[] Core = Offsets(0, 10);
    private static readonly (int X, int Y)[] Middle = Offsets(11, 36);
    private static readonly (int X, int Y)[] Surround = Offsets(49, 121);

    /// <param name="bgra">Tightly packed, top-to-bottom BGRA32 pixels.</param>
    /// <param name="expectedX">The calibrated city center in image pixel coordinates.</param>
    /// <param name="expectedY">The calibrated city center in image pixel coordinates.</param>
    /// <remarks>
    /// Searches only eight pixels around the reference. A warm disk must fill the center and
    /// middle while the surrounding board is mostly non-warm. This rejects a solid-color
    /// preview and most colored route segments without introducing a false relocation.
    /// </remarks>
    public static bool TryLocate(ReadOnlySpan<byte> bgra, int width, int height,
        double expectedX, double expectedY, out double x, out double y)
    {
        x = expectedX;
        y = expectedY;
        if (width <= 0 || height <= 0 || !double.IsFinite(expectedX) || !double.IsFinite(expectedY) ||
            (long)width * height * 4 > bgra.Length) return false;

        var referenceX = (int)Math.Round(expectedX);
        var referenceY = (int)Math.Round(expectedY);
        if (referenceX < PatchRadius || referenceY < PatchRadius ||
            referenceX >= width - PatchRadius || referenceY >= height - PatchRadius) return false;

        Span<byte> warm = stackalloc byte[PatchWidth * PatchWidth];
        for (var row = 0; row < PatchWidth; row++)
        {
            var offset = ((referenceY + row - PatchRadius) * width + referenceX - PatchRadius) * 4;
            for (var column = 0; column < PatchWidth; column++, offset += 4)
            {
                var blue = bgra[offset];
                var green = bgra[offset + 1];
                var red = bgra[offset + 2];
                warm[row * PatchWidth + column] = IsWarm(red, green, blue) ? (byte)1 : (byte)0;
            }
        }

        var found = false;
        var bestScore = double.NegativeInfinity;
        var bestX = referenceX;
        var bestY = referenceY;
        for (var dy = -SearchRadius; dy <= SearchRadius; dy++)
        for (var dx = -SearchRadius; dx <= SearchRadius; dx++)
        {
            var center = (PatchRadius + dy) * PatchWidth + PatchRadius + dx;
            var core = Count(warm, center, Core) / (double)Core.Length;
            if (core < 0.8) continue;
            var middle = Count(warm, center, Middle) / (double)Middle.Length;
            if (middle < 0.57) continue;
            var surround = Count(warm, center, Surround) / (double)Surround.Length;
            if (surround > 0.3) continue;

            // The reference is already close. Prefer a round, isolated disk, then the least
            // movement when several adjacent pixel centers have nearly identical scores.
            var score = 2 * core + 2 * middle - 1.5 * surround -
                0.02 * Math.Sqrt(dx * dx + dy * dy);
            if (score <= bestScore) continue;
            found = true;
            bestScore = score;
            bestX = referenceX + dx;
            bestY = referenceY + dy;
        }

        if (!found) return false;
        x = bestX;
        y = bestY;
        return true;
    }

    private static bool IsWarm(byte red, byte green, byte blue) =>
        red > 75 && red * 100 > green * 118 && red * 100 > blue * 125 && red - green > 18;

    private static int Count(ReadOnlySpan<byte> warm, int center, (int X, int Y)[] offsets)
    {
        var count = 0;
        foreach (var (dx, dy) in offsets) count += warm[center + dy * PatchWidth + dx];
        return count;
    }

    private static (int X, int Y)[] Offsets(int minimumSquared, int maximumSquared)
    {
        var offsets = new List<(int X, int Y)>();
        for (var dy = -SampleRadius; dy <= SampleRadius; dy++)
        for (var dx = -SampleRadius; dx <= SampleRadius; dx++)
        {
            var radiusSquared = dx * dx + dy * dy;
            if (radiusSquared >= minimumSquared && radiusSquared <= maximumSquared)
                offsets.Add((dx, dy));
        }
        return [.. offsets];
    }
}
