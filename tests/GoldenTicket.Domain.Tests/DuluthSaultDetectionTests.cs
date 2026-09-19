using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class DuluthSaultDetectionTests
{
    private const string Sault = "duluth--sault-st-marie";
    private const string Toronto = "duluth--toronto";
    private static readonly (string RouteId, int TrainCount)[] LegalRoutes = [(Sault, 3), (Toronto, 6)];
    private static readonly Train[] OriginalCenters =
    [
        new(1184, 371), new(1253, 340), new(1316, 307)
    ];
    // Actual centers from the supplied board after canonical alignment. All three
    // detections were black at >95% confidence; the first two approached the old lane edge.
    private static readonly Train[] ObservedCenters =
    [
        new(1188.15, 352.19), new(1250.91, 325.39), new(1313.43, 301.97)
    ];

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(false, -2, -2)]
    [InlineData(false, -2, 2)]
    [InlineData(false, 2, -2)]
    [InlineData(false, 2, 2)]
    [InlineData(true, 0, 0)]
    [InlineData(true, -2, -2)]
    [InlineData(true, -2, 2)]
    [InlineData(true, 2, -2)]
    [InlineData(true, 2, 2)]
    public void Original_and_observed_trains_survive_small_drift_and_confirm_before_payment(
        bool observed, int dx, int dy)
    {
        var trains = observed ? ObservedCenters : OriginalCenters;
        var at = DateTimeOffset.UtcNow;
        var first = Scene(1, at, Offset(trains, dx, dy));
        var second = Scene(2, at.AddSeconds(1.1), Offset(trains, -dx, -dy));
        var placement = new RoutePlacementVerifier();
        var detector = new BoardFirstRouteDetector();
        var inventory = ClaimedInventory();
        var pending = PendingInventory();

        var initial = placement.Observe(first.Frame, first.Candidates, Sault,
            MarkerColor.Black, 3, "human-claim", 1, 1);
        Assert.Equal(RoutePlacementState.Stabilizing, initial.State);
        Assert.Equal(3, initial.MatchedCount);
        Assert.Null(detector.Observe(first.Frame, first.Candidates, LegalRoutes,
            MarkerColor.Black, "human-turn", 1, 1));
        Assert.Equal(BoardInventoryState.Stabilizing,
            inventory.Observe(first.Frame, first.Candidates, 1, 1).State);
        Assert.Equal(BoardInventoryState.Stabilizing,
            pending.Observe(first.Frame, first.Candidates, 1, 1).State);

        var accepted = placement.Observe(second.Frame, second.Candidates, Sault,
            MarkerColor.Black, 3, "human-claim", 1, 1);
        Assert.True(accepted.Confirmed);
        Assert.Equal(3, accepted.MatchedCount);
        Assert.Equal(0, accepted.UnverifiedSlotMask);
        Assert.Equal(Sault, detector.Observe(second.Frame, second.Candidates, LegalRoutes,
            MarkerColor.Black, "human-turn", 1, 1));
        var committed = inventory.Observe(second.Frame, second.Candidates, 1, 1);
        Assert.True(committed.Confirmed);
        Assert.Equal(3, committed.ConfirmedByColor[MarkerColor.Black]);
        var beforePayment = pending.Observe(second.Frame, second.Candidates, 1, 1);
        Assert.True(beforePayment.Confirmed);
        Assert.Equal(0b111, beforePayment.PendingSlotMask);
        Assert.Equal(3, beforePayment.ConfirmedByColor[MarkerColor.Black]);
    }

    [Fact]
    public void A_missing_or_wrong_color_train_never_confirms_or_offers_payment()
    {
        AssertRejected(ObservedCenters[1..], RoutePlacementState.Incomplete,
            BoardInventoryState.MissingTrains, 0b001);
        AssertRejected([ObservedCenters[0], ObservedCenters[1], ObservedCenters[2] with { Color = MarkerColor.Red }],
            RoutePlacementState.WrongColor, BoardInventoryState.WrongColor, 0b100);
    }

    [Fact]
    public void A_black_train_on_the_neighboring_pink_route_cannot_fill_the_missing_first_space()
    {
        // The first Toronto space is only 14px below the original Sault coordinate.
        // Keep both other Sault trains present so rejection depends on this shared-city edge.
        AssertRejected([new(1182, 385), ObservedCenters[1], ObservedCenters[2]],
            RoutePlacementState.Incomplete, BoardInventoryState.MissingTrains, 0b001);
        // Drift toward Sault must not turn that neighboring train into evidence either.
        AssertRejected([new(1180, 383), ObservedCenters[1], ObservedCenters[2]],
            RoutePlacementState.Incomplete, BoardInventoryState.MissingTrains, 0b001);
    }

    private static void AssertRejected(Train[] trains, RoutePlacementState placementState,
        BoardInventoryState inventoryState, int unverifiedMask)
    {
        var placement = new RoutePlacementVerifier();
        var detector = new BoardFirstRouteDetector();
        var inventory = ClaimedInventory();
        var pending = PendingInventory();
        var at = DateTimeOffset.UtcNow;
        for (var sequence = 1; sequence <= 2; sequence++)
        {
            var scene = Scene(sequence, at.AddSeconds((sequence - 1) * 1.1), trains);
            var observation = placement.Observe(scene.Frame, scene.Candidates, Sault,
                MarkerColor.Black, 3, "human-claim", 1, 1);
            Assert.Equal(placementState, observation.State);
            Assert.Equal(2, observation.MatchedCount);
            Assert.Equal(unverifiedMask, observation.UnverifiedSlotMask);
            Assert.False(observation.Confirmed);
            Assert.Null(detector.Observe(scene.Frame, scene.Candidates, LegalRoutes,
                MarkerColor.Black, "human-turn", 1, 1));
            Assert.Null(detector.ProposedRouteId);
            Assert.Equal(inventoryState, inventory.Observe(scene.Frame, scene.Candidates, 1, 1).State);
            Assert.Equal(inventoryState, pending.Observe(scene.Frame, scene.Candidates, 1, 1).State);
        }
    }

    private static BoardInventoryVerifier ClaimedInventory() => new([new(Sault, MarkerColor.Black, 3)]);

    private static BoardInventoryVerifier PendingInventory() => new([], new(Sault, MarkerColor.Black, 3),
        pendingSlotMask: 0b111, verifyClaimedRouteColors: false);

    private static Train[] Offset(Train[] trains, double dx, double dy) =>
        trains.Select(train => train with { X = train.X + dx, Y = train.Y + dy }).ToArray();

    private sealed record Train(double X, double Y, MarkerColor Color = MarkerColor.Black);

    private static (CameraFrame Frame, PieceCandidate[] Candidates) Scene(long sequence,
        DateTimeOffset capturedAt, Train[] trains)
    {
        const int width = 1996, height = 1248;
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)180);
        var candidates = new List<PieceCandidate>();
        foreach (var train in trains)
        {
            const int halfWidth = 11, halfHeight = 7;
            // Paint the full detected body; preserve subpixel model centers in the outline.
            for (var y = (int)Math.Floor(train.Y - halfHeight); y <= Math.Ceiling(train.Y + halfHeight); y++)
            for (var x = (int)Math.Floor(train.X - halfWidth); x <= Math.Ceiling(train.X + halfWidth); x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = train.Color == MarkerColor.Red ? (byte)30 : (byte)20;
                pixels[offset + 1] = train.Color == MarkerColor.Red ? (byte)35 : (byte)20;
                pixels[offset + 2] = train.Color == MarkerColor.Red ? (byte)190 : (byte)20;
                pixels[offset + 3] = 255;
            }
            candidates.Add(new(PieceCandidateKind.Train,
                [new((train.X - halfWidth) / width, (train.Y - halfHeight) / height),
                 new((train.X + halfWidth) / width, (train.Y - halfHeight) / height),
                 new((train.X + halfWidth) / width, (train.Y + halfHeight) / height),
                 new((train.X - halfWidth) / width, (train.Y + halfHeight) / height)], .96));
        }
        return (CameraFrame.CopyFromBgra32(width, height, pixels, sequence, 1, capturedAt), candidates.ToArray());
    }
}
