using System.Text.Json;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class LearnedBoardCornerDetectorTests
{
    [Fact]
    public void Letterbox_preserves_whole_landscape_and_portrait_frames_with_centered_padding()
    {
        Assert.Equal(new BoardCornerLetterbox(1920, 1080, 384, 216, 0, 84),
            BoardCornerModelGeometry.Letterbox(1920, 1080));
        Assert.Equal(new BoardCornerLetterbox(1080, 1920, 216, 384, 84, 0),
            BoardCornerModelGeometry.Letterbox(1080, 1920));
        // The odd unused pixel belongs on the right/bottom, avoiding a half-pixel translation.
        Assert.Equal(new BoardCornerLetterbox(301, 200, 384, 255, 0, 64),
            BoardCornerModelGeometry.Letterbox(301, 200));
    }

    [Fact]
    public void Preprocessing_uses_rgb_normalized_planes_and_keeps_padding_separate()
    {
        var pixels = new byte[40 * 20 * 4];
        for (var p = 0; p < pixels.Length; p += 4)
        { pixels[p] = 25; pixels[p + 1] = 100; pixels[p + 2] = 250; pixels[p + 3] = 255; }
        var frame = CameraFrame.CopyFromBgra32(40, 20, pixels);
        var tensor = new float[3 * 384 * 384];
        var transform = BoardCornerModelGeometry.FillInput(frame, tensor, TestContext.Current.CancellationToken);
        Assert.Equal(96, transform.Top);
        Assert.Equal(114f / 255, tensor[0]);
        var first = 96 * 384;
        Assert.Equal(250f / 255, tensor[first]);
        Assert.Equal(100f / 255, tensor[384 * 384 + first]);
        Assert.Equal(25f / 255, tensor[2 * 384 * 384 + first]);
        Assert.Equal(114f / 255, tensor[^1]);
    }

    [Fact]
    public void Decoder_maps_heatmap_cell_centers_back_through_padding_to_full_sensor_pixels()
    {
        var frame = Frame(384, 216);
        var maps = Maps((20, 50), (170, 50), (170, 140), (20, 140));
        var result = Decode(frame, maps);
        Assert.Null(result.RejectionReason);
        Assert.Equal(4, result.Corners.Count);
        Assert.Equal(40.5 / 383, result.Corners[0].X, 9);
        Assert.Equal(16.5 / 215, result.Corners[0].Y, 9);
        Assert.Equal(340.5 / 383, result.Corners[2].X, 9);
        Assert.Equal(196.5 / 215, result.Corners[2].Y, 9);
        Assert.All(result.Confidences, score => Assert.Equal(.9, score, 6));
    }

    [Fact]
    public void Decoder_refines_each_peak_with_only_its_local_five_by_five_neighborhood()
    {
        var frame = Frame(384, 216);
        var maps = Maps((20, 50), (170, 50), (170, 140), (20, 140));
        maps[50 * 192 + 21] = .3f; // 20.25 local centroid: sensor x = 41 rather than 40.5.
        maps[50 * 192 + 25] = .6f; // Outside the radius must not pull the corner toward another feature.
        var result = Decode(frame, maps);
        Assert.Null(result.RejectionReason);
        Assert.Equal(41d / 383, result.Corners[0].X, 8);
        Assert.Equal(16.5 / 215, result.Corners[0].Y, 9);
    }

    [Fact]
    public void Decoder_requires_all_four_confident_corners_and_never_invents_a_missing_one()
    {
        var frame = Frame(384, 216);
        var maps = Maps((20, 50), (170, 50), (170, 140), (20, 140));
        maps[3 * 192 * 192 + 140 * 192 + 20] = .54f;
        var result = Decode(frame, maps);
        Assert.Empty(result.Corners);
        Assert.Contains("confidently", result.RejectionReason);
        Assert.Equal(.54, result.Confidences[3], 6);
        Assert.Empty(Decode(frame, new float[maps.Length]).Corners);
    }

    [Fact]
    public void Decoder_rejects_padding_peaks_instead_of_clamping_them_to_camera_edges()
    {
        var frame = Frame(384, 216);
        var result = Decode(frame, Maps((20, 10), (170, 10), (170, 140), (20, 140)));
        Assert.Empty(result.Corners);
        Assert.Contains("outside", result.RejectionReason);
    }

    [Fact]
    public void Confident_crossed_reversed_or_tiny_quadrilaterals_are_not_usable_crops()
    {
        var frame = Frame(384, 216);
        foreach (var maps in new[]
        {
            Maps((20, 50), (170, 50), (20, 140), (170, 140)),
            Maps((170, 140), (20, 140), (20, 50), (170, 50)),
            Maps((90, 80), (100, 80), (100, 90), (90, 90))
        })
        {
            var result = Decode(frame, maps);
            Assert.Empty(result.Corners);
            Assert.NotNull(result.RejectionReason);
        }
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-.1f)]
    [InlineData(1.1f)]
    public void Nonfinite_or_nonprobability_heatmaps_are_model_failures(float value)
    {
        var frame = Frame(384, 216);
        var maps = Maps((20, 50), (170, 50), (170, 140), (20, 140));
        maps[1234] = value;
        Assert.Throws<InvalidDataException>(() => Decode(frame, maps));
    }

    [Fact]
    public void Corner_manifest_rejects_wrong_coordinate_contract_or_nonlocal_model_paths()
    {
        Assert.Equal("corner-test", BoardCornerModelManifest.Parse(Manifest()).ModelId);
        foreach (var (key, value) in new (string, object?)[]
        {
            ("cornerOrder", new[] { "top-left", "bottom-left", "bottom-right", "top-right" }),
            ("modelFile", "../board-corners.onnx"), ("architecture", "image-difference"),
            ("inputSize", 640), ("heatmapSize", 96), ("pixelScale", 1), ("channelOrder", "BGR"),
            ("letterbox", "stretch"), ("decoder", "argmax"), ("confidenceThreshold", 0),
            ("modelSha256", null), ("opset", 18)
        })
            Assert.Throws<InvalidDataException>(() => BoardCornerModelManifest.Parse(Manifest(key, value)));
    }

    [Fact]
    public void Missing_or_tampered_corner_model_never_silently_uses_an_image_heuristic()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GoldenTicket-corner-model-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<FileNotFoundException>(() => LearnedBoardCornerDetector.Load(directory));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "manifest.json"), Manifest());
            File.WriteAllBytes(Path.Combine(directory, "board-corners.onnx"), [1, 2, 3, 4]);
            var error = Assert.Throws<InvalidDataException>(() => LearnedBoardCornerDetector.Load(directory));
            Assert.Contains("hash", error.Message);
        }
        finally
        {
            File.Delete(Path.Combine(directory, "manifest.json"));
            File.Delete(Path.Combine(directory, "board-corners.onnx"));
            Directory.Delete(directory);
        }
    }

    private static CameraFrame Frame(int width, int height) =>
        CameraFrame.CopyFromBgra32(width, height, new byte[width * height * 4]);

    private static BoardCornerGeometryResult Decode(CameraFrame frame, float[] maps) =>
        BoardCornerModelGeometry.Decode(maps, BoardCornerModelGeometry.Letterbox(frame.Width, frame.Height), .55, frame);

    private static float[] Maps(params (int X, int Y)[] peaks)
    {
        var maps = new float[4 * 192 * 192];
        for (var corner = 0; corner < peaks.Length; corner++)
            maps[corner * 192 * 192 + peaks[corner].Y * 192 + peaks[corner].X] = .9f;
        return maps;
    }

    private static byte[] Manifest(string? key = null, object? value = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["version"] = 1, ["modelId"] = "corner-test", ["modelFile"] = "board-corners.onnx",
            ["modelSha256"] = new string('a', 64), ["architecture"] = "board-corner-unet",
            ["cornerOrder"] = new[] { "top-left", "top-right", "bottom-right", "bottom-left" },
            ["inputName"] = "images", ["outputName"] = "heatmaps", ["inputSize"] = 384, ["heatmapSize"] = 192,
            ["channelOrder"] = "RGB", ["pixelScale"] = 1d / 255, ["pixelOffset"] = 0, ["paddingValue"] = 114,
            ["resize"] = "bilinear-half-pixel", ["letterbox"] = "center-round-half-up", ["decoder"] = "peak-centroid-5x5",
            ["confidenceThreshold"] = .55, ["opset"] = 17
        };
        if (key is not null) fields[key] = value;
        return JsonSerializer.SerializeToUtf8Bytes(fields);
    }
}
