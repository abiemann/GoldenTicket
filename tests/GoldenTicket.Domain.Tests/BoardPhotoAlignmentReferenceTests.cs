using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class BoardPhotoAlignmentReferenceTests
{
    private static readonly NormalizedPoint[] TrueCorners =
        [new(.1, .1), new(.9, .1), new(.9, .9), new(.1, .9)];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovers_small_translation_and_corner_scale_errors_from_artwork(bool lightingChanged)
    {
        var original = Pattern();
        var reference = new BoardPhotoAlignmentReference(
            BoardRegistration.Create(original, TrueCorners).Rectify(original, 640, 400));
        var live = lightingChanged ? Pattern(brighter: true, movedPieces: true) : original;
        NormalizedPoint[] displaced =
            [new(.103, .099), new(.901, .102), new(.903, .898), new(.101, .896)];
        var initial = BoardRegistration.Create(live, displaced);

        var refined = reference.Refine(live, initial, token: TestContext.Current.CancellationToken);

        Assert.NotSame(initial, refined);
        foreach (var (expected, actual) in TrueCorners.Zip(refined.Corners))
        {
            Assert.InRange(Math.Abs(expected.X - actual.X), 0, .0012);
            Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, .0012);
        }
        Assert.True(refined.Matches(live));
    }

    [Fact]
    public void Blank_unrelated_or_inverted_photos_cannot_change_the_registration()
    {
        var source = Pattern();
        var initial = BoardRegistration.Create(source, TrueCorners);
        var blank = CameraFrame.CopyFromBgra32(320, 200, new byte[320 * 200 * 4]);
        var unrelated = initial.Rectify(Pattern(seed: 9), 640, 400);
        var photo = initial.Rectify(source, 640, 400);
        var reversed = photo.Bgra32.ToArray();
        for (var first = 0; first < reversed.Length / 2; first += 4)
        for (var channel = 0; channel < 4; channel++)
        {
            var last = reversed.Length - 4 - first;
            (reversed[first + channel], reversed[last + channel]) =
                (reversed[last + channel], reversed[first + channel]);
        }
        var inverted = CameraFrame.CopyFromBgra32(photo.Width, photo.Height, reversed);

        foreach (var reference in new[] { blank, unrelated, inverted })
            Assert.Same(initial, new BoardPhotoAlignmentReference(reference).Refine(source, initial, token: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Already_aligned_photo_does_not_change_corners_for_a_negligible_improvement()
    {
        var source = Pattern();
        var initial = BoardRegistration.Create(source, TrueCorners);
        var reference = new BoardPhotoAlignmentReference(initial.Rectify(source, 640, 400));

        Assert.Same(initial, reference.Refine(source, initial, token: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Correction_is_bounded_and_never_changes_camera_identity()
    {
        var source = Pattern();
        var reference = new BoardPhotoAlignmentReference(
            BoardRegistration.Create(source, TrueCorners).Rectify(source, 640, 400));
        var shifted = TrueCorners.Select(point => new NormalizedPoint(point.X + .015, point.Y)).ToArray();
        var initial = BoardRegistration.Create(source, shifted);

        var refined = reference.Refine(source, initial, token: TestContext.Current.CancellationToken);

        foreach (var (before, after) in initial.Corners.Zip(refined.Corners))
        {
            Assert.InRange(Math.Abs(before.X - after.X), 0, .00801);
            Assert.InRange(Math.Abs(before.Y - after.Y), 0, .00801);
        }
        Assert.Equal(initial.CameraEpoch, refined.CameraEpoch);
        Assert.Equal(initial.SensorWidth, refined.SensorWidth);
        Assert.Equal(initial.SensorHeight, refined.SensorHeight);
    }

    [Fact]
    public void Cancellation_and_camera_restarts_do_not_return_an_alignment()
    {
        var source = Pattern();
        var initial = BoardRegistration.Create(source, TrueCorners);
        var reference = new BoardPhotoAlignmentReference(initial.Rectify(source, 640, 400));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => reference.Refine(source, initial, cancelled.Token));
        var restarted = CameraFrame.CopyFromBgra32(source.Width, source.Height, source.Bgra32.Span, epoch: 2);
        Assert.Throws<InvalidOperationException>(() => reference.Refine(restarted, initial, token: TestContext.Current.CancellationToken));
    }

    private static CameraFrame Pattern(int seed = 1, bool brighter = false, bool movedPieces = false)
    {
        const int width = 960, height = 600;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var u = ((double)x / (width - 1) - .1) / .8;
            var v = ((double)y / (height - 1) - .1) / .8;
            var value = 120 + 32 * Math.Sin(u * (119 + seed * 6)) +
                34 * Math.Cos(v * (130 + seed * 7) + u * 23) +
                24 * Math.Sin(v * 87 - u * (133 + seed * 6));
            if (brighter) value = value * (.72 + u * .15) + 24;
            if (movedPieces && ((u is > .25 and < .28 && v is > .3 and < .34) ||
                (u is > .7 and < .74 && v is > .65 and < .68))) value = 30;
            var offset = (y * width + x) * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = (byte)Math.Clamp(value, 0, 255);
            pixels[offset + 3] = 255;
        }
        return CameraFrame.CopyFromBgra32(width, height, pixels);
    }
}
