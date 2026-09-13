using System.Security.Cryptography;
using System.Text.Json;

namespace GoldenTicket.Vision;

/// <summary>The fixed input and decoder contract for the local corner heatmap model.</summary>
internal sealed record BoardCornerModelManifest
{
    public int Version { get; init; }
    public string ModelId { get; init; } = "";
    public string ModelFile { get; init; } = "";
    public string ModelSha256 { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string[] CornerOrder { get; init; } = [];
    public string InputName { get; init; } = "";
    public string OutputName { get; init; } = "";
    public int InputSize { get; init; }
    public int HeatmapSize { get; init; }
    public string ChannelOrder { get; init; } = "";
    public double PixelScale { get; init; }
    public double PixelOffset { get; init; }
    public int PaddingValue { get; init; }
    public string Resize { get; init; } = "";
    public string Letterbox { get; init; } = "";
    public string Decoder { get; init; } = "";
    public double ConfidenceThreshold { get; init; }
    public int Opset { get; init; }

    internal static (BoardCornerModelManifest Manifest, byte[] Model) Read(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var manifestPath = Path.Combine(Path.GetFullPath(directory), "manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The local board-corner model is missing. Select the corners manually or build with its model and manifest.", manifestPath);
        if (new FileInfo(manifestPath).Length is <= 0 or > 1024 * 1024)
            throw new InvalidDataException("The board-corner manifest exceeds its size limit.");
        var manifest = Parse(File.ReadAllBytes(manifestPath));
        var modelPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, manifest.ModelFile);
        using var file = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 64 * 1024 * 1024)
            throw new InvalidDataException("The board-corner model exceeds the 64 MiB experiment limit.");
        var model = new byte[checked((int)file.Length)];
        file.ReadExactly(model);
        if (!Convert.ToHexString(SHA256.HashData(model)).Equals(manifest.ModelSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The board-corner model hash does not match its local manifest.");
        PieceModelGraphValidator.Validate(model, manifest.Opset);
        return (manifest, model);
    }

    internal static BoardCornerModelManifest Parse(ReadOnlySpan<byte> json)
    {
        BoardCornerModelManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BoardCornerModelManifest>(json, new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 32 })
                ?? throw new InvalidDataException("The board-corner manifest is empty.");
        }
        catch (JsonException error) { throw new InvalidDataException("The board-corner manifest is not valid JSON.", error); }
        if (manifest.Version != 1 || string.IsNullOrWhiteSpace(manifest.ModelId) || manifest.ModelId.Length > 160 ||
            manifest.ModelId.Any(char.IsControl) || manifest.ModelFile != "board-corners.onnx" ||
            manifest.ModelSha256 is null || manifest.ModelSha256.Length != 64 || !manifest.ModelSha256.All(Uri.IsHexDigit) ||
            manifest.Architecture != "board-corner-unet" || manifest.CornerOrder is null ||
            !manifest.CornerOrder.SequenceEqual(["top-left", "top-right", "bottom-right", "bottom-left"]) ||
            manifest.InputName != "images" || manifest.OutputName != "heatmaps" ||
            manifest.InputSize != BoardCornerModelGeometry.InputSize || manifest.HeatmapSize != BoardCornerModelGeometry.HeatmapSize ||
            manifest.ChannelOrder != "RGB" || Math.Abs(manifest.PixelScale - 1d / 255) > 1e-12 || manifest.PixelOffset != 0 ||
            manifest.PaddingValue != 114 || manifest.Resize != "bilinear-half-pixel" ||
            manifest.Letterbox != "center-round-half-up" || manifest.Decoder != "peak-centroid-5x5" || manifest.Opset != 17 ||
            !double.IsFinite(manifest.ConfidenceThreshold) || manifest.ConfidenceThreshold is < .1 or > .99)
            throw new InvalidDataException("The local board-corner model has an unsupported manifest contract.");
        return manifest;
    }
}
