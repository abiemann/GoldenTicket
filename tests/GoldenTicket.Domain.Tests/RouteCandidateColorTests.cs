using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class RouteCandidateColorTests
{
    private const string CalgaryHelena = "calgary--helena";

    [Theory]
    [InlineData(1, 0)] // 17 of the current 29 interior samples are black.
    [InlineData(2, -4)] // 18 black samples.
    [InlineData(3, -8)] // 19 black samples.
    public void Neutral_highlights_do_not_compete_with_a_supported_black_piece(int slope, int cutoff)
    {
        var scene = CandidateScene(MarkerColor.Black, null, slope, cutoff);

        Assert.Equal(MarkerColor.Black,
            RoutePlacementVerifier.ReadCandidateColor(scene.Frame, scene.Candidates[0]));
    }

    [Fact]
    public void Neutral_pixels_still_count_against_the_minimum_color_support()
    {
        // The sloping highlight leaves only 15/29 black samples, below the 55% floor.
        var insufficient = CandidateScene(MarkerColor.Black, null, 3, 0);
        Assert.Null(RoutePlacementVerifier.ReadCandidateColor(insufficient.Frame,
            insufficient.Candidates[0]));

        var entirelyNeutral = CandidateScene(null, null, 1, 0);
        Assert.Null(RoutePlacementVerifier.ReadCandidateColor(entirelyNeutral.Frame,
            entirelyNeutral.Candidates[0]));
    }

    [Theory]
    [InlineData(MarkerColor.Blue)]
    [InlineData(MarkerColor.Red)]
    public void A_real_competing_piece_color_remains_ambiguous(MarkerColor competing)
    {
        // Black clears the total-support floor, but 17 black versus 12 colored samples
        // cannot establish the required lead over another physical piece color.
        var mixed = CandidateScene(MarkerColor.Black, competing, 1, 0);

        Assert.Null(RoutePlacementVerifier.ReadCandidateColor(mixed.Frame, mixed.Candidates[0]));
    }

    [Theory]
    [InlineData(MarkerColor.Blue)]
    [InlineData(MarkerColor.Red)]
    [InlineData(MarkerColor.Green)]
    [InlineData(MarkerColor.Yellow)]
    [InlineData(MarkerColor.Black)]
    public void Neutral_highlight_handling_preserves_every_physical_color(MarkerColor color)
    {
        var scene = CandidateScene(color, null, 1, 0);

        Assert.Equal(color, RoutePlacementVerifier.ReadCandidateColor(scene.Frame, scene.Candidates[0]));
    }

    [Fact]
    public void Four_highlighted_black_trains_confirm_but_a_red_train_still_rejects_the_route()
    {
        var verifier = new RoutePlacementVerifier();
        var at = DateTimeOffset.UtcNow;
        var first = RouteScene(1, at);
        var second = RouteScene(2, at.AddSeconds(1.1));
        Assert.Equal(RoutePlacementState.Stabilizing, verifier.Observe(first.Frame,
            first.Candidates, CalgaryHelena, MarkerColor.Black, 4, "claim", 1, 1).State);
        Assert.True(verifier.Observe(second.Frame, second.Candidates,
            CalgaryHelena, MarkerColor.Black, 4, "claim", 1, 1).Confirmed);

        var wrong = RouteScene(3, at.AddSeconds(2.2), lastColor: MarkerColor.Red);
        var rejected = verifier.Observe(wrong.Frame, wrong.Candidates,
            CalgaryHelena, MarkerColor.Black, 4, "claim", 1, 1);
        Assert.Equal(RoutePlacementState.WrongColor, rejected.State);
        Assert.Equal(3, rejected.MatchedCount);
        Assert.Equal(0b1000, rejected.UnverifiedSlotMask);
        Assert.False(rejected.Confirmed);
    }

    private static (CameraFrame Frame, PieceCandidate[] Candidates) CandidateScene(
        MarkerColor? color, MarkerColor? highlight, int slope, int cutoff)
    {
        const int width = 96, height = 60;
        var pixels = Blank(width, height);
        var candidate = PaintPiece(pixels, width, height, 48, 30, color, highlight, slope, cutoff);
        return (CameraFrame.CopyFromBgra32(width, height, pixels), [candidate]);
    }

    private static (CameraFrame Frame, PieceCandidate[] Candidates) RouteScene(
        long sequence, DateTimeOffset at, MarkerColor lastColor = MarkerColor.Black)
    {
        const int width = 1996, height = 1248;
        var pixels = Blank(width, height);
        Assert.True(ClassicUsRouteGeometry.TryGetSlots(CalgaryHelena, out var slots));
        var candidates = slots.Select((slot, index) => PaintPiece(pixels, width, height,
            (int)Math.Round(slot.ReferenceX), (int)Math.Round(slot.ReferenceY),
            index == slots.Count - 1 ? lastColor : MarkerColor.Black, null, 1, 0)).ToArray();
        return (CameraFrame.CopyFromBgra32(width, height, pixels, sequence, 1, at), candidates);
    }

    private static byte[] Blank(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)180);
        return pixels;
    }

    private static PieceCandidate PaintPiece(byte[] pixels, int width, int height, int cx, int cy,
        MarkerColor? body, MarkerColor? highlight, int slope, int cutoff)
    {
        // Paint continuous regions, not individual verifier sampling points: a diagonal
        // neutral highlight across the detected piece's body represents reflected light.
        const int halfSize = 24;
        for (var y = cy - halfSize; y <= cy + halfSize; y++)
        for (var x = cx - halfSize; x <= cx + halfSize; x++)
        {
            var color = x - cx + slope * (y - cy) >= cutoff ? body : highlight;
            var (red, green, blue) = color switch
            {
                MarkerColor.Blue => (20, 75, 195),
                MarkerColor.Red => (190, 35, 30),
                MarkerColor.Green => (20, 125, 35),
                MarkerColor.Yellow => (225, 180, 20),
                MarkerColor.Black => (20, 20, 20),
                _ => (180, 180, 180)
            };
            var offset = (y * width + x) * 4;
            pixels[offset] = (byte)blue;
            pixels[offset + 1] = (byte)green;
            pixels[offset + 2] = (byte)red;
            pixels[offset + 3] = 255;
        }
        return new(PieceCandidateKind.Train,
            [new((cx - halfSize) / (double)width, (cy - halfSize) / (double)height),
             new((cx + halfSize) / (double)width, (cy - halfSize) / (double)height),
             new((cx + halfSize) / (double)width, (cy + halfSize) / (double)height),
             new((cx - halfSize) / (double)width, (cy + halfSize) / (double)height)], .96);
    }
}
