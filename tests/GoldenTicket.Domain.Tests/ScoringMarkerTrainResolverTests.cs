using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class ScoringMarkerTrainResolverTests
{
    private const int Width = 1996;
    private const int Height = 1248;
    private const string ClaimedRoute = "atlanta--raleigh--a";
    private const double MarkerX = .0311 * Width;
    private const double MarkerY = (.974 - 5 * (.974 - .026) / 20) * Height;
    private static readonly Piece Marker = new(PieceCandidateKind.PlayerMarker, MarkerX, MarkerY, 50, 44);
    private static readonly Piece Duplicate = Marker with { Kind = PieceCandidateKind.Train, Confidence = .98 };
    private static readonly Piece[] Claimed =
    [
        new(PieceCandidateKind.Train, 1586, 755, 22, 14, MarkerColor.Blue),
        new(PieceCandidateKind.Train, 1643, 712, 22, 14, MarkerColor.Blue)
    ];

    [Fact]
    public void Same_marker_cannot_be_counted_as_a_train_even_if_train_class_confidence_is_higher()
    {
        var scene = Scene([Marker, Duplicate]);
        var reading = Assert.Single(ScoreMarkerReader.Read(scene.Frame, scene.Candidates));
        Assert.Equal(ScoreMarkerReadingStatus.Read, reading.Status);
        Assert.Equal(5, reading.Score);

        var resolved = ScoringMarkerTrainResolver.Resolve(scene.Frame, scene.Candidates);

        Assert.Same(scene.Candidates[0], Assert.Single(resolved));
        Assert.Equal(2, scene.Candidates.Length); // Original model evidence is untouched.
    }

    [Fact]
    public void Saved_board_restore_confirms_claimed_trains_and_marker_despite_duplicate_train_outline()
    {
        var verifier = new SavedBoardRestoreVerifier([new(MarkerColor.Green, 5)],
            [new(ClaimedRoute, MarkerColor.Blue, 2)]);
        var at = DateTimeOffset.UtcNow;
        SavedBoardRestoreObservation? result = null;
        for (var sequence = 1; sequence <= 4; sequence++)
        {
            var scene = Scene([Marker, Duplicate, .. Claimed], sequence, at.AddSeconds(sequence * 1.1));
            result = verifier.Observe(scene.Frame, ScoreMarkerReader.Read(scene.Frame, scene.Candidates),
                scene.Candidates, 1, 1);
            Assert.True(result.Inventory is null || result.Inventory.UnexpectedDetections.Count == 0);
        }
        Assert.Equal(SavedBoardRestoreStage.Confirmed, result!.Stage);
        Assert.Equal(2, result.Inventory!.ConfirmedByColor[MarkerColor.Blue]);
    }

    [Theory]
    [InlineData(104, 920)] // A separate compact body next to the score marker.
    [InlineData(950, 1140)] // A genuine off-route train elsewhere.
    [InlineData(1154, 640)] // A train on an unclaimed printed route.
    public void Removing_marker_duplicate_does_not_hide_real_unexpected_trains(double x, double y)
    {
        var scene = Scene([Marker, Duplicate, .. Claimed,
            new(PieceCandidateKind.Train, x, y, 22, 14, MarkerColor.Green)]);
        var result = new BoardInventoryVerifier([new(ClaimedRoute, MarkerColor.Blue, 2)])
            .Observe(scene.Frame, scene.Candidates, 1, 1);

        Assert.Equal(BoardInventoryState.UnexpectedTrain, result.State);
        var detection = Assert.Single(result.UnexpectedDetections);
        Assert.Equal((x - 11) / Width, detection.X, 9);
        Assert.Equal((y - 7) / Height, detection.Y, 9);
        Assert.Equal(MarkerColor.Green, detection.Color);
    }

    [Fact]
    public void Removing_marker_duplicate_does_not_replace_a_missing_claimed_train()
    {
        var scene = Scene([Marker, Duplicate, Claimed[0]]);
        var result = new BoardInventoryVerifier([new(ClaimedRoute, MarkerColor.Blue, 2)])
            .Observe(scene.Frame, scene.Candidates, 1, 1);

        Assert.Equal(BoardInventoryState.MissingTrains, result.State);
        Assert.Equal(ClaimedRoute, result.RouteId);
        Assert.Empty(result.UnexpectedDetections);
    }

    [Theory]
    [InlineData("no marker")]
    [InlineData("weak marker")]
    [InlineData("off track")]
    [InlineData("ambiguous score")]
    [InlineData("unclear color")]
    [InlineData("nearby")]
    [InlineData("partial overlap")]
    [InlineData("elongated train")]
    [InlineData("oversized train")]
    public void Uncertain_or_distinct_bodies_are_never_suppressed(string condition)
    {
        Piece[] pieces = condition switch
        {
            "no marker" => [Duplicate],
            "weak marker" => [Marker with { Confidence = .54 }, Duplicate],
            "off track" => [Marker with { X = Width * .5 }, Duplicate with { X = Width * .5 }],
            "ambiguous score" => [Marker with { Y = MarkerY + (.974 - .026) / 40 * Height },
                Duplicate with { Y = MarkerY + (.974 - .026) / 40 * Height }],
            "unclear color" => [Marker with { Color = null }, Duplicate with { Color = null }],
            "nearby" => [Marker, Duplicate with { X = MarkerX + 54 }],
            "partial overlap" => [Marker, Duplicate with { X = MarkerX + 16 }],
            "elongated train" => [Marker, Duplicate with { Width = 70, Height = 20 }],
            "oversized train" => [Marker, Duplicate with { Width = 74, Height = 74 }],
            _ => throw new ArgumentOutOfRangeException(nameof(condition))
        };
        var scene = Scene(pieces);

        Assert.Same(scene.Candidates, ScoringMarkerTrainResolver.Resolve(scene.Frame, scene.Candidates));
        Assert.Equal(BoardInventoryState.UnexpectedTrain,
            new BoardInventoryVerifier([]).Observe(scene.Frame, scene.Candidates, 1, 1).State);
    }

    [Fact]
    public void Overlapping_boxes_with_different_sampled_colors_remain_separate()
    {
        var scene = Scene([Marker, Duplicate]);
        var pixels = scene.Frame.Bgra32.ToArray();
        // Use distinct sampling evidence: all sparse train samples are blue while the marker's
        // denser interior samples still independently read green. Overlap alone cannot decide.
        for (var gy = -3; gy <= 3; gy++)
        for (var gx = -3; gx <= 3; gx++)
        {
            if (gx * gx + gy * gy > 9) continue;
            Paint(pixels, (int)Math.Round(MarkerX + gx * Marker.Width / 12),
                (int)Math.Round(MarkerY + gy * Marker.Height / 12), MarkerColor.Blue);
        }
        var frame = CameraFrame.CopyFromBgra32(Width, Height, pixels, sequence: 1);
        Assert.Equal(MarkerColor.Green, Assert.Single(ScoreMarkerReader.Read(frame, scene.Candidates)).Color);
        Assert.Equal(MarkerColor.Blue, RoutePlacementVerifier.ReadCandidateColor(frame, scene.Candidates[1]));

        Assert.Same(scene.Candidates, ScoringMarkerTrainResolver.Resolve(frame, scene.Candidates));
    }

    private sealed record Piece(PieceCandidateKind Kind, double X, double Y, double Width, double Height,
        MarkerColor? Color = MarkerColor.Green, double Confidence = .9);

    private static (CameraFrame Frame, PieceCandidate[] Candidates) Scene(Piece[] pieces,
        long sequence = 1, DateTimeOffset? at = null)
    {
        var pixels = new byte[Width * Height * 4];
        Array.Fill(pixels, (byte)180);
        var candidates = new List<PieceCandidate>();
        foreach (var piece in pieces)
        {
            var left = piece.X - piece.Width / 2;
            var right = piece.X + piece.Width / 2;
            var top = piece.Y - piece.Height / 2;
            var bottom = piece.Y + piece.Height / 2;
            for (var y = Math.Max(0, (int)Math.Floor(top)); y < Math.Min(Height, bottom); y++)
            for (var x = Math.Max(0, (int)Math.Floor(left)); x < Math.Min(Width, right); x++)
                Paint(pixels, x, y, piece.Color);
            candidates.Add(new(piece.Kind,
                [new(left / Width, top / Height), new(right / Width, top / Height),
                    new(right / Width, bottom / Height), new(left / Width, bottom / Height)], piece.Confidence));
        }
        return (CameraFrame.CopyFromBgra32(Width, Height, pixels, sequence, 1, at), candidates.ToArray());
    }

    private static void Paint(byte[] pixels, int x, int y, MarkerColor? color)
    {
        var (r, g, b) = color switch
        {
            MarkerColor.Blue => (20, 75, 195),
            MarkerColor.Green => (20, 125, 35),
            _ => (180, 180, 180)
        };
        var offset = (y * Width + x) * 4;
        pixels[offset] = (byte)b;
        pixels[offset + 1] = (byte)g;
        pixels[offset + 2] = (byte)r;
        pixels[offset + 3] = 255;
    }
}
