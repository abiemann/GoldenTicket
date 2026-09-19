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
    public void Canonical_artwork_recovers_Boston_mapping_after_a_low_similarity_crop_jump()
    {
        var source = SensorFrame();
        var shifted = TrueCorners.Select(point =>
            new NormalizedPoint(point.X + .007, point.Y - .006)).ToArray();
        var initial = BoardRegistration.Create(source, shifted);
        var token = TestContext.Current.CancellationToken;
        // This offset still fits the one-percent correction budget, but starts below
        // the old early-rejection gate: local fine refinement alone never ran.
        var similarity = (double)typeof(BoardPhotoAlignmentReference)
            .GetMethod("Similarity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(ClassicUsBoardAlignment.Reference, [source, initial, token])!;
        Assert.InRange(similarity, 0, .549999);

        var recovered = Assert.IsType<BoardRegistration>(
            ClassicUsBoardAlignment.Reference.TryRefine(source, initial, token));

        Assert.InRange(MaximumCornerError(recovered), 0, .0012);
        foreach (var (before, after) in initial.Corners.Zip(recovered.Corners))
        {
            Assert.InRange(Math.Abs(before.X - after.X), 0, .00801);
            Assert.InRange(Math.Abs(before.Y - after.Y), 0, .00801);
        }
        Assert.Equal(initial.CameraEpoch, recovered.CameraEpoch);
        Assert.Equal(initial.SensorWidth, recovered.SensorWidth);
        Assert.Equal(initial.SensorHeight, recovered.SensorHeight);
        var expected = BoardRegistration.Create(source, TrueCorners);
        Assert.True(ClassicUsRouteGeometry.TryGetSlots("boston--new-york--a", out var slots));
        Assert.All(slots, slot =>
        {
            var target = expected.MapToSensor(slot.X, slot.Y);
            var actual = recovered.MapToSensor(slot.X, slot.Y);
            Assert.InRange(Math.Abs(target.X - actual.X), 0, .0012);
            Assert.InRange(Math.Abs(target.Y - actual.Y), 0, .0012);
        });
    }

    [Fact]
    public void Current_artwork_prefers_a_freshly_refined_previous_crop_over_a_worse_new_proposal()
    {
        var source = SensorFrame();
        var previous = BoardRegistration.Create(source, ShiftedCorners(.003, -.002));
        var proposed = BoardRegistration.Create(source, ShiftedCorners(.0115, 0));
        var token = TestContext.Current.CancellationToken;
        var proposedOnly = Assert.IsType<BoardRegistration>(
            ClassicUsBoardAlignment.Reference.TryRefine(source, proposed, token));
        Assert.True(MaximumCornerError(proposedOnly) > .002);

        var recovered = Assert.IsType<BoardRegistration>(
            ClassicUsBoardAlignment.Reference.TryRefine(source, proposed, previous, token));

        Assert.NotSame(previous, recovered); // The old crop itself also needed fresh refinement.
        Assert.InRange(MaximumCornerError(recovered), 0, .0012);
        Assert.True(recovered.Matches(source));
    }

    [Fact]
    public void A_moved_board_uses_the_new_crop_instead_of_retaining_old_geometry()
    {
        var previousFrame = SensorFrame();
        var previous = BoardRegistration.Create(previousFrame, TrueCorners);
        const double movement = .018;
        var movedFrame = SensorFrame(dx: movement);
        var newCorners = ShiftedCorners(movement, 0);
        var proposed = BoardRegistration.Create(movedFrame, newCorners);

        var recovered = Assert.IsType<BoardRegistration>(ClassicUsBoardAlignment.Reference.TryRefine(
            movedFrame, proposed, previous, TestContext.Current.CancellationToken));

        foreach (var (expected, actual) in newCorners.Zip(recovered.Corners))
        {
            Assert.InRange(Math.Abs(expected.X - actual.X), 0, .0012);
            Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, .0012);
        }
        Assert.True(recovered.Corners[0].X - previous.Corners[0].X > .016);
    }

    [Theory]
    [InlineData(2, 960, 600)]
    [InlineData(1, 1200, 750)]
    public void A_previous_crop_from_another_camera_identity_cannot_override_the_current_proposal(
        long epoch, int width, int height)
    {
        var previousFrame = SensorFrame();
        var previous = BoardRegistration.Create(previousFrame, TrueCorners);
        var current = SensorFrame(epoch: epoch, width: width, height: height);
        var proposed = BoardRegistration.Create(current, ShiftedCorners(.0115, 0));
        var token = TestContext.Current.CancellationToken;
        var proposedOnly = Assert.IsType<BoardRegistration>(
            ClassicUsBoardAlignment.Reference.TryRefine(current, proposed, token));

        var actual = Assert.IsType<BoardRegistration>(
            ClassicUsBoardAlignment.Reference.TryRefine(current, proposed, previous, token));

        Assert.False(previous.Matches(current));
        Assert.Equal(proposedOnly.Corners, actual.Corners);
        Assert.True(MaximumCornerError(actual) > .002);
        Assert.True(actual.Matches(current));
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

    private static NormalizedPoint[] ShiftedCorners(double dx, double dy) =>
        TrueCorners.Select(point => new NormalizedPoint(point.X + dx, point.Y + dy)).ToArray();

    private static CameraFrame SensorFrame(double dx = 0, long epoch = 1, int width = 960, int height = 600)
    {
        var reference = ClassicUsBoardAlignment.ReferenceFrame;
        var artwork = reference.Bgra32.Span;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var u = ((double)x / (width - 1) - .1 - dx) / .8;
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
        return CameraFrame.CopyFromBgra32(width, height, pixels, epoch: epoch);
    }
}
