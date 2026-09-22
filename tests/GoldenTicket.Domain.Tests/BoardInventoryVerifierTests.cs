using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class BoardInventoryVerifierTests
{
    private const string BlueRoute = "atlanta--raleigh--a";
    private const string GreenRoute = "kansas-city--saint-louis--a";
    private static readonly Train[] Complete =
    [
        new(1586, 755, MarkerColor.Blue), new(1643, 712, MarkerColor.Blue),
        new(1154, 640, MarkerColor.Green), new(1220, 638, MarkerColor.Green)
    ];

    [Fact]
    public void Moving_trains_from_an_earlier_claim_to_a_new_route_blocks_the_new_claim()
    {
        const string earlierRoute = "houston--new-orleans";
        const string pendingRoute = "kansas-city--saint-louis--a";
        var verifier = new BoardInventoryVerifier([
            new(earlierRoute, MarkerColor.Blue, 2),
            new(pendingRoute, MarkerColor.Blue, 2)
        ]);
        var now = DateTimeOffset.UtcNow;
        var moved = Scene(1, 1, now,
            [new(1154, 640, MarkerColor.Blue), new(1220, 638, MarkerColor.Blue)]);
        var stillMoved = Scene(2, 1, now.AddSeconds(1.1),
            [new(1154, 640, MarkerColor.Blue), new(1220, 638, MarkerColor.Blue)]);

        // The requested route alone is complete, but Houston–New Orleans is empty.
        var requested = new RoutePlacementVerifier();
        requested.Observe(moved.Frame, moved.Candidates, pendingRoute, MarkerColor.Blue,
            2, "claim", 1, 1);
        Assert.True(requested.Observe(stillMoved.Frame, stillMoved.Candidates,
            pendingRoute, MarkerColor.Blue, 2, "claim", 1, 1).Confirmed);
        foreach (var scene in new[] { moved, stillMoved })
        {
            var observation = verifier.Observe(scene.Frame, scene.Candidates, 1, 1);
            Assert.Equal(BoardInventoryState.MissingTrains, observation.State);
            Assert.Equal(earlierRoute, observation.RouteId);
        }

        var restored = Scene(3, 1, now.AddSeconds(2.2),
            [new(1240, 1034, MarkerColor.Blue), new(1308, 1030, MarkerColor.Blue),
                new(1154, 640, MarkerColor.Blue), new(1220, 638, MarkerColor.Blue)]);
        var stable = Scene(4, 1, now.AddSeconds(3.3),
            [new(1240, 1034, MarkerColor.Blue), new(1308, 1030, MarkerColor.Blue),
                new(1154, 640, MarkerColor.Blue), new(1220, 638, MarkerColor.Blue)]);
        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(restored.Frame, restored.Candidates, 1, 1).State);
        Assert.True(verifier.Observe(stable.Frame, stable.Candidates, 1, 1).Confirmed);
    }

    [Fact]
    public void Confirms_all_claimed_routes_and_reports_color_inventory_after_two_stable_frames()
    {
        var verifier = Inventory();
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, now, Complete);
        var second = Scene(2, 1, now.AddSeconds(1.1), Complete);

        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(first.Frame, first.Candidates, 1, 1).State);
        var confirmed = verifier.Observe(second.Frame, second.Candidates, 1, 1);
        Assert.True(confirmed.Confirmed);
        Assert.Equal(2, confirmed.ConfirmedByColor[MarkerColor.Blue]);
        Assert.Equal(2, confirmed.ConfirmedByColor[MarkerColor.Green]);
        Assert.Equal(2, confirmed.ConfirmedByColor.Count);
    }

    [Fact]
    public void Claimed_duluth_omaha_trains_remain_in_inventory_after_small_center_drift()
    {
        var verifier = new BoardInventoryVerifier([
            new("duluth--omaha--a", MarkerColor.Blue, 2)
        ]);
        var now = DateTimeOffset.UtcNow;
        var first = SceneAtResolution(1996, 1248, 1, 1, now,
            [new(1094.2, 444.6, MarkerColor.Blue), new(1069.9, 505.4, MarkerColor.Blue)]);
        var second = SceneAtResolution(1996, 1248, 2, 1, now.AddSeconds(1.1),
            [new(1093.7, 445.1, MarkerColor.Blue), new(1069.2, 505.4, MarkerColor.Blue)]);

        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(first.Frame, first.Candidates, 1, 1).State);
        Assert.True(verifier.Observe(second.Frame, second.Candidates, 1, 1).Confirmed);
    }

    [Fact]
    public void Gameplay_retains_confirmed_route_colors_when_current_color_samples_become_unreadable()
    {
        BoardInventoryRoute[] routes = [new(BlueRoute, MarkerColor.Blue, 2), new(GreenRoute, MarkerColor.Green, 2)];
        var gameplay = new BoardInventoryVerifier(routes, verifyClaimedRouteColors: false);
        var at = DateTimeOffset.UtcNow;
        var first = NeutralScene(1, at, Complete);
        var second = NeutralScene(2, at.AddSeconds(1.1), Complete);

        Assert.All(first.Candidates, candidate => Assert.Null(RoutePlacementVerifier.ReadCandidateColor(first.Frame, candidate)));
        Assert.Equal(BoardInventoryState.Stabilizing, gameplay.Observe(first.Frame, first.Candidates, 1, 1).State);
        var accepted = gameplay.Observe(second.Frame, second.Candidates, 1, 1);
        Assert.True(accepted.Confirmed);
        Assert.Equal(2, accepted.ConfirmedByColor[MarkerColor.Blue]);
        Assert.Equal(2, accepted.ConfirmedByColor[MarkerColor.Green]);
        // The default checkpoint audit still requires visible evidence of every train color.
        Assert.Equal(BoardInventoryState.Ambiguous,
            new BoardInventoryVerifier(routes).Observe(second.Frame, second.Candidates, 1, 1).State);
    }

    [Fact]
    public void Gameplay_color_trust_does_not_allow_missing_moved_duplicate_or_extra_trains()
    {
        BoardInventoryVerifier Gameplay() => new(
            [new(BlueRoute, MarkerColor.Blue, 2), new(GreenRoute, MarkerColor.Green, 2)],
            verifyClaimedRouteColors: false);
        var at = DateTimeOffset.UtcNow;
        var missing = NeutralScene(1, at, Complete[..^1]);
        Assert.Equal(BoardInventoryState.MissingTrains, Gameplay().Observe(missing.Frame, missing.Candidates, 1, 1).State);
        var moved = NeutralScene(1, at, [.. Complete[..^1], new(900, 400, MarkerColor.Green)]);
        Assert.Equal(BoardInventoryState.MissingTrains, Gameplay().Observe(moved.Frame, moved.Candidates, 1, 1).State);
        var complete = NeutralScene(1, at, Complete);
        Assert.Equal(BoardInventoryState.Ambiguous,
            Gameplay().Observe(complete.Frame, [.. complete.Candidates, complete.Candidates[0]], 1, 1).State);
        var extra = NeutralScene(1, at, [.. Complete, new(900, 400, MarkerColor.Yellow)]);
        Assert.Equal(BoardInventoryState.UnexpectedTrain, Gameplay().Observe(extra.Frame, extra.Candidates, 1, 1).State);
    }

    [Fact]
    public void Gameplay_must_still_verify_new_route_colors_before_payment()
    {
        BoardInventoryVerifier Pending() => new([new(GreenRoute, MarkerColor.Green, 2)],
            new(BlueRoute, MarkerColor.Blue, 2), pendingSlotMask: 3, verifyClaimedRouteColors: false);
        var at = DateTimeOffset.UtcNow;
        var unreadable = NeutralScene(1, at, Complete);
        var ambiguous = Pending().Observe(unreadable.Frame, unreadable.Candidates, 1, 1);
        Assert.Equal(BoardInventoryState.Ambiguous, ambiguous.State);
        Assert.Equal(BlueRoute, ambiguous.RouteId);
        var wrong = Scene(1, 1, at, [new(1586, 755, MarkerColor.Red), .. Complete[1..]]);
        Assert.Equal(BoardInventoryState.WrongColor, Pending().Observe(wrong.Frame, wrong.Candidates, 1, 1).State);
        var partial = Scene(1, 1, at, Complete[1..]);
        Assert.Equal(BoardInventoryState.MissingTrains, Pending().Observe(partial.Frame, partial.Candidates, 1, 1).State);

        var verifier = Pending();
        var first = Scene(1, 1, at, Complete);
        var stable = Scene(2, 1, at.AddSeconds(1.1), Complete);
        Assert.Equal(BoardInventoryState.Stabilizing, verifier.Observe(first.Frame, first.Candidates, 1, 1).State);
        Assert.True(verifier.Observe(stable.Frame, stable.Candidates, 1, 1).Confirmed);
    }

    [Fact]
    public void Gameplay_occupancy_still_requires_distinct_frames_and_current_calibration()
    {
        var verifier = new BoardInventoryVerifier(
            [new(BlueRoute, MarkerColor.Blue, 2), new(GreenRoute, MarkerColor.Green, 2)],
            verifyClaimedRouteColors: false);
        var at = DateTimeOffset.UtcNow;
        var first = NeutralScene(1, at, Complete);
        Assert.Equal(BoardInventoryState.Stabilizing, verifier.Observe(first.Frame, first.Candidates, 1, 1).State);
        Assert.Equal(BoardInventoryState.WaitingForFreshFrame, verifier.Observe(first.Frame, first.Candidates, 1, 1).State);
        var changed = NeutralScene(2, at.AddSeconds(1.1), Complete);
        Assert.Equal(BoardInventoryState.Stabilizing, verifier.Observe(changed.Frame, changed.Candidates, 2, 1).State);
        var stable = NeutralScene(3, at.AddSeconds(2.2), Complete);
        Assert.True(verifier.Observe(stable.Frame, stable.Candidates, 2, 1).Confirmed);
    }

    [Fact]
    public void Missing_wrong_color_or_ambiguous_route_never_confirms()
    {
        var now = DateTimeOffset.UtcNow;
        var missing = Scene(1, 1, now, Complete[..^1]);
        Assert.Equal(BoardInventoryState.MissingTrains,
            Inventory().Observe(missing.Frame, missing.Candidates, 1, 1).State);

        var wrong = Scene(1, 1, now,
            [.. Complete[..^1], new Train(1220, 638, MarkerColor.Red)]);
        Assert.Equal(BoardInventoryState.WrongColor,
            Inventory().Observe(wrong.Frame, wrong.Candidates, 1, 1).State);

        var good = Scene(1, 1, now, Complete);
        Assert.Equal(BoardInventoryState.Ambiguous,
            Inventory().Observe(good.Frame,
                [good.Candidates[0], good.Candidates[0], good.Candidates[2], good.Candidates[3]],
                1, 1).State);
    }

    [Fact]
    public void Extra_train_elsewhere_on_the_board_blocks_save_even_when_claims_match()
    {
        var verifier = Inventory();
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, now, Complete);
        var extra = Scene(2, 1, now.AddSeconds(1.1),
            [.. Complete, new Train(900, 400, MarkerColor.Yellow)]);
        var restored = Scene(3, 1, now.AddSeconds(2.2), Complete);
        var final = Scene(4, 1, now.AddSeconds(3.3), Complete);

        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(first.Frame, first.Candidates, 1, 1).State);
        var rejected = verifier.Observe(extra.Frame, extra.Candidates, 1, 1);
        Assert.Equal(BoardInventoryState.UnexpectedTrain, rejected.State);
        var extraDetection = Assert.Single(rejected.UnexpectedDetections);
        Assert.Equal(MarkerColor.Yellow, extraDetection.Color);
        var recovering = verifier.Observe(restored.Frame, restored.Candidates, 1, 1);
        Assert.Equal(BoardInventoryState.Stabilizing, recovering.State);
        Assert.Empty(recovering.UnexpectedDetections);
        var confirmed = verifier.Observe(final.Frame, final.Candidates, 1, 1);
        Assert.True(confirmed.Confirmed);
        Assert.Empty(confirmed.UnexpectedDetections);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Missing_Boston_train_identifies_only_its_unverified_space(int missing)
    {
        const string route = "boston--new-york--a";
        Train[] trains = [new(1838, 299, MarkerColor.Blue), new(1804, 357, MarkerColor.Blue)];
        var scene = Scene(1, 1, DateTimeOffset.UtcNow, [trains[1 - missing]]);
        var result = new BoardInventoryVerifier([new(route, MarkerColor.Blue, 2)],
                verifyClaimedRouteColors: false)
            .Observe(scene.Frame, scene.Candidates, 1, 1);

        Assert.Equal(BoardInventoryState.MissingTrains, result.State);
        Assert.Equal(route, result.RouteId);
        Assert.Equal(1 << missing, result.UnverifiedSlotMask);
        Assert.Empty(result.UnexpectedDetections);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Ambiguous_Boston_train_identifies_the_duplicate_space(int ambiguous)
    {
        const string route = "boston--new-york--a";
        var scene = Scene(1, 1, DateTimeOffset.UtcNow,
            [new(1838, 299, MarkerColor.Blue), new(1804, 357, MarkerColor.Blue)]);
        var result = new BoardInventoryVerifier([new(route, MarkerColor.Blue, 2)],
                verifyClaimedRouteColors: false)
            .Observe(scene.Frame, [.. scene.Candidates, scene.Candidates[ambiguous]], 1, 1);

        Assert.Equal(BoardInventoryState.Ambiguous, result.State);
        Assert.Equal(route, result.RouteId);
        Assert.Equal(1 << ambiguous, result.UnverifiedSlotMask);
    }

    [Fact]
    public void A_piece_changed_after_the_first_matching_frame_cannot_use_stale_route_confirmation()
    {
        var verifier = Inventory();
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, now, Complete);
        var changed = Scene(2, 1, now.AddSeconds(1.1),
            [.. Complete[..^1], new Train(1220, 638, MarkerColor.Red)]);
        var restored = Scene(3, 1, now.AddSeconds(2.2), Complete);
        var final = Scene(4, 1, now.AddSeconds(3.3), Complete);

        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(first.Frame, first.Candidates, 1, 1).State);
        Assert.Equal(BoardInventoryState.WrongColor,
            verifier.Observe(changed.Frame, changed.Candidates, 1, 1).State);
        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(restored.Frame, restored.Candidates, 1, 1).State);
        Assert.True(verifier.Observe(final.Frame, final.Candidates, 1, 1).Confirmed);
    }

    [Fact]
    public void Both_frames_must_be_at_least_one_second_apart()
    {
        var verifier = Inventory();
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, now, Complete);
        var tooSoon = Scene(2, 1, now.AddSeconds(.5), Complete);
        var later = Scene(3, 1, now.AddSeconds(1.1), Complete);

        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(first.Frame, first.Candidates, 1, 1).State);
        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(tooSoon.Frame, tooSoon.Candidates, 1, 1).State);
        Assert.True(verifier.Observe(later.Frame, later.Candidates, 1, 1).Confirmed);
    }

    [Fact]
    public void Empty_board_can_confirm_zero_inventory_but_any_train_blocks_it()
    {
        var verifier = new BoardInventoryVerifier([]);
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, now, []);
        var second = Scene(2, 1, now.AddSeconds(1.1), []);
        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(first.Frame, first.Candidates, 1, 1).State);
        var confirmed = verifier.Observe(second.Frame, second.Candidates, 1, 1);
        Assert.True(confirmed.Confirmed);
        Assert.Empty(confirmed.ConfirmedByColor);

        var train = Scene(3, 1, now.AddSeconds(2.2), [new Train(1586, 755, MarkerColor.Blue)]);
        Assert.Equal(BoardInventoryState.UnexpectedTrain,
            verifier.Observe(train.Frame, train.Candidates, 1, 1).State);
    }

    [Fact]
    public void Repeated_frame_and_camera_or_calibration_change_restart_stability()
    {
        var verifier = Inventory();
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, now, Complete);
        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(first.Frame, first.Candidates, 1, 1).State);
        Assert.Equal(BoardInventoryState.WaitingForFreshFrame,
            verifier.Observe(first.Frame, first.Candidates, 1, 1).State);

        var changedCrop = Scene(2, 1, now.AddSeconds(1.1), Complete);
        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(changedCrop.Frame, changedCrop.Candidates, 2, 1).State);
        var changedModel = Scene(3, 1, now.AddSeconds(2.2), Complete);
        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(changedModel.Frame, changedModel.Candidates, 2, 2).State);
        var changedCamera = Scene(4, 2, now.AddSeconds(3.3), Complete);
        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(changedCamera.Frame, changedCamera.Candidates, 2, 2).State);
        var stable = Scene(5, 2, now.AddSeconds(4.4), Complete);
        Assert.True(verifier.Observe(stable.Frame, stable.Candidates, 2, 2).Confirmed);
    }

    [Fact]
    public void Invalid_or_duplicate_expected_route_is_unsupported()
    {
        var frame = Scene(1, 1, DateTimeOffset.UtcNow, []);
        Assert.Equal(BoardInventoryState.Unsupported,
            new BoardInventoryVerifier([new("unmeasured", MarkerColor.Blue, 2)])
                .Observe(frame.Frame, frame.Candidates, 1, 1).State);
        Assert.Equal(BoardInventoryState.Unsupported,
            new BoardInventoryVerifier([
                new(BlueRoute, MarkerColor.Blue, 2), new(BlueRoute, MarkerColor.Red, 2)])
                .Observe(frame.Frame, frame.Candidates, 1, 1).State);
    }

    private static BoardInventoryVerifier Inventory() => new(
        [new(BlueRoute, MarkerColor.Blue, 2), new(GreenRoute, MarkerColor.Green, 2)]);

    [Fact]
    public void Unexpected_trains_report_the_unclaimed_route_color_and_count_without_confirming_it()
    {
        const string pending = "los-angeles--phoenix";
        const string unexpected = "calgary--winnipeg";
        ClassicUsRouteGeometry.TryGetSlots(pending, out var blueSlots);
        ClassicUsRouteGeometry.TryGetSlots(unexpected, out var yellowSlots);
        var blue = blueSlots.Select(slot => new Train(slot.ReferenceX, slot.ReferenceY, MarkerColor.Blue)).ToArray();
        var yellow = yellowSlots.Select(slot => new Train(slot.ReferenceX, slot.ReferenceY, MarkerColor.Yellow)).ToArray();
        var verifier = new BoardInventoryVerifier([new(pending, MarkerColor.Blue, 3)]);
        var at = DateTimeOffset.UtcNow;
        for (var sequence = 1; sequence <= 2; sequence++)
        {
            var extra = Scene(sequence, 1, at.AddSeconds(sequence), [.. blue, .. yellow]);
            var result = verifier.Observe(extra.Frame, extra.Candidates, 1, 1);
            Assert.Equal(BoardInventoryState.UnexpectedTrain, result.State);
            Assert.Equal(new UnexpectedTrainLocation(unexpected, MarkerColor.Yellow, 6), result.UnexpectedTrains);
            Assert.Empty(result.ConfirmedByColor);
            Assert.Equal(6, result.UnexpectedDetections.Count);
            Assert.All(result.UnexpectedDetections, detection =>
            {
                Assert.Equal(unexpected, detection.RouteId);
                Assert.Equal(MarkerColor.Yellow, detection.Color);
            });
        }
        var removed = Scene(3, 1, at.AddSeconds(3.1), blue);
        Assert.Equal(BoardInventoryState.Stabilizing, verifier.Observe(removed.Frame, removed.Candidates, 1, 1).State);
        var stable = Scene(4, 1, at.AddSeconds(4.2), blue);
        var confirmed = verifier.Observe(stable.Frame, stable.Candidates, 1, 1);
        Assert.True(confirmed.Confirmed);
        Assert.Null(confirmed.UnexpectedTrains);
    }

    [Theory]
    [InlineData(950, 1140)] // Off the printed routes: retain the box without inventing a route.
    [InlineData(1596, 762)] // Between Atlanta-Raleigh lanes: do not guess a lane.
    public void Unexpected_trains_with_no_unique_route_still_report_the_exact_detected_location(double x, double y)
    {
        var scene = SceneAtResolution(1996, 1248, 1, 1, DateTimeOffset.UtcNow,
            [new(x, y, MarkerColor.Blue)]);
        var result = new BoardInventoryVerifier([]).Observe(scene.Frame, scene.Candidates, 1, 1);
        Assert.Equal(BoardInventoryState.UnexpectedTrain, result.State);
        Assert.Null(result.UnexpectedTrains);
        var detection = Assert.Single(result.UnexpectedDetections);
        Assert.Null(detection.RouteId);
        Assert.Equal(MarkerColor.Blue, detection.Color);
        Assert.Equal(.9, detection.Confidence);
        Assert.Equal((x - 11) / 1996, detection.X, 9);
        Assert.Equal((y - 7) / 1248, detection.Y, 9);
        Assert.Equal(22d / 1996, detection.Width, 9);
        Assert.Equal(14d / 1248, detection.Height, 9);
    }

    [Fact]
    public void Multiple_unmatched_detections_are_all_reported_including_unnamed_locations()
    {
        var scene = SceneAtResolution(1996, 1248, 1, 1, DateTimeOffset.UtcNow,
            [new(950, 1140, MarkerColor.Blue), new(1596, 762, MarkerColor.Green),
                new(1154, 640, MarkerColor.Yellow)]);
        var result = new BoardInventoryVerifier([]).Observe(scene.Frame, scene.Candidates, 1, 1);

        Assert.Equal(BoardInventoryState.UnexpectedTrain, result.State);
        Assert.Equal(3, result.UnexpectedDetections.Count);
        Assert.Collection(result.UnexpectedDetections,
            detection =>
            {
                Assert.Null(detection.RouteId);
                Assert.Equal(MarkerColor.Blue, detection.Color);
            },
            detection =>
            {
                Assert.Null(detection.RouteId);
                Assert.Equal(MarkerColor.Green, detection.Color);
            },
            detection =>
            {
                Assert.Equal(GreenRoute, detection.RouteId);
                Assert.Equal(MarkerColor.Yellow, detection.Color);
            });
    }

    [Fact]
    public void Duplicate_detections_over_an_expected_train_space_are_not_omitted_from_details()
    {
        var scene = SceneAtResolution(1996, 1248, 1, 1, DateTimeOffset.UtcNow, Complete);
        var result = Inventory().Observe(scene.Frame, [.. scene.Candidates, scene.Candidates[0]], 1, 1);

        Assert.Equal(BoardInventoryState.Ambiguous, result.State);
        Assert.Equal(2, result.UnexpectedDetections.Count);
        Assert.All(result.UnexpectedDetections, detection =>
        {
            Assert.Equal(BlueRoute, detection.RouteId);
            Assert.Equal((1586d - 11) / 1996, detection.X, 9);
            Assert.Equal((755d - 7) / 1248, detection.Y, 9);
        });
    }

    [Fact]
    public void An_unreadable_color_does_not_discard_the_detected_location()
    {
        var scene = NeutralScene(1, DateTimeOffset.UtcNow, [new(950, 1140, MarkerColor.Blue)]);
        var result = new BoardInventoryVerifier([]).Observe(scene.Frame, scene.Candidates, 1, 1);

        Assert.Equal(BoardInventoryState.UnexpectedTrain, result.State);
        var detection = Assert.Single(result.UnexpectedDetections);
        Assert.Null(detection.Color);
        Assert.Null(detection.RouteId);
        Assert.True(detection.Width > 0 && detection.Height > 0);
    }

    [Fact]
    public void Wrong_color_details_highlight_the_observed_piece_without_relabeling_its_color()
    {
        var scene = SceneAtResolution(1996, 1248, 1, 1, DateTimeOffset.UtcNow,
            [.. Complete[..^1], new(1220, 638, MarkerColor.Red)]);
        var result = Inventory().Observe(scene.Frame, scene.Candidates, 1, 1);

        Assert.Equal(BoardInventoryState.WrongColor, result.State);
        var detection = Assert.Single(result.UnexpectedDetections);
        Assert.Equal(GreenRoute, detection.RouteId);
        Assert.Equal(MarkerColor.Red, detection.Color);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Save_records_exact_stable_pending_slots_without_requiring_the_whole_route(int mask)
    {
        var verifier = new BoardInventoryVerifier([new(GreenRoute, MarkerColor.Green, 2)],
            new(BlueRoute, MarkerColor.Blue, 2));
        var trains = Complete.Where((_, index) => index >= 2 || (mask & (1 << index)) != 0).ToArray();
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, now, trains);
        var stable = Scene(2, 1, now.AddSeconds(1.1), trains);
        Assert.Equal(BoardInventoryState.Stabilizing,
            verifier.Observe(first.Frame, first.Candidates, 1, 1).State);
        var result = verifier.Observe(stable.Frame, stable.Candidates, 1, 1);
        Assert.True(result.Confirmed);
        Assert.Equal(mask, result.PendingSlotMask);
        Assert.Equal(System.Numerics.BitOperations.PopCount((uint)mask), result.ConfirmedByColor[MarkerColor.Blue]);
        Assert.Equal(2, result.ConfirmedByColor[MarkerColor.Green]);
    }

    [Fact]
    public void Moving_pending_train_to_another_slot_restarts_save_stability_and_fails_exact_restore()
    {
        var verifier = new BoardInventoryVerifier([new(GreenRoute, MarkerColor.Green, 2)],
            new(BlueRoute, MarkerColor.Blue, 2));
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, 1, now, [Complete[0], .. Complete[2..]]);
        var moved = Scene(2, 1, now.AddSeconds(1.1), [.. Complete[1..]]);
        var stable = Scene(3, 1, now.AddSeconds(2.2), [.. Complete[1..]]);
        verifier.Observe(first.Frame, first.Candidates, 1, 1);
        Assert.Equal(BoardInventoryState.Stabilizing, verifier.Observe(moved.Frame, moved.Candidates, 1, 1).State);
        Assert.Equal(2, verifier.Observe(stable.Frame, stable.Candidates, 1, 1).PendingSlotMask);

        var restore = new BoardInventoryVerifier([new(GreenRoute, MarkerColor.Green, 2)],
            new(BlueRoute, MarkerColor.Blue, 2), pendingSlotMask: 1);
        Assert.Equal(BoardInventoryState.UnexpectedTrain,
            restore.Observe(moved.Frame, moved.Candidates, 1, 1).State);
    }

    [Fact]
    public void Pending_progress_does_not_allow_wrong_colors_duplicates_or_missing_committed_trains()
    {
        BoardInventoryVerifier Pending() => new([new(GreenRoute, MarkerColor.Green, 2)],
            new(BlueRoute, MarkerColor.Blue, 2));
        var now = DateTimeOffset.UtcNow;
        var wrong = Scene(1, 1, now, [new(1586, 755, MarkerColor.Red), .. Complete[2..]]);
        Assert.Equal(BoardInventoryState.WrongColor, Pending().Observe(wrong.Frame, wrong.Candidates, 1, 1).State);
        var missingCommitted = Scene(1, 1, now, [Complete[0]]);
        Assert.Equal(BoardInventoryState.MissingTrains,
            Pending().Observe(missingCommitted.Frame, missingCommitted.Candidates, 1, 1).State);
        var good = Scene(1, 1, now, Complete);
        Assert.Equal(BoardInventoryState.Ambiguous,
            Pending().Observe(good.Frame, [.. good.Candidates, good.Candidates[0]], 1, 1).State);
        var extra = Scene(1, 1, now, [.. Complete, new(900, 400, MarkerColor.Blue)]);
        Assert.Equal(BoardInventoryState.UnexpectedTrain, Pending().Observe(extra.Frame, extra.Candidates, 1, 1).State);
    }

    private readonly record struct Train(double X, double Y, MarkerColor Color);

    private static (CameraFrame Frame, PieceCandidate[] Candidates) NeutralScene(long sequence,
        DateTimeOffset capturedAt, Train[] trains)
    {
        var scene = Scene(sequence, 1, capturedAt, trains);
        var neutral = new byte[scene.Frame.Width * scene.Frame.Height * 4];
        Array.Fill(neutral, (byte)120);
        return (CameraFrame.CopyFromBgra32(scene.Frame.Width, scene.Frame.Height, neutral,
            sequence, 1, capturedAt), scene.Candidates);
    }

    private static (CameraFrame Frame, PieceCandidate[] Candidates) Scene(long sequence, long epoch,
        DateTimeOffset capturedAt, Train[] trains)
        => SceneAtResolution(960, 600, sequence, epoch, capturedAt, trains);

    private static (CameraFrame Frame, PieceCandidate[] Candidates) SceneAtResolution(int width, int height,
        long sequence, long epoch, DateTimeOffset capturedAt, Train[] trains)
    {
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = pixels[offset + 3] = 180;
        var candidates = new List<PieceCandidate>();
        foreach (var train in trains)
        {
            var x = (int)Math.Round(train.X / 1996 * width);
            var y = (int)Math.Round(train.Y / 1248 * height);
            var (red, green, blue) = train.Color switch
            {
                MarkerColor.Red => (190, 35, 30),
                MarkerColor.Green => (20, 125, 35),
                MarkerColor.Yellow => (225, 180, 20),
                MarkerColor.Blue => (20, 75, 195),
                _ => (20, 20, 20),
            };
            for (var py = y - 7; py <= y + 7; py++)
            for (var px = x - 11; px <= x + 11; px++)
            {
                var offset = (py * width + px) * 4;
                pixels[offset] = (byte)blue;
                pixels[offset + 1] = (byte)green;
                pixels[offset + 2] = (byte)red;
            }
            candidates.Add(new(PieceCandidateKind.Train,
                [new((double)(x - 11) / width, (double)(y - 7) / height),
                    new((double)(x + 11) / width, (double)(y - 7) / height),
                    new((double)(x + 11) / width, (double)(y + 7) / height),
                    new((double)(x - 11) / width, (double)(y + 7) / height)], .9));
        }
        return (CameraFrame.CopyFromBgra32(width, height, pixels, sequence, epoch, capturedAt),
            candidates.ToArray());
    }
}
