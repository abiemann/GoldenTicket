using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.Views;

public partial class CameraView
{
    // Presentation coordinates only. Camera frames, registration and crop revisions never
    // depend on these values. Overlays and pointer input use the same ImageRectangle.
    private double _previewZoom = 1;
    private Vector _previewPan;
    private bool _panning;
    private bool _spacePanning;
    private MouseButton _panButton;
    private Point _lastPanPosition;
    private bool PanRequested => _spacePanning || PanPreviewToggle?.IsChecked == true;

    private void PreviewImage_TargetUpdated(object sender, DataTransferEventArgs e)
    {
        if (e.Property == Image.SourceProperty) UpdateViewport();
    }

    private void UpdateViewport()
    {
        if (PreviewImage is null || PreviewArea is null || ZoomText is null) return;
        var fit = FitImageRectangle();
        var available = !fit.IsEmpty;
        if (!available)
        {
            _previewZoom = 1;
            _previewPan = default;
            EndPan();
            EndDrag();
        }
        else ClampPan(fit);

        // Uniform Image can arrange itself smaller than its Grid in a letterboxed
        // viewport. Scale around the Image's own center (RenderTransformOrigin), not
        // a viewport-sized local origin, or image pixels drift away from the handles.
        var transform = new MatrixTransform(_previewZoom, 0, 0, _previewZoom, _previewPan.X, _previewPan.Y);
        transform.Freeze();
        PreviewImage.RenderTransform = transform;
        ZoomText.Text = (_previewZoom * 100).ToString("0", CultureInfo.CurrentCulture) + "%";
        ZoomInButton.IsEnabled = available && _previewZoom < 8;
        ZoomOutButton.IsEnabled = available && _previewZoom > 1;
        FitPreviewButton.IsEnabled = available;
        PanPreviewToggle.IsEnabled = available && _previewZoom > 1;
        if (!PanPreviewToggle.IsEnabled) PanPreviewToggle.IsChecked = false;
        DrawCorners();
        UpdatePreviewCursor();
    }

    private void ClampPan(Rect fit)
    {
        if (_previewZoom <= 1) { _previewPan = default; return; }
        // Allow a little space beyond the image edge so the complete corner handle can
        // be reached, but prevent dragging the image out of the viewport.
        var limitX = Math.Max(0, (fit.Width * _previewZoom - PreviewArea.ActualWidth) / 2 + 20);
        var limitY = Math.Max(0, (fit.Height * _previewZoom - PreviewArea.ActualHeight) / 2 + 20);
        _previewPan = new Vector(Math.Clamp(_previewPan.X, -limitX, limitX),
            Math.Clamp(_previewPan.Y, -limitY, limitY));
    }

