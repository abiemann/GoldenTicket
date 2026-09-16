using System.Windows;
using System.Windows.Controls;

namespace GoldenTicket.Desktop.Views;

public partial class GameTableView : UserControl
{
    public GameTableView() => InitializeComponent();

    private void OpenControls_Click(object sender, RoutedEventArgs e) => ControlsOverlay.Visibility = Visibility.Visible;

    private void CloseControls_Click(object sender, RoutedEventArgs e) => ControlsOverlay.Visibility = Visibility.Collapsed;
}
