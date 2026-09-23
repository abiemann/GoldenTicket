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
    private bool _showKeyboardPoint;
    private int _activeCorner = -1;
    private int _draggedCorner = -1;
    private Vector _dragOffset;

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
        UpdateViewport();
    }

    private void Unsubscribe()
    {
        EndPan();
        EndDrag();
        _spacePanning = false;
        _activeCorner = -1;
        _showKeyboardPoint = false;
        if (_subscribed is not null)
        {
            _subscribed.SelectedCorners.CollectionChanged -= CornersChanged;
            _subscribed.PropertyChanged -= CameraPropertyChanged;
        }
        _subscribed = null;
    }

    private void CornersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        EndPan();
        if (e.Action != NotifyCollectionChangedAction.Replace)
        {
            EndDrag();
            _activeCorner = -1;
            _showKeyboardPoint = false;
            ResetKeyboardPoint();
        }
        DrawCorners();
    }

    private void CameraPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CameraViewModel.SelectingCorners))
        {
            EndPan();
            _showKeyboardPoint = false;
            ResetKeyboardPoint();
            if (_subscribed?.SelectingCorners == true)
            {
                PanPreviewToggle.IsChecked = false;
                _activeCorner = -1;
                PreviewImage.Focus();
            }
            DrawCorners();
            UpdatePreviewCursor();
        }
        else if (e.PropertyName == nameof(CameraViewModel.Preview))
        {
            if (_subscribed?.Preview is null) { EndDrag(); EndPan(); ResetZoom(); }
            UpdateViewport();
        }
        else if (e.PropertyName == nameof(CameraViewModel.HasBoardCrop)) DrawCorners();
        else if (e.PropertyName == nameof(CameraViewModel.IsBusy) && _subscribed?.IsBusy == true) EndPan();
    }

    private void ResetKeyboardPoint()
    {
        _keyboardPoint = (_subscribed?.SelectedCorners.Count ?? 0) switch
        {
            0 => new(.05, .05), 1 => new(.95, .05), 2 => new(.95, .95), _ => new(.05, .95)
        };
    }
    private void PreviewArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        EndPan();
        EndDrag();
        UpdateViewport();
    }

    private Rect FitImageRectangle()
    {
        if (PreviewImage.Source is not { } source || source.Width <= 0 || source.Height <= 0 ||
            PreviewArea.ActualWidth <= 0 || PreviewArea.ActualHeight <= 0) return Rect.Empty;
        var scale = Math.Min(PreviewArea.ActualWidth / source.Width, PreviewArea.ActualHeight / source.Height);
        var width = source.Width * scale;
        var height = source.Height * scale;
        return new Rect((PreviewArea.ActualWidth - width) / 2, (PreviewArea.ActualHeight - height) / 2, width, height);
    }

    private Rect ImageRectangle()
    {
        var fit = FitImageRectangle();
        if (fit.IsEmpty) return fit;
        var width = fit.Width * _previewZoom;
        var height = fit.Height * _previewZoom;
        return new Rect((PreviewArea.ActualWidth - width) / 2 + _previewPan.X,
            (PreviewArea.ActualHeight - height) / 2 + _previewPan.Y, width, height);
    }

    private void PreviewImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HideKeyboardPoint();
        if (_panning || _backgroundPress is not null || PanRequested) return;
        if (DataContext is not CameraViewModel { IsBusy: false } vm) return;
        var position = e.GetPosition(PreviewArea);
        // Image is not a Control, so clicking plain image pixels does not focus it
        // automatically after using a toolbar button.
        if (PointInImage(position, false) is not null) PreviewImage.Focus();
        var corner = HitCorner(position);
        if (corner >= 0)
        {
            PreviewImage.Focus();
            _activeCorner = corner;
            var rectangle = ImageRectangle();
            var point = vm.SelectedCorners[corner];
            _dragOffset = position - new Point(rectangle.Left + point.X * rectangle.Width, rectangle.Top + point.Y * rectangle.Height);
            // Capture the viewport, which stays in place while the image is zoomed or panned.
            if (PreviewArea.CaptureMouse()) _draggedCorner = corner;
            DrawCorners();
        }
        else if (BeginBackgroundPress(position))
        {
            // Capture only after focus is established. Failure cancels the gesture;
            // it must never fall through and accidentally place a crop corner.
            if (!PreviewArea.CaptureMouse()) EndPan();
        }
        else if (vm.SelectingCorners && PointInImage(position, false) is { } point)
        {
            PreviewImage.Focus();
            vm.AddBoardCorner(point);
        }
        else return;
        UpdatePreviewCursor();
        e.Handled = true;
    }

    private NormalizedPoint? PointInImage(Point position, bool clamp)
    {
        var rectangle = ImageRectangle();
        if (rectangle.IsEmpty || !double.IsFinite(position.X) || !double.IsFinite(position.Y) ||
            (!clamp && !rectangle.Contains(position))) return null;
        return new(Math.Clamp((position.X - rectangle.Left) / rectangle.Width, 0, 1),
            Math.Clamp((position.Y - rectangle.Top) / rectangle.Height, 0, 1));
    }

    private int HitCorner(Point position)
    {
        if (DataContext is not CameraViewModel vm) return -1;
        var rectangle = ImageRectangle();
        if (rectangle.IsEmpty) return -1;
        var nearest = -1;
        var distanceSquared = 20d * 20;
        for (var i = 0; i < vm.SelectedCorners.Count; i++)
        {
            var point = vm.SelectedCorners[i];
            var delta = position - new Point(rectangle.Left + point.X * rectangle.Width, rectangle.Top + point.Y * rectangle.Height);
            if (delta.LengthSquared <= distanceSquared)
            {
                nearest = i;
                distanceSquared = delta.LengthSquared;
            }
        }
        return nearest;
    }

    private void PreviewImage_MouseMove(object sender, MouseEventArgs e)
    {
        HideKeyboardPoint();
        var position = e.GetPosition(PreviewArea);
        if (UpdatePanGesture(position, e.LeftButton, e.MiddleButton))
        {
            e.Handled = true;
            UpdatePreviewCursor();
            return;
        }
        if (_draggedCorner >= 0)
        {
            if (e.LeftButton != MouseButtonState.Pressed) EndDrag();
            else
            {
                MoveDraggedCorner(position);
                e.Handled = true;
            }
        }
        UpdatePreviewCursor();
    }

    private Cursor CursorAt(Point position) =>
        _panning || (PanRequested && _previewZoom > 1) ? Cursors.Hand :
        _draggedCorner >= 0 || HitCorner(position) >= 0 ? Cursors.SizeAll :
        _subscribed?.SelectingCorners == true && PointInImage(position, false) is not null ? Cursors.Cross :
        _previewZoom > 1 && PointInImage(position, false) is not null ? Cursors.Hand : Cursors.Arrow;

    private void MoveDraggedCorner(Point position)
    {
        if (DataContext is CameraViewModel vm && PointInImage(position - _dragOffset, true) is { } point)
            vm.MoveBoardCorner(_draggedCorner, point);
    }

    private void PreviewImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        HideKeyboardPoint();
        if (_draggedCorner < 0) return;
        MoveDraggedCorner(e.GetPosition(PreviewArea));
        EndDrag();
        e.Handled = true;
    }

    private void PreviewImage_LostMouseCapture(object sender, MouseEventArgs e) { EndDrag(); EndPan(); }

    private void HideKeyboardPoint()
    {
        if (!_showKeyboardPoint) return;
        _showKeyboardPoint = false;
        DrawCorners();
    }

    private void EndDrag()
    {
        _draggedCorner = -1;
        if (PreviewArea is null) return;
        if (!_panning && _backgroundPress is null && PreviewArea.IsMouseCaptured) PreviewArea.ReleaseMouseCapture();
        UpdatePreviewCursor();
    }

    private void PreviewImage_KeyDown(object sender, KeyEventArgs e)
    {
        if (HandleViewportKey(e)) return;
        if (DataContext is not CameraViewModel { IsBusy: false, Preview: not null } vm ||
            (!vm.SelectingCorners && vm.SelectedCorners.Count == 0)) return;
        var selected = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0, Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2, Key.D4 or Key.NumPad4 => 3, _ => -1
        };
        if (selected >= 0)
        {
            if (selected < vm.SelectedCorners.Count)
            {
                _activeCorner = selected;
                RevealPoint(vm.SelectedCorners[selected]);
            }
            DrawCorners();
            e.Handled = true;
            return;
        }
        if (_activeCorner >= vm.SelectedCorners.Count) _activeCorner = -1;
        if (!vm.SelectingCorners && _activeCorner < 0) _activeCorner = 0;
        var step = ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? .025 : .0025) / _previewZoom;
        var point = _activeCorner >= 0 ? vm.SelectedCorners[_activeCorner] : _keyboardPoint;
        switch (e.Key)
        {
            case Key.Left: point = point with { X = Math.Max(0, point.X - step) }; break;
            case Key.Right: point = point with { X = Math.Min(1, point.X + step) }; break;
            case Key.Up: point = point with { Y = Math.Max(0, point.Y - step) }; break;
            case Key.Down: point = point with { Y = Math.Min(1, point.Y + step) }; break;
            case Key.Enter:
                if (vm.SelectingCorners)
                {
                    if (_activeCorner >= 0) { _activeCorner = -1; ResetKeyboardPoint(); }
                    else vm.AddBoardCorner(_keyboardPoint);
                    _showKeyboardPoint = vm.SelectingCorners;
                    if (vm.SelectingCorners) RevealPoint(_keyboardPoint);
                }
                DrawCorners();
                e.Handled = true;
                return;
            default: return;
        }
        if (_activeCorner >= 0) vm.MoveBoardCorner(_activeCorner, point);
        else
        {
            _keyboardPoint = point;
            _showKeyboardPoint = true;
        }
        RevealPoint(point);
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
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Brushes.Gold,
                BorderBrush = i == _activeCorner ? Brushes.White : Brushes.Black, BorderThickness = new Thickness(i == _activeCorner ? 4 : 2),
                Child = new TextBlock { Text = (i + 1).ToString(), Foreground = Brushes.Black,
                    FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center }
            };
            Canvas.SetLeft(marker, point.X - 15);
            Canvas.SetTop(marker, point.Y - 15);
            CornerOverlay.Children.Add(marker);
        }
        if (_showKeyboardPoint && vm.SelectingCorners && _activeCorner < 0)
        {
            var x = rectangle.Left + _keyboardPoint.X * rectangle.Width;
            var y = rectangle.Top + _keyboardPoint.Y * rectangle.Height;
            CornerOverlay.Children.Add(new Line { X1 = x - 12, X2 = x + 12, Y1 = y, Y2 = y, Stroke = Brushes.White, StrokeThickness = 3 });
            CornerOverlay.Children.Add(new Line { X1 = x, X2 = x, Y1 = y - 12, Y2 = y + 12, Stroke = Brushes.White, StrokeThickness = 3 });
        }
    }

}
