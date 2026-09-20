using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class OrientedTrainColorTests
{
    private const int Width = 320, Height = 200;
    private const string DuluthWinnipeg = "duluth--winnipeg";

    [Theory]
    [InlineData(MarkerColor.Black, 45, 0)]
    [InlineData(MarkerColor.Blue, -45, 0)]
    [InlineData(MarkerColor.Red, 35, 0)]
    [InlineData(MarkerColor.Green, -35, 0)]
    [InlineData(MarkerColor.Yellow, 55, 0)]
    [InlineData(MarkerColor.Black, 35, 1)]
    [InlineData(MarkerColor.Black, -35, -1)]
    public void Image_fitted_diagonal_body_recovers_the_same_color_despite_background_in_the_ml_box(
        MarkerColor color, double degrees, double boxShift)
    {
        var train = new Train(160, 100, degrees, color);
        var pixels = Background(Width, Height);
        Paint(pixels, Width, Height, train);
        var frame = CameraFrame.CopyFromBgra32(Width, Height, pixels);
        var original = Detection(train, Width, Height, boxShift);
        var fitted = Assert.Single(TrainOutlineFitter.Refine(frame, [original], TestContext.Current.CancellationToken));

        Assert.NotNull(fitted.OrientedOutline);
        Assert.Same(original.Outline, fitted.Outline);
        Assert.Equal(original.Confidence, fitted.Confidence);
        Assert.Null(RoutePlacementVerifier.ReadCandidateColor(frame, fitted with { OrientedOutline = null }));
        Assert.Equal(color, RoutePlacementVerifier.ReadCandidateColor(frame, fitted));
    }

    [Fact]
    public void A_favorable_fitted_region_cannot_override_real_competing_colors()
    {
        foreach (var competing in new[] { MarkerColor.Red, MarkerColor.Blue })
        {
            var pixels = Background(Width, Height);
            for (var y = 76; y <= 124; y++)
            for (var x = 136; x <= 184; x++)
                SetPixel(pixels, Width, x, y, x - 160 + y - 100 >= 0 ? MarkerColor.Black : competing);
            var frame = CameraFrame.CopyFromBgra32(Width, Height, pixels);
            var candidate = Box(160, 100, 48, 48, Width, Height);
            // This valid diagonal rectangle's entire central ellipse is on the black side.
            // The original box still contains substantial evidence of another physical color.
            var favorable = Rectangle(new(165, 105, -45, MarkerColor.Black, 40, 10), Width, Height);
            Assert.Null(RoutePlacementVerifier.ReadCandidateColor(frame, candidate));
            Assert.Null(RoutePlacementVerifier.ReadCandidateColor(frame,
                candidate with { OrientedOutline = favorable }));
        }
    }

    [Fact]
    public void A_fitted_region_cannot_change_the_original_winning_color()
    {
        var pixels = Background(Width, Height);
        var black = new Train(160, 100, 45, MarkerColor.Black);
        Paint(pixels, Width, Height, black);
        // A separate red patch lies inside a valid fitted rectangle. The original
        // central samples retain a clear black lead, but the proposed region reads red.
        Paint(pixels, Width, Height, new(150, 90, -45, MarkerColor.Red, 24, 8));
        var frame = CameraFrame.CopyFromBgra32(Width, Height, pixels);
        var candidate = Detection(black, Width, Height) with
        {
            OrientedOutline = Rectangle(new(150, 90, -45, MarkerColor.Red, 44, 12), Width, Height)
        };

        Assert.Null(RoutePlacementVerifier.ReadCandidateColor(frame, candidate with { OrientedOutline = null }));
        Assert.Null(RoutePlacementVerifier.ReadCandidateColor(frame, candidate));
    }

    [Fact]
    public void Neutral_or_insufficient_original_evidence_cannot_be_rescued_by_a_fitted_region()
    {
        var train = new Train(160, 100, -45, MarkerColor.Black);
        foreach (var breadth in new[] { 0d, 8d })
        {
            var pixels = Background(Width, Height);
            if (breadth > 0) Paint(pixels, Width, Height, train with { Breadth = breadth });
            var frame = CameraFrame.CopyFromBgra32(Width, Height, pixels);
            // The narrower continuous stripe covers the fitted central ellipse but
            // supplies too little original evidence to establish the required lead.
            var candidate = Detection(train, Width, Height) with
            {
                OrientedOutline = Rectangle(train, Width, Height)
            };
            Assert.Null(RoutePlacementVerifier.ReadCandidateColor(frame, candidate));
        }
    }

    [Fact]
    public void Invalid_or_out_of_box_fitted_geometry_cannot_rescue_an_unknown_color()
    {
        var train = new Train(160, 100, 45, MarkerColor.Black);
        var pixels = Background(Width, Height);
        Paint(pixels, Width, Height, train);
        var frame = CameraFrame.CopyFromBgra32(Width, Height, pixels);
        var original = Detection(train, Width, Height);
        Assert.Null(RoutePlacementVerifier.ReadCandidateColor(frame, original));
        var valid = Assert.Single(TrainOutlineFitter.Refine(frame, [original], TestContext.Current.CancellationToken));
        Assert.NotNull(valid.OrientedOutline);
        Assert.Equal(MarkerColor.Black, RoutePlacementVerifier.ReadCandidateColor(frame, valid));
        var corners = valid.OrientedOutline;
        IReadOnlyList<NormalizedPoint>[] invalid =
        [
            [],
            [corners[0], corners[1], corners[2]],
            [new(double.NaN, .5), corners[1], corners[2], corners[3]],
            [corners[0], corners[0], corners[0], corners[0]],
            [corners[0], corners[2], corners[1], corners[3]],
            Rectangle(train with { Breadth = 2 }, Width, Height),
            Rectangle(train with { Length = 20, Breadth = 8 }, Width, Height),
            Rectangle(train with { X = 185 }, Width, Height),
            Rectangle(train with { Length = 180, Breadth = 40 }, Width, Height)
        ];
        foreach (var outline in invalid)
            Assert.Null(RoutePlacementVerifier.ReadCandidateColor(frame, original with { OrientedOutline = outline }));
    }

    [Fact]
    public void Four_diagonal_black_trains_confirm_but_one_red_train_still_rejects_duluth_winnipeg()
    {
        var at = DateTimeOffset.UtcNow;
        var first = RouteScene(1, at);
        var second = RouteScene(2, at.AddSeconds(1.1));
        Assert.Contains(first.Candidates, candidate => RoutePlacementVerifier.ReadCandidateColor(
            first.Frame, candidate with { OrientedOutline = null }) is null);
        var verifier = new RoutePlacementVerifier();
        Assert.Equal(RoutePlacementState.Stabilizing, verifier.Observe(first.Frame,
            first.Candidates, DuluthWinnipeg, MarkerColor.Black, 4, "claim", 1, 1).State);
        Assert.True(verifier.Observe(second.Frame, second.Candidates,
            DuluthWinnipeg, MarkerColor.Black, 4, "claim", 1, 1).Confirmed);

        var wrong = RouteScene(3, at.AddSeconds(2.2), MarkerColor.Red);
        var rejected = verifier.Observe(wrong.Frame, wrong.Candidates,
            DuluthWinnipeg, MarkerColor.Black, 4, "claim", 1, 1);
        Assert.Equal(RoutePlacementState.WrongColor, rejected.State);
        Assert.Equal(3, rejected.MatchedCount);
        Assert.Equal(0b1000, rejected.UnverifiedSlotMask);
        Assert.False(rejected.Confirmed);
    }

    private static (CameraFrame Frame, IReadOnlyList<PieceCandidate> Candidates) RouteScene(
        long sequence, DateTimeOffset at, MarkerColor lastColor = MarkerColor.Black)
    {
        const int width = 1996, height = 1248;
        var pixels = Background(width, height);
        Assert.True(ClassicUsRouteGeometry.TryGetSlots(DuluthWinnipeg, out var slots));
        var candidates = slots.Select((slot, index) =>
        {
            var train = new Train(slot.ReferenceX, slot.ReferenceY,
                Math.Atan2(slot.TangentY, slot.TangentX) * 180 / Math.PI,
                index == slots.Count - 1 ? lastColor : MarkerColor.Black);
            Paint(pixels, width, height, train);
            return Detection(train, width, height);
        }).ToArray();
        var frame = CameraFrame.CopyFromBgra32(width, height, pixels, sequence, 1, at);
        var fitted = TrainOutlineFitter.Refine(frame, candidates, TestContext.Current.CancellationToken);
        Assert.All(fitted, candidate => Assert.NotNull(candidate.OrientedOutline));
        return (frame, fitted);
    }

    private sealed record Train(double X, double Y, double Degrees, MarkerColor Color,
        double Length = 64, double Breadth = 12);

    private static PieceCandidate Detection(Train train, int width, int height, double shift = 0)
    {
        var angle = train.Degrees * Math.PI / 180;
        var extentX = train.Length * Math.Abs(Math.Cos(angle)) + train.Breadth * Math.Abs(Math.Sin(angle));
        var extentY = train.Length * Math.Abs(Math.Sin(angle)) + train.Breadth * Math.Abs(Math.Cos(angle));
        return Box(train.X + shift, train.Y + shift, extentX + 8, extentY + 8, width, height);
    }

    private static PieceCandidate Box(double x, double y, double boxWidth, double boxHeight, int width, int height) =>
        new(PieceCandidateKind.Train,
            [new((x - boxWidth / 2) / width, (y - boxHeight / 2) / height),
             new((x + boxWidth / 2) / width, (y - boxHeight / 2) / height),
             new((x + boxWidth / 2) / width, (y + boxHeight / 2) / height),
             new((x - boxWidth / 2) / width, (y + boxHeight / 2) / height)], .96);

    private static IReadOnlyList<NormalizedPoint> Rectangle(Train train, int width, int height)
    {
        var angle = train.Degrees * Math.PI / 180;
        (double U, double V)[] corners = [(-1, -1), (1, -1), (1, 1), (-1, 1)];
        return corners.Select(corner => new NormalizedPoint(
            (train.X + corner.U * train.Length / 2 * Math.Cos(angle) -
                corner.V * train.Breadth / 2 * Math.Sin(angle)) / width,
            (train.Y + corner.U * train.Length / 2 * Math.Sin(angle) +
                corner.V * train.Breadth / 2 * Math.Cos(angle)) / height)).ToArray();
    }

    private static byte[] Background(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)180);
        return pixels;
    }

    private static void Paint(byte[] pixels, int width, int height, Train train)
    {
        var angle = train.Degrees * Math.PI / 180;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var radius = (train.Length + train.Breadth) / 2;
        for (var y = Math.Max(0, (int)Math.Floor(train.Y - radius)); y <= Math.Min(height - 1, Math.Ceiling(train.Y + radius)); y++)
        for (var x = Math.Max(0, (int)Math.Floor(train.X - radius)); x <= Math.Min(width - 1, Math.Ceiling(train.X + radius)); x++)
        {
            var dx = x + .5 - train.X;
            var dy = y + .5 - train.Y;
            if (Math.Abs(dx * cos + dy * sin) <= train.Length / 2 &&
                Math.Abs(-dx * sin + dy * cos) <= train.Breadth / 2)
                SetPixel(pixels, width, x, y, train.Color);
        }
    }

    private static void SetPixel(byte[] pixels, int width, int x, int y, MarkerColor color)
    {
        var (red, green, blue) = color switch
        {
            MarkerColor.Blue => (20, 75, 195),
            MarkerColor.Red => (190, 35, 30),
            MarkerColor.Green => (20, 125, 35),
            MarkerColor.Yellow => (225, 180, 20),
            _ => (20, 20, 20)
        };
        var offset = (y * width + x) * 4;
        pixels[offset] = (byte)blue;
        pixels[offset + 1] = (byte)green;
        pixels[offset + 2] = (byte)red;
        pixels[offset + 3] = 255;
    }
}
