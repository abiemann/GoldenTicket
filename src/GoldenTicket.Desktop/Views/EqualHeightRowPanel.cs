using System.Windows;
using System.Windows.Controls;

namespace GoldenTicket.Desktop.Views;

/// <summary>A horizontal row whose children share the tallest measured height and stay centered.</summary>
public sealed class EqualHeightRowPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = 0d;
        var height = 0d;
        foreach (UIElement child in InternalChildren)
        {
            // Preserve the viewport's height constraint so a card's internal trail
            // scroller can fit small windows. Width remains free for horizontal scrolling.
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var height = Math.Min(finalSize.Height,
            InternalChildren.Cast<UIElement>().Select(child => child.DesiredSize.Height).DefaultIfEmpty().Max());
        var top = Math.Max(0, (finalSize.Height - height) / 2);
        var left = 0d;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(left, top, child.DesiredSize.Width, height));
            left += child.DesiredSize.Width;
        }
        return finalSize;
    }
}
