using System.IO;
using System.Windows;
using System.Windows.Threading;
using GoldenTicket.Desktop.ViewModels;

namespace GoldenTicket.Desktop;

public partial class App : System.Windows.Application
{
    private bool _reported;

    /// <summary>
    /// DESIGN 19.1 / 21.2: a bounded, user-clearable diagnostics location outside the installation
    /// directory. It records timings and errors, never game objects or card text.
    /// </summary>
    public static string DiagnosticsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GoldenTicket", "diagnostics");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
    }

    /// <summary>
    /// DESIGN 21.1: explain the failure and leave the saved match alone. The dialog is shown at most
    /// once: a fault raised during layout would otherwise repeat on every frame, and showing a modal
    /// dialog from inside that loop pumps messages and re-enters the same fault.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        var alreadyReported = _reported;
        _reported = true;

        DiagnosticLog.Write(e.Exception, DiagnosticsDirectory, DateTimeOffset.UtcNow);

        if (alreadyReported) return;

        if (MainWindow?.DataContext is MainViewModel model)
        {
            model.SetWindowActive(false);
            model.PauseAfterUnhandledFault();
        }
        if (MainWindow is { } window) window.IsEnabled = false;

        MessageBox.Show(
            "GoldenTicket hit a problem it could not handle.\n\n" +
            "The action in progress may or may not have been saved. Reopen the application to verify " +
            "the last saved state. Diagnostic error codes are stored, when storage permits, at:\n" +
            DiagnosticsDirectory + "\n\n" +
            "Close and reopen the application to continue the match.",
            "GoldenTicket", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }
}
