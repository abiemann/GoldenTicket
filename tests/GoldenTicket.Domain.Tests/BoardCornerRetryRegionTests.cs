using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class BoardCornerRetryRegionTests
{
    [Fact]
    public void Retry_requires_exactly_one_weak_proposal_corner_and_keeps_the_regular_confidence_threshold()
    {
        var frame = Frame();
        Assert.NotNull(BoardCornerRetryRegion.TryCreate(frame, Proposal(), .55));
        foreach (var confidence in new double[][]
        {
            [.9, .9, .9, .299], [.9, .9, .52, .52], [.9, .9, .32, .35],
            [.9, .9, .54, .31], [.9, .9, .9, .9],
            [.9, .9, .9, double.NaN], [.9, .9, .9, 1.1]
        })
            Assert.Null(BoardCornerRetryRegion.TryCreate(frame, Proposal() with { Confidences = confidence }, .55));
        Assert.Null(BoardCornerRetryRegion.TryCreate(frame,
            Proposal() with { RejectionReason = "Missing corner", Corners = [] }, .55));
        Assert.Null(BoardCornerRetryRegion.TryCreate(frame,
            Proposal() with { Confidences = [.9, .9, .9] }, .55));
        // The proposal floor follows a stricter model manifest rather than remaining fixed at .30.
        Assert.Null(BoardCornerRetryRegion.TryCreate(frame,
            Proposal() with { Confidences = [.9, .9, .9, .44] }, .70));
    }

    [Theory]
    [InlineData(.30f)]
    [InlineData(.31f)]
    [InlineData(.35f)]
    [InlineData(.38f)]
    public void Weak_model_peaks_can_propose_a_retry_but_cannot_pass_as_its_final_corners(float weakConfidence)
    {
        var frame = Frame();
        // Model peaks are float32 before Decode widens them to doubles.
        var proposal = Proposal() with { Confidences = [.9, .9, .9, weakConfidence] };
        var region = Assert.IsType<BoardCornerRetryRegion>(BoardCornerRetryRegion.TryCreate(frame, proposal, .55));
        Assert.Null(region.MapAndValidate(frame, Retry() with { Confidences = proposal.Confidences }, .55));
        Assert.Null(region.MapAndValidate(frame, Retry() with { Confidences = [.9, .9, .9, .54] }, .55));
        Assert.NotNull(region.MapAndValidate(frame, Retry(), .55));
    }

    [Fact]
    public void Retry_rejects_outside_crossed_reversed_tiny_and_near_full_frame_proposals()
    {
        var frame = Frame();
        foreach (var corners in new NormalizedPoint[][]
        {
            [new(-.01, .15), new(.8, .15), new(.8, .85), new(.2, .85)],
            [new(.2, .15), new(.8, .15), new(.2, .85), new(.8, .85)],
            [new(.8, .85), new(.2, .85), new(.2, .15), new(.8, .15)],
            [new(.45, .45), new(.55, .45), new(.55, .55), new(.45, .55)],
            [new(.01, .01), new(.99, .01), new(.99, .99), new(.01, .99)]
        })
            Assert.Null(BoardCornerRetryRegion.TryCreate(frame, Proposal() with { Corners = corners }, .55));
        Assert.Null(BoardCornerRetryRegion.TryCreate(Frame(32, 32), Proposal(), .55));
    }

    [Fact]
    public void Padded_portrait_crop_copies_exact_sensor_pixels_and_preserves_capture_clock()
    {
        var clock = new ManualFrameTimeProvider(1000);
        var pixels = new byte[201 * 401 * 4];
        for (var y = 0; y < 401; y++)
        for (var x = 0; x < 201; x++)
        {
            var index = (y * 201 + x) * 4;
            pixels[index] = (byte)x;
            pixels[index + 1] = (byte)(y % 256);
            pixels[index + 2] = (byte)((x + y) % 256);
            pixels[index + 3] = 255;
        }
        var frame = CameraFrame.CopyFromBgra32(201, 401, pixels, sequence: 42, epoch: 7, clock: clock);
        clock.Advance(TimeSpan.FromSeconds(3));
        var region = Assert.IsType<BoardCornerRetryRegion>(BoardCornerRetryRegion.TryCreate(frame, Proposal(), .55));
        Assert.Equal((36, 51, 129, 299), (region.X, region.Y, region.Width, region.Height));
        var crop = region.Crop(frame, TestContext.Current.CancellationToken);
        Assert.Equal((129, 299, 42L, 7L), (crop.Width, crop.Height, crop.Sequence, crop.Epoch));
        Assert.Equal(frame.CapturedAt, crop.CapturedAt);
        Assert.Equal(frame.MonotonicTimestamp, crop.MonotonicTimestamp);
        Assert.Equal(TimeSpan.FromSeconds(3), crop.Age);
        for (var y = 0; y < 299; y++)
            Assert.True(frame.Bgra32.Span.Slice(((51 + y) * 201 + 36) * 4, 129 * 4)
                .SequenceEqual(crop.Bgra32.Span.Slice(y * 129 * 4, 129 * 4)));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(5), crop.Age);
    }

    [Fact]
    public void Retry_maps_pixel_centers_back_to_the_original_sensor_without_half_pixel_shift()
    {
        var frame = Frame();
        var region = Assert.IsType<BoardCornerRetryRegion>(BoardCornerRetryRegion.TryCreate(frame, Proposal(), .55));
        var result = Assert.IsType<BoardCornerGeometryResult>(region.MapAndValidate(frame, Retry(), .55));
        Assert.Null(result.RejectionReason);
        Assert.Equal(.2, result.Corners[0].X, 12);
        Assert.Equal(.15, result.Corners[0].Y, 12);
        Assert.Equal(.8, result.Corners[2].X, 12);
        Assert.Equal(.85, result.Corners[2].Y, 12);
        Assert.Equal(Retry().Confidences, result.Confidences);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(0, false)]
    [InlineData(3, true)]
    [InlineData(3, false)]
    public void Retry_cannot_move_either_a_strong_or_weak_corner_beyond_two_percent_of_board_span(int index, bool horizontal)
    {
        var frame = Frame();
        var region = Assert.IsType<BoardCornerRetryRegion>(BoardCornerRetryRegion.TryCreate(frame, Proposal(), .55));
        var corners = Retry().Corners.ToArray();
        var point = corners[index];
        // Three horizontal or six vertical sensor pixels exceed the permitted 2.4 / 5.6 pixels.
        corners[index] = horizontal ? point with { X = point.X + 3d / 128 } : point with { Y = point.Y + 6d / 298 };
        Assert.Null(region.MapAndValidate(frame, Retry() with { Corners = corners }, .55));
    }

    [Fact]
    public void Retry_requires_every_corner_to_pass_the_original_confidence_threshold()
    {
        var frame = Frame();
        var region = Assert.IsType<BoardCornerRetryRegion>(BoardCornerRetryRegion.TryCreate(frame, Proposal(), .55));
        foreach (var confidence in new[] { .54, double.NaN, 1.1 })
            Assert.Null(region.MapAndValidate(frame,
                Retry() with { Confidences = [.9, .9, .9, confidence] }, .55));
        Assert.Null(region.MapAndValidate(frame, Retry() with { RejectionReason = "Rejected" }, .55));
        Assert.Null(region.MapAndValidate(frame, Retry() with { Corners = [] }, .55));
    }

    [Fact]
    public void Clipped_padding_does_not_authorize_a_corner_on_the_retry_crop_edge()
    {
        var frame = Frame();
        NormalizedPoint[] points = [new(.001, .15), new(.8, .15), new(.8, .85), new(.001, .85)];
        var region = Assert.IsType<BoardCornerRetryRegion>(
            BoardCornerRetryRegion.TryCreate(frame, Proposal() with { Corners = points }, .55));
        Assert.Equal(0, region.X);
        var unchangedCorners = points.Select(point => new NormalizedPoint(
            (point.X * 200 - region.X) / (region.Width - 1),
            (point.Y * 400 - region.Y) / (region.Height - 1))).ToArray();
        Assert.Null(region.MapAndValidate(frame, Retry() with { Corners = unchangedCorners }, .55));
    }

    [Fact]
    public void Mapping_rechecks_geometry_in_the_full_sensor_instead_of_the_enlarged_retry_crop()
    {
        var frame = Frame();
        NormalizedPoint[] points = [new(.3, .4), new(.7, .4), new(.7, .602), new(.3, .602)];
        var region = Assert.IsType<BoardCornerRetryRegion>(
            BoardCornerRetryRegion.TryCreate(frame, Proposal() with { Corners = points }, .55));
        // A slight inward shift stays inside the movement allowance but shrinks below 8% of the sensor.
        NormalizedPoint[] shifted = [new(.3, .402), new(.7, .402), new(.7, .6), new(.3, .6)];
        var retryCorners = shifted.Select(point => new NormalizedPoint(
            (point.X * 200 - region.X) / (region.Width - 1),
            (point.Y * 400 - region.Y) / (region.Height - 1))).ToArray();
        Assert.NotNull(BoardRegistration.Create(region.Crop(frame, TestContext.Current.CancellationToken), retryCorners));
        Assert.Null(region.MapAndValidate(frame, Retry() with { Corners = retryCorners }, .55));
    }

    [Fact]
    public void Retry_cannot_switch_capture_frames_or_ignore_cancellation()
    {
        var frame = Frame();
        var region = Assert.IsType<BoardCornerRetryRegion>(BoardCornerRetryRegion.TryCreate(frame, Proposal(), .55));
        var other = Frame();
        Assert.Throws<ArgumentException>(() => region.Crop(other, TestContext.Current.CancellationToken));
        Assert.Null(region.MapAndValidate(other, Retry(), .55));
        Assert.Throws<OperationCanceledException>(() => region.Crop(frame, new CancellationToken(true)));
    }

    private static CameraFrame Frame(int width = 201, int height = 401) =>
        CameraFrame.CopyFromBgra32(width, height, new byte[width * height * 4]);

    private static BoardCornerGeometryResult Proposal() => new(
        [new(.2, .15), new(.8, .15), new(.8, .85), new(.2, .85)], [.9, .9, .9, .52], null);

    private static BoardCornerGeometryResult Retry() => new(
        [new(4d / 128, 9d / 298), new(124d / 128, 9d / 298),
            new(124d / 128, 289d / 298), new(4d / 128, 289d / 298)], [.9, .9, .9, .77], null);
}
