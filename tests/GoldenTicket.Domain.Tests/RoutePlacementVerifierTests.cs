using GoldenTicket.Vision;
using GoldenTicket.Domain.Manifest;

namespace GoldenTicket.Domain.Tests;

public sealed class RoutePlacementVerifierTests
{
    private const string LaneA = "atlanta--raleigh--a";
    private const string LaneB = "atlanta--raleigh--b";
    private static readonly (double X, double Y)[] A = [(1586, 755), (1643, 712)];
    private static readonly (double X, double Y)[] B = [(1605, 770), (1656, 727)];

    [Fact]
    public void Two_blue_trains_in_the_exact_lane_confirm_only_after_a_second_fresh_frame()
    {
        var verifier = new RoutePlacementVerifier();
        var firstAt = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, firstAt, (A[0].X, A[0].Y, MarkerColor.Blue, .9),
            (A[1].X, A[1].Y, MarkerColor.Blue, .9));
        var second = Scene(2, 1, firstAt.AddSeconds(1.1), (A[0].X, A[0].Y, MarkerColor.Blue, .9),
            (A[1].X, A[1].Y, MarkerColor.Blue, .9));

        Assert.Equal(RoutePlacementState.Stabilizing,
            verifier.Observe(first.Frame, first.Candidates, LaneA, MarkerColor.Blue, 2, "op-1", 1, 1).State);
        var accepted = verifier.Observe(second.Frame, second.Candidates, LaneA, MarkerColor.Blue, 2, "op-1", 1, 1);
        Assert.True(accepted.Confirmed);
        Assert.Equal(2, accepted.MatchedCount);
        Assert.True(verifier.Observe(Scene(3, 1, firstAt.AddSeconds(2.2),
            (A[0].X, A[0].Y, MarkerColor.Blue, .9), (A[1].X, A[1].Y, MarkerColor.Blue, .9)).Frame,
            second.Candidates, LaneA, MarkerColor.Blue, 2, "op-1", 1, 1).Confirmed);

