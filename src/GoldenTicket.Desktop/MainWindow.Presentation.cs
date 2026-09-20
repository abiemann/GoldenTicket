using System.Windows;
using System.Windows.Threading;
using GoldenTicket.Desktop.Services;
using GoldenTicket.Desktop.ViewModels;

namespace GoldenTicket.Desktop;

public partial class MainWindow
{
    private readonly DispatcherTimer _windowPresentationTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(350),
    };
    private bool _applyingDisplayMode;

    private void InitializeWindowPresentation()
    {
        if (_model?.SavedWindowPresentation is { } saved)
        {
            // Sizes are WPF device-independent units. Keep a restored window usable if the
            // available display is smaller than the one used during the previous session.
            Width = Math.Clamp(saved.Width, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
            Height = Math.Clamp(saved.Height, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
            _windowedState = saved.Maximized ? WindowState.Maximized : WindowState.Normal;
            WindowState = _windowedState;
        }

        // The model loaded this choice before we subscribed to PropertyChanged. Apply it
        // before the first show, retaining the underlying windowed size and maximized state.
        ApplyDisplayMode();
        SizeChanged += (_, _) => QueueWindowPresentationSave();
        Loaded += (_, _) => QueueWindowPresentationSave();
        _windowPresentationTimer.Tick += (_, _) =>
        {
            _windowPresentationTimer.Stop();
            SaveWindowPresentation();
        };
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        if (args.Property != WindowStateProperty || _model is null || _applyingDisplayMode) return;
        // Track the chosen state even before a window is shown; WPF's native StateChanged
        // event can lag behind the property. Minimize never replaces its restore state.
        if (_model.DisplayMode != DisplayMode.FullScreen && WindowState != WindowState.Minimized)
            _windowedState = WindowState;
        QueueWindowPresentationSave();
    }

    private void QueueWindowPresentationSave()
    {
        if (_applyingDisplayMode || _cleanupStarted || !IsLoaded) return;
        _windowPresentationTimer.Stop();
        _windowPresentationTimer.Start();
    }

    private void SaveWindowPresentation(bool force = false)
    {
        if (_model is null || _applyingDisplayMode || !force && !IsLoaded) return;
        var bounds = _model.DisplayMode == DisplayMode.FullScreen && _windowedBounds is { } windowed
            ? windowed : ReadWindowedBounds();
        _model.SaveWindowPresentation(new WindowPresentation(bounds.Width, bounds.Height,
            _windowedState == WindowState.Maximized));
    }

    private Rect ReadWindowedBounds()
    {
        if (WindowState != WindowState.Normal && RestoreBounds is { } restore &&
            !restore.IsEmpty && double.IsFinite(restore.Left) && double.IsFinite(restore.Top) &&
            double.IsFinite(restore.Width) && double.IsFinite(restore.Height) &&
            restore.Width > 0 && restore.Height > 0)
            return restore;

        // Before the first HWND exists, startup coordinates and RestoreBounds can be unset.
        var workArea = SystemParameters.WorkArea;
        return new Rect(double.IsFinite(Left) ? Left : workArea.Left + (workArea.Width - Width) / 2,
            double.IsFinite(Top) ? Top : workArea.Top + (workArea.Height - Height) / 2, Width, Height);
    }

    private void ApplyDisplayMode()
    {
        if (_model is null) return;
        _applyingDisplayMode = true;
        try
        {
            if (_model.DisplayMode == DisplayMode.FullScreen)
            {
                if (WindowStyle == WindowStyle.None) return;
                if (WindowState != WindowState.Minimized) _windowedState = WindowState;
                _windowedBounds = ReadWindowedBounds();
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;
            }
            else
            {
                if (WindowStyle != WindowStyle.None) return;
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                if (_windowedBounds is { } bounds)
                {
                    Left = bounds.Left;
                    Top = bounds.Top;
                    Width = bounds.Width;
                    Height = bounds.Height;
                }
                if (_windowedState == WindowState.Maximized) WindowState = WindowState.Maximized;
            }
        }
        finally { _applyingDisplayMode = false; }
    }
}
