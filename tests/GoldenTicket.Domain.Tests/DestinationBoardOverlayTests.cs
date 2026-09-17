using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GoldenTicket.Domain.Tests;

public sealed class DestinationBoardOverlayTests
{
    [Fact]
    public void KeptSoloDestinationsMarkEachDistinctEndpoint()
    {
        var markers = DestinationBoardOverlay.BuildKept(TestManifest.Manifest,
            [new TicketId("t-seattle--los-angeles"), new TicketId("t-helena--los-angeles")]);

        Assert.Equal(3, markers.Count);
        Assert.Equal(new[] { "Helena", "Los Angeles", "Seattle" },
            markers.Select(marker => marker.CityName).OrderBy(name => name));
        Assert.All(markers, marker => Assert.True(marker.IsVisible));
    }

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

    [Fact]
    public void EveryOfferedDestinationHasALineBetweenItsExistingCityMarkers()
    {
        var manifest = TestManifest.Manifest;
        var choices = new[]
        {
            new TicketChoiceRow(new TicketId("t-denver--el-paso"), "", 4, 0),
            new TicketChoiceRow(new TicketId("t-duluth--el-paso"), "", 10, 0),
            new TicketChoiceRow(new TicketId("t-seattle--new-york"), "", 22, 0),
        };
        var markers = DestinationBoardOverlay.Build(manifest, choices);

        var lines = DestinationBoardOverlay.BuildLines(manifest, choices, markers);

        Assert.Equal(choices.Length, lines.Count);
        foreach (var choice in choices)
        {
            var ticket = manifest.Ticket(choice.TicketId);
            var line = Assert.Single(lines, line => ReferenceEquals(line.Choice, choice));
            Assert.Same(Assert.Single(markers, marker => marker.CityId == ticket.CityA), line.Start);
            Assert.Same(Assert.Single(markers, marker => marker.CityId == ticket.CityB), line.End);
            Assert.True(line.IsVisible);
        }
    }

    [Fact]
    public void DroppingADestinationHidesOnlyItsLineEvenWithASharedEndpoint()
    {
        var first = new TicketChoiceRow(new TicketId("t-denver--el-paso"), "", 4, 0);
        var second = new TicketChoiceRow(new TicketId("t-duluth--el-paso"), "", 10, 0);
        var markers = DestinationBoardOverlay.Build(TestManifest.Manifest, [first, second]);
        var lines = DestinationBoardOverlay.BuildLines(TestManifest.Manifest, [first, second], markers);
        var firstLine = Assert.Single(lines, line => ReferenceEquals(line.Choice, first));
        var secondLine = Assert.Single(lines, line => ReferenceEquals(line.Choice, second));
        Assert.Same(firstLine.End, secondLine.End);
        var visibilityChanges = 0;
        firstLine.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DestinationLineRow.IsVisible)) visibilityChanges++;
        };

        first.Keep = false;

        Assert.False(firstLine.IsVisible);
        Assert.True(secondLine.IsVisible);
        Assert.Equal(1, visibilityChanges);

        first.Keep = true;
        Assert.True(firstLine.IsVisible);
        Assert.Equal(2, visibilityChanges);
    }

    [Fact]
    public void LiveBoardCropMovesRingToThePrintedDotAndNotifiesTheCanvas()
    {
        var choice = new TicketChoiceRow(new TicketId("t-denver--el-paso"), "", 4, 0);
        var markers = DestinationBoardOverlay.Build(TestManifest.Manifest, [choice]);
        var denver = Assert.Single(markers, marker => marker.CityId == new CityId("denver"));
        var line = Assert.Single(DestinationBoardOverlay.BuildLines(TestManifest.Manifest, [choice], markers));
        Assert.Same(denver, line.Start);
        var referenceX = (int)Math.Round(denver.Left + denver.Diameter / 2);
        var referenceY = (int)Math.Round(denver.Top + denver.Diameter / 2);
        var targetX = referenceX + 5;
        var targetY = referenceY - 3;
        const int width = 960;
        const int height = 600;
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 205;
            pixels[offset + 1] = 212;
            pixels[offset + 2] = 215;
            pixels[offset + 3] = 255;
        }
        for (var dy = -7; dy <= 7; dy++)
        for (var dx = -7; dx <= 7; dx++)
        {
            var squared = dx * dx + dy * dy;
            if (squared > 49) continue;
            var offset = ((targetY + dy) * width + targetX + dx) * 4;
            var inner = squared <= 36;
            pixels[offset] = inner ? (byte)48 : (byte)41;
            pixels[offset + 1] = inner ? (byte)97 : (byte)52;
            pixels[offset + 2] = inner ? (byte)198 : (byte)67;
        }
        var preview = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, pixels, width * 4);
        var changed = new List<string>();
        var lineChanged = new List<string>();
        var originalLineStartX = line.X1;
        denver.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null) changed.Add(args.PropertyName);
        };
        line.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null) lineChanged.Add(args.PropertyName);
        };

        DestinationBoardOverlay.AlignToPreview(preview, markers);

        Assert.InRange(denver.CenterX, targetX - 1, targetX + 1);
        Assert.InRange(denver.CenterY, targetY - 1, targetY + 1);
        Assert.Equal(denver.CenterX, line.Start.CenterX);
        Assert.Equal(denver.CenterY, line.Start.CenterY);
        Assert.Contains(nameof(DestinationMarkerRow.Left), changed);
        Assert.Contains(nameof(DestinationMarkerRow.Top), changed);
        Assert.Contains(nameof(DestinationMarkerRow.CenterX), changed);
        Assert.Contains(nameof(DestinationMarkerRow.CenterY), changed);
        Assert.NotEqual(originalLineStartX, line.X1);
        Assert.Contains(nameof(DestinationLineRow.X1), lineChanged);
        Assert.Contains(nameof(DestinationLineRow.Y1), lineChanged);
    }
}
