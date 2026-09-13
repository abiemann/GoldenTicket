using System.Security.Cryptography;
using System.Text.Json;

namespace GoldenTicket.Vision;

/// <summary>The deliberately fixed contract for the first local two-class model experiment.</summary>
internal sealed record PieceModelManifest
{
    public int Version { get; init; }
    public string ModelId { get; init; } = "";
    public string ModelFile { get; init; } = "";
    public string ModelSha256 { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string[] Classes { get; init; } = [];
    public string InputName { get; init; } = "";
    public string OutputName { get; init; } = "";
    public int InputSize { get; init; }
    public int BoardWidth { get; init; }
    public int BoardHeight { get; init; }
    public int TileStride { get; init; }
    public string TileOwnership { get; init; } = "";
    public string ChannelOrder { get; init; } = "";
    public double PixelScale { get; init; }
    public double PixelOffset { get; init; }
    public int PaddingValue { get; init; }
    public string Resize { get; init; } = "";
    public double ConfidenceThreshold { get; init; }
    public double NmsThreshold { get; init; }
    public int Opset { get; init; }

    internal static (PieceModelManifest Manifest, byte[] Model) Read(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var manifestPath = Path.Combine(Path.GetFullPath(directory), "manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The local piece model is missing. Build with the reviewed experimental model and manifest.", manifestPath);
        if (new FileInfo(manifestPath).Length is <= 0 or > 1024 * 1024)
            throw new InvalidDataException("The piece-model manifest exceeds its size limit.");
        var manifest = Parse(File.ReadAllBytes(manifestPath));
        var modelPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, manifest.ModelFile);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("The local piece model is missing.", modelPath);
        using var file = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 64 * 1024 * 1024)
            throw new InvalidDataException("The piece model exceeds the 64 MiB experiment limit.");
        var model = new byte[checked((int)file.Length)];
        file.ReadExactly(model);
        // The exact verified bytes are passed to ONNX Runtime, avoiding a second file read after hashing.
        if (!Convert.ToHexString(SHA256.HashData(model)).Equals(manifest.ModelSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The piece model hash does not match its local manifest.");
        PieceModelGraphValidator.Validate(model, manifest.Opset);
        return (manifest, model);
    }

    internal static PieceModelManifest Parse(ReadOnlySpan<byte> json)
    {
        PieceModelManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PieceModelManifest>(json, new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 32 })
                ?? throw new InvalidDataException("The piece-model manifest is empty.");
        }
        catch (JsonException error) { throw new InvalidDataException("The piece-model manifest is not valid JSON.", error); }
        if (manifest.Version != 1 || string.IsNullOrWhiteSpace(manifest.ModelId) || manifest.ModelId.Length > 160 ||
            manifest.ModelId.Any(char.IsControl) || manifest.ModelFile != "piece-detector.onnx" ||
            manifest.ModelSha256 is null || manifest.ModelSha256.Length != 64 || !manifest.ModelSha256.All(Uri.IsHexDigit) ||
            manifest.Architecture != "yolox-nano-decoded" ||
            manifest.Classes is null || !manifest.Classes.SequenceEqual(["train", "player-marker"]) ||
            manifest.InputName != "images" || manifest.OutputName != "detections" ||
            manifest.InputSize != 640 || manifest.BoardWidth != LearnedPieceDetector.BoardWidth ||
            manifest.BoardHeight != LearnedPieceDetector.BoardHeight || manifest.TileStride != 512 ||
            manifest.TileOwnership != "center-midpoint" ||
            manifest.ChannelOrder != "BGR" || manifest.PixelScale != 1 || manifest.PixelOffset != 0 ||
            manifest.PaddingValue != 114 || manifest.Resize != "bilinear-half-pixel" || manifest.Opset != 17 ||
            !double.IsFinite(manifest.ConfidenceThreshold) || manifest.ConfidenceThreshold is < .01 or > .99 ||
            !double.IsFinite(manifest.NmsThreshold) || manifest.NmsThreshold is < .1 or > .9)
            throw new InvalidDataException("The local piece model has an unsupported manifest contract.");
        return manifest;
    }
}
