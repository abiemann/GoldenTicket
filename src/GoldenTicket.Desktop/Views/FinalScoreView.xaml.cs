using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GoldenTicket.Desktop.Views;

public partial class FinalScoreView : UserControl
{
    public FinalScoreView() => InitializeComponent();

    internal double PrepareForImage(double width)
    {
        // This is a detached export instance. Never scroll or resize the live standings.
        FinalActionsPanel.Visibility = Visibility.Collapsed;
        PlayerNavigation.Visibility = Visibility.Collapsed;
        ResultCards.Height = double.NaN;
        ResultsScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        ResultCards.Measure(new Size(width - 48, double.PositiveInfinity));
        var panels = Descendants<Border>(ResultCards).Where(panel => panel.Name == "PlayerResultPanel").ToArray();
        foreach (var panel in panels)
            panel.MaxHeight = double.PositiveInfinity;
        foreach (var scroll in Descendants<ScrollViewer>(ResultCards).Where(scroll => scroll.Name == "WitnessTrailScroll"))
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        foreach (var name in Descendants<TextBlock>(ResultCards).Where(text => text.TextTrimming != TextTrimming.None))
        {
            name.TextTrimming = TextTrimming.None;
            name.TextWrapping = TextWrapping.Wrap;
        }
        // Measure the detached cards directly after lifting their on-screen bounds;
        // the parent row may still carry the old viewport measurement in this pass.
        var cardHeight = 0d;
        foreach (var panel in panels)
        {
            panel.Measure(new Size(panel.Width + panel.Margin.Left + panel.Margin.Right, double.PositiveInfinity));
            cardHeight = Math.Max(cardHeight, panel.DesiredSize.Height);
        }
        ResultCards.Measure(new Size(width - 48, double.PositiveInfinity));
        return Math.Max(1000, cardHeight + 190);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

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
