using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class BoardFirstMoveFeedbackDetectorTests
{
    private const string SeattleCalgary = "calgary--seattle";
    private const string SeattleHelena = "helena--seattle";
    private const string AtlantaRaleigh = "atlanta--raleigh--a";

    [Fact]
    public void Two_trains_on_the_four_space_Seattle_route_get_stable_feedback()
    {
        var detector = new BoardFirstMoveFeedbackDetector();
        var routes = new[] { new BoardFirstDiagnosticRoute(SeattleCalgary, 4, false) };
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, now, (SeattleCalgary, 2));
        var second = Scene(2, now.AddSeconds(1.1), (SeattleCalgary, 2));

        Assert.Null(detector.Observe(first.Frame, first.Candidates, routes,
            MarkerColor.Yellow, "turn-2", 1, 1));
        Assert.Equal(new BoardFirstMoveFeedback(SeattleCalgary, 2, 4, false),
            detector.Observe(second.Frame, second.Candidates, routes,
                MarkerColor.Yellow, "turn-2", 1, 1));
    }

    [Fact]
    public void One_piece_or_an_ambiguous_partial_board_does_not_show_invalid_move()
    {
        var detector = new BoardFirstMoveFeedbackDetector();
        var routes = new[]
        {
            new BoardFirstDiagnosticRoute(SeattleCalgary, 4, false),
            new BoardFirstDiagnosticRoute(SeattleHelena, 6, false)
        };
        var now = DateTimeOffset.UtcNow;
        var one = Scene(1, now, (SeattleCalgary, 1));
        var oneLater = Scene(2, now.AddSeconds(1.1), (SeattleCalgary, 1));
        Assert.Null(detector.Observe(one.Frame, one.Candidates, routes,
            MarkerColor.Yellow, "turn-2", 1, 1));
        Assert.Null(detector.Observe(oneLater.Frame, oneLater.Candidates, routes,
            MarkerColor.Yellow, "turn-2", 1, 1));

        var ambiguous = Scene(3, now.AddSeconds(2.2), (SeattleCalgary, 2), (SeattleHelena, 2));
        var ambiguousLater = Scene(4, now.AddSeconds(3.3), (SeattleCalgary, 2), (SeattleHelena, 2));
        Assert.Null(detector.Observe(ambiguous.Frame, ambiguous.Candidates, routes,
            MarkerColor.Yellow, "turn-2", 1, 1));
        Assert.Null(detector.Observe(ambiguousLater.Frame, ambiguousLater.Candidates, routes,
            MarkerColor.Yellow, "turn-2", 1, 1));
    }

    [Fact]
    public void Complete_payable_route_never_shows_invalid_move()
    {
        var detector = new BoardFirstMoveFeedbackDetector();
        var routes = new[] { new BoardFirstDiagnosticRoute(AtlantaRaleigh, 2, true) };
        var now = DateTimeOffset.UtcNow;
        var first = Scene(1, now, (AtlantaRaleigh, 2));
        var second = Scene(2, now.AddSeconds(1.1), (AtlantaRaleigh, 2));
        Assert.Null(detector.Observe(first.Frame, first.Candidates, routes,
            MarkerColor.Yellow, "turn-2", 1, 1));
        Assert.Null(detector.Observe(second.Frame, second.Candidates, routes,
            MarkerColor.Yellow, "turn-2", 1, 1));
    }

    private static (CameraFrame Frame, PieceCandidate[] Candidates) Scene(long sequence,
        DateTimeOffset capturedAt, params (string RouteId, int Count)[] occupied)
    {
        const int width = 960;
        const int height = 600;
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = pixels[offset + 3] = 180;
        var candidates = new List<PieceCandidate>();
        foreach (var (routeId, count) in occupied)
        {
            ClassicUsRouteGeometry.TryGetSlots(routeId, out var slots);
            foreach (var spot in slots.Take(count))
            {
                var x = (int)Math.Round(spot.X * width);
                var y = (int)Math.Round(spot.Y * height);
                for (var py = y - 7; py <= y + 7; py++)
                for (var px = x - 11; px <= x + 11; px++)
                {
                    var offset = (py * width + px) * 4;
                    pixels[offset] = 20;
                    pixels[offset + 1] = 180;
                    pixels[offset + 2] = 225;
                }
                candidates.Add(new(PieceCandidateKind.Train,
                    [new((double)(x - 11) / width, (double)(y - 7) / height),
                        new((double)(x + 11) / width, (double)(y - 7) / height),
                        new((double)(x + 11) / width, (double)(y + 7) / height),
                        new((double)(x - 11) / width, (double)(y + 7) / height)], .9));
            }
        }
        return (CameraFrame.CopyFromBgra32(width, height, pixels, sequence, 1, capturedAt),
            candidates.ToArray());
    }
}
