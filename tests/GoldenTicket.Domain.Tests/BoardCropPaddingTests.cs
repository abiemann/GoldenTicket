using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class BoardCropPaddingTests
{
    [Fact]
    public void AddsTwoPercentOfBoardExtentOnEachSide()
    {
        NormalizedPoint[] original = [new(.2, .3), new(.8, .3), new(.8, .7), new(.2, .7)];
        var result = BoardCropPadding.Expand(Frame(), original);

        Assert.False(result.LimitedByFrame);
        Assert.Equal(.188, result.Corners[0].X, 12);
        Assert.Equal(.292, result.Corners[0].Y, 12);
        Assert.Equal(.812, result.Corners[2].X, 12);
        Assert.Equal(.708, result.Corners[2].Y, 12);
        AssertContains(result.Corners, original);
    }

    [Fact]
    public void TouchingCameraEdgeStillAllowsPaddingOnFreeEdges()
    {
        NormalizedPoint[] original = [new(0, .2), new(.8, .2), new(.8, .8), new(0, .8)];
        var result = BoardCropPadding.Expand(Frame(), original);

        Assert.True(result.LimitedByFrame);
        Assert.Equal(original[0], result.Corners[0]);
        Assert.Equal(original[3], result.Corners[3]);
        Assert.True(result.Corners[1].X > original[1].X);
        Assert.True(result.Corners[2].Y > original[2].Y);
        AssertContains(result.Corners, original);
        _ = BoardRegistration.Create(Frame(), result.Corners);
    }

    [Fact]
    public void FullFrameCropDoesNotDiscardEdgesOrInventPixels()
    {
        NormalizedPoint[] original = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        var result = BoardCropPadding.Expand(Frame(), original);

        Assert.True(result.LimitedByFrame);
        Assert.Equal(original, result.Corners);
    }

    [Theory]
    [MemberData(nameof(PerspectiveCorners))]
    public void PerspectiveAndEdgeLimitedCropsRemainValidAndContainEntireOriginal(NormalizedPoint[] original)
    {
        var frame = Frame();
        var result = BoardCropPadding.Expand(frame, original);

        Assert.Equal(4, result.Corners.Count);
        Assert.All(result.Corners, point =>
        {
            Assert.InRange(point.X, 0, 1);
            Assert.InRange(point.Y, 0, 1);
        });
        AssertContains(result.Corners, original);
        _ = BoardRegistration.Create(frame, result.Corners);
    }

    public static TheoryData<NormalizedPoint[]> PerspectiveCorners => new()
    {
        new[] { new NormalizedPoint(.12, .25), new(.8, .1), new(.95, .85), new(.06, .96) },
        new[] { new NormalizedPoint(.002, .28), new(.74, .005), new(.997, .77), new(.23, .995) },
        new[] { new NormalizedPoint(0, .18), new(.97, .05), new(1, 1), new(.005, .72) },
        new[] { new NormalizedPoint(.08, 0), new(.95, .27), new(.99, .98), new(0, .65) }
    };

    [Fact]
    public void RepeatedDetectionInputsGiveSamePaddingWithoutMutatingPrediction()
    {
        NormalizedPoint[] original = [new(.1, .2), new(.9, .15), new(.86, .87), new(.15, .91)];
        var untouched = original.ToArray();

        var first = BoardCropPadding.Expand(Frame(), original);
        var second = BoardCropPadding.Expand(Frame(), original);

        Assert.Equal(untouched, original);
        Assert.Equal(first.Corners, second.Corners);
        Assert.NotSame(original, first.Corners);
    }

    [Fact]
    public void PaddingFractionIsIndependentOfSensorResolution()
    {
        NormalizedPoint[] original = [new(.2, .2), new(.8, .2), new(.8, .8), new(.2, .8)];
        var small = BoardCropPadding.Expand(Frame(), original);
        var large = BoardCropPadding.Expand(Frame(640, 400), original);

        Assert.Equal(small.Corners, large.Corners);
    }

    [Fact]
    public void InvalidOriginalCropIsRejectedInsteadOfHiddenByPadding()
    {
        NormalizedPoint[] crossed = [new(.1, .1), new(.9, .9), new(.9, .1), new(.1, .9)];
        Assert.Throws<ArgumentException>(() => BoardCropPadding.Expand(Frame(), crossed));
    }

    private static void AssertContains(IReadOnlyList<NormalizedPoint> crop, IEnumerable<NormalizedPoint> original)
    {
        for (var i = 0; i < crop.Count; i++)
        {
            var a = crop[i];
            var b = crop[(i + 1) % crop.Count];
            foreach (var point in original)
                Assert.True((b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X) >= -1e-12,
                    $"Expanded edge {i} excluded original point {point}.");
        }
    }

    private static CameraFrame Frame(int width = 320, int height = 200) =>
        CameraFrame.CopyFromBgra32(width, height, new byte[width * height * 4]);
}
