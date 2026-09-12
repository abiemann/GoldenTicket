using System.Windows;
using System.Windows.Input;
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

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            _model = new MainViewModel();
            DataContext = _model;
            Loaded += async (_, _) => await _model.LoadSavedSessionsAsync();
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

        // DESIGN 4.7: hide the private view on deactivation, and give Escape the same effect as Hide.
        Deactivated += (_, _) => _model.SetWindowActive(false);
        Activated += (_, _) => _model.SetWindowActive(true);
        PreviewKeyDown += OnPreviewKeyDown;
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
            if (_model.PrivateSeat is not null && Environment.TickCount64 - _lastInteraction >= 60_000)
                _model.HidePrivateSeat();
        };
        _privacyTimer.Start();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Closing += async (_, args) =>
        {
            if (_closingAfterCleanup || _model is null) return;
            args.Cancel = true;
            if (_cleanupStarted) return;
            _cleanupStarted = true;
            IsEnabled = false;
            try { await _model.DisposeToolsAsync(); }
            catch (Exception exception) { DiagnosticLog.Write(exception, App.DiagnosticsDirectory, DateTimeOffset.UtcNow); }
            finally { _closingAfterCleanup = true; Close(); }
        };
        Closed += (_, _) =>
        {
            _privacyTimer.Stop();
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        };
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
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(async () =>
        {
            _model?.HidePrivateSeat();
            _model?.SetWindowActive(canInteract && IsActive);
            if (_model is not null) await _model.SetSystemAvailableAsync(canInteract);
        }));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        _lastInteraction = Environment.TickCount64;
        if (e.Key != Key.Escape || _model is null) return;

        _model.HidePrivateSeatCommand.Execute(null);
        e.Handled = true;
    }
}
