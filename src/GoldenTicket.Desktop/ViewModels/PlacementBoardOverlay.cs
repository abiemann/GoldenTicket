using GoldenTicket.Domain;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

/// <summary>
/// Presentation positions for a pending physical route placement. Coordinates are measured on
/// the upright classic-US board, then returned in the same rectified 960 x 600 space as
/// <see cref="DestinationBoardOverlay"/>. The rules manifest deliberately has no image geometry.
/// </summary>
public static class PlacementBoardOverlay
{
    /// <summary>
    /// Locate every requested train space in the displayed upright crop. These are the exact
    /// measured slots used by camera verification, so a long route gets one cue per train.
    /// </summary>
    public static bool TryGetTargets(BoardManifest manifest, RouteId routeId, int trainCount,
        out IReadOnlyList<(double X, double Y)> targets)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        targets = [];
        if (manifest.ProfileId != ManifestLoader.ClassicUsProfileId ||
            !manifest.TryGetRoute(routeId, out var route) || route.Length != trainCount ||
            !ClassicUsRouteGeometry.TryGetSlots(routeId.Value, out var slots) ||
            slots.Count != trainCount)
            return false;

        targets = slots.Select(point =>
            (point.X * DestinationBoardOverlay.Width,
                point.Y * DestinationBoardOverlay.Height)).ToArray();
        return true;
    }

    // Route centers digitized from the empty upright board. Each parallel lane needs its own
    // point: the midpoint between endpoint cities often lies between both physical tracks.
    // For uncolored parallel tracks, lane A is the upper/left track in this upright view.
    private static readonly IReadOnlyDictionary<string, (int X, int Y)> RouteCenters =
        new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal)
        {
            ["atlanta--new-orleans--a"] = (1439, 879), // yellow
            ["atlanta--new-orleans--b"] = (1455, 885), // orange
            ["atlanta--raleigh--a"] = (1607, 721), // upper track; blue trains in reported frame
            ["atlanta--raleigh--b"] = (1619, 738), // lower track
            ["boston--montreal--a"] = (1812, 190),
            ["boston--montreal--b"] = (1830, 171),
            ["boston--new-york--a"] = (1833, 328), // yellow
            ["boston--new-york--b"] = (1862, 335), // red
            ["chicago--pittsburgh--a"] = (1483, 458), // orange
            ["chicago--pittsburgh--b"] = (1482, 485), // black
            ["chicago--saint-louis--a"] = (1309, 576), // green
            ["chicago--saint-louis--b"] = (1344, 583), // white
            ["dallas--houston--a"] = (1131, 1015),
            ["dallas--houston--b"] = (1157, 996),
            ["dallas--oklahoma-city--a"] = (1081, 888),
            ["dallas--oklahoma-city--b"] = (1110, 885),
            ["denver--kansas-city--a"] = (941, 678), // black
            ["denver--kansas-city--b"] = (941, 706), // orange
            ["denver--salt-lake-city--a"] = (652, 614), // red
            ["denver--salt-lake-city--b"] = (651, 639), // yellow
            ["duluth--omaha--a"] = (1077, 472),
            ["duluth--omaha--b"] = (1103, 479),
            ["kansas-city--oklahoma-city--a"] = (1075, 730),
            ["kansas-city--oklahoma-city--b"] = (1102, 733),
            ["kansas-city--omaha--a"] = (1078, 606),
            ["kansas-city--omaha--b"] = (1106, 607),
            ["kansas-city--saint-louis--a"] = (1183, 637), // blue
            ["kansas-city--saint-louis--b"] = (1183, 660), // pink
            ["los-angeles--san-francisco--a"] = (170, 846), // yellow
            ["los-angeles--san-francisco--b"] = (198, 855), // pink
            ["new-york--pittsburgh--a"] = (1701, 422), // white
            ["new-york--pittsburgh--b"] = (1701, 453), // green
            ["new-york--washington--a"] = (1778, 479), // orange
            ["new-york--washington--b"] = (1806, 478), // black
            ["portland--san-francisco--a"] = (98, 567), // green
            ["portland--san-francisco--b"] = (124, 565), // pink
            ["portland--seattle--a"] = (172, 339),
            ["portland--seattle--b"] = (196, 345),
            ["raleigh--washington--a"] = (1692, 623),
            ["raleigh--washington--b"] = (1722, 632),
            ["salt-lake-city--san-francisco--a"] = (331, 664), // orange
            ["salt-lake-city--san-francisco--b"] = (336, 690), // white
            ["seattle--vancouver--a"] = (190, 244),
            ["seattle--vancouver--b"] = (215, 244),
            ["calgary--winnipeg"] = (683, 120), // the white arc rises above both cities
            ["calgary--seattle"] = (326, 285),
            ["charleston--raleigh"] = (1720, 742),
            ["chicago--omaha"] = (1210, 498),
            ["chicago--toronto"] = (1468, 379),
            ["denver--oklahoma-city"] = (922, 780),
            ["denver--omaha"] = (918, 600),
            ["denver--phoenix"] = (590, 800),
            ["el-paso--houston"] = (970, 1088),
            ["el-paso--los-angeles"] = (520, 1035),
            ["el-paso--oklahoma-city"] = (910, 929),
            ["las-vegas--los-angeles"] = (340, 833),
            ["los-angeles--phoenix"] = (404, 918),
            ["miami--new-orleans"] = (1586, 980),
            ["montreal--sault-st-marie"] = (1550, 165), // the black arc rises above both cities
            ["montreal--toronto"] = (1659, 209),
            ["nashville--raleigh"] = (1569, 663),
            ["portland--salt-lake-city"] = (340, 455),
        };

    /// <summary>Locate the center of the named physical lane in the displayed board crop.</summary>
    public static bool TryGetTarget(BoardManifest manifest, RouteId routeId, out double x, out double y)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        x = y = 0;
        if (manifest.ProfileId != ManifestLoader.ClassicUsProfileId ||
            !manifest.TryGetRoute(routeId, out var route)) return false;

        if (TryGetTargets(manifest, routeId, route.Length, out var slots))
        {
            x = slots.Average(point => point.X);
            y = slots.Average(point => point.Y);
            return true;
        }

        (double X, double Y) point;
        if (RouteCenters.TryGetValue(routeId.Value, out var measured))
            point = (measured.X, measured.Y);
        else if (route.ParallelGroupId is not null)
            return false; // Never present a city-pair midpoint as one of two lanes.
        else if (DestinationBoardOverlay.TryGetReferenceCityCenter(route.CityA, out var a) &&
                 DestinationBoardOverlay.TryGetReferenceCityCenter(route.CityB, out var b))
            point = ((a.X + b.X) / 2d, (a.Y + b.Y) / 2d);
        else
            return false;

        x = point.X / 1996d * DestinationBoardOverlay.Width;
        y = point.Y / 1248d * DestinationBoardOverlay.Height;
        return true;
    }
}
