using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Manifest;

namespace GoldenTicket.Desktop.ViewModels;

/// <summary>
/// A city ring in the board's canonical, rectified 960 x 600 coordinate space. The game-table crop
/// is rotated to that same orientation after setup, so a matching overlay can scale with the image.
/// </summary>
public sealed class DestinationMarkerRow : ObservableObject
{
    private readonly IReadOnlyList<TicketChoiceRow> _choices;

    internal DestinationMarkerRow(CityId cityId, string cityName, double x, double y,
        IReadOnlyList<TicketChoiceRow> choices)
    {
        CityId = cityId;
        CityName = cityName;
        Left = x * DestinationBoardOverlay.Width - Diameter / 2;
        Top = y * DestinationBoardOverlay.Height - Diameter / 2;
        _choices = choices;
        foreach (var choice in choices) choice.PropertyChanged += ChoiceChanged;
    }

    public CityId CityId { get; }
    public string CityName { get; }
    public double Left { get; }
    public double Top { get; }
    public double Diameter => 34;
    public bool IsVisible => _choices.Any(choice => choice.Keep);

    private void ChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TicketChoiceRow.Keep)) OnPropertyChanged(nameof(IsVisible));
    }
}

/// <summary>
/// City centers digitized from the upright classic-US board image. This is presentation-only
/// geometry, separate from the audited rules manifest. The live image is projectively rectified
/// to the same upright board orientation before display. Other board profiles get no markers.
/// </summary>
public static class DestinationBoardOverlay
{
    public const double Width = 960;
    public const double Height = 600;

    // City centers in the 1996 x 1248 upright reference image. Each value is normalized below,
    // so the rings stay on the same printed cities when the window or live crop size changes.
    private static readonly IReadOnlyDictionary<string, (int X, int Y)> CityCenters =
        new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            ["atlanta"] = (1554, 790),
            ["boston"] = (1877, 264),
            ["calgary"] = (462, 162),
            ["charleston"] = (1738, 802),
            ["chicago"] = (1357, 508),
            ["dallas"] = (1107, 970),
            ["denver"] = (779, 686),
            ["duluth"] = (1120, 395),
            ["el-paso"] = (755, 1013),
            ["helena"] = (661, 403),
            ["houston"] = (1187, 1042),
            ["kansas-city"] = (1103, 655),
            ["las-vegas"] = (415, 832),
            ["little-rock"] = (1241, 817),
            ["los-angeles"] = (284, 941),
            ["miami"] = (1802, 1088),
            ["montreal"] = (1740, 156),
            ["nashville"] = (1457, 725),
            ["new-orleans"] = (1371, 1027),
            ["new-york"] = (1779, 397),
            ["oklahoma-city"] = (1068, 809),
            ["omaha"] = (1062, 562),
            ["phoenix"] = (525, 946),
            ["pittsburgh"] = (1613, 479),
            ["portland"] = (159, 386),
            ["raleigh"] = (1681, 684),
            ["saint-louis"] = (1270, 655),
            ["salt-lake-city"] = (518, 630),
            ["san-francisco"] = (142, 743),
            ["santa-fe"] = (766, 851),
            ["sault-st-marie"] = (1366, 270),
            ["seattle"] = (202, 295),
            ["toronto"] = (1581, 313),
            ["vancouver"] = (208, 191),
            ["washington"] = (1796, 559),
            ["winnipeg"] = (907, 176),
        };

    public static IReadOnlyList<DestinationMarkerRow> Build(BoardManifest manifest,
        IEnumerable<TicketChoiceRow> offer)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(offer);
        if (manifest.ProfileId != "ttr-us-classic-en-v1") return [];

        var choicesByCity = new Dictionary<CityId, List<TicketChoiceRow>>();
        foreach (var choice in offer)
        {
            var ticket = manifest.Ticket(choice.TicketId);
            Add(ticket.CityA, choice);
            Add(ticket.CityB, choice);
        }

        var markers = new List<DestinationMarkerRow>(choicesByCity.Count);
        foreach (var (city, choices) in choicesByCity)
        {
            if (!CityCenters.TryGetValue(city.Value, out var point)) continue;
            markers.Add(new DestinationMarkerRow(city, manifest.City(city).DisplayName,
                point.X / 1996d, point.Y / 1248d, choices));
        }
        return markers;

        void Add(CityId city, TicketChoiceRow choice)
        {
            if (!choicesByCity.TryGetValue(city, out var choices))
                choicesByCity.Add(city, choices = []);
            choices.Add(choice);
        }
    }
}
