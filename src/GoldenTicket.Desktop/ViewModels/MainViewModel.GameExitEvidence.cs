using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using GoldenTicket.Domain;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

public sealed record BoardCheckDetectionRow(int Number, double Left, double Top,
    double Width, double Height, string Description);

public sealed record BoardCheckEvidence(BitmapSource Image, string Caption,
    IReadOnlyList<BoardCheckDetectionRow> Detections);

public sealed partial class MainViewModel
{
    [ObservableProperty] private BoardCheckEvidence? _gameExitEvidence;

    private void UpdateGameExitEvidence(GameTableAnalysis analysis, BoardInventoryObservation observation)
    {
        Game.UpdateInventoryProblemMarkers("save", observation);
        if (observation.State is BoardInventoryState.Confirmed or BoardInventoryState.Stabilizing)
        {
            GameExitEvidence = null;
            return;
        }
        // A stale analysis cannot replace the image associated with a real failed check.
        if (observation.State == BoardInventoryState.WaitingForFreshFrame) return;

        var rows = observation.UnexpectedDetections.Select((detection, index) =>
        {
            var place = detection.RouteId is { } routeId
                ? _manifest.Describe(new RouteId(routeId))
                : DescribeBoardRegion(detection.X + detection.Width / 2,
                    detection.Y + detection.Height / 2);
            var color = detection.Color is { } knownColor ? $"{knownColor} · " : "";
            return EvidenceRow(index + 1, detection.X, detection.Y, detection.Width,
                detection.Height, $"{index + 1}. {color}{place} · detector confidence {detection.Confidence:P0}");
        }).ToArray();
        if ((rows.Length == 0 || observation.State == BoardInventoryState.MissingTrains) &&
            observation.RouteId is { } expectedRoute &&
            ClassicUsRouteGeometry.TryGetSlots(expectedRoute, out var slots))
        {
            // Missing pieces have no detection box. Mark their expected route spaces instead.
            var first = rows.Length + 1;
            rows = [.. rows, .. slots.Select((slot, index) => EvidenceRow(first + index,
                slot.X - .012, slot.Y - .016, .024, .032,
                $"{first + index}. Check space {index + 1} on {_manifest.Describe(new RouteId(expectedRoute))}."))];
        }
        if (rows.Length == 0)
        {
            GameExitEvidence = null;
            return;
        }

        // Bind image and boxes as one value. Using the independently refreshed preview here
        // could put an old detection over a newer board position.
        var frame = analysis.Board;
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32,
            null, frame.Bgra32.ToArray(), frame.Stride);
        bitmap.Freeze();
        GameExitEvidence = new(bitmap,
            $"Camera frame at {frame.CapturedAt.ToLocalTime():T}. Numbered boxes show what needs checking.", rows);
    }

    private static BoardCheckDetectionRow EvidenceRow(int number, double x, double y,
        double width, double height, string description)
    {
        // Use one canonical canvas so boxes keep their positions at every window size.
        var left = Math.Clamp(x * 960 - 4, 0, 959);
        var top = Math.Clamp(y * 600 - 4, 0, 599);
        return new(number, left, top, Math.Min(Math.Max(width * 960 + 8, 12), 960 - left),
            Math.Min(Math.Max(height * 600 + 8, 12), 600 - top), description);
    }

    private static string DescribeBoardRegion(double x, double y)
    {
        var horizontal = x < 1d / 3 ? "left" : x > 2d / 3 ? "right" : "center";
        var vertical = y < 1d / 3 ? "upper" : y > 2d / 3 ? "lower" : "middle";
        return $"{vertical} {horizontal} of the board (route uncertain)";
    }

    private void ClearGameExitInventoryCheck()
    {
        Game.UpdateInventoryProblemMarkers("save", null);
        _saveGameWorkflow?.Reset();
        _saveGameWorkflow = null;
        GameExitEvidence = null;
    }
}
