using GoldenTicket.Domain;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class GameScreenViewModel
{
    private readonly Dictionary<string, IReadOnlyList<PlacementTargetRow>> _inventoryProblemMarkers = [];

    // Independent checks own their cues so a card-monitor reset cannot erase a reload or
    // placement failure. All use the existing spheres on the live, upright board.
    internal void UpdateInventoryProblemMarkers(string source, BoardInventoryObservation? observation,
        int? expectedSlotMask = null)
    {
        if (observation?.State == BoardInventoryState.WaitingForFreshFrame) return;
        var targets = new List<PlacementTargetRow>();
        if (observation is { State: BoardInventoryState.UnexpectedTrain or BoardInventoryState.MissingTrains
            or BoardInventoryState.WrongColor or BoardInventoryState.Ambiguous })
        {
            foreach (var detection in observation.UnexpectedDetections)
            {
                var x = detection.X + detection.Width / 2;
                var y = detection.Y + detection.Height / 2;
                if (!double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1)
                    continue;
                var place = detection.RouteId is { } id
                    ? _main.Manifest.Describe(new RouteId(id)) : "route uncertain";
                Add(x * DestinationBoardOverlay.Width, y * DestinationBoardOverlay.Height,
                    $"Check detected {(detection.Color is { } color ? color + " " : "")}train: {place}");
            }

            // A missing piece has no detection center: show the expected spaces instead.
            // Named-route fallback also supports observations without detector geometry.
            var routeId = observation.RouteId ?? observation.UnexpectedTrains?.RouteId;
            if ((targets.Count == 0 || observation.State == BoardInventoryState.MissingTrains) &&
                routeId is not null && _main.Manifest.TryGetRoute(new RouteId(routeId), out var route) &&
                PlacementBoardOverlay.TryGetTargets(_main.Manifest, route.RouteId, route.Length, out var slots))
            {
                for (var index = 0; index < slots.Count; index++)
                    if (expectedSlotMask is null || (expectedSlotMask.Value & (1 << index)) != 0)
                        Add(slots[index].X, slots[index].Y,
                            $"Check train space {index + 1} on {_main.Manifest.Describe(route.RouteId)}");
            }
        }

        if (_inventoryProblemMarkers.TryGetValue(source, out var previous) && previous.SequenceEqual(targets)) return;
        if (targets.Count == 0)
        {
            if (!_inventoryProblemMarkers.Remove(source)) return;
        }
        else _inventoryProblemMarkers[source] = targets;
        RefreshPlacementTarget();

        void Add(double x, double y, string description)
        {
            if (targets.Any(target => Math.Abs(target.X - x) < 1 && Math.Abs(target.Y - y) < 1)) return;
            targets.Add(new(x, y, targets.Count + 1) { ProblemDescription = description });
        }
    }

    private IReadOnlyList<PlacementTargetRow>? CurrentInventoryProblemMarkers()
    {
        foreach (var source in new[] { "save", "restore", "placement", "proposal", "card" })
        {
            // Specific incomplete/cancelled placement guidance identifies the spaces to
            // fix. The generic card monitor otherwise mistakes placed pieces for extras.
            if (source == "card" && _unverifiedTrainSpaces is not null) continue;
            if (_inventoryProblemMarkers.TryGetValue(source, out var targets)) return targets;
        }
        return null;
    }
}
