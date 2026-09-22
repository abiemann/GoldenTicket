using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class FittedTrainPositionTests
{
    private const string LaneA = "boston--new-york--a";
    private const string LaneB = "boston--new-york--b";
    private const int Width = 1920;
    private const int Height = 1200;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Recorded_boston_body_centers_fix_the_missing_train_without_changing_model_boxes(int sample)
    {
        // Three September 22 camera results: the upper raw box sits at the parallel-lane
        // boundary. These are the independently image-fitted rectangles from those results.
        // Synthetic blue pixels isolate position matching from the separately tested color reader.
        var candidates = RecordedTrains(sample);
        var raw = candidates.Select(candidate => candidate with { OrientedOutline = null }).ToArray();
        var at = DateTimeOffset.UtcNow;
        var first = BlueFrame(1, at);
        var second = BlueFrame(2, at.AddSeconds(1.1));
        var rejected = Placement(first, raw);
        Assert.Equal(RoutePlacementState.Incomplete, rejected.State);
        Assert.Equal(1, rejected.MatchedCount);
        Assert.Equal(0b01, rejected.UnverifiedSlotMask);
        Assert.Equal(BoardInventoryState.MissingTrains,
            Inventory().Observe(first, raw, 1, 1).State);

        var placement = new RoutePlacementVerifier();
        var inventory = Inventory();
        var occupancy = Inventory(verifyColors: false);
        Assert.Equal(RoutePlacementState.Stabilizing,
            placement.Observe(first, candidates, LaneA, MarkerColor.Blue, 2, "claim", 1, 1).State);
        Assert.Equal(BoardInventoryState.Stabilizing, inventory.Observe(first, candidates, 1, 1).State);
        Assert.Equal(BoardInventoryState.Stabilizing, occupancy.Observe(first, candidates, 1, 1).State);
        var accepted = placement.Observe(second, candidates, LaneA, MarkerColor.Blue, 2, "claim", 1, 1);
        Assert.True(accepted.Confirmed);
        Assert.Equal(2, accepted.MatchedCount);
        Assert.Equal(0, accepted.UnverifiedSlotMask);
        Assert.True(inventory.Observe(second, candidates, 1, 1).Confirmed);
        Assert.True(occupancy.Observe(second, candidates, 1, 1).Confirmed);

        var expectedCenter = Center(candidates[0].OrientedOutline!);
        var actualCenter = TrainCandidateGeometry.GetCenter(first, candidates[0]);
        Assert.Equal(expectedCenter.X, actualCenter.X, 12);
        Assert.Equal(expectedCenter.Y, actualCenter.Y, 12);
        Assert.NotEqual(Center(raw[0].Outline), actualCenter);
        Assert.Equal(raw[0].Outline, candidates[0].Outline);
        Assert.Equal(raw[0].Confidence, candidates[0].Confidence);
        Assert.Equal(RoutePlacementState.Incomplete, Placement(second, candidates, LaneB).State);
    }

    [Theory]
    [InlineData("missing-corner")]
    [InlineData("nonfinite")]
    [InlineData("contradictory-corner")]
    [InlineData("tiny-patch")]
    [InlineData("overshifted")]
    [InlineData("out-of-bounds")]
    public void Invalid_fitted_geometry_cannot_move_a_rejected_box_into_a_route(string invalidFit)
    {
        var candidates = RecordedTrains(0);
        var outline = candidates[0].OrientedOutline!.ToArray();
        var center = Center(outline);
        candidates[0] = candidates[0] with
        {
            OrientedOutline = invalidFit switch
            {
                "missing-corner" => outline[..3],
                "nonfinite" => [new(double.NaN, outline[0].Y), .. outline[1..]],
                "contradictory-corner" => [outline[0], outline[1], outline[0], outline[3]],
                "tiny-patch" => outline.Select(point => new NormalizedPoint(
                    center.X + (point.X - center.X) * .1,
                    center.Y + (point.Y - center.Y) * .1)).ToArray(),
                "overshifted" => outline.Select(point => new NormalizedPoint(point.X - .02, point.Y)).ToArray(),
                "out-of-bounds" => [new(1.01, outline[0].Y), .. outline[1..]],
                _ => throw new ArgumentOutOfRangeException(nameof(invalidFit))
            }
        };
        var frame = BlueFrame(1, DateTimeOffset.UtcNow);
        Assert.Equal(Center(candidates[0].Outline), TrainCandidateGeometry.GetCenter(frame, candidates[0]));
        var placement = Placement(frame, candidates);
        Assert.Equal(RoutePlacementState.Incomplete, placement.State);
        Assert.Equal(1, placement.MatchedCount);
        Assert.Equal(0b01, placement.UnverifiedSlotMask);
        Assert.Equal(BoardInventoryState.MissingTrains, Inventory().Observe(frame, candidates, 1, 1).State);
    }

    [Fact]
    public void A_valid_fit_toward_the_sibling_lane_is_not_replaced_with_a_more_favorable_center()
    {
        var candidates = RecordedTrains(0);
        var rawCenter = Center(candidates[0].Outline);
        var fittedCenter = Center(candidates[0].OrientedOutline!);
        // Reflect the small center correction toward lane B while retaining the same body.
        candidates[0] = candidates[0] with
        {
            OrientedOutline = candidates[0].OrientedOutline!.Select(point => new NormalizedPoint(
                point.X + 2 * (rawCenter.X - fittedCenter.X),
                point.Y + 2 * (rawCenter.Y - fittedCenter.Y))).ToArray()
        };
        var frame = BlueFrame(1, DateTimeOffset.UtcNow);
        var observedCenter = TrainCandidateGeometry.GetCenter(frame, candidates[0]);
        Assert.True(observedCenter.X > rawCenter.X);
        Assert.Equal(Center(candidates[0].OrientedOutline!).X, observedCenter.X, 12);
        Assert.Equal(RoutePlacementState.Incomplete, Placement(frame, candidates).State);
        Assert.Equal(BoardInventoryState.MissingTrains, Inventory().Observe(frame, candidates, 1, 1).State);
    }

    [Theory]
    [InlineData(LaneA, LaneB)]
    [InlineData(LaneB, LaneA)]
    public void Real_fitted_trains_on_the_parallel_lane_cannot_verify_the_requested_lane(
        string occupied, string requested)
    {
        Assert.True(ClassicUsRouteGeometry.TryGetSlots(occupied, out var slots));
        var candidates = slots.Select(FittedTrainAt).ToArray();
        var at = DateTimeOffset.UtcNow;
        var first = BlueFrame(1, at);
        var second = BlueFrame(2, at.AddSeconds(1.1));
        var placement = new RoutePlacementVerifier();
        var inventory = new BoardInventoryVerifier([new(requested, MarkerColor.Blue, 2)]);
        Assert.Equal(0, placement.Observe(first, candidates, requested, MarkerColor.Blue, 2, "claim", 1, 1).MatchedCount);
        Assert.False(placement.Observe(second, candidates, requested, MarkerColor.Blue, 2, "claim", 1, 1).Confirmed);
        Assert.Equal(BoardInventoryState.MissingTrains, inventory.Observe(first, candidates, 1, 1).State);
        Assert.False(inventory.Observe(second, candidates, 1, 1).Confirmed);
        Assert.Equal(RoutePlacementState.Stabilizing, Placement(first, candidates, occupied).State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_fitted_center_does_not_substitute_for_a_missing_train(int missing)
    {
        var candidates = RecordedTrains(0).Where((_, index) => index != missing).ToArray();
        var frame = BlueFrame(1, DateTimeOffset.UtcNow);
        var observation = Placement(frame, candidates);
        Assert.Equal(RoutePlacementState.Incomplete, observation.State);
        Assert.Equal(1, observation.MatchedCount);
        Assert.Equal(1 << missing, observation.UnverifiedSlotMask);
        Assert.Equal(BoardInventoryState.MissingTrains, Inventory().Observe(frame, candidates, 1, 1).State);
        Assert.Equal(0, Placement(frame, []).MatchedCount);
    }

    [Fact]
    public void Fitted_positions_do_not_hide_duplicate_detections_or_override_the_required_color()
    {
        var candidates = RecordedTrains(0);
        PieceCandidate[] duplicates = [.. candidates, candidates[0] with { }];
        var frame = BlueFrame(1, DateTimeOffset.UtcNow);
        Assert.Equal(RoutePlacementState.Ambiguous, Placement(frame, duplicates).State);
        Assert.Equal(BoardInventoryState.Ambiguous, Inventory().Observe(frame, duplicates, 1, 1).State);
        Assert.Equal(RoutePlacementState.WrongColor, new RoutePlacementVerifier().Observe(
            frame, candidates, LaneA, MarkerColor.Red, 2, "claim", 1, 1).State);
    }

    [Fact]
    public void Inventory_diagnostics_use_the_same_fitted_lane_and_preserve_original_detection_bounds()
    {
        var candidates = RecordedTrains(0);
        var frame = BlueFrame(1, DateTimeOffset.UtcNow);
        var observation = new BoardInventoryVerifier([]).Observe(frame, candidates, 1, 1);
        Assert.Equal(BoardInventoryState.UnexpectedTrain, observation.State);
        Assert.Equal(new UnexpectedTrainLocation(LaneA, MarkerColor.Blue, 2), observation.UnexpectedTrains);
        Assert.Equal(2, observation.UnexpectedDetections.Count);
        Assert.All(observation.UnexpectedDetections, detection => Assert.Equal(LaneA, detection.RouteId));
        var upper = observation.UnexpectedDetections[0];
        Assert.Equal(candidates[0].Outline.Min(point => point.X), upper.X);
        Assert.Equal(candidates[0].Outline.Min(point => point.Y), upper.Y);
        Assert.Equal(candidates[0].Confidence, upper.Confidence);
    }

    [Fact]
    public void A_marker_cannot_use_a_train_fit_to_supply_route_occupancy()
    {
        var candidates = RecordedTrains(0);
        candidates[0] = candidates[0] with { Kind = PieceCandidateKind.PlayerMarker };
        var frame = BlueFrame(1, DateTimeOffset.UtcNow);
        Assert.Equal(Center(candidates[0].Outline), TrainCandidateGeometry.GetCenter(frame, candidates[0]));
        Assert.Equal(RoutePlacementState.Incomplete, Placement(frame, candidates).State);
    }

    private static BoardInventoryVerifier Inventory(bool verifyColors = true) =>
        new([new(LaneA, MarkerColor.Blue, 2)], verifyClaimedRouteColors: verifyColors);

    private static RoutePlacementObservation Placement(CameraFrame frame,
        IReadOnlyList<PieceCandidate> candidates, string route = LaneA) =>
        new RoutePlacementVerifier().Observe(frame, candidates, route, MarkerColor.Blue, 2, "claim", 1, 1);

    private static NormalizedPoint Center(IReadOnlyList<NormalizedPoint> outline) =>
        new(outline.Average(point => point.X), outline.Average(point => point.Y));

    private static PieceCandidate Box(double left, double top, double right, double bottom,
        double confidence, IReadOnlyList<NormalizedPoint>? fitted = null) =>
        new(PieceCandidateKind.Train,
            [new(left, top), new(right, top), new(right, bottom), new(left, bottom)], confidence)
        { OrientedOutline = fitted };

    private static PieceCandidate FittedTrainAt(BoardSlotPoint slot)
    {
        var length = Math.Sqrt(slot.TangentX * slot.TangentX + slot.TangentY * slot.TangentY);
        var tx = slot.TangentX / length;
        var ty = slot.TangentY / length;
        NormalizedPoint Point(double along, double across) => new(
            (slot.ReferenceX + along * tx - across * ty) / 1996,
            (slot.ReferenceY + along * ty + across * tx) / 1248);
        NormalizedPoint[] body = [Point(-28, -9), Point(28, -9), Point(28, 9), Point(-28, 9)];
        return Box(body.Min(point => point.X), body.Min(point => point.Y),
            body.Max(point => point.X), body.Max(point => point.Y), .96, body);
    }

    private static CameraFrame BlueFrame(long sequence, DateTimeOffset at)
    {
        var pixels = new byte[Width * Height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 195;
            pixels[offset + 1] = 75;
            pixels[offset + 2] = 20;
            pixels[offset + 3] = 255;
        }
        return CameraFrame.CopyFromBgra32(Width, Height, pixels, sequence, epoch: 1, capturedAt: at);
    }

    private static PieceCandidate[] RecordedTrains(int sample) => sample switch
    {
        0 =>
        [
            Box(.9127893666426341, .2180652109781901, .9411479095617931, .2695196787516276, .9473318799788828,
            [new(.9092880002937633, .2638415627084411), new(.9312448736678739, .21313984729121543),
                new(.9420335138423948, .22510049002012797), new(.9200766404682841, .2758022054373537)]),
            Box(.8921383599440257, .26073833465576174, .9222846289475759, .3131339391072591, .9550940728441439,
            [new(.8922181974898076, .30017185547435954), new(.9139689787595269, .25289679103767354),
                new(.9245714532195746, .26538470597664243), new(.9028206719498555, .31265977041332843)])
        ],
        1 =>
        [
            Box(.9129568348328273, .2180382982889811, .9410233249266943, .26935717900594075, .951851763273055,
            [new(.9099548004468931, .264643483985906), new(.9308751745494614, .21305715660102617),
                new(.9417968621525787, .22439589020163234), new(.9208764880500104, .27598221758651215)]),
            Box(.8921980212132136, .26046345233917234, .9224286725123724, .3131075112024943, .9544291972085972)
        ],
        2 =>
        [
            Box(.9125958214203517, .21793400764465332, .9407205174366633, .26989471753438316, .9449767938216382,
            [new(.9098567155417384, .26565941641463026), new(.9308127145524201, .21300653608373926),
                new(.9418153804701858, .22421701398874097), new(.9208593814595041, .27686989431963194)]),
            Box(.8920776436726252, .2604933818181356, .9224223703145981, .31354231357574464, .9538814128828932)
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(sample))
    };
}
