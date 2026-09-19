using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GoldenTicket.Desktop.Views;

public partial class FinalScoreView : UserControl
{
    public FinalScoreView() => InitializeComponent();

    private void OnPreviousPlayer(object sender, RoutedEventArgs e) =>
        ResultsScroll.ScrollToHorizontalOffset(ResultsScroll.HorizontalOffset - 356);

    private void OnNextPlayer(object sender, RoutedEventArgs e) =>
        ResultsScroll.ScrollToHorizontalOffset(ResultsScroll.HorizontalOffset + 356);

    private void OnResultsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (PreviousPlayerButton is null || NextPlayerButton is null || PlayerNavigation is null) return;
        PreviousPlayerButton.IsEnabled = ResultsScroll.HorizontalOffset > 1;
        NextPlayerButton.IsEnabled = ResultsScroll.HorizontalOffset < ResultsScroll.ScrollableWidth - 1;
        PlayerNavigation.Visibility = ResultsScroll.ScrollableWidth > 1
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnResultsMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Long route descriptions keep their own vertical scrolling.
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != ResultsScroll;)
        {
            if (element is ScrollViewer { ScrollableHeight: > 0 } &&
                (Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
            element = element is Visual ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        if (ResultsScroll.ScrollableWidth <= 0) return;
        ResultsScroll.ScrollToHorizontalOffset(ResultsScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }
}
