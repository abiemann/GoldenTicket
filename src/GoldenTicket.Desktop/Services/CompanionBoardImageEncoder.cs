using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GoldenTicket.Desktop.Services;

/// <summary>Encodes only the accepted camera crop, never desktop UI or private overlays.</summary>
internal static class CompanionBoardImageEncoder
{
    internal const int Width = 960;
    internal const int Height = 600;
    internal const int MaximumJpegBytes = 1024 * 1024;

    internal static byte[] Encode(BitmapSource frozenBoard)
    {
        if (!frozenBoard.IsFrozen)
            throw new ArgumentException("The board frame must be frozen before leaving the UI thread.", nameof(frozenBoard));
        var scaled = new TransformedBitmap(frozenBoard,
            new ScaleTransform((double)Width / frozenBoard.PixelWidth, (double)Height / frozenBoard.PixelHeight));
        scaled.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
        encoder.Frames.Add(BitmapFrame.Create(scaled));
        using var output = new MemoryStream();
        encoder.Save(output);
        if (output.Length > MaximumJpegBytes)
            throw new InvalidOperationException("The companion board image exceeded its size limit.");
        return output.ToArray();
    }
}
