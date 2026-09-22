using System.IO;
using System.Text.Json;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.Services;

/// <summary>The last selected webcam identity; no images or match data.</summary>
internal static class CameraDevicePreferences
{
    public static CameraDevice? Load(string? path)
    {
        if (path is null) return null;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16384) return null;
            var device = JsonSerializer.Deserialize<CameraDevice>(File.ReadAllText(path));
            return IsValid(device) ? device : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Save(string? path, CameraDevice device)
    {
        if (!IsValid(device)) throw new ArgumentException("A webcam identity and name are required.", nameof(device));
        if (path is null) return;
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(device));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool IsValid(CameraDevice? device) => device is not null &&
        !string.IsNullOrWhiteSpace(device.Id) && device.Id.Length <= 4096 &&
        !string.IsNullOrWhiteSpace(device.Name) && device.Name.Length <= 512;
}
