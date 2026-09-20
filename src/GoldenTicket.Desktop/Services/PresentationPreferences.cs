using System.IO;
using System.Text.Json;
using GoldenTicket.Desktop.ViewModels;

namespace GoldenTicket.Desktop.Services;

public sealed record WindowPresentation(double Width, double Height, bool Maximized);

/// <summary>Local display choices only; no cards or match state are written here.</summary>
internal static class PresentationPreferences
{
    private static readonly object Gate = new();

    public static bool LoadShowDestinationsWithTrainCards(string? path) =>
        Load(path).ShowDestinationsWhenViewingTrainCards ?? true;

    public static WindowPresentation? LoadWindowPresentation(string? path) => Load(path).WindowPresentation;

    public static DisplayMode LoadDisplayMode(string? path) => Load(path).DisplayMode ?? DisplayMode.Resizable;

    internal static bool IsValid(WindowPresentation? value) => value is not null &&
        double.IsFinite(value.Width) && double.IsFinite(value.Height) &&
        value.Width is >= 320 and <= 16384 && value.Height is >= 240 and <= 16384;

    private static Preference Load(string? path)
    {
        if (path is null) return new();
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return new();
            var value = JsonSerializer.Deserialize<Preference>(File.ReadAllText(path)) ?? new();
            return value with
            {
                WindowPresentation = IsValid(value.WindowPresentation) ? value.WindowPresentation : null,
                DisplayMode = value.DisplayMode is { } mode && Enum.IsDefined(mode) ? mode : null
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public static void Save(string? path, bool showDestinationsWithTrainCards) =>
        Update(path, current => current with { ShowDestinationsWhenViewingTrainCards = showDestinationsWithTrainCards });

    public static void SaveWindowPresentation(string? path, WindowPresentation value)
    {
        if (IsValid(value)) Update(path, current => current with { WindowPresentation = value });
    }

    public static void SaveDisplayMode(string? path, DisplayMode value)
    {
        if (Enum.IsDefined(value)) Update(path, current => current with { DisplayMode = value });
    }

    private static void Update(string? path, Func<Preference, Preference> update)
    {
        if (path is null) return;
        lock (Gate)
        {
            var value = update(Load(path));
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(value));
                File.Move(temporary, fullPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    private sealed record Preference(bool? ShowDestinationsWhenViewingTrainCards = null,
        WindowPresentation? WindowPresentation = null, DisplayMode? DisplayMode = null);
}
