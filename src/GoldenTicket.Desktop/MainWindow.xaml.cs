using System.Windows;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using GoldenTicket.Desktop.ViewModels;

namespace GoldenTicket.Desktop;

public partial class MainWindow : Window
{
    private MainViewModel? _model;
    private readonly DispatcherTimer _privacyTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private long _lastInteraction = Environment.TickCount64;
    private bool _closingAfterCleanup;
    private bool _cleanupStarted;
    private bool _exitPromptOpen;
    private bool _technicalVisible;
    private bool _layerTransition;
    private Rect? _windowedBounds;
    private WindowState _windowedState;
    private readonly TranslateTransform _gameTranslation = new();

    public MainWindow() : this(() => new MainViewModel()) { }

    public MainWindow(MainViewModel model, Func<ExitPrompt, bool>? confirmExit = null)
        : this(() => model ?? throw new ArgumentNullException(nameof(model)), confirmExit) { }

    private MainWindow(Func<MainViewModel> createModel, Func<ExitPrompt, bool>? confirmExit = null)
    {
        InitializeComponent();
        GameLayer.RenderTransform = _gameTranslation;

        try
        {
            _model = createModel();
            DataContext = _model;
            _model.PropertyChanged += OnDisplayModeChanged;
            _model.SetGameLayerVisible(true);
            Loaded += async (_, _) =>
            {
                GameLayer.FocusCurrentChoice();
                await Task.WhenAll(_model.LoadSavedSessionsAsync(), _model.Camera.InitializeProcessingAsync());
            };
        }
        catch (Exception exception)
        {
            // DESIGN 21.1: explain the failure instead of starting a match on unknown data.
            MessageBox.Show(
                "GoldenTicket could not load its board data package.\n\n" +
                $"{exception.Message}\n\n" +
                "The reviewed data file must ship with the application at data/classic-us/.",
                "GoldenTicket", MessageBoxButton.OK, MessageBoxImage.Error);

            Loaded += (_, _) => Close();
            return;
        }

        // DESIGN 4.7: preserve the solo opening destination choice on focus loss; cover other
        // private views on deactivation. Plain Escape covers only later private views.
        Deactivated += (_, _) => _model.SetWindowActive(false);
        Activated += (_, _) => _model.SetWindowActive(true);
        PreviewKeyDown += OnPreviewKeyDown;
        Root.SizeChanged += (_, _) =>
        {
            if (_technicalVisible && !_layerTransition) _gameTranslation.X = Root.ActualWidth;
        };
        PreviewMouseDown += (_, _) => _lastInteraction = Environment.TickCount64;
        PreviewMouseWheel += (_, _) => _lastInteraction = Environment.TickCount64;
        PreviewTouchDown += (_, _) => _lastInteraction = Environment.TickCount64;
        _model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.PrivateSeat) && _model.PrivateSeat is not null)
                _lastInteraction = Environment.TickCount64;
        };
        _privacyTimer.Tick += (_, _) =>
        {
            // The phone owns its own reveal timeout. An idle, already-covered laptop must not
            // revoke an active phone hand every time this timer ticks.
            if (_model.PrivateSeat is not null && !_model.ShowSoloOpeningTicketsOnBoard &&
                Environment.TickCount64 - _lastInteraction >= 60_000)
                _model.HidePrivateSeat();
        };
        _privacyTimer.Start();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Closing += async (_, args) =>
        {
            if (_closingAfterCleanup || _model is null) return;
            args.Cancel = true;
            if (_cleanupStarted || _exitPromptOpen) return;
            _exitPromptOpen = true;
            try
            {
                var prompt = _model.BeginExitRequest();
                if (prompt is not null && (!(confirmExit ?? ConfirmExit)(prompt) || !prompt.CanExit)) return;
                _cleanupStarted = true;
            }
            finally
            {
                _exitPromptOpen = false;
                if (!_cleanupStarted) _model.CancelExitRequest();
            }
            _privacyTimer.Stop();
            IsEnabled = false;
            try { await _model.DisposeToolsAsync(); }
            catch (Exception exception) { DiagnosticLog.Write(exception, App.DiagnosticsDirectory, DateTimeOffset.UtcNow); }
            finally
            {
                // Idle tools can dispose synchronously. WPF must finish the first canceled
                // Closing event before Close is called again, even when no await yielded.
                if (!Dispatcher.HasShutdownStarted)
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        _closingAfterCleanup = true;
                        Close();
                    }));
            }
        };
        Closed += (_, _) =>
        {
            _privacyTimer.Stop();
            if (_model is not null) _model.PropertyChanged -= OnDisplayModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        };
    }

    private void OnDisplayModeChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainViewModel.DisplayMode) || _model is null) return;

        if (_model.DisplayMode == DisplayMode.FullScreen)
        {
            if (WindowStyle == WindowStyle.None) return;
            _windowedState = WindowState;
            _windowedBounds = WindowState == WindowState.Maximized
                ? RestoreBounds : new Rect(Left, Top, Width, Height);
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

    private bool ConfirmExit(ExitPrompt prompt)
    {
        if (!prompt.CanExit)
        {
            MessageBox.Show(this, prompt.Message, "Please wait", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return MessageBox.Show(this, prompt.Message, "Exit GoldenTicket?", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or
            SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            UpdateSystemPrivacy(canInteract: false);
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or
                 SessionSwitchReason.RemoteConnect)
            UpdateSystemPrivacy(canInteract: true);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) UpdateSystemPrivacy(canInteract: false);
        else if (e.Mode == PowerModes.Resume) UpdateSystemPrivacy(canInteract: true);
    }

    private void UpdateSystemPrivacy(bool canInteract)
    {
        if (_cleanupStarted || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(async () =>
        {
            if (_cleanupStarted) return;
            if (_model?.ShowSoloOpeningTicketsOnBoard != true) _model?.HidePrivateSeat();
            _model?.SetWindowActive(canInteract && IsActive);
            if (_model is not null) await _model.SetSystemAvailableAsync(canInteract);
        }));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        _lastInteraction = Environment.TickCount64;
        if (e.Key != Key.Escape || _model is null) return;

        if (_model.IsGameExitSaving)
        {
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            if (!e.IsRepeat)
            {
                _model.CloseGameExitMenu();
                RevealTechnicalLayer();
            }
            e.Handled = true;
            return;
        }

        if (_model.IsGameExitMenuOpen)
        {
            if (!e.IsRepeat)
            {
                _model.CloseGameExitMenu();
                GameLayer.FocusCurrentChoice();
            }
            e.Handled = true;
            return;
        }

        if (_model.Game.IsPlaying && !_technicalVisible && !_layerTransition && GameLayer.IsEnabled)
        {
            if (!e.IsRepeat)
            {
                _model.OpenGameExitMenu();
                if (_model.IsGameExitMenuOpen) SaveGameMenuButton.Focus();
            }
            e.Handled = true;
            return;
        }

        if (_model.ShowSoloOpeningTicketsOnBoard)
        {
            e.Handled = true;
            return;
        }

        if (_model.IsPrivateVisible)
        {
            _model.HidePrivateSeatCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void CloseGameExitMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_model?.IsGameExitSaving == true) return;
        _model?.CloseGameExitMenu();
        GameLayer.FocusCurrentChoice();
    }

    private void ReturnToGame_Click(object sender, RoutedEventArgs e) => ReturnToGameLayer();

    private void RevealTechnicalLayer()
    {
        if (_technicalVisible || _layerTransition || _exitPromptOpen || _cleanupStarted) return;
        _model?.HidePrivateSeat();
        _model?.SetGameLayerVisible(false);
        _technicalVisible = true;
        _layerTransition = true;
        GameLayer.IsEnabled = false;
        TechnicalLayer.IsEnabled = false;
        SlideGameLayer(Root.ActualWidth, () =>
        {
            GameLayer.Visibility = Visibility.Collapsed;
            TechnicalLayer.IsEnabled = true;
            _layerTransition = false;
            ReturnToGameButton.Focus();
        });
    }

    private void ReturnToGameLayer()
    {
        if (!_technicalVisible || _layerTransition || _exitPromptOpen || _cleanupStarted) return;
        _model?.HidePrivateSeat();
        _technicalVisible = false;
        _layerTransition = true;
        TechnicalLayer.IsEnabled = false;
        GameLayer.Visibility = Visibility.Visible;
        GameLayer.IsEnabled = false;
        _gameTranslation.X = Root.ActualWidth;
        SlideGameLayer(0, () =>
        {
            GameLayer.IsEnabled = true;
            _model?.SetGameLayerVisible(true);
            _layerTransition = false;
            GameLayer.FocusCurrentChoice();
        });
    }

    private void SlideGameLayer(double destination, Action completed)
    {
        if (!SystemParameters.ClientAreaAnimation || Root.ActualWidth <= 0)
        {
            _gameTranslation.X = destination;
            completed();
            return;
        }

        var animation = new DoubleAnimation(_gameTranslation.X, destination,
            new Duration(TimeSpan.FromMilliseconds(260)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        animation.Completed += (_, _) =>
        {
            _gameTranslation.BeginAnimation(TranslateTransform.XProperty, null);
            _gameTranslation.X = destination == 0 ? 0 : Root.ActualWidth;
            completed();
        };
        _gameTranslation.BeginAnimation(TranslateTransform.XProperty, animation);
    }
}
