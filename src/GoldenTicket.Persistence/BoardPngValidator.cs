using System.Buffers.Binary;
using System.IO.Compression;

namespace GoldenTicket.Persistence;

/// <summary>
/// Bounded structural and decompression validation of the non-interlaced RGB/RGBA8 camera PNGs
/// produced by the desktop encoder. Does not recognize trains or certify board contents.
/// </summary>
internal static class BoardPngValidator
{
    private static readonly uint[] CrcTable = CreateCrcTable();
    internal static (int Width, int Height) Validate(ReadOnlySpan<byte> png)
    {
        if (png.Length is < 45 or > CheckpointPhotoStore.MaximumPngBytes ||
            !png[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("The reference must be a complete camera PNG.");
        var offset = 8;
        var width = 0;
        var height = 0;
        var channels = 0;
        var sawData = false;
        var endedData = false;
        var sawEnd = false;
        var metadataTypes = new HashSet<string>(StringComparer.Ordinal);
        using var compressed = new MemoryStream();
        while (offset < png.Length)
        {
            if (png.Length - offset < 12) throw new InvalidDataException("A PNG chunk is truncated.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset, 4));
            if (length > png.Length - offset - 12) throw new InvalidDataException("A PNG chunk exceeds the file bounds.");
            var chunkLength = checked((int)length);
            var type = png.Slice(offset + 4, 4);
            var data = png.Slice(offset + 8, chunkLength);
            if (Crc(png.Slice(offset + 4, chunkLength + 4)) !=
                BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset + 8 + chunkLength, 4)))
                throw new InvalidDataException("The PNG chunk checksum is incorrect.");
            if (offset == 8 && !type.SequenceEqual("IHDR"u8))
                throw new InvalidDataException("The PNG header must be its first chunk.");
            if (type.SequenceEqual("IHDR"u8))
            {
                if (offset != 8 || chunkLength != 13) throw new InvalidDataException("The PNG header is invalid.");
                var w = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
                var h = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
                if (w is 0 or > CheckpointPhotoStore.MaximumDimension || h is 0 or > CheckpointPhotoStore.MaximumDimension ||
                    (long)w * h > CheckpointPhotoStore.MaximumPixels)
                    throw new InvalidDataException("The camera PNG exceeds the supported dimensions or pixel count.");
                if (data[8] != 8 || data[9] is not (2 or 6) || data[10] != 0 || data[11] != 0 || data[12] != 0)
                    throw new InvalidDataException("Encode the camera PNG as non-interlaced 8-bit RGB or RGBA.");
                (width, height, channels) = ((int)w, (int)h, data[9] == 2 ? 3 : 4);
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (endedData || sawEnd) throw new InvalidDataException("PNG image chunks must be consecutive.");
                sawData = true;
                compressed.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (chunkLength != 0 || !sawData || offset + 12 != png.Length)
                    throw new InvalidDataException("The PNG end marker or image data is invalid.");
                sawEnd = true;
            }
            else
            {
                if (sawData) endedData = true;
                // The desktop's encoder may include color-space/physical-density metadata. Reject
                // unknown critical chunks, palettes, embedded files, EXIF, text and animation.
                var name = System.Text.Encoding.ASCII.GetString(type);
                var expectedLength = name switch { "sRGB" => 1, "gAMA" => 4, "cHRM" => 32, "pHYs" => 9, _ => -1 };
                if (expectedLength < 0)
                    throw new InvalidDataException("The board PNG contains unsupported extra metadata or image chunks.");
                if (sawData || chunkLength != expectedLength || !metadataTypes.Add(name) ||
                    (name == "sRGB" && data[0] > 3) || (name == "pHYs" && data[8] > 1) ||
                    (name == "gAMA" && BinaryPrimitives.ReadUInt32BigEndian(data) == 0))
                    throw new InvalidDataException("The PNG color or density metadata is invalid.");
            }
            offset += chunkLength + 12;
        }
        if (!sawEnd || compressed.Length == 0) throw new InvalidDataException("The PNG is incomplete.");
        var zlib = compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length));
        if (zlib.Length < 6 || (zlib[0] & 15) != 8 || (zlib[0] >> 4) > 7 ||
            (zlib[1] & 32) != 0 || ((zlib[0] << 8) + zlib[1]) % 31 != 0)
            throw new InvalidDataException("The PNG compression header is invalid.");
        var expectedAdler = BinaryPrimitives.ReadUInt32BigEndian(zlib[^4..]);
        compressed.Position = 0;
        using var decoded = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        var row = new byte[checked(width * channels)];
        uint adlerA = 1, adlerB = 0;
        try
        {
            for (var y = 0; y < height; y++)
            {
                var filter = decoded.ReadByte();
                if (filter is < 0 or > 4) throw new InvalidDataException("The PNG row filter is invalid.");
                UpdateAdler((byte)filter, ref adlerA, ref adlerB);
                decoded.ReadExactly(row);
                foreach (var value in row) UpdateAdler(value, ref adlerA, ref adlerB);
            }
            if (decoded.ReadByte() != -1) throw new InvalidDataException("The PNG expands beyond its declared image dimensions.");
            if ((adlerB << 16 | adlerA) != expectedAdler)
                throw new InvalidDataException("The PNG compressed pixel checksum is incorrect.");
        }
        catch (EndOfStreamException ex) { throw new InvalidDataException("The PNG image pixels are truncated.", ex); }
        return (width, height);
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
            crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 255];
        return ~crc;
    }

    private static void UpdateAdler(byte value, ref uint a, ref uint b)
    {
        a = (a + value) % 65521;
        b = (b + a) % 65521;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xedb88320);
            table[i] = value;
        }
        return table;
    }
}
