using System.Diagnostics;
using System.Reflection;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class PieceCandidateDetectorTests
{
    private const int Width = 960;
    private const int Height = 600;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void An_unchanged_printed_board_does_not_produce_piece_outlines()
    {
        var pixels = Board();
        Rectangle(pixels, 200, 200, 150, 12, 30, 50, 150);
        var detector = Reference(pixels);
        var result = detector.Detect(Frame(pixels, 2), Token);
        Assert.Equal(PieceDetectionState.Ready, result.State);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void A_new_train_and_a_score_disc_have_different_outline_shapes()
    {
        var pixels = Board();
        var detector = Reference(pixels);
        Rectangle(pixels, 300, 240, 35, 12, 20, 120, 50);
        Circle(pixels, 22, 430, 10, 200, 30, 30);
        var result = detector.Detect(Frame(pixels, 2), Token);
        Assert.Equal(PieceDetectionState.Ready, result.State);
        var train = Assert.Single(result.Candidates, candidate => candidate.Kind == PieceCandidateKind.Train);
        var marker = Assert.Single(result.Candidates, candidate => candidate.Kind == PieceCandidateKind.PlayerMarker);
        AssertContains(train, 317, 245);
        AssertContains(marker, 22, 430);
        var boxWidth = (marker.Outline.Max(point => point.X) - marker.Outline.Min(point => point.X)) * (Width - 1);
        var boxHeight = (marker.Outline.Max(point => point.Y) - marker.Outline.Min(point => point.Y)) * (Height - 1);
        Assert.Equal(boxWidth, boxHeight, 6);
        Assert.All(result.Candidates, candidate => Assert.All(candidate.Outline, point =>
        { Assert.InRange(point.X, 0, 1); Assert.InRange(point.Y, 0, 1); }));
    }

    [Fact]
    public void Dark_trains_are_candidates_even_on_a_dark_printed_route()
    {
        var pixels = Board();
        Rectangle(pixels, 300, 240, 210, 12, 75, 75, 75);
        var detector = Reference(pixels);
        Rectangle(pixels, 300, 240, 105, 12, 20, 20, 20);
        var result = detector.Detect(Frame(pixels, 2), Token);
        Assert.Equal(PieceDetectionState.Ready, result.State);
        Assert.Equal(3, result.Candidates.Count(candidate => candidate.Kind == PieceCandidateKind.Train));
        Assert.All(result.Candidates, candidate => Assert.True(candidate.Outline.Max(point => point.X) < .44));
    }

    [Fact]
    public void Pieces_already_in_the_reference_are_not_claimed_as_new_detections()
    {
        var pixels = Board();
        Rectangle(pixels, 300, 240, 35, 12, 20, 120, 50);
        var detector = Reference(pixels);
        Assert.Empty(detector.Detect(Frame(pixels, 2), Token).Candidates);
    }

    [Fact]
    public void No_reference_or_a_cleared_reference_never_returns_old_boxes()
    {
        var detector = new PieceCandidateDetector();
        var pixels = Board();
        Assert.Equal(PieceDetectionState.NoReference, detector.Detect(Frame(pixels, 1), Token).State);
        detector.SetReference(Frame(pixels, 1));
        var revision = detector.ReferenceRevision;
        Rectangle(pixels, 300, 240, 35, 12, 20, 120, 50);
        Assert.NotEmpty(detector.Detect(Frame(pixels, 2), Token).Candidates);
        detector.Clear();
        var result = detector.Detect(Frame(pixels, 3), Token);
        Assert.Equal(PieceDetectionState.NoReference, result.State);
        Assert.Empty(result.Candidates);
        Assert.True(result.ReferenceRevision > revision);
    }

    [Fact]
    public void Duplicated_and_out_of_order_frames_do_not_reuse_old_boxes()
    {
        var pixels = Board();
        var detector = Reference(pixels);
        Rectangle(pixels, 300, 240, 35, 12, 20, 120, 50);
        Assert.NotEmpty(detector.Detect(Frame(pixels, 3), Token).Candidates);
        Assert.Equal(PieceDetectionState.Stale, detector.Detect(Frame(pixels, 3), Token).State);
        var older = detector.Detect(Frame(pixels, 2), Token);
        Assert.Equal(PieceDetectionState.Stale, older.State);
        Assert.Empty(older.Candidates);
    }

    [Fact]
    public void Camera_epoch_and_dimension_changes_invalidate_evidence()
    {
        var pixels = Board();
        var detector = Reference(pixels);
        var epoch = detector.Detect(CameraFrame.CopyFromBgra32(Width, Height, pixels, sequence: 2, epoch: 2), Token);
        Assert.Equal(PieceDetectionState.CameraChanged, epoch.State);
        Assert.Empty(epoch.Candidates);
        var size = detector.Detect(CameraFrame.CopyFromBgra32(480, 300, new byte[480 * 300 * 4], sequence: 3), Token);
        Assert.Equal(PieceDetectionState.CameraChanged, size.State);
        Assert.Empty(size.Candidates);
    }

    [Fact]
    public void A_large_obstruction_suppresses_candidates()
    {
        var pixels = Board();
        var detector = Reference(pixels);
        Rectangle(pixels, 200, 120, 500, 400, 210, 90, 60);
        var result = detector.Detect(Frame(pixels, 2), Token);
        Assert.Equal(PieceDetectionState.SceneChanged, result.State);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void A_camera_jog_suppresses_printed_route_differences()
    {
        var pixels = Board();
        var detector = Reference(pixels);
        var shifted = new byte[pixels.Length];
        for (var y = 0; y < Height; y++)
            pixels.AsSpan(y * Width * 4, (Width - 18) * 4).CopyTo(shifted.AsSpan((y * Width + 18) * 4));
        var result = detector.Detect(Frame(shifted, 2), Token);
        Assert.Equal(PieceDetectionState.SceneChanged, result.State);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Stale_frames_are_rejected_using_monotonic_age()
    {
        var pixels = Board();
        var detector = Reference(pixels);
        var stale = (CameraFrame)typeof(CameraFrame).GetMethod("TakeOwnership", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [Width, Height, pixels, 2L, 1L, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp() - Stopwatch.Frequency * 3])!;
        Assert.Throws<InvalidOperationException>(() => new PieceCandidateDetector().SetReference(stale));
        var result = detector.Detect(stale, Token);
        Assert.Equal(PieceDetectionState.Stale, result.State);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Featureless_and_tiny_references_are_rejected()
    {
        var detector = new PieceCandidateDetector();
        Assert.Throws<InvalidOperationException>(() => detector.SetReference(Frame(new byte[Width * Height * 4], 1)));
        Assert.Throws<ArgumentException>(() => detector.SetReference(CameraFrame.CopyFromBgra32(100, 100, new byte[100 * 100 * 4])));
        Assert.False(detector.HasReference);
    }

    [Fact]
    public void Cancellation_stops_processing_without_emitting_candidates()
    {
        var pixels = Board();
        var detector = Reference(pixels);
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => detector.Detect(Frame(pixels, 2), source.Token));
    }

    private static PieceCandidateDetector Reference(byte[] pixels)
    {
        var detector = new PieceCandidateDetector();
        detector.SetReference(Frame(pixels, 1));
        return detector;
    }

    private static CameraFrame Frame(byte[] pixels, long sequence) => CameraFrame.CopyFromBgra32(Width, Height, pixels, sequence);

    private static byte[] Board()
    {
        var pixels = new byte[Width * Height * 4];
        uint state = 12345;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            state = unchecked(state * 1664525 + 1013904223);
            var value = (byte)(150 + state % 70);
            Set(pixels, x, y, value, value, value);
        }
        return pixels;
    }

    private static void Rectangle(byte[] pixels, int left, int top, int width, int height, byte r, byte g, byte b)
    {
        for (var y = top; y < top + height; y++)
        for (var x = left; x < left + width; x++) Set(pixels, x, y, r, g, b);
    }

    private static void Circle(byte[] pixels, int cx, int cy, int radius, byte r, byte g, byte b)
    {
        for (var y = cy - radius; y <= cy + radius; y++)
        for (var x = cx - radius; x <= cx + radius; x++)
            if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius) Set(pixels, x, y, r, g, b);
    }

    private static void Set(byte[] pixels, int x, int y, byte r, byte g, byte b)
    {
        var index = (y * Width + x) * 4;
        pixels[index] = b; pixels[index + 1] = g; pixels[index + 2] = r; pixels[index + 3] = 255;
    }

    private static void AssertContains(PieceCandidate candidate, double x, double y)
    {
        Assert.InRange(x / (Width - 1), candidate.Outline.Min(point => point.X), candidate.Outline.Max(point => point.X));
        Assert.InRange(y / (Height - 1), candidate.Outline.Min(point => point.Y), candidate.Outline.Max(point => point.Y));
    }
}
