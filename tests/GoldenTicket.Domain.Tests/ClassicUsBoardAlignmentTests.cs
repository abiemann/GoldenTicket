using System.Security.Cryptography;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class ClassicUsBoardAlignmentTests
{
    private static readonly NormalizedPoint[] TrueCorners =
        [new(.1, .1), new(.9, .1), new(.9, .9), new(.1, .9)];

    [Fact]
    public void Embedded_artwork_has_the_reviewed_dimensions_bytes_and_opaque_grayscale_pixels()
    {
        var frame = ClassicUsBoardAlignment.ReferenceFrame;
        Assert.Equal(320, frame.Width);
        Assert.Equal(200, frame.Height);
        var pixels = frame.Bgra32.Span;
        var gray = new byte[320 * 200];
        for (var index = 0; index < gray.Length; index++)
        {
            gray[index] = pixels[index * 4];
            Assert.Equal(gray[index], pixels[index * 4 + 1]);
            Assert.Equal(gray[index], pixels[index * 4 + 2]);
            Assert.Equal(255, pixels[index * 4 + 3]);
        }
        Assert.Equal("0b8ab46ff79d5ea6ddc9aef338c340e394a4bca498bf6a797a4740c909baccec",
            Convert.ToHexStringLower(SHA256.HashData(gray)));
        Assert.Same(ClassicUsBoardAlignment.Reference, ClassicUsBoardAlignment.Reference);
    }

    [Fact]
    public void Canonical_artwork_corrects_a_bias_already_present_in_the_saved_photo()
    {
        var source = SensorFrame();
        var biasedCorners = TrueCorners.Select(point =>
            new NormalizedPoint(point.X + .004, point.Y - .002)).ToArray();
        var initial = BoardRegistration.Create(source, biasedCorners);
        var savedReference = new BoardPhotoAlignmentReference(initial.Rectify(source, 640, 400));

        var savedAligned = savedReference.Refine(source, initial, TestContext.Current.CancellationToken);
        var canonical = Assert.IsType<BoardRegistration>(ClassicUsBoardAlignment.Reference.TryRefine(
            source, savedAligned, TestContext.Current.CancellationToken));

        Assert.True(MaximumCornerError(savedAligned) > .003);
        Assert.NotSame(savedAligned, canonical);
        Assert.InRange(MaximumCornerError(canonical), 0, .0012);
        var expected = BoardRegistration.Create(source, TrueCorners);
        Assert.True(ClassicUsRouteGeometry.TryGetSlots("los-angeles--san-francisco--a", out var slots));
        Assert.All(slots, slot =>
        {
            var target = expected.MapToSensor(slot.X, slot.Y);
            var actual = canonical.MapToSensor(slot.X, slot.Y);
            Assert.InRange(Math.Abs(target.X - actual.X), 0, .0012);
            Assert.InRange(Math.Abs(target.Y - actual.Y), 0, .0012);
        });
    }

    [Fact]
    public void Canonical_artwork_does_not_steer_a_blank_image_and_honors_cancellation()
    {
        var blank = CameraFrame.CopyFromBgra32(960, 600, new byte[960 * 600 * 4]);
        var initial = BoardRegistration.Create(blank, TrueCorners);
        Assert.Null(ClassicUsBoardAlignment.Reference.TryRefine(
            blank, initial, TestContext.Current.CancellationToken));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ClassicUsBoardAlignment.Reference.TryRefine(blank, initial, cancelled.Token));
    }

    private static double MaximumCornerError(BoardRegistration registration) =>
        TrueCorners.Zip(registration.Corners).Max(pair =>
            Math.Max(Math.Abs(pair.First.X - pair.Second.X), Math.Abs(pair.First.Y - pair.Second.Y)));

    private static CameraFrame SensorFrame()
    {
        const int width = 960, height = 600;
        var reference = ClassicUsBoardAlignment.ReferenceFrame;
        var artwork = reference.Bgra32.Span;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var u = ((double)x / (width - 1) - .1) / .8;
            var v = ((double)y / (height - 1) - .1) / .8;
            var gray = 24d;
            if (u is >= 0 and <= 1 && v is >= 0 and <= 1)
            {
                var rx = u * (reference.Width - 1);
                var ry = v * (reference.Height - 1);
                var left = (int)rx;
                var top = (int)ry;
                var right = Math.Min(left + 1, reference.Width - 1);
                var bottom = Math.Min(top + 1, reference.Height - 1);
                var wx = rx - left;
                var wy = ry - top;
                var upper = artwork[(top * reference.Width + left) * 4] * (1 - wx) +
                    artwork[(top * reference.Width + right) * 4] * wx;
                var lower = artwork[(bottom * reference.Width + left) * 4] * (1 - wx) +
                    artwork[(bottom * reference.Width + right) * 4] * wx;
                gray = upper * (1 - wy) + lower * wy;
            }
            var offset = (y * width + x) * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = (byte)Math.Round(gray);
            pixels[offset + 3] = 255;
        }
        return CameraFrame.CopyFromBgra32(width, height, pixels);
    }
}
