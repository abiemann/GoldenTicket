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
    private bool _exitPromptOpen;

    public MainWindow() : this(() => new MainViewModel()) { }

    public MainWindow(MainViewModel model, Func<ExitPrompt, bool>? confirmExit = null)
        : this(() => model ?? throw new ArgumentNullException(nameof(model)), confirmExit) { }

    private MainWindow(Func<MainViewModel> createModel, Func<ExitPrompt, bool>? confirmExit = null)
    {
        InitializeComponent();

        try
        {
            _model = createModel();
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
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        };
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
