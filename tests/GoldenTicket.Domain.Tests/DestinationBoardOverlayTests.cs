using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain;

namespace GoldenTicket.Domain.Tests;

public sealed class DestinationBoardOverlayTests
{
    [Fact]
    public void EveryClassicDestinationEndpointHasAnOnBoardMarker()
    {
        var manifest = TestManifest.Manifest;
        var markers = DestinationBoardOverlay.Build(manifest,
            manifest.Tickets.Select(ticket => new TicketChoiceRow(ticket.TicketId, "", ticket.Points, 0)));
        var expectedCities = manifest.Tickets.SelectMany(ticket => new[] { ticket.CityA, ticket.CityB }).Distinct();

        Assert.Equal(expectedCities.Count(), markers.Count);
        Assert.All(markers, marker =>
        {
            Assert.True(marker.Left >= 0 && marker.Left + marker.Diameter <= DestinationBoardOverlay.Width,
                $"{marker.CityName} extends beyond the board width.");
            Assert.True(marker.Top >= 0 && marker.Top + marker.Diameter <= DestinationBoardOverlay.Height,
                $"{marker.CityName} extends beyond the board height.");
        });
    }

    [Fact]
    public void DroppingADestinationRemovesOnlyItsUnsharedCityRings()
    {
        var first = new TicketChoiceRow(new TicketId("t-denver--el-paso"), "", 4, 0);
        var second = new TicketChoiceRow(new TicketId("t-duluth--el-paso"), "", 10, 0);
        var markers = DestinationBoardOverlay.Build(TestManifest.Manifest, [first, second]);
        var denver = Assert.Single(markers, marker => marker.CityId == new CityId("denver"));
        var elPaso = Assert.Single(markers, marker => marker.CityId == new CityId("el-paso"));
        var duluth = Assert.Single(markers, marker => marker.CityId == new CityId("duluth"));
        Assert.All(markers, marker => Assert.True(marker.IsVisible));
        var visibilityChanges = 0;
        denver.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DestinationMarkerRow.IsVisible)) visibilityChanges++;
        };

        first.Keep = false;
        Assert.False(denver.IsVisible);
        Assert.Equal(1, visibilityChanges);
        Assert.True(elPaso.IsVisible);
        Assert.True(duluth.IsVisible);

        second.Keep = false;
        Assert.False(elPaso.IsVisible);
        Assert.False(duluth.IsVisible);

        first.Keep = true;
        Assert.True(denver.IsVisible);
        Assert.True(elPaso.IsVisible);
    }
}
