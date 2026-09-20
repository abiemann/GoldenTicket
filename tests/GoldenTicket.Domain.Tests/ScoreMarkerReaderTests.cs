using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class ScoreMarkerReaderTests
{
    [Theory]
    [InlineData(.017, .026, 20)]
    [InlineData(.983, .026, 50)]
    [InlineData(.983, .974, 70)]
    [InlineData(.017, .974, 100)]
    [InlineData(.017, .9266, 1)]
    [InlineData(.017, .4526, 11)]
    [InlineData(.017, .263, 15)]
    [InlineData(.339, .026, 30)]
    [InlineData(.983, .4526, 59)]
    [InlineData(.661, .974, 80)]
    public void Reads_the_printed_track_including_all_corners(double x, double y, int expected)
    {
        var (frame, candidates) = Scene((x, y, MarkerColor.Blue));
        var result = Assert.Single(ScoreMarkerReader.Read(frame, candidates));
        Assert.Equal(expected, result.Score);
        Assert.Equal(MarkerColor.Blue, result.Color);
        Assert.Equal(ScoreMarkerReadingStatus.Read, result.Status);
    }

    [Fact]
    public void Inward_markers_share_a_vertical_row_even_near_a_corner()
    {
        var (frame, candidates) = Scene((.0117, .9345, MarkerColor.Black), (.0305, .9313, MarkerColor.Red),
            (.0502, .9258, MarkerColor.Blue), (.0712, .9232, MarkerColor.Yellow));
        var readings = ScoreMarkerReader.Read(frame, candidates);
        Assert.Equal(4, readings.Count);
        Assert.All(readings, reading => Assert.Equal(1, reading.Score));
        Assert.Equal(4, readings.Select(r => r.Color).Distinct().Count());
    }

    [Fact]
    public void Inward_markers_share_a_horizontal_column()
    {
        var (frame, candidates) = Scene((.5, .026, MarkerColor.Green), (.5, .072, MarkerColor.Red),
            (.5, .118, MarkerColor.Blue));
        Assert.All(ScoreMarkerReader.Read(frame, candidates), reading => Assert.Equal(35, reading.Score));
    }

    [Fact]
    public void Two_colors_at_row_eleven_are_not_assigned_distinct_scores()
    {
        var (frame, candidates) = Scene((.008568, .458412, MarkerColor.Red), (.027082, .454714, MarkerColor.Black));
        var readings = ScoreMarkerReader.Read(frame, candidates);
        Assert.All(readings, reading => Assert.Equal(11, reading.Score));
        Assert.Equal(new MarkerColor?[] { MarkerColor.Red, MarkerColor.Black }, readings.Select(r => r.Color));
    }

    [Theory]
    [InlineData(.017, .026, 20, .0492, .026, 21)]
    [InlineData(.017, .026, 20, .017, .0734, 19)]
    [InlineData(.983, .026, 50, .9508, .026, 49)]
    [InlineData(.983, .026, 50, .983, .0734, 51)]
    [InlineData(.983, .974, 70, .983, .9266, 69)]
    [InlineData(.983, .974, 70, .9508, .974, 71)]
    [InlineData(.017, .974, 100, .0492, .974, 99)]
    [InlineData(.017, .974, 100, .017, .9266, 1)]
    public void Occupied_corner_does_not_override_the_next_real_perimeter_cell(
        double cornerX, double cornerY, int cornerScore, double neighborX, double neighborY, int neighborScore)
    {
        var (frame, candidates) = Scene((cornerX, cornerY, MarkerColor.Green),
            (neighborX, neighborY, MarkerColor.Red));
        var readings = ScoreMarkerReader.Read(frame, candidates);
        Assert.Equal(cornerScore, readings[0].Score);
        Assert.Equal(neighborScore, readings[1].Score);
        Assert.All(readings, reading => Assert.Equal(ScoreMarkerReadingStatus.Read, reading.Status));
    }

    [Theory]
    [InlineData(.017, .026, 20)]
    [InlineData(.983, .026, 50)]
    [InlineData(.983, .974, 70)]
    [InlineData(.017, .974, 100)]
    public void Diagonally_inward_markers_share_a_clearly_occupied_corner_with_small_camera_drift(
        double cornerX, double cornerY, int score)
    {
        var inwardX = cornerX < .5 ? 1 : -1;
        var inwardY = cornerY < .5 ? 1 : -1;
        // Original and canonically aligned centers from the supplied top-right image,
        // mirrored to each corner. The aligned pair also tolerates 2px detector drift.
        (double X, double Y)[] observed = [(1931.7, 57.8), (1930.02 - 2, 57.62 + 2),
            (1930.02 + 2, 57.62 - 2)];
        foreach (var center in observed)
        {
            var target = (cornerX + inwardX * (.983 - center.X / 1996),
                cornerY + inwardY * (center.Y / 1248 - .026), MarkerColor.Black);
            var anchor = (cornerX + inwardX * (.983 - 1961.5 / 1996),
                cornerY + inwardY * (33.7 / 1248 - .026), MarkerColor.Red);
            var (frame, candidates) = Scene(anchor, target);
            var readings = ScoreMarkerReader.Read(frame, candidates);

            Assert.Equal(2, readings.Count);
            Assert.Equal(new MarkerColor?[] { MarkerColor.Red, MarkerColor.Black }, readings.Select(r => r.Color));
            Assert.All(readings, reading =>
            {
                Assert.Equal(score, reading.Score);
                Assert.Equal(ScoreMarkerReadingStatus.Read, reading.Status);
            });
        }
    }

    [Fact]
    public void Diagonal_corner_reading_requires_a_known_anchor_and_keeps_its_own_color_evidence()
    {
        var target = (1930.02 / 1996, 57.62 / 1248, MarkerColor.Black);
        var (alone, aloneCandidates) = Scene(target);
        var unanchored = Assert.Single(ScoreMarkerReader.Read(alone, aloneCandidates));
        Assert.Equal(MarkerColor.Black, unanchored.Color);
        Assert.Null(unanchored.Score);
        Assert.Equal(ScoreMarkerReadingStatus.AmbiguousPosition, unanchored.Status);

        var (frame, candidates) = Scene((1961.5 / 1996, 33.7 / 1248, MarkerColor.Red), target);
        var unknownAnchor = ScoreMarkerReader.Read(NeutralMarker(frame, candidates[0]), candidates);
        Assert.Null(unknownAnchor[0].Color);
        Assert.Null(unknownAnchor[0].Score);
        Assert.Equal(MarkerColor.Black, unknownAnchor[1].Color);
        Assert.Null(unknownAnchor[1].Score);

        var unknownTarget = ScoreMarkerReader.Read(NeutralMarker(frame, candidates[1]), candidates);
        Assert.Equal(50, unknownTarget[0].Score);
        Assert.Null(unknownTarget[1].Color);
        Assert.Null(unknownTarget[1].Score);
        Assert.NotEqual(ScoreMarkerReadingStatus.Read, unknownTarget[1].Status);
    }

    [Theory]
    [InlineData(.56, .47)]
    [InlineData(.47, .56)]
    public void A_diagonal_marker_beyond_either_corner_sharing_limit_remains_unresolved(double xSteps, double ySteps)
    {
        var (frame, candidates) = Scene((.983, .026, MarkerColor.Red),
            (.983 - xSteps * .0322, .026 + ySteps * .0474, MarkerColor.Black));
        var readings = ScoreMarkerReader.Read(frame, candidates);
        Assert.Equal(50, readings[0].Score);
        Assert.Equal(MarkerColor.Black, readings[1].Color);
        Assert.Null(readings[1].Score);
        Assert.Equal(ScoreMarkerReadingStatus.AmbiguousPosition, readings[1].Status);
    }

    [Theory]
    [InlineData(1, 0, 49)]
    [InlineData(.60, .47, 49)]
    [InlineData(0, 1, 51)]
    [InlineData(.47, .60, 51)]
    public void Clear_neighbor_scores_keep_their_reading_even_when_placed_inward_beside_fifty(
        double xSteps, double ySteps, int expected)
    {
        var (frame, candidates) = Scene((.983, .026, MarkerColor.Red),
            (.983 - xSteps * .0322, .026 + ySteps * .0474, MarkerColor.Black));
        var readings = ScoreMarkerReader.Read(frame, candidates);
        Assert.Equal(50, readings[0].Score);
        Assert.Equal(expected, readings[1].Score);
        Assert.Equal(MarkerColor.Black, readings[1].Color);
        Assert.Equal(ScoreMarkerReadingStatus.Read, readings[1].Status);
    }

    [Fact]
    public void A_shared_corner_score_still_requires_two_fresh_marker_observations()
    {
        var (image, candidates) = Scene((1961.5 / 1996, 33.7 / 1248, MarkerColor.Red),
            (1930.02 / 1996, 57.62 / 1248, MarkerColor.Black));
        var at = DateTimeOffset.UtcNow;
        var first = CameraFrame.CopyFromBgra32(Width, Height, image.Bgra32.Span, 1, 1, at);
        var second = CameraFrame.CopyFromBgra32(Width, Height, image.Bgra32.Span, 2, 1, at.AddSeconds(1.1));
        var verifier = new ScoreMarkerMoveVerifier();
        Assert.Equal(ScoreMarkerMoveState.Stabilizing, verifier.Observe(first,
            ScoreMarkerReader.Read(first, candidates), MarkerColor.Black, 50, "score-move", 1, 1).State);
        Assert.True(verifier.Observe(second, ScoreMarkerReader.Read(second, candidates),
            MarkerColor.Black, 50, "score-move", 1, 1).Confirmed);
    }

    [Theory]
    [InlineData(MarkerColor.Blue)]
    [InlineData(MarkerColor.Red)]
    [InlineData(MarkerColor.Green)]
    [InlineData(MarkerColor.Yellow)]
    [InlineData(MarkerColor.Black)]
    public void Reads_color_from_the_marker_interior_despite_a_white_outer_rim(MarkerColor color)
    {
        var (frame, candidates) = Scene((.5, .026, color));
        var result = Assert.Single(ScoreMarkerReader.Read(frame, candidates));
        Assert.Equal(color, result.Color);
        Assert.Equal(35, result.Score);
    }

    [Fact]
    public void Excludes_train_detections_and_preserves_original_candidate_indices()
    {
        var (frame, candidates) = Scene((.5, .026, MarkerColor.Red));
        var marker = candidates[0];
        var reading = Assert.Single(ScoreMarkerReader.Read(frame,
            [marker with { Kind = PieceCandidateKind.Train }, marker]));
        Assert.Equal(1, reading.CandidateIndex);
    }

    [Fact]
    public void Overlapping_duplicate_of_one_blue_marker_does_not_block_its_score()
    {
        var scoreSevenY = .974 - 7 * (.974 - .026) / 20;
        var (frame, candidates) = Scene((.017, scoreSevenY, MarkerColor.Blue),
            (.052, scoreSevenY, MarkerColor.Yellow));
        var duplicate = candidates[0] with
        {
            Confidence = .7,
            Outline = candidates[0].Outline.Select(point =>
                new NormalizedPoint(point.X + .004, point.Y)).ToArray()
        };

        var readings = ScoreMarkerReader.Read(frame, [.. candidates, duplicate]);

        Assert.Equal(2, readings.Count);
        Assert.Equal(new[] { 0, 1 }, readings.Select(reading => reading.CandidateIndex));
        Assert.Equal(new MarkerColor?[] { MarkerColor.Blue, MarkerColor.Yellow },
            readings.Select(reading => reading.Color));
        Assert.All(readings, reading => Assert.Equal(7, reading.Score));
    }

    [Fact]
    public void Separate_same_color_markers_on_one_score_remain_ambiguous()
    {
        var scoreSevenY = .974 - 7 * (.974 - .026) / 20;
        var (frame, candidates) = Scene((.017, scoreSevenY, MarkerColor.Blue),
            (.09, scoreSevenY, MarkerColor.Blue));

        var readings = ScoreMarkerReader.Read(frame, candidates);

        Assert.Equal(2, readings.Count);
        Assert.All(readings, reading =>
        {
            Assert.Equal(MarkerColor.Blue, reading.Color);
            Assert.Equal(7, reading.Score);
        });
    }

    [Fact]
    public void Marker_in_the_middle_of_the_map_is_off_track()
    {
        var (frame, candidates) = Scene((.5, .5, MarkerColor.Green));
        var reading = Assert.Single(ScoreMarkerReader.Read(frame, candidates));
        Assert.Equal(MarkerColor.Green, reading.Color);
        Assert.Null(reading.Score);
        Assert.Equal(ScoreMarkerReadingStatus.OffTrack, reading.Status);
    }

    [Fact]
    public void A_marker_halfway_between_rows_has_no_invented_score()
    {
        var (frame, candidates) = Scene((.017, (.9266 + .8792) / 2, MarkerColor.Red));
        var reading = Assert.Single(ScoreMarkerReader.Read(frame, candidates));
        Assert.Null(reading.Score);
        Assert.Equal(ScoreMarkerReadingStatus.AmbiguousPosition, reading.Status);
    }

    [Theory]
    [InlineData(160, 160, 160)]
    [InlineData(3, 12, 30)]
    public void Unclear_or_very_dark_chromatic_color_is_not_forced_to_black(int r, int g, int b)
    {
        var (frame, candidates) = Scene((.5, .026, MarkerColor.Blue));
        var pixels = frame.Bgra32.ToArray();
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = (byte)b; pixels[offset + 1] = (byte)g; pixels[offset + 2] = (byte)r;
        }
        var reading = Assert.Single(ScoreMarkerReader.Read(CameraFrame.CopyFromBgra32(Width, Height, pixels), candidates));
        Assert.Null(reading.Color);
        Assert.Null(reading.Score);
        Assert.Equal(ScoreMarkerReadingStatus.UnknownColor, reading.Status);
    }

    [Fact]
    public void Mixed_color_interior_remains_unknown()
    {
        var (frame, candidates) = Scene((.5, .026, MarkerColor.Red));
        var pixels = frame.Bgra32.ToArray();
        for (var y = 0; y < Height; y++)
        for (var x = Width / 2; x < Width; x++) Paint(pixels, x, y, (0, 48, 100));
        var reading = Assert.Single(ScoreMarkerReader.Read(CameraFrame.CopyFromBgra32(Width, Height, pixels), candidates));
        Assert.Null(reading.Color);
        Assert.Null(reading.Score);
    }

    [Fact]
    public void Edge_clipped_markers_are_valid_and_vertex_order_does_not_matter()
    {
        var (frame, candidates) = Scene((.008, .263, MarkerColor.Blue));
        var marker = candidates[0] with { Outline = candidates[0].Outline.Reverse().ToArray() };
        Assert.Contains(marker.Outline, point => point.X == 0);
        var reading = Assert.Single(ScoreMarkerReader.Read(frame, [marker]));
        Assert.Equal(15, reading.Score);
        Assert.Equal(MarkerColor.Blue, reading.Color);
    }

    [Fact]
    public void Invalid_geometry_and_tiny_pixels_do_not_claim_a_reading()
    {
        var (frame, candidates) = Scene((.5, .026, MarkerColor.Blue));
        PieceCandidate[] invalid = [candidates[0] with { Outline = [new(double.NaN, .026), new(.5, .026), new(.6, .026)] },
            new(PieceCandidateKind.PlayerMarker, [new(.5, .026), new(.501, .026), new(.501, .027), new(.5, .027)], .9)];
        Assert.All(ScoreMarkerReader.Read(frame, invalid), reading => Assert.Null(reading.Score));
    }

    private const int Width = 1000;
    private const int Height = 625;

    private static CameraFrame NeutralMarker(CameraFrame frame, PieceCandidate marker)
    {
        var pixels = frame.Bgra32.ToArray();
        var left = (int)Math.Floor(marker.Outline.Min(point => point.X) * Width);
        var right = (int)Math.Ceiling(marker.Outline.Max(point => point.X) * Width);
        var top = (int)Math.Floor(marker.Outline.Min(point => point.Y) * Height);
        var bottom = (int)Math.Ceiling(marker.Outline.Max(point => point.Y) * Height);
        for (var y = Math.Max(0, top); y < Math.Min(Height, bottom); y++)
        for (var x = Math.Max(0, left); x < Math.Min(Width, right); x++)
            Paint(pixels, x, y, (160, 160, 160));
        return CameraFrame.CopyFromBgra32(Width, Height, pixels);
    }

    private static (CameraFrame Frame, IReadOnlyList<PieceCandidate> Candidates) Scene(
        params (double X, double Y, MarkerColor Color)[] markers)
    {
        var pixels = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++) Paint(pixels, x, y, (180, 160, 125));
        var candidates = new List<PieceCandidate>();
        foreach (var marker in markers)
        {
            var cx = marker.X * Width;
            var cy = marker.Y * Height;
            const int radius = 9;
            for (var y = Math.Max(0, (int)cy - radius); y <= Math.Min(Height - 1, (int)cy + radius); y++)
            for (var x = Math.Max(0, (int)cx - radius); x <= Math.Min(Width - 1, (int)cx + radius); x++)
            {
                var distance = Math.Sqrt((x + .5 - cx) * (x + .5 - cx) + (y + .5 - cy) * (y + .5 - cy));
                if (distance <= radius) Paint(pixels, x, y, distance > radius * .75 ? (245, 245, 245) : Rgb(marker.Color));
            }
            var left = Math.Max(0, (cx - radius) / Width);
            var right = Math.Min(1, (cx + radius) / Width);
            var top = Math.Max(0, (cy - radius) / Height);
            var bottom = Math.Min(1, (cy + radius) / Height);
            candidates.Add(new(PieceCandidateKind.PlayerMarker,
                [new(left, top), new(right, top), new(right, bottom), new(left, bottom)], .9));
        }
        return (CameraFrame.CopyFromBgra32(Width, Height, pixels), candidates);
    }

    private static (int R, int G, int B) Rgb(MarkerColor color) => color switch
    {
        MarkerColor.Blue => (0, 48, 100),
        MarkerColor.Red => (138, 55, 48),
        MarkerColor.Green => (26, 77, 49),
        MarkerColor.Yellow => (216, 181, 17),
        _ => (19, 20, 22)
    };

    private static void Paint(byte[] pixels, int x, int y, (int R, int G, int B) rgb)
    {
        var offset = (y * Width + x) * 4;
        pixels[offset] = (byte)rgb.B; pixels[offset + 1] = (byte)rgb.G;
        pixels[offset + 2] = (byte)rgb.R; pixels[offset + 3] = 255;
    }
}
