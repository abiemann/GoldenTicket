using System.Buffers;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private readonly double _referenceX;
    private readonly double _referenceY;
    private double _left;
    private double _top;

    internal DestinationMarkerRow(CityId cityId, string cityName, double x, double y,
        IReadOnlyList<TicketChoiceRow> choices)
    {
        CityId = cityId;
        CityName = cityName;
        _referenceX = x * DestinationBoardOverlay.Width;
        _referenceY = y * DestinationBoardOverlay.Height;
        MoveTo(_referenceX, _referenceY);
        _choices = choices;
        foreach (var choice in choices) choice.PropertyChanged += ChoiceChanged;
    }

    public CityId CityId { get; }
    public string CityName { get; }
    public double Left { get => _left; private set => SetProperty(ref _left, value); }
    public double Top { get => _top; private set => SetProperty(ref _top, value); }
    // Keep the printed city dot fully visible inside the ring, including a small margin.
    public double Diameter => 40;
    public bool IsVisible => _choices.Any(choice => choice.Keep);

    internal double ReferenceX => _referenceX;
    internal double ReferenceY => _referenceY;

    internal void MoveTo(double x, double y)
    {
        Left = x - Diameter / 2;
        Top = y - Diameter / 2;
    }

    internal void ResetToReference() => MoveTo(_referenceX, _referenceY);

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

    /// <summary>Refine the reference positions against the printed city dots in the live crop.</summary>
    public static void AlignToPreview(BitmapSource? preview, IEnumerable<DestinationMarkerRow> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        if (preview is null || preview.PixelWidth != Width || preview.PixelHeight != Height ||
            preview.Format != PixelFormats.Bgra32) return;

        var stride = checked(preview.PixelWidth * 4);
        var length = checked(stride * preview.PixelHeight);
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            preview.CopyPixels(rented, stride, 0);
            var pixels = rented.AsSpan(0, length);
            foreach (var marker in markers)
            {
                if (CityDotLocator.TryLocate(pixels, preview.PixelWidth, preview.PixelHeight,
                        marker.ReferenceX, marker.ReferenceY, out var x, out var y))
                    marker.MoveTo(x, y);
                else
                    marker.ResetToReference();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    // Centers of the printed city-dot rims, redigitized from the 3456 x 2160 upright board photo
    // and expressed in 1996 x 1248 reference coordinates. Normalize below so the rings follow
    // the same printed dots when the window or live crop size changes.
    private static readonly IReadOnlyDictionary<string, (int X, int Y)> CityCenters =
        new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            ["atlanta"] = (1554, 790),
            ["boston"] = (1877, 263),
            ["calgary"] = (460, 164),
            ["charleston"] = (1737, 802),
            ["chicago"] = (1358, 509),
            ["dallas"] = (1105, 971),
            ["denver"] = (777, 686),
            ["duluth"] = (1119, 396),
            ["el-paso"] = (754, 1016),
            ["helena"] = (662, 404),
            ["houston"] = (1187, 1043),
            ["kansas-city"] = (1104, 653),
            ["las-vegas"] = (414, 830),
            ["little-rock"] = (1240, 818),
            ["los-angeles"] = (284, 941),
            ["miami"] = (1800, 1089),
            ["montreal"] = (1738, 157),
            ["nashville"] = (1455, 726),
            ["new-orleans"] = (1371, 1027),
            ["new-york"] = (1778, 398),
            ["oklahoma-city"] = (1064, 810),
            ["omaha"] = (1060, 563),
            ["phoenix"] = (523, 948),
            ["pittsburgh"] = (1615, 480),
            ["portland"] = (161, 387),
            ["raleigh"] = (1683, 685),
            ["saint-louis"] = (1272, 656),
            ["salt-lake-city"] = (519, 630),
            ["san-francisco"] = (143, 745),
            ["santa-fe"] = (764, 850),
            ["sault-st-marie"] = (1367, 275),
            ["seattle"] = (201, 295),
            ["toronto"] = (1580, 313),
            ["vancouver"] = (209, 194),
            ["washington"] = (1794, 563),
            ["winnipeg"] = (905, 179),
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
