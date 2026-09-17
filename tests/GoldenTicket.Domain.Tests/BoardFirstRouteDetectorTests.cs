using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class BoardFirstRouteDetectorTests
{
    private const string AtlantaRaleigh = "atlanta--raleigh--a";
    private const string AtlantaRaleighOtherLane = "atlanta--raleigh--b";
    private const string CalgaryVancouver = "calgary--vancouver";

    [Fact]
    public void Proposes_only_one_legal_route_after_distinct_stable_frames()
    {
        var detector = new BoardFirstRouteDetector();
        var legal = Routes(AtlantaRaleigh, AtlantaRaleighOtherLane, CalgaryVancouver);
        var at = DateTimeOffset.UtcNow;
        var first = Scene(1, at, AtlantaRaleigh);
        var second = Scene(2, at.AddSeconds(1.1), AtlantaRaleigh);

        Assert.Null(detector.Observe(first.Frame, first.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1));
        Assert.Null(detector.ProposedRouteId);
        Assert.Equal(AtlantaRaleigh,
            detector.Observe(second.Frame, second.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1));
        Assert.Equal(AtlantaRaleigh, detector.ProposedRouteId);
        Assert.Null(detector.Observe(Scene(3, at.AddSeconds(1.2), AtlantaRaleigh).Frame,
            second.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1));
        Assert.Equal(AtlantaRaleigh, detector.ProposedRouteId);
    }

    [Fact]
    public void Two_stably_empty_fresh_frames_clear_a_proposal_and_allow_a_new_one()
    {
        var detector = new BoardFirstRouteDetector();
        var legal = Routes(AtlantaRaleigh, CalgaryVancouver);
        var at = DateTimeOffset.UtcNow;
        var first = Scene(1, at, AtlantaRaleigh);
        var second = Scene(2, at.AddSeconds(1.1), AtlantaRaleigh);
        var fullAgain = Scene(3, at.AddSeconds(1.2), AtlantaRaleigh);
        var fullMuchLater = Scene(4, at.AddSeconds(2.4), AtlantaRaleigh);
        var empty = Scene(5, at.AddSeconds(2.5));
        var stillEmpty = Scene(6, at.AddSeconds(3.6));
        var newFirst = Scene(7, at.AddSeconds(3.7), CalgaryVancouver);
        var newSecond = Scene(8, at.AddSeconds(4.9), CalgaryVancouver);

        detector.Observe(first.Frame, first.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1);
        Assert.Equal(AtlantaRaleigh,
            detector.Observe(second.Frame, second.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1));
        // Reobserving the confirming frame cannot dismiss a still valid proposal.
        Assert.Null(detector.Observe(second.Frame, [], legal, MarkerColor.Blue, "turn-1", 1, 1));
        Assert.Equal(AtlantaRaleigh, detector.ProposedRouteId);
        detector.Observe(fullAgain.Frame, fullAgain.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1);
        detector.Observe(fullMuchLater.Frame, fullMuchLater.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1);
        Assert.Equal(AtlantaRaleigh, detector.ProposedRouteId);
        Assert.Null(detector.Observe(empty.Frame, empty.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1));
        Assert.Equal(AtlantaRaleigh, detector.ProposedRouteId);
        Assert.Null(detector.Observe(stillEmpty.Frame, stillEmpty.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1));
        Assert.Null(detector.ProposedRouteId);
        Assert.Null(detector.Observe(newFirst.Frame, newFirst.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1));
        Assert.Equal(CalgaryVancouver,
            detector.Observe(newSecond.Frame, newSecond.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1));
    }

    [Fact]
    public void A_transient_missing_frame_does_not_dismiss_a_proposal()
    {
        var detector = new BoardFirstRouteDetector();
        var legal = Routes(AtlantaRaleigh);
        var at = DateTimeOffset.UtcNow;
        var first = Scene(1, at, AtlantaRaleigh);
        var second = Scene(2, at.AddSeconds(1.1), AtlantaRaleigh);
        var missing = Scene(3, at.AddSeconds(1.2));
        var restored = Scene(4, at.AddSeconds(2.4), AtlantaRaleigh);
        var missingAgain = Scene(5, at.AddSeconds(2.5));
        var stillMissing = Scene(6, at.AddSeconds(3.6));

        detector.Observe(first.Frame, first.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1);
        Assert.Equal(AtlantaRaleigh,
            detector.Observe(second.Frame, second.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1));
        detector.Observe(missing.Frame, missing.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1);
        Assert.Equal(AtlantaRaleigh, detector.ProposedRouteId);
        detector.Observe(restored.Frame, restored.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1);
        detector.Observe(missingAgain.Frame, missingAgain.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1);
        Assert.Equal(AtlantaRaleigh, detector.ProposedRouteId);
        detector.Observe(stillMissing.Frame, stillMissing.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1);
        Assert.Null(detector.ProposedRouteId);
    }

    [Fact]
    public void An_ambiguous_frame_does_not_count_as_absence()
    {
        var detector = new BoardFirstRouteDetector();
        var legal = Routes(AtlantaRaleigh);
        var at = DateTimeOffset.UtcNow;
        var first = Scene(1, at, AtlantaRaleigh);
        var second = Scene(2, at.AddSeconds(1.1), AtlantaRaleigh);
        var ambiguous = Scene(3, at.AddSeconds(1.2), AtlantaRaleigh);
        var empty = Scene(4, at.AddSeconds(2.3));
        var stillEmpty = Scene(5, at.AddSeconds(3.5));

        detector.Observe(first.Frame, first.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1);
        Assert.Equal(AtlantaRaleigh,
            detector.Observe(second.Frame, second.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1));
        detector.Observe(ambiguous.Frame,
            [.. ambiguous.Candidates, ambiguous.Candidates[0]], legal, MarkerColor.Blue, "turn-1", 1, 1);
        Assert.Equal(AtlantaRaleigh, detector.ProposedRouteId);
        detector.Observe(empty.Frame, empty.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1);
        Assert.Equal(AtlantaRaleigh, detector.ProposedRouteId);
        detector.Observe(stillEmpty.Frame, stillEmpty.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1);
        Assert.Null(detector.ProposedRouteId);
    }

    [Fact]
    public void Simultaneous_complete_routes_are_ambiguous_even_if_one_was_stabilizing()
    {
        var detector = new BoardFirstRouteDetector();
        var legal = Routes(AtlantaRaleigh, CalgaryVancouver);
        var at = DateTimeOffset.UtcNow;
        var first = Scene(1, at, AtlantaRaleigh);
        var both = Scene(2, at.AddSeconds(1.1), AtlantaRaleigh, CalgaryVancouver);
        var onlyOneAgain = Scene(3, at.AddSeconds(1.2), AtlantaRaleigh);
        var stable = Scene(4, at.AddSeconds(2.4), AtlantaRaleigh);

        Assert.Null(detector.Observe(first.Frame, first.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1));
        Assert.Null(detector.Observe(both.Frame, both.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1));
        Assert.Null(detector.ProposedRouteId);
        Assert.Null(detector.Observe(onlyOneAgain.Frame, onlyOneAgain.Candidates,
            legal, MarkerColor.Blue, "turn-1", 1, 1));
        Assert.Equal(AtlantaRaleigh,
            detector.Observe(stable.Frame, stable.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1));
    }

    [Fact]
    public void Changes_to_turn_or_legal_set_restart_proposal_stability()
    {
        var detector = new BoardFirstRouteDetector();
        var legal = Routes(AtlantaRaleigh);
        var at = DateTimeOffset.UtcNow;
        var first = Scene(1, at, AtlantaRaleigh);
        var next = Scene(2, at.AddSeconds(1.1), AtlantaRaleigh);
        var third = Scene(3, at.AddSeconds(2.2), AtlantaRaleigh);

        Assert.Null(detector.Observe(first.Frame, first.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1));
        Assert.Null(detector.Observe(next.Frame, next.Candidates, legal, MarkerColor.Blue, "turn-2", 1, 1));
        Assert.Null(detector.Observe(third.Frame, third.Candidates, [], MarkerColor.Blue, "turn-2", 1, 1));
        Assert.Null(detector.ProposedRouteId);
        detector.Reset();
        Assert.Null(detector.ProposedRouteId);
    }

    [Fact]
    public void Unsupported_or_nonlegal_routes_and_wrong_color_never_propose()
    {
        var detector = new BoardFirstRouteDetector();
        var legal = Routes(CalgaryVancouver).Append(("unmeasured", 2)).ToArray();
        var at = DateTimeOffset.UtcNow;
        var first = Scene(1, at, AtlantaRaleigh);
        var second = Scene(2, at.AddSeconds(1.1), AtlantaRaleigh);
        Assert.Null(detector.Observe(first.Frame, first.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1));
        Assert.Null(detector.Observe(second.Frame, second.Candidates, legal, MarkerColor.Blue, "turn-1", 1, 1));

        legal = Routes(AtlantaRaleigh);
        Assert.Null(detector.Observe(first.Frame, first.Candidates, legal, MarkerColor.Red, "turn-1", 1, 1));
        Assert.Null(detector.Observe(second.Frame, second.Candidates, legal, MarkerColor.Red, "turn-1", 1, 1));
        Assert.Null(detector.ProposedRouteId);
    }

    private static (string RouteId, int TrainCount)[] Routes(params string[] routeIds) =>
        routeIds.Select(id =>
        {
            ClassicUsRouteGeometry.TryGetSlots(id, out var slots);
            return (id, slots.Count);
        }).ToArray();

    private static (CameraFrame Frame, PieceCandidate[] Candidates) Scene(long sequence,
        DateTimeOffset capturedAt, params string[] occupiedRoutes)
    {
        const int width = 960;
        const int height = 600;
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = pixels[offset + 3] = 180;
        var candidates = new List<PieceCandidate>();
        foreach (var routeId in occupiedRoutes)
        {
            ClassicUsRouteGeometry.TryGetSlots(routeId, out var slots);
            foreach (var spot in slots)
            {
                var x = (int)Math.Round(spot.X * width);
                var y = (int)Math.Round(spot.Y * height);
                const int halfWidth = 11;
                const int halfHeight = 7;
                for (var py = y - halfHeight; py <= y + halfHeight; py++)
                for (var px = x - halfWidth; px <= x + halfWidth; px++)
                {
                    var offset = (py * width + px) * 4;
                    pixels[offset] = 195;
                    pixels[offset + 1] = 75;
                    pixels[offset + 2] = 20;
                }
                candidates.Add(new(PieceCandidateKind.Train,
                    [new((double)(x - halfWidth) / width, (double)(y - halfHeight) / height),
                     new((double)(x + halfWidth) / width, (double)(y - halfHeight) / height),
                     new((double)(x + halfWidth) / width, (double)(y + halfHeight) / height),
                     new((double)(x - halfWidth) / width, (double)(y + halfHeight) / height)], .9));
            }
        }
        return (CameraFrame.CopyFromBgra32(width, height, pixels, sequence, epoch: 1, capturedAt),
            candidates.ToArray());
    }
}
