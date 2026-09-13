using System.Text;
using System.Text.Json;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class LearnedPieceDetectorTests
{
    [Fact]
    public void Tiles_cover_the_entire_board_and_anchor_the_last_tile_without_padding()
    {
        Assert.Equal([0, 512, 1024, 1280], PieceModelGeometry.TileStarts(1920));
        Assert.Equal([0, 512, 560], PieceModelGeometry.TileStarts(1200));
        Assert.Equal([0], PieceModelGeometry.TileStarts(200));
    }

    [Fact]
    public void Input_is_raw_Bgr_in_separate_planes_and_small_tiles_pad_with_114()
    {
        var frame = CameraFrame.CopyFromBgra32(2, 1, [10, 20, 30, 255, 40, 50, 60, 255]);
        var input = new float[3 * 640 * 640];
        PieceModelGeometry.FillInput(frame, 0, 0, input, TestContext.Current.CancellationToken);
        Assert.Equal(10, input[0]);
        Assert.Equal(40, input[1]);
        Assert.Equal(20, input[640 * 640]);
        Assert.Equal(30, input[2 * 640 * 640]);
        Assert.Equal(114, input[2]);
        Assert.Equal(114, input[^1]);
    }

    [Fact]
    public void Resize_uses_half_pixel_bilinear_and_preserves_capture_identity_and_age()
    {
        var clock = new ManualClock();
        var frame = CameraFrame.CopyFromBgra32(2, 2,
            [0, 0, 0, 255, 40, 40, 40, 255, 80, 80, 80, 255, 120, 120, 120, 255],
            sequence: 17, epoch: 9, clock: clock);
        clock.Advance(TimeSpan.FromSeconds(5));
        var resized = PieceModelGeometry.Resize(frame, 1, 1, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 60, 60, 60, 255 }, resized.Bgra32.ToArray());
        Assert.Equal(17, resized.Sequence);
        Assert.Equal(9, resized.Epoch);
        Assert.Equal(frame.CapturedAt, resized.CapturedAt);
        Assert.Equal(TimeSpan.FromSeconds(5), resized.Age);
    }

    [Fact]
    public void Decoder_uses_objectness_times_best_class_and_clips_boxes_to_the_observed_tile()
    {
        var output = new float[8400 * 7];
        Write(0, [635, 10, 30, 30, .8f, .25f, .75f]);
        Write(1, [30, 10, 20, 10, .4f, .5f, .1f]); // .2 is below the .25 threshold.
        Write(2, [float.NaN, 0, 20, 20, 1, 1, 0]);
        Write(3, [40, 40, -1, 20, 1, 1, 0]);
        var boxes = new List<PieceModelBox>();
        PieceModelGeometry.Decode(output, 1280, 560, .25, boxes);
        var box = Assert.Single(boxes);
        Assert.Equal(PieceCandidateKind.PlayerMarker, box.Kind);
        Assert.Equal(1900, box.X);
        Assert.Equal(560, box.Y);
        Assert.Equal(20, box.Width);
        Assert.Equal(25, box.Height);
        Assert.Equal(.6, box.Confidence, precision: 6);
        void Write(int row, float[] values) => values.CopyTo(output, row * 7);
    }

    [Fact]
    public void Global_nms_removes_tile_duplicates_but_keeps_touching_trains_and_different_classes()
    {
        PieceModelBox[] boxes = [
            new(PieceCandidateKind.Train, 500, 500, 60, 20, .9),
            new(PieceCandidateKind.Train, 501, 500, 60, 20, .8),
            new(PieceCandidateKind.Train, 560, 500, 60, 20, .85),
            new(PieceCandidateKind.PlayerMarker, 501, 500, 60, 20, .7),
            new(PieceCandidateKind.PlayerMarker, 1910, 1190, 30, 30, .95)
        ];
        var result = PieceModelGeometry.Merge(boxes, 1920, 1200, .45);
        Assert.Equal(4, result.Count);
        Assert.Equal(2, result.Count(box => box.Kind == PieceCandidateKind.Train));
        Assert.All(result, box => Assert.All(box.Outline, point =>
        { Assert.InRange(point.X, 0, 1); Assert.InRange(point.Y, 0, 1); }));
        Assert.Equal(1, result[0].Outline[2].X);
        Assert.Equal(1, result[0].Outline[2].Y);
    }

    [Fact]
    public void Overlap_midpoint_has_one_owner_and_discards_a_neighboring_tile_fragment()
    {
        var x = PieceModelGeometry.TileStarts(1920);
        var y = PieceModelGeometry.TileStarts(1200);
        var boundary = new PieceModelBox(PieceCandidateKind.Train, 556, 100, 40, 20, .9);
        Assert.False(PieceModelGeometry.OwnsCenter(boundary, 0, 0, x, y));
        Assert.True(PieceModelGeometry.OwnsCenter(boundary, 512, 0, x, y));
        var fragment = boundary with { X = 630, Width = 10 };
        Assert.False(PieceModelGeometry.OwnsCenter(fragment, 0, 0, x, y));
        var irregularOverlap = boundary with { X = 1452, Y = 846 };
        Assert.False(PieceModelGeometry.OwnsCenter(irregularOverlap, 1024, 512, x, y));
        Assert.True(PieceModelGeometry.OwnsCenter(irregularOverlap, 1280, 560, x, y));
    }

    [Fact]
    public void Manifest_requires_the_exact_class_order_pixel_contract_and_local_filename()
    {
        Assert.Equal("test-model", PieceModelManifest.Parse(Manifest()).ModelId);
        foreach (var (key, value) in new (string, object?)[]
        {
            ("classes", new[] { "player-marker", "train" }), ("modelFile", "../other.onnx"),
            ("inputSize", 960), ("pixelScale", 1d / 255), ("channelOrder", "RGB"),
            ("confidenceThreshold", 0), ("modelSha256", null), ("opset", 18)
        })
            Assert.Throws<InvalidDataException>(() => PieceModelManifest.Parse(Manifest(key, value)));
    }

    [Fact]
    public void Model_reader_rejects_custom_operators_external_weights_and_truncation()
    {
        PieceModelGraphValidator.Validate(Graph("Identity"), 17);
        Assert.Throws<InvalidDataException>(() => PieceModelGraphValidator.Validate(Graph("UserExecutable"), 17));
        Assert.Throws<InvalidDataException>(() => PieceModelGraphValidator.Validate(Graph("Identity", external: true), 17));
        Assert.Throws<InvalidDataException>(() => PieceModelGraphValidator.Validate(Graph("Identity")[..^1], 17));
        Assert.Throws<InvalidDataException>(() => PieceModelGraphValidator.Validate(Graph("Identity"), 18));
    }

    [Fact]
    public void Missing_model_does_not_silently_fall_back_to_image_difference()
    {
        Assert.Throws<FileNotFoundException>(() => LearnedPieceDetector.Load(
            Path.Combine(Path.GetTempPath(), "GoldenTicket-missing-model-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void Changed_model_bytes_are_rejected_before_creating_a_native_session()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GoldenTicket-model-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var manifest = Path.Combine(directory, "manifest.json");
        var model = Path.Combine(directory, "piece-detector.onnx");
        try
        {
            File.WriteAllBytes(manifest, Manifest());
            File.WriteAllBytes(model, Graph("Identity"));
            var error = Assert.Throws<InvalidDataException>(() => LearnedPieceDetector.Load(directory));
            Assert.Contains("hash", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(manifest);
            File.Delete(model);
            Directory.Delete(directory);
        }
    }

    private static byte[] Manifest(string? key = null, object? value = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["version"] = 1, ["modelId"] = "test-model", ["modelFile"] = "piece-detector.onnx",
            ["modelSha256"] = new string('a', 64), ["architecture"] = "yolox-nano-decoded",
            ["classes"] = new[] { "train", "player-marker" }, ["inputName"] = "images", ["outputName"] = "detections",
            ["inputSize"] = 640, ["boardWidth"] = 1920, ["boardHeight"] = 1200, ["tileStride"] = 512,
            ["tileOwnership"] = "center-midpoint",
            ["channelOrder"] = "BGR", ["pixelScale"] = 1, ["pixelOffset"] = 0, ["paddingValue"] = 114,
            ["resize"] = "bilinear-half-pixel", ["confidenceThreshold"] = .25, ["nmsThreshold"] = .45, ["opset"] = 17
        };
        if (key is not null) fields[key] = value;
        return JsonSerializer.SerializeToUtf8Bytes(fields);
    }

    private static byte[] Graph(string op, bool external = false)
    {
        var node = Field(4, Encoding.UTF8.GetBytes(op));
        var graph = Field(1, node);
        if (external) graph = [.. graph, .. Field(5, [0x70, 1])]; // TensorProto.data_location = EXTERNAL.
        return [.. Field(7, graph), .. Field(8, [0x10, 17])];
    }

    private static byte[] Field(int number, byte[] bytes)
    {
        // All synthetic fixtures deliberately stay below a one-byte protobuf field length.
        Assert.True(bytes.Length < 128);
        return [(byte)(number * 8 + 2), (byte)bytes.Length, .. bytes];
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
}
