using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class BoardOrientationReferenceTests
{
    [Fact]
    public void Only_the_upright_view_matches_an_asymmetric_board()
    {
        var upright = Pattern();
        var reference = new BoardOrientationReference(upright);
        var inverted = RotateHalfCircle(upright);

        Assert.True(reference.IsAligned(upright));
        Assert.False(reference.IsAligned(inverted));
        Assert.True(reference.Similarity(upright) > .99);
        Assert.True(reference.Similarity(inverted, turnHalfCircle: true) > .99);
    }

    [Fact]
    public void Ambiguous_or_obscured_views_cannot_select_an_orientation()
    {
        var reference = new BoardOrientationReference(Pattern());
        var blank = CameraFrame.CopyFromBgra32(BoardOrientationReference.Width,
            BoardOrientationReference.Height,
            Enumerable.Repeat((byte)120, BoardOrientationReference.Width * BoardOrientationReference.Height * 4).ToArray());

        Assert.False(reference.IsAligned(blank));
        Assert.Null(reference.ChooseOrientation([blank, blank, blank, blank]));
    }

    [Fact]
    public void A_rotated_camera_view_selects_the_canonical_board_registration()
    {
        var upright = Pattern();
        var inverted = RotateHalfCircle(upright);
        var reference = new BoardOrientationReference(upright);

        Assert.Equal(1, reference.ChooseOrientation([inverted, upright, inverted, inverted]));
    }

    [Fact]
    public void Moderate_lighting_changes_preserve_orientation_but_a_shifted_crop_does_not()
    {
        var upright = Pattern();
        var reference = new BoardOrientationReference(upright);
        var lightChanged = Transform(upright, (x, y) => (x, y), value =>
            (byte)Math.Clamp((int)Math.Round(value * .8 + 22), 0, 255));
        var shifted = Transform(upright, (x, y) => ((x + 21) % BoardOrientationReference.Width, y),
            value => value);
        var quarterTurn = QuarterTurn(upright);

        Assert.True(reference.IsAligned(lightChanged));
        Assert.False(reference.IsAligned(shifted));
        Assert.False(reference.IsAligned(quarterTurn));
    }

    private static CameraFrame Pattern()
    {
        var width = BoardOrientationReference.Width;
        var height = BoardOrientationReference.Height;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            pixels[offset] = (byte)(30 + ((x * 7 + y * 3 + x * y) % 180));
            pixels[offset + 1] = (byte)(20 + ((x * 11 + y * 13 + x * y / 3) % 190));
            pixels[offset + 2] = (byte)(40 + ((x * 17 + y * 5 + x * y / 5) % 170));
            pixels[offset + 3] = 255;
        }
        return CameraFrame.CopyFromBgra32(width, height, pixels);
    }

    private static CameraFrame RotateHalfCircle(CameraFrame source)
    {
        var pixels = new byte[source.Bgra32.Length];
        var input = source.Bgra32.Span;
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var from = (y * source.Width + x) * 4;
            var to = ((source.Height - 1 - y) * source.Width + source.Width - 1 - x) * 4;
            input.Slice(from, 4).CopyTo(pixels.AsSpan(to, 4));
        }
        return CameraFrame.CopyFromBgra32(source.Width, source.Height, pixels);
    }

    private static CameraFrame Transform(CameraFrame source, Func<int, int, (int X, int Y)> location,
        Func<byte, byte> color)
    {
        var pixels = new byte[source.Bgra32.Length];
        var input = source.Bgra32.Span;
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var (targetX, targetY) = location(x, y);
            var from = (y * source.Width + x) * 4;
            var to = (targetY * source.Width + targetX) * 4;
            for (var channel = 0; channel < 3; channel++) pixels[to + channel] = color(input[from + channel]);
            pixels[to + 3] = 255;
        }
        return CameraFrame.CopyFromBgra32(source.Width, source.Height, pixels);
    }

    private static CameraFrame QuarterTurn(CameraFrame source)
    {
        var pixels = new byte[source.Bgra32.Length];
        var input = source.Bgra32.Span;
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var sourceX = Math.Min(source.Width - 1, y * source.Width / source.Height);
            var sourceY = Math.Max(0, source.Height - 1 - x * source.Height / source.Width);
            input.Slice((sourceY * source.Width + sourceX) * 4, 4)
                .CopyTo(pixels.AsSpan((y * source.Width + x) * 4, 4));
        }
        return CameraFrame.CopyFromBgra32(source.Width, source.Height, pixels);
    }
}
