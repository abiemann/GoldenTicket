using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class TrainOutlineFitterTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(25, 1)]
    [InlineData(-30, 2)]
    [InlineData(45, 3)]
    [InlineData(-45, 4)]
    [InlineData(70, 0)]
    [InlineData(90, 1)]
    [InlineData(135, 2)]
    public void Fits_visible_train_at_its_pixel_orientation(double degrees, int color)
    {
        var (frame, candidate) = Train(degrees, color);
        var actual = Assert.Single(TrainOutlineFitter.Refine(frame, [candidate], TestContext.Current.CancellationToken));
        Assert.NotNull(actual.OrientedOutline);
        Assert.Same(candidate.Outline, actual.Outline);
        Assert.Equal(candidate.Confidence, actual.Confidence);
        Assert.Equal(candidate.Kind, actual.Kind);
        var corners = actual.OrientedOutline;
        var angle = Math.Atan2((corners[1].Y - corners[0].Y) * frame.Height,
            (corners[1].X - corners[0].X) * frame.Width) * 180 / Math.PI;
        // Compare undirected axes, so a train turned 180 degrees has the same rectangle.
        var error = Math.Abs(((angle - degrees + 270) % 180) - 90);
        Assert.InRange(error, 0, 4);
        Assert.All(corners, point =>
        {
            Assert.InRange(point.X, 0, 1);
            Assert.InRange(point.Y, 0, 1);
        });
        var length = Distance(corners[0], corners[1], frame);
        var width = Distance(corners[1], corners[2], frame);
        Assert.InRange(length, 60, 70);
        Assert.InRange(width, 18, 27);
    }

    [Fact]
    public void Opposite_diagonals_with_identical_ml_boxes_use_the_image()
    {
        var (positive, positiveCandidate) = Train(45, 2);
        var (negative, negativeCandidate) = Train(-45, 2);
        Assert.Equal(positiveCandidate.Outline, negativeCandidate.Outline);
        var a = Assert.Single(TrainOutlineFitter.Refine(positive, [positiveCandidate], TestContext.Current.CancellationToken)).OrientedOutline!;
        var b = Assert.Single(TrainOutlineFitter.Refine(negative, [negativeCandidate], TestContext.Current.CancellationToken)).OrientedOutline!;
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True((a[1].Y - a[0].Y) * (b[1].Y - b[0].Y) < 0);
    }

    [Fact]
    public void Does_not_change_score_markers_or_candidate_count()
    {
        var (frame, train) = Train(30, 3);
        var marker = train with { Kind = PieceCandidateKind.PlayerMarker };
        var result = TrainOutlineFitter.Refine(frame, [train, marker], TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
        Assert.NotNull(result[0].OrientedOutline);
        Assert.Same(marker, result[1]);
        Assert.Null(result[1].OrientedOutline);
    }

    [Theory]
    [InlineData(190, 190, 190)]
    [InlineData(20, 75, 150)]
    [InlineData(25, 25, 25)]
    public void Uniform_images_do_not_invent_an_angle_even_for_elongated_boxes(int r, int g, int b)
    {
        var pixels = Background(r, g, b);
        var frame = CameraFrame.CopyFromBgra32(320, 200, pixels);
        var candidate = Candidate(120, 87, 80, 26);
        Assert.Null(Assert.Single(TrainOutlineFitter.Refine(frame, [candidate], TestContext.Current.CancellationToken)).OrientedOutline);
    }

    [Fact]
    public void Round_foreground_is_ambiguous_and_keeps_the_ml_box()
    {
        var pixels = Background();
        for (var y = 75; y < 125; y++)
        for (var x = 135; x < 185; x++)
            if ((x - 160) * (x - 160) + (y - 100) * (y - 100) < 20 * 20)
                Paint(pixels, x, y, (210, 40, 35));
        var candidate = Candidate(135, 75, 50, 50);
        Assert.Null(Assert.Single(TrainOutlineFitter.Refine(
            CameraFrame.CopyFromBgra32(320, 200, pixels), [candidate], TestContext.Current.CancellationToken)).OrientedOutline);
    }

    [Fact]
    public void Tiny_and_frame_clipped_proposals_fall_back_without_moving_them()
    {
        var (frame, candidate) = Train(45, 1);
        PieceCandidate[] candidates = [Candidate(158, 98, 4, 4), Candidate(0, 80, 40, 20),
            candidate with { Outline = [new(double.NaN, .2), new(.4, .2), new(.4, .4), new(.2, .4)] }];
        var result = TrainOutlineFitter.Refine(frame, candidates, TestContext.Current.CancellationToken);
        for (var i = 0; i < candidates.Length; i++)
        {
            Assert.Same(candidates[i].Outline, result[i].Outline);
            Assert.Null(result[i].OrientedOutline);
        }
    }

    [Fact]
    public void Already_cancelled_work_does_not_return_partially_refined_candidates()
    {
        var (frame, candidate) = Train(30, 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            TrainOutlineFitter.Refine(frame, [candidate], cancellation.Token));
    }

    private static (CameraFrame Frame, PieceCandidate Candidate) Train(double degrees, int color)
    {
        (int R, int G, int B)[] colors = [(25, 25, 25), (210, 40, 35), (25, 115, 65),
            (20, 75, 150), (220, 185, 40)];
        var pixels = Background();
        var angle = degrees * Math.PI / 180;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        for (var y = 50; y < 150; y++)
        for (var x = 110; x < 210; x++)
        {
            var dx = x + .5 - 160;
            var dy = y + .5 - 100;
            var u = dx * cos + dy * sin;
            var v = -dx * sin + dy * cos;
            if (Math.Abs(u) <= 32 && Math.Abs(v) <= 10) Paint(pixels, x, y, colors[color]);
        }
        var width = 64 * Math.Abs(cos) + 20 * Math.Abs(sin) + 8;
        var height = 64 * Math.Abs(sin) + 20 * Math.Abs(cos) + 8;
        return (CameraFrame.CopyFromBgra32(320, 200, pixels),
            Candidate(160 - width / 2, 100 - height / 2, width, height));
    }

    private static PieceCandidate Candidate(double x, double y, double width, double height) => new(
        PieceCandidateKind.Train,
        Array.AsReadOnly<NormalizedPoint>([new(x / 320, y / 200), new((x + width) / 320, y / 200),
            new((x + width) / 320, (y + height) / 200), new(x / 320, (y + height) / 200)]), .86);

    private static byte[] Background(int r = 190, int g = 190, int b = 190)
    {
        var pixels = new byte[320 * 200 * 4];
        for (var y = 0; y < 200; y++)
        for (var x = 0; x < 320; x++) Paint(pixels, x, y, (r, g, b));
        return pixels;
    }

    private static void Paint(byte[] pixels, int x, int y, (int R, int G, int B) color)
    {
        var offset = (y * 320 + x) * 4;
        pixels[offset] = (byte)color.B;
        pixels[offset + 1] = (byte)color.G;
        pixels[offset + 2] = (byte)color.R;
        pixels[offset + 3] = 255;
    }

    private static double Distance(NormalizedPoint a, NormalizedPoint b, CameraFrame frame) =>
        Math.Sqrt(Math.Pow((a.X - b.X) * frame.Width, 2) + Math.Pow((a.Y - b.Y) * frame.Height, 2));
}
