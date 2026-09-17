using GoldenTicket.Desktop.ViewModels;

namespace GoldenTicket.Domain.Tests;

public sealed class CityDotLocatorTests
{
    private const int Width = 96;
    private const int Height = 96;

    [Fact]
    public void FindsPrintedDotCenterNearTheCalibratedPosition()
    {
        var image = Board();
        Dot(image, 53, 45);

        Assert.True(CityDotLocator.TryLocate(image, Width, Height, 48, 48, out var x, out var y));
        Assert.InRange(x, 52, 54);
        Assert.InRange(y, 44, 46);
    }

    [Fact]
    public void FindsDotEvenWithAnOrangeRouteNearby()
    {
        var image = Board();
        Dot(image, 53, 45);
        Rectangle(image, 65, 36, 90, 42, 198, 97, 48);

        Assert.True(CityDotLocator.TryLocate(image, Width, Height, 48, 48, out var x, out var y));
        Assert.InRange(x, 52, 54);
        Assert.InRange(y, 44, 46);
    }

    [Fact]
    public void ColoredTrainRouteAloneIsNotMistakenForACity()
    {
        var image = Board();
        Rectangle(image, 25, 45, 74, 50, 198, 97, 48);

        Assert.False(CityDotLocator.TryLocate(image, Width, Height, 48, 48, out var x, out var y));
        Assert.Equal(48, x);
        Assert.Equal(48, y);
    }

    [Fact]
    public void BlankAndSolidBrownFramesKeepTheReferenceCoordinate()
    {
        var blank = Board();
        Assert.False(CityDotLocator.TryLocate(blank, Width, Height, 48, 48, out var x, out var y));
        Assert.Equal(48, x);
        Assert.Equal(48, y);

        var brown = Board(110, 62, 37);
        Assert.False(CityDotLocator.TryLocate(brown, Width, Height, 48, 48, out x, out y));
        Assert.Equal(48, x);
        Assert.Equal(48, y);
    }

    private static byte[] Board(byte red = 215, byte green = 212, byte blue = 205)
    {
        var pixels = new byte[Width * Height * 4];
        Rectangle(pixels, 0, 0, Width - 1, Height - 1, red, green, blue);
        return pixels;
    }

    private static void Dot(byte[] pixels, int centerX, int centerY)
    {
        for (var y = centerY - 7; y <= centerY + 7; y++)
        for (var x = centerX - 7; x <= centerX + 7; x++)
        {
            var radiusSquared = (x - centerX) * (x - centerX) + (y - centerY) * (y - centerY);
            if (radiusSquared <= 36) Pixel(pixels, x, y, 198, 97, 48);
            else if (radiusSquared <= 49) Pixel(pixels, x, y, 67, 52, 41);
        }
    }

    private static void Rectangle(byte[] pixels, int left, int top, int right, int bottom,
        byte red, byte green, byte blue)
    {
        for (var y = top; y <= bottom; y++)
        for (var x = left; x <= right; x++) Pixel(pixels, x, y, red, green, blue);
    }

    private static void Pixel(byte[] pixels, int x, int y, byte red, byte green, byte blue)
    {
        var index = (y * Width + x) * 4;
        pixels[index] = blue;
        pixels[index + 1] = green;
        pixels[index + 2] = red;
        pixels[index + 3] = 255;
    }
}
