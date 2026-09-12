using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.Views;

public partial class CameraView : UserControl
{
    private CameraViewModel? _subscribed;
    private NormalizedPoint _keyboardPoint = new(.05, .05);

    public CameraView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        Unsubscribe();
        _subscribed = DataContext as CameraViewModel;
        if (_subscribed is not null)
        {
            _subscribed.SelectedCorners.CollectionChanged += CornersChanged;
            _subscribed.PropertyChanged += CameraPropertyChanged;
        }
        DrawCorners();
    }

    private void Unsubscribe()
    {
        if (_subscribed is not null)
        {
            _subscribed.SelectedCorners.CollectionChanged -= CornersChanged;
            _subscribed.PropertyChanged -= CameraPropertyChanged;
        }
        _subscribed = null;
    }

    private void CornersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ResetKeyboardPoint();
        DrawCorners();
    }

    private void CameraPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CameraViewModel.SelectingCorners))
        {
            ResetKeyboardPoint();
            if (_subscribed?.SelectingCorners == true) PreviewImage.Focus();
            DrawCorners();
        }
        else if (e.PropertyName == nameof(CameraViewModel.Preview)) DrawCorners();
    }

    private void ResetKeyboardPoint()
    {
        _keyboardPoint = (_subscribed?.SelectedCorners.Count ?? 0) switch
        {
            0 => new(.05, .05), 1 => new(.95, .05), 2 => new(.95, .95), _ => new(.05, .95)
        };
    }
    private void PreviewArea_SizeChanged(object sender, SizeChangedEventArgs e) => DrawCorners();

    private Rect ImageRectangle()
    {
        if (PreviewImage.Source is not { } source || source.Width <= 0 || source.Height <= 0) return Rect.Empty;
        var scale = Math.Min(PreviewArea.ActualWidth / source.Width, PreviewArea.ActualHeight / source.Height);
        var width = source.Width * scale;
        var height = source.Height * scale;
        return new Rect((PreviewArea.ActualWidth - width) / 2, (PreviewArea.ActualHeight - height) / 2, width, height);
    }

    private void PreviewImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not CameraViewModel { SelectingCorners: true } vm) return;
        var rectangle = ImageRectangle();
        var position = e.GetPosition(PreviewArea);
        if (rectangle.IsEmpty || !rectangle.Contains(position)) return;
        vm.AddBoardCorner(new NormalizedPoint((position.X - rectangle.Left) / rectangle.Width,
            (position.Y - rectangle.Top) / rectangle.Height));
        e.Handled = true;
    }

    private void PreviewImage_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not CameraViewModel { SelectingCorners: true } vm) return;
        var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? .025 : .0025;
        switch (e.Key)
        {
            case Key.Left: _keyboardPoint = _keyboardPoint with { X = Math.Max(0, _keyboardPoint.X - step) }; break;
            case Key.Right: _keyboardPoint = _keyboardPoint with { X = Math.Min(1, _keyboardPoint.X + step) }; break;
            case Key.Up: _keyboardPoint = _keyboardPoint with { Y = Math.Max(0, _keyboardPoint.Y - step) }; break;
            case Key.Down: _keyboardPoint = _keyboardPoint with { Y = Math.Min(1, _keyboardPoint.Y + step) }; break;
            case Key.Enter: vm.AddBoardCorner(_keyboardPoint); break;
            default: return;
        }
        DrawCorners();
        e.Handled = true;
    }

    private void DrawCorners()
    {
        if (CornerOverlay is null) return;
        CornerOverlay.Children.Clear();
        if (DataContext is not CameraViewModel vm) return;
        var rectangle = ImageRectangle();
        if (rectangle.IsEmpty) return;
        var points = new PointCollection(vm.SelectedCorners.Select(p =>
            new Point(rectangle.Left + p.X * rectangle.Width, rectangle.Top + p.Y * rectangle.Height)));
        if (points.Count > 1)
        {
            if (points.Count == 4) points.Add(points[0]);
            CornerOverlay.Children.Add(new Polyline { Points = points, Stroke = Brushes.Gold, StrokeThickness = 3 });
        }
        for (var i = 0; i < vm.SelectedCorners.Count; i++)
        {
            var point = points[i];
            var marker = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Brushes.Gold,
                BorderBrush = Brushes.Black, BorderThickness = new Thickness(2),
                Child = new TextBlock { Text = (i + 1).ToString(), Foreground = Brushes.Black,
                    FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center }
            };
            Canvas.SetLeft(marker, point.X - 13);
            Canvas.SetTop(marker, point.Y - 13);
            CornerOverlay.Children.Add(marker);
        }
        if (vm.SelectingCorners)
        {
            var x = rectangle.Left + _keyboardPoint.X * rectangle.Width;
            var y = rectangle.Top + _keyboardPoint.Y * rectangle.Height;
            CornerOverlay.Children.Add(new Line { X1 = x - 12, X2 = x + 12, Y1 = y, Y2 = y, Stroke = Brushes.White, StrokeThickness = 3 });
            CornerOverlay.Children.Add(new Line { X1 = x, X2 = x, Y1 = y - 12, Y2 = y + 12, Stroke = Brushes.White, StrokeThickness = 3 });
        }
    }
}
