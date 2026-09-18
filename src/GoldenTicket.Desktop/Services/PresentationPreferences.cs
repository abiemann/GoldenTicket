using System.IO;
using System.Text.Json;

namespace GoldenTicket.Desktop.Services;

/// <summary>Local display choices only; no cards or match state are written here.</summary>
internal static class PresentationPreferences
{
    public static bool LoadShowDestinationsWithTrainCards(string? path)
    {
        if (path is null) return true;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return true;
            return JsonSerializer.Deserialize<Preference>(File.ReadAllText(path))?
                .ShowDestinationsWhenViewingTrainCards ?? true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;
        }
    }

    public static void Save(string? path, bool showDestinationsWithTrainCards)
    {
        if (path is null) return;
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                new Preference(showDestinationsWithTrainCards)));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record Preference(bool? ShowDestinationsWhenViewingTrainCards);
}
