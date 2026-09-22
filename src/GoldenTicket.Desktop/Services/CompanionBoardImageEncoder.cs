using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenTicket.CompanionHost;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Manifest;

namespace GoldenTicket.Desktop.Services;

/// <summary>Encodes only the accepted camera crop, never desktop UI or private overlays.</summary>
internal static class CompanionBoardImageEncoder
{
    internal const int Width = 960;
    internal const int Height = 600;
    internal const int MaximumJpegBytes = 1024 * 1024;

    internal sealed record Preview(byte[] Jpeg, IReadOnlyList<CompanionMapCity> Cities);

    internal static Preview Encode(BitmapSource frozenBoard, BoardManifest manifest)
    {
        if (!frozenBoard.IsFrozen)
            throw new ArgumentException("The board frame must be frozen before leaving the UI thread.", nameof(frozenBoard));
        var scaled = new TransformedBitmap(frozenBoard,
            new ScaleTransform((double)Width / frozenBoard.PixelWidth, (double)Height / frozenBoard.PixelHeight));
        scaled.Freeze();
        // Refine every public city against the same frozen image that is encoded below.
        // No ticket selection or private laptop overlay is consulted or rendered here.
        var cities = DestinationBoardOverlay.BuildAllCities(manifest);
        BitmapSource alignment = scaled;
        if (alignment.Format != PixelFormats.Bgra32)
        {
            alignment = new FormatConvertedBitmap(alignment, PixelFormats.Bgra32, null, 0);
            alignment.Freeze();
        }
        DestinationBoardOverlay.AlignToPreview(alignment, cities);
        var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
        encoder.Frames.Add(BitmapFrame.Create(scaled));
        using var output = new MemoryStream();
        encoder.Save(output);
        if (output.Length > MaximumJpegBytes)
            throw new InvalidOperationException("The companion board image exceeded its size limit.");
        return new(output.ToArray(), cities.Select(city => new CompanionMapCity(city.CityId.Value,
            city.CityName, city.CenterX, city.CenterY)).ToArray());
    }
}