    private void SetZoom(double zoom, Point anchor)
    {
        var rectangle = ImageRectangle();
        if (rectangle.IsEmpty || !double.IsFinite(zoom) || !double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y)) return;
        EndPan();
        EndDrag();
        anchor = new Point(Math.Clamp(anchor.X, 0, PreviewArea.ActualWidth),
            Math.Clamp(anchor.Y, 0, PreviewArea.ActualHeight));
        var x = (anchor.X - rectangle.Left) / rectangle.Width;
        var y = (anchor.Y - rectangle.Top) / rectangle.Height;
        _previewZoom = Math.Clamp(zoom, 1, 8);
        var fit = FitImageRectangle();
        _previewPan = new Vector(anchor.X - PreviewArea.ActualWidth / 2 - (x - .5) * fit.Width * _previewZoom,
            anchor.Y - PreviewArea.ActualHeight / 2 - (y - .5) * fit.Height * _previewZoom);
        UpdateViewport();
    }

    private Point ZoomAnchor()
    {
        var center = new Point(PreviewArea.ActualWidth / 2, PreviewArea.ActualHeight / 2);
        if (_subscribed is null || _activeCorner < 0 || _activeCorner >= _subscribed.SelectedCorners.Count) return center;
        var rectangle = ImageRectangle();
        if (rectangle.IsEmpty) return center;
        var corner = _subscribed.SelectedCorners[_activeCorner];
        var point = new Point(rectangle.Left + corner.X * rectangle.Width, rectangle.Top + corner.Y * rectangle.Height);
        return new Rect(PreviewArea.RenderSize).Contains(point) ? point : center;
    }

    private void PanBy(Vector delta)
    {
        if (_previewZoom <= 1 || !double.IsFinite(delta.X) || !double.IsFinite(delta.Y)) return;
        _previewPan += delta;
        UpdateViewport();
    }

    private void ResetZoom()
    {
        EndPan();
        EndDrag();
        _previewZoom = 1;
        _previewPan = default;
        UpdateViewport();
    }

    private void RevealPoint(NormalizedPoint point)
    {
        if (_previewZoom <= 1) return;
        var rectangle = ImageRectangle();
        if (rectangle.IsEmpty) return;
        var position = new Point(rectangle.Left + point.X * rectangle.Width, rectangle.Top + point.Y * rectangle.Height);
        const double margin = 30;
        if (position.X < margin || position.Y < margin ||
            position.X > PreviewArea.ActualWidth - margin || position.Y > PreviewArea.ActualHeight - margin)
            PanBy(new Vector(PreviewArea.ActualWidth / 2 - position.X, PreviewArea.ActualHeight / 2 - position.Y));
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(_previewZoom * 1.25, ZoomAnchor());
        PreviewImage.Focus();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(_previewZoom / 1.25, ZoomAnchor());
        PreviewImage.Focus();
    }

    private void FitPreview_Click(object sender, RoutedEventArgs e)
    {
        ResetZoom();
        PreviewImage.Focus();
    }

    private void PreviewArea_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 ||
            PointInImage(e.GetPosition(PreviewArea), false) is null) return;
        SetZoom(_previewZoom * Math.Pow(1.25, Math.Clamp(e.Delta / 120d, -8, 8)), e.GetPosition(PreviewArea));
        e.Handled = true;
    }

    private void PreviewArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_previewZoom <= 1 || ImageRectangle().IsEmpty ||
            !(e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && PanRequested))) return;
        EndDrag();
        PreviewImage.Focus();
        HideKeyboardPoint();
        if (PreviewArea.CaptureMouse())
        {
            _panning = true;
            _panButton = e.ChangedButton;
            _lastPanPosition = e.GetPosition(PreviewArea);
        }
        UpdatePreviewCursor();
        // A pan attempt must never fall through into placing or moving a corner.
        e.Handled = true;
    }

    private void PreviewArea_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning || e.ChangedButton != _panButton) return;
        PanBy(e.GetPosition(PreviewArea) - _lastPanPosition);
        EndPan();
        e.Handled = true;
    }

    private void EndPan()
    {
        _panning = false;
        if (PreviewArea is null) return;
        if (_draggedCorner < 0 && PreviewArea.IsMouseCaptured) PreviewArea.ReleaseMouseCapture();
        UpdatePreviewCursor();
    }

    private void PanPreview_Changed(object sender, RoutedEventArgs e)
    {
        EndPan();
        EndDrag();
        if (PanPreviewToggle.IsChecked == true) PreviewImage?.Focus();
        UpdatePreviewCursor();
    }

    private bool HandleViewportKey(KeyEventArgs e)
    {
        if (PreviewImage.Source is null) return false;
        switch (e.Key)
        {
            case Key.Add: case Key.OemPlus: SetZoom(_previewZoom * 1.25, ZoomAnchor()); break;
            case Key.Subtract: case Key.OemMinus: SetZoom(_previewZoom / 1.25, ZoomAnchor()); break;
            case Key.D0: case Key.NumPad0: ResetZoom(); break;
            case Key.Space:
                if (_previewZoom > 1) { EndDrag(); _spacePanning = true; UpdatePreviewCursor(); }
                break;
            case Key.Escape:
                if (!_panning && !PanRequested && _draggedCorner < 0) return false;
                _spacePanning = false;
                EndPan();
                EndDrag();
                PanPreviewToggle.IsChecked = false;
                break;
            default: return false;
        }
        e.Handled = true;
        return true;
    }

    private void PreviewImage_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        _spacePanning = false;
        if (_panning && _panButton == MouseButton.Left && PanPreviewToggle.IsChecked != true) EndPan();
        UpdatePreviewCursor();
        e.Handled = true;
    }

    private void PreviewImage_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _spacePanning = false;
        EndPan();
        EndDrag();
    }

    private void UpdatePreviewCursor()
    {
        if (PreviewImage is null || PreviewArea is null) return;
        PreviewArea.Cursor = PreviewImage.Cursor = CursorAt(Mouse.GetPosition(PreviewArea));
    }
}
