using System.IO;
using System.Text.Json;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.Services;

/// <summary>Local processor preference only. No images, adapters, or game data are persisted here.</summary>
internal static class FrameProcessingPreferences
{
    public static FrameComputeMode Load(string? path)
    {
        if (path is null) return FrameComputeMode.Auto;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return FrameComputeMode.Auto;
            var setting = JsonSerializer.Deserialize<Preference>(File.ReadAllText(path));
            return setting is not null && Enum.IsDefined(setting.Mode) ? setting.Mode : FrameComputeMode.Auto;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return FrameComputeMode.Auto;
        }
    }

    public static void Save(string? path, FrameComputeMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (path is null) return;
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Preference(mode)));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record Preference(FrameComputeMode Mode);
}
