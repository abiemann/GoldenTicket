using System.Windows;
using System.Windows.Controls;
using GoldenTicket.Desktop.ViewModels;

namespace GoldenTicket.Desktop.Views;

public partial class SetupView : UserControl
{
    public SetupView() => InitializeComponent();

    private MainViewModel? Model => DataContext as MainViewModel;

    private void OnAddSeat(object sender, RoutedEventArgs e) => Model?.Setup.AddSeat();

    private void OnRemoveSeat(object sender, RoutedEventArgs e) => Model?.Setup.RemoveSeat();
}
