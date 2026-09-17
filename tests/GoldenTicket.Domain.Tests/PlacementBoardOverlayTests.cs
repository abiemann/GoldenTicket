using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Manifest;

namespace GoldenTicket.Domain.Tests;

public sealed class PlacementBoardOverlayTests
{
    [Fact]
    public void EveryClassicRouteHasATargetInsideTheUprightBoard()
    {
        var manifest = TestManifest.Manifest;
        foreach (var route in manifest.Routes)
        {
            Assert.True(PlacementBoardOverlay.TryGetTarget(manifest, route.RouteId, out var x, out var y),
                $"Missing physical-lane target for {route.RouteId.Value}.");
            Assert.InRange(x, 0, DestinationBoardOverlay.Width);
            Assert.InRange(y, 0, DestinationBoardOverlay.Height);
        }
    }

    [Fact]
    public void EveryClassicRouteHasOneDistinctSphereForEachRequestedTrain()
    {
        var manifest = TestManifest.Manifest;
        foreach (var route in manifest.Routes)
        {
            Assert.True(PlacementBoardOverlay.TryGetTargets(manifest, route.RouteId,
                    route.Length, out var targets),
                $"Missing measured train-space targets for {route.RouteId.Value}.");
            Assert.Equal(route.Length, targets.Count);
            Assert.Equal(route.Length, targets.Distinct().Count());
            foreach (var (x, y) in targets)
            {
                Assert.InRange(x, 0, DestinationBoardOverlay.Width);
                Assert.InRange(y, 0, DestinationBoardOverlay.Height);
            }
        }
    }

    [Fact]
    public void ParallelLanesHaveSeparateTargetsAndAtlantaRaleighAIsTheUpperLane()
    {
        var manifest = TestManifest.Manifest;
        var parallelGroups = manifest.Routes.Where(route => route.ParallelGroupId is not null)
            .GroupBy(route => route.ParallelGroupId, StringComparer.Ordinal);
        foreach (var group in parallelGroups)
        {
            var lanes = group.ToArray();
            Assert.Equal(2, lanes.Length);
            Assert.True(PlacementBoardOverlay.TryGetTarget(manifest, lanes[0].RouteId, out var ax, out var ay));
            Assert.True(PlacementBoardOverlay.TryGetTarget(manifest, lanes[1].RouteId, out var bx, out var by));
            Assert.True(Math.Sqrt(Math.Pow(ax - bx, 2) + Math.Pow(ay - by, 2)) >= 5,
                $"Parallel lanes {lanes[0].RouteId.Value} and {lanes[1].RouteId.Value} point to the same track.");
        }

        var atlantaRaleigh = manifest.Routes.Where(route =>
            new[] { route.CityA.Value, route.CityB.Value }.OrderBy(city => city)
                .SequenceEqual(["atlanta", "raleigh"]))
            .OrderBy(route => route.DisplayLaneLabel).ToArray();
        Assert.Equal(2, atlantaRaleigh.Length);
        Assert.Equal("lane A", atlantaRaleigh[0].DisplayLaneLabel);
        Assert.Equal("lane B", atlantaRaleigh[1].DisplayLaneLabel);
        Assert.True(PlacementBoardOverlay.TryGetTarget(manifest, atlantaRaleigh[0].RouteId,
            out _, out var upperY));
        Assert.True(PlacementBoardOverlay.TryGetTarget(manifest, atlantaRaleigh[1].RouteId,
            out _, out var lowerY));
        Assert.True(upperY < lowerY, "Atlanta–Raleigh lane A must mark the upper (blue) track.");
    }
}
