using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GoldenTicket.Desktop.Views;

public partial class FinalStandingsView : UserControl
{
    public FinalStandingsView() => InitializeComponent();

    public static readonly DependencyProperty BoardImageProperty = DependencyProperty.Register(
        nameof(BoardImage), typeof(ImageSource), typeof(FinalStandingsView));

    public ImageSource? BoardImage
    {
        get => (ImageSource?)GetValue(BoardImageProperty);
        set => SetValue(BoardImageProperty, value);
    }

    internal double PrepareForImage(double width) => Standings.PrepareForImage(width);
}