        var wrongColor = Scene(4, 1, firstAt.AddSeconds(3.3),
            (A[0].X, A[0].Y, MarkerColor.Yellow, .9),
            (A[1].X, A[1].Y, MarkerColor.Blue, .9));
        Assert.Equal(RoutePlacementState.WrongColor,
            verifier.Observe(wrongColor.Frame, wrongColor.Candidates,
                LaneA, MarkerColor.Blue, 2, "op-1", 1, 1).State);
        var restored = Scene(5, 1, firstAt.AddSeconds(4.4),
            (A[0].X, A[0].Y, MarkerColor.Blue, .9),
            (A[1].X, A[1].Y, MarkerColor.Blue, .9));
        Assert.Equal(RoutePlacementState.Stabilizing,
            verifier.Observe(restored.Frame, restored.Candidates,
                LaneA, MarkerColor.Blue, 2, "op-1", 1, 1).State);
    }

    [Fact]
    public void Printed_color_without_detected_trains_never_confirms()
    {
        var verifier = new RoutePlacementVerifier();
        var first = Scene(1, 1, DateTimeOffset.UtcNow,
            (A[0].X, A[0].Y, MarkerColor.Blue, .9), (A[1].X, A[1].Y, MarkerColor.Blue, .9));
        Assert.Equal(RoutePlacementState.Incomplete,
            verifier.Observe(first.Frame, [], LaneA, MarkerColor.Blue, 2, "op-1", 1, 1).State);
    }

    [Theory]
    [InlineData(MarkerColor.Blue)]
    [InlineData(MarkerColor.Red)]
    [InlineData(MarkerColor.Green)]
    [InlineData(MarkerColor.Yellow)]
    [InlineData(MarkerColor.Black)]
    public void Reads_each_physical_train_color_from_the_detected_piece_interior(MarkerColor color)
    {
        var verifier = new RoutePlacementVerifier();
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, now, (A[0].X, A[0].Y, color, .9),
            (A[1].X, A[1].Y, color, .9));
        var second = Scene(2, 1, now.AddSeconds(1.1), (A[0].X, A[0].Y, color, .9),
            (A[1].X, A[1].Y, color, .9));
        Assert.Equal(RoutePlacementState.Stabilizing,
            verifier.Observe(first.Frame, first.Candidates, LaneA, color, 2, "op-1", 1, 1).State);
        Assert.True(verifier.Observe(second.Frame, second.Candidates, LaneA, color,
            2, "op-1", 1, 1).Confirmed);
    }

    [Fact]
    public void Parallel_lane_is_not_interchangeable()
    {
        var verifier = new RoutePlacementVerifier();
        var wrongLane = Scene(1, 1, DateTimeOffset.UtcNow,
            (B[0].X, B[0].Y, MarkerColor.Blue, .9), (B[1].X, B[1].Y, MarkerColor.Blue, .9));
        Assert.Equal(RoutePlacementState.Incomplete,
            verifier.Observe(wrongLane.Frame, wrongLane.Candidates, LaneA, MarkerColor.Blue, 2, "op-1", 1, 1).State);
        Assert.Equal(RoutePlacementState.Stabilizing,
            verifier.Observe(wrongLane.Frame, wrongLane.Candidates, LaneB, MarkerColor.Blue, 2, "op-1", 1, 1).State);
    }

    [Fact]
    public void Observed_duluth_omaha_lane_a_trains_survive_small_model_center_drift()
    {
        const string claimed = "duluth--omaha--a";
        const string parallel = "duluth--omaha--b";
        var now = DateTimeOffset.UtcNow;
        // The live audit saw these two blue pieces at high confidence. The upper
        // detection moved from x=1092.6 at claim time to x=1094.2 later, while
        // remaining closer to lane A than lane B.
        var first = SceneAtResolution(1996, 1248, 1, 1, now,
            (1094.2, 444.6, MarkerColor.Blue, .96),
            (1069.9, 505.4, MarkerColor.Blue, .96));
        var second = SceneAtResolution(1996, 1248, 2, 1, now.AddSeconds(1.1),
            (1093.7, 445.1, MarkerColor.Blue, .96),
            (1069.2, 505.4, MarkerColor.Blue, .96));
        var claimedVerifier = new RoutePlacementVerifier();

        Assert.Equal(RoutePlacementState.Stabilizing,
            claimedVerifier.Observe(first.Frame, first.Candidates, claimed,
                MarkerColor.Blue, 2, "later-claim", 1, 1).State);
        Assert.True(claimedVerifier.Observe(second.Frame, second.Candidates, claimed,
            MarkerColor.Blue, 2, "later-claim", 1, 1).Confirmed);
        Assert.False(new RoutePlacementVerifier().Observe(first.Frame, first.Candidates,
            parallel, MarkerColor.Blue, 2, "later-claim", 1, 1).Confirmed);
    }

    [Fact]
    public void Four_observed_atlanta_new_orleans_trains_confirm_lane_a_and_identify_an_unverified_space()
    {
        const string laneA = "atlanta--new-orleans--a";
        const string laneB = "atlanta--new-orleans--b";
        var now = DateTimeOffset.UtcNow;
        // Centers measured from three supplied frames. The model found every train,
        // but the old geometry put the second one outside the lane tolerance.
        var first = SceneAtResolution(1996, 1248, 1, 1, now,
            (1509.6, 802, MarkerColor.Yellow, .96),
            (1453.1, 851.3, MarkerColor.Yellow, .96),
            (1409.5, 910.8, MarkerColor.Yellow, .96),
            (1380.7, 970.5, MarkerColor.Yellow, .96));
        var second = SceneAtResolution(1996, 1248, 2, 1, now.AddSeconds(1.1),
            (1506.7, 808.5, MarkerColor.Yellow, .96),
            (1450, 856.5, MarkerColor.Yellow, .96),
            (1406.2, 916.8, MarkerColor.Yellow, .96),
            (1376.2, 975, MarkerColor.Yellow, .96));
        var verifier = new RoutePlacementVerifier();

        Assert.Equal(RoutePlacementState.Stabilizing,
            verifier.Observe(first.Frame, first.Candidates, laneA,
                MarkerColor.Yellow, 4, "human-claim", 1, 1).State);
        Assert.True(verifier.Observe(second.Frame, second.Candidates, laneA,
            MarkerColor.Yellow, 4, "human-claim", 1, 1).Confirmed);
        Assert.False(new RoutePlacementVerifier().Observe(first.Frame, first.Candidates,
            laneB, MarkerColor.Yellow, 4, "human-claim", 1, 1).Confirmed);

        var missingSecond = SceneAtResolution(1996, 1248, 3, 1, now.AddSeconds(2.2),
            (1506.7, 808.5, MarkerColor.Yellow, .96),
            (1406.2, 916.8, MarkerColor.Yellow, .96),
            (1376.2, 975, MarkerColor.Yellow, .96));
        var incomplete = verifier.Observe(missingSecond.Frame, missingSecond.Candidates,
            laneA, MarkerColor.Yellow, 4, "human-claim", 1, 1);
        Assert.Equal(RoutePlacementState.Incomplete, incomplete.State);
        Assert.Equal(3, incomplete.MatchedCount);
        Assert.Equal(0b0010, incomplete.UnverifiedSlotMask);
    }

    [Fact]
    public void Observed_chicago_pittsburgh_positions_confirm_placement_and_inventory()
    {
        const string route = "chicago--pittsburgh--a";
        var now = DateTimeOffset.UtcNow;
        // September 19 camera diagnostics: all three were blue at >95% confidence,
        // but the last center sat 14.3 reference pixels across from the printed slot.
        var first = SceneAtResolution(1996, 1248, 1, 1, now,
            (1402.4, 469.4, MarkerColor.Blue, .958),
            (1478.8, 451.4, MarkerColor.Blue, .955),
            (1557.7, 445.4, MarkerColor.Blue, .954));
        var second = SceneAtResolution(1996, 1248, 2, 1, now.AddSeconds(1.1),
            (1403.1, 469.7, MarkerColor.Blue, .956),
            (1478.7, 451.7, MarkerColor.Blue, .956),
            (1557.5, 445.8, MarkerColor.Blue, .954));
        var placement = new RoutePlacementVerifier();
        var inventory = new BoardInventoryVerifier([new(route, MarkerColor.Blue, 3)]);

        Assert.Equal(RoutePlacementState.Stabilizing,
            placement.Observe(first.Frame, first.Candidates, route, MarkerColor.Blue, 3, "claim", 1, 1).State);
        Assert.Equal(BoardInventoryState.Stabilizing,
            inventory.Observe(first.Frame, first.Candidates, 1, 1).State);
        Assert.True(placement.Observe(second.Frame, second.Candidates,
            route, MarkerColor.Blue, 3, "claim", 1, 1).Confirmed);
        Assert.True(inventory.Observe(second.Frame, second.Candidates, 1, 1).Confirmed);
        var sibling = new RoutePlacementVerifier().Observe(second.Frame, second.Candidates,
            "chicago--pittsburgh--b", MarkerColor.Blue, 3, "other-lane", 1, 1);
        Assert.Equal(RoutePlacementState.Incomplete, sibling.State);
        Assert.Equal(0, sibling.MatchedCount);
    }

    [Theory]
    [InlineData(-15, true)] // A little outside lane A, away from lane B.
    [InlineData(-19, false)] // Beyond the permitted slack.
    [InlineData(10, false)] // Between the lanes.
    [InlineData(15, false)] // Closer to lane B.
    public void Positioning_slack_still_requires_the_correct_parallel_lane(double across, bool accepted)
    {
        const string route = "chicago--pittsburgh--a";
        ClassicUsRouteGeometry.TryGetSlots(route, out var slots);
        var trains = slots.Select(slot =>
            (slot.ReferenceX - slot.TangentY * across,
                slot.ReferenceY + slot.TangentX * across, MarkerColor.Blue, .96)).ToArray();
        var now = DateTimeOffset.UtcNow;
        var first = SceneAtResolution(1996, 1248, 1, 1, now, trains);
        var second = SceneAtResolution(1996, 1248, 2, 1, now.AddSeconds(1.1), trains);
        var placement = new RoutePlacementVerifier();
        var inventory = new BoardInventoryVerifier([new(route, MarkerColor.Blue, 3)]);

        Assert.Equal(accepted ? RoutePlacementState.Stabilizing : RoutePlacementState.Incomplete,
            placement.Observe(first.Frame, first.Candidates, route, MarkerColor.Blue, 3, "claim", 1, 1).State);
        Assert.Equal(accepted ? BoardInventoryState.Stabilizing : BoardInventoryState.MissingTrains,
            inventory.Observe(first.Frame, first.Candidates, 1, 1).State);
        Assert.Equal(accepted, placement.Observe(second.Frame, second.Candidates,
            route, MarkerColor.Blue, 3, "claim", 1, 1).Confirmed);
        Assert.Equal(accepted, inventory.Observe(second.Frame, second.Candidates, 1, 1).Confirmed);
    }

    [Fact]
    public void Yellow_trains_on_kansas_city_oklahoma_city_lane_b_do_not_confirm_blue_lane_a()
    {
        const string requested = "kansas-city--oklahoma-city--a";
        const string occupied = "kansas-city--oklahoma-city--b";
        ClassicUsRouteGeometry.TryGetSlots(occupied, out var yellowSlots);
        ClassicUsRouteGeometry.TryGetSlots(requested, out var blueSlots);
        var yellowWithEarlierBlue = yellowSlots.Select(slot =>
            (slot.ReferenceX, slot.ReferenceY, MarkerColor.Yellow, .9))
            .Append((1074d, 611d, MarkerColor.Blue, .9))
            .ToArray();
        var at = DateTimeOffset.UtcNow;
        var verifier = new RoutePlacementVerifier();
        var first = Scene(1, 1, at, yellowWithEarlierBlue);
        var second = Scene(2, 1, at.AddSeconds(1.1), yellowWithEarlierBlue);
        Assert.False(verifier.Observe(first.Frame, first.Candidates, requested,
            MarkerColor.Blue, 2, "computer-claim", 1, 1).Confirmed);
        Assert.False(verifier.Observe(second.Frame, second.Candidates, requested,
            MarkerColor.Blue, 2, "computer-claim", 1, 1).Confirmed);

        var withNewBlue = yellowWithEarlierBlue.Concat(blueSlots.Select(slot =>
            (slot.ReferenceX, slot.ReferenceY, MarkerColor.Blue, .9))).ToArray();
        var placed = Scene(3, 1, at.AddSeconds(2.2), withNewBlue);
        var stable = Scene(4, 1, at.AddSeconds(3.3), withNewBlue);
        Assert.Equal(RoutePlacementState.Stabilizing,
            verifier.Observe(placed.Frame, placed.Candidates, requested,
                MarkerColor.Blue, 2, "computer-claim", 1, 1).State);
        Assert.True(verifier.Observe(stable.Frame, stable.Candidates, requested,
            MarkerColor.Blue, 2, "computer-claim", 1, 1).Confirmed);
    }

    [Fact]
    public void Observed_live_blue_positions_match_lane_a_on_raw_camera_frames()
    {
        var verifier = new RoutePlacementVerifier();
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, now, (1577, 762, MarkerColor.Blue, .928),
            (1663, 698, MarkerColor.Blue, .928));
        var second = Scene(2, 1, now.AddSeconds(1.1), (1577, 762, MarkerColor.Blue, .928),
            (1663, 698, MarkerColor.Blue, .928));

        Assert.Equal(RoutePlacementState.Stabilizing,
            verifier.Observe(first.Frame, first.Candidates, LaneA, MarkerColor.Blue, 2, "op-1", 1, 1).State);
        Assert.True(verifier.Observe(second.Frame, second.Candidates, LaneA,
            MarkerColor.Blue, 2, "op-1", 1, 1).Confirmed);
        Assert.Equal(RoutePlacementState.Incomplete,
            new RoutePlacementVerifier().Observe(first.Frame, first.Candidates, LaneB,
                MarkerColor.Blue, 2, "op-1", 1, 1).State);

        var oneRealPieceAndCenterArtifact = Scene(3, 1, now.AddSeconds(2.2),
            (1577, 762, MarkerColor.Blue, .928), (1603, 740, MarkerColor.Blue, .898));
        Assert.False(new RoutePlacementVerifier().Observe(oneRealPieceAndCenterArtifact.Frame,
            oneRealPieceAndCenterArtifact.Candidates, LaneA, MarkerColor.Blue,
            2, "op-partial", 1, 1).Confirmed);
    }

    [Fact]
    public void Missing_wrong_color_low_confidence_and_duplicate_detections_are_rejected()
    {
        var now = DateTimeOffset.UtcNow;
        var one = Scene(1, 1, now, (A[0].X, A[0].Y, MarkerColor.Blue, .9));
        Assert.Equal(RoutePlacementState.Incomplete, Observe(one));

        var wrong = Scene(1, 1, now, (A[0].X, A[0].Y, MarkerColor.Red, .9),
            (A[1].X, A[1].Y, MarkerColor.Blue, .9));
        Assert.Equal(RoutePlacementState.WrongColor, Observe(wrong));

        var low = Scene(1, 1, now, (A[0].X, A[0].Y, MarkerColor.Blue, .4),
            (A[1].X, A[1].Y, MarkerColor.Blue, .9));
        Assert.Equal(RoutePlacementState.Incomplete, Observe(low));

        var good = Scene(1, 1, now, (A[0].X, A[0].Y, MarkerColor.Blue, .9),
            (A[1].X, A[1].Y, MarkerColor.Blue, .9));
        Assert.Equal(RoutePlacementState.Ambiguous,
            new RoutePlacementVerifier().Observe(good.Frame,
                [good.Candidates[0], good.Candidates[0], good.Candidates[1]],
                LaneA, MarkerColor.Blue, 2, "op-1", 1, 1).State);

        static RoutePlacementState Observe((CameraFrame Frame, PieceCandidate[] Candidates) scene) =>
            new RoutePlacementVerifier().Observe(scene.Frame, scene.Candidates,
                LaneA, MarkerColor.Blue, 2, "op-1", 1, 1).State;
    }

    [Fact]
    public void Stale_repeated_frame_does_not_finish_confirmation()
    {
        var verifier = new RoutePlacementVerifier();
        var now = DateTimeOffset.UtcNow;
        var frame = Scene(1, 1, now, (A[0].X, A[0].Y, MarkerColor.Blue, .9),
            (A[1].X, A[1].Y, MarkerColor.Blue, .9));
        verifier.Observe(frame.Frame, frame.Candidates, LaneA, MarkerColor.Blue, 2, "op-1", 1, 1);
        Assert.Equal(RoutePlacementState.WaitingForFreshFrame,
            verifier.Observe(frame.Frame, frame.Candidates, LaneA, MarkerColor.Blue, 2, "op-1", 1, 1).State);
        var next = Scene(2, 1, now.AddSeconds(1.2), (A[0].X, A[0].Y, MarkerColor.Blue, .9),
            (A[1].X, A[1].Y, MarkerColor.Blue, .9));
        Assert.Equal(RoutePlacementState.Stabilizing,
            verifier.Observe(next.Frame, next.Candidates, LaneA, MarkerColor.Blue, 2, "op-1", 1, 1).State);
    }

    [Theory]
    [InlineData("op-2", 1, 1, 1)]
    [InlineData("op-1", 2, 1, 1)]
    [InlineData("op-1", 1, 2, 1)]
    [InlineData("op-1", 1, 1, 2)]
    public void Operation_crop_model_and_camera_changes_restart_stability(
        string operation, long cropRevision, long modelRevision, long epoch)
    {
        var verifier = new RoutePlacementVerifier();
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, now, (A[0].X, A[0].Y, MarkerColor.Blue, .9),
            (A[1].X, A[1].Y, MarkerColor.Blue, .9));
        var second = Scene(2, epoch, now.AddSeconds(1.2), (A[0].X, A[0].Y, MarkerColor.Blue, .9),
            (A[1].X, A[1].Y, MarkerColor.Blue, .9));
        verifier.Observe(first.Frame, first.Candidates, LaneA, MarkerColor.Blue, 2, "op-1", 1, 1);
        Assert.Equal(RoutePlacementState.Stabilizing,
            verifier.Observe(second.Frame, second.Candidates, LaneA, MarkerColor.Blue, 2,
                operation, cropRevision, modelRevision).State);
    }

    [Fact]
    public void Unmeasured_routes_are_never_accepted_from_a_city_midpoint()
    {
        var verifier = new RoutePlacementVerifier();
        var frame = Scene(1, 1, DateTimeOffset.UtcNow,
            (A[0].X, A[0].Y, MarkerColor.Blue, .9), (A[1].X, A[1].Y, MarkerColor.Blue, .9));
        Assert.False(RoutePlacementVerifier.Supports("no-such-route", 2));
        Assert.Equal(RoutePlacementState.Unsupported,
            verifier.Observe(frame.Frame, frame.Candidates, "no-such-route", MarkerColor.Blue,
                2, "op-1", 1, 1).State);
    }

    [Fact]
    public void Every_classic_route_has_one_distinct_measured_position_per_printed_train_space()
    {
        var manifest = ManifestLoader.LoadClassicUs();
        Assert.Equal(100, manifest.Routes.Length);
        Assert.Equal(100, ClassicUsRouteGeometry.RouteCount);
        Assert.Equal(309, ClassicUsRouteGeometry.SlotCount);
        foreach (var route in manifest.Routes)
        {
            var routeId = route.RouteId.Value;
            Assert.True(ClassicUsRouteGeometry.TryGetSlots(routeId, out var spots), routeId);
            Assert.Equal(route.Length, spots.Count);
            Assert.True(RoutePlacementVerifier.Supports(routeId, route.Length), routeId);
            foreach (var point in spots)
            {
                Assert.InRange(point.X, .01, .99);
                Assert.InRange(point.Y, .01, .99);
                Assert.InRange(Math.Sqrt(point.TangentX * point.TangentX + point.TangentY * point.TangentY), .98, 1.02);
            }
            for (var first = 0; first < spots.Count; first++)
            for (var second = first + 1; second < spots.Count; second++)
            {
                var dx = spots[first].ReferenceX - spots[second].ReferenceX;
                var dy = spots[first].ReferenceY - spots[second].ReferenceY;
                Assert.True(dx * dx + dy * dy > 12 * 12, $"{routeId} repeats a train space");
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Every_train_on_each_printed_space_is_required_for_all_route_lengths(int length)
    {
        var route = ManifestLoader.LoadClassicUs().Routes.First(route =>
            route.Length == length && ClassicUsRouteGeometry.TryGetSlots(route.RouteId.Value, out _));
        ClassicUsRouteGeometry.TryGetSlots(route.RouteId.Value, out var spots);
        var complete = spots.Select(point => (point.ReferenceX, point.ReferenceY, MarkerColor.Green, .9)).ToArray();
        var partial = complete.Take(length - 1).Append((100d, 100d, MarkerColor.Green, .9)).ToArray();
        var verifier = new RoutePlacementVerifier();
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, now, partial);
        Assert.Equal(RoutePlacementState.Incomplete,
            verifier.Observe(first.Frame, first.Candidates, route.RouteId.Value,
                MarkerColor.Green, length, "multi", 1, 1).State);

        var second = Scene(2, 1, now.AddSeconds(.2), complete);
        Assert.Equal(RoutePlacementState.Stabilizing,
            verifier.Observe(second.Frame, second.Candidates, route.RouteId.Value,
                MarkerColor.Green, length, "multi", 1, 1).State);
        var third = Scene(3, 1, now.AddSeconds(1.4), complete);
        Assert.Equal(RoutePlacementState.Confirmed,
            verifier.Observe(third.Frame, third.Candidates, route.RouteId.Value,
                MarkerColor.Green, length, "multi", 1, 1).State);
    }

    [Fact]
    public void Every_measured_classic_route_can_confirm_its_own_complete_set_of_spaces()
    {
        var verifier = new RoutePlacementVerifier();
        foreach (var route in ManifestLoader.LoadClassicUs().Routes)
        {
            var routeId = route.RouteId.Value;
            ClassicUsRouteGeometry.TryGetSlots(routeId, out var spots);
            var trains = spots.Select(point =>
                (point.ReferenceX, point.ReferenceY, MarkerColor.Blue, .9)).ToArray();
            var now = DateTimeOffset.UtcNow;
            var first = Scene(1, 1, now, trains);
            var second = Scene(2, 1, now.AddSeconds(1.1), trains);
            Assert.Equal(RoutePlacementState.Stabilizing,
                verifier.Observe(first.Frame, first.Candidates, routeId,
                    MarkerColor.Blue, route.Length, routeId, 1, 1).State);
            Assert.True(verifier.Observe(second.Frame, second.Candidates, routeId,
                MarkerColor.Blue, route.Length, routeId, 1, 1).Confirmed, routeId);
        }
    }

    [Fact]
    public void Every_parallel_lanes_trains_are_rejected_for_the_sibling_lane()
    {
        foreach (var routeId in ClassicUsRouteGeometry.RouteIds.Where(id => id.EndsWith("--a", StringComparison.Ordinal)))
        {
            var otherId = routeId[..^3] + "--b";
            if (!ClassicUsRouteGeometry.TryGetSlots(otherId, out var otherSpots)) continue;
            ClassicUsRouteGeometry.TryGetSlots(routeId, out var requestedSpots);
            AssertRejected(routeId, otherId, otherSpots);
            AssertRejected(otherId, routeId, requestedSpots);
        }

        static void AssertRejected(string requestedId, string occupiedId, IReadOnlyList<BoardSlotPoint> occupiedSpots)
        {
            var trains = occupiedSpots.Select(point =>
                (point.ReferenceX, point.ReferenceY, MarkerColor.Red, .9)).ToArray();
            var now = DateTimeOffset.UtcNow;
            var first = Scene(1, 1, now, trains);
            var second = Scene(2, 1, now.AddSeconds(1.1), trains);
            var verifier = new RoutePlacementVerifier();
            verifier.Observe(first.Frame, first.Candidates, requestedId,
                MarkerColor.Red, trains.Length, requestedId, 1, 1);
            Assert.False(verifier.Observe(second.Frame, second.Candidates,
                requestedId, MarkerColor.Red, trains.Length, requestedId, 1, 1).Confirmed,
                $"{occupiedId} occupied while {requestedId} requested");
        }
    }

    [Fact]
    public void Trains_between_parallel_lanes_are_not_evidence_for_either_lane()
    {
        var halfway = A.Zip(B, (first, second) =>
            ((first.X + second.X) / 2, (first.Y + second.Y) / 2, MarkerColor.Blue, .9)).ToArray();
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, now, halfway);
        var second = Scene(2, 1, now.AddSeconds(1.1), halfway);
        foreach (var routeId in new[] { LaneA, LaneB })
        {
            var verifier = new RoutePlacementVerifier();
            verifier.Observe(first.Frame, first.Candidates, routeId, MarkerColor.Blue,
                2, routeId, 1, 1);
            Assert.False(verifier.Observe(second.Frame, second.Candidates, routeId,
                MarkerColor.Blue, 2, routeId, 1, 1).Confirmed, routeId);
        }
    }

    private static (CameraFrame Frame, PieceCandidate[] Candidates) Scene(long sequence, long epoch,
        DateTimeOffset capturedAt, params (double X, double Y, MarkerColor Color, double Confidence)[] trains)
        => SceneAtResolution(960, 600, sequence, epoch, capturedAt, trains);

    private static (CameraFrame Frame, PieceCandidate[] Candidates) SceneAtResolution(int width, int height,
        long sequence, long epoch, DateTimeOffset capturedAt,
        params (double X, double Y, MarkerColor Color, double Confidence)[] trains)
    {
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = pixels[offset + 3] = 180;
        var candidates = new List<PieceCandidate>();
        foreach (var (referenceX, referenceY, color, confidence) in trains)
        {
            var x = (int)Math.Round(referenceX / 1996 * width);
            var y = (int)Math.Round(referenceY / 1248 * height);
            const int halfWidth = 11;
            const int halfHeight = 7;
            var (red, green, blue) = color switch
            {
                MarkerColor.Red => (190, 35, 30),
                MarkerColor.Green => (20, 125, 35),
                MarkerColor.Yellow => (225, 180, 20),
                MarkerColor.Blue => (20, 75, 195),
                _ => (20, 20, 20),
            };
            for (var py = y - halfHeight; py <= y + halfHeight; py++)
            for (var px = x - halfWidth; px <= x + halfWidth; px++)
            {
                var offset = (py * width + px) * 4;
                pixels[offset] = (byte)blue;
                pixels[offset + 1] = (byte)green;
                pixels[offset + 2] = (byte)red;
            }
            candidates.Add(new(PieceCandidateKind.Train,
                [new((double)(x - halfWidth) / width, (double)(y - halfHeight) / height),
                    new((double)(x + halfWidth) / width, (double)(y - halfHeight) / height),
                    new((double)(x + halfWidth) / width, (double)(y + halfHeight) / height),
                    new((double)(x - halfWidth) / width, (double)(y + halfHeight) / height)], confidence));
        }
        return (CameraFrame.CopyFromBgra32(width, height, pixels, sequence, epoch, capturedAt),
            candidates.ToArray());
    }
}
