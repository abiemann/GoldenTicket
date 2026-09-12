using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoldenTicket.CompanionHost;
using GoldenTicket.CompanionHost.Connect;
using GoldenTicket.CompanionHost.Networking;

namespace GoldenTicket.Desktop.ViewModels;

/// <summary>Consent-driven local hosting, QR setup and physical laptop approval of one controller.</summary>
public sealed partial class ConnectionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly CompanionServer _server;
    private readonly Dispatcher? _dispatcher;
    private readonly DispatcherTimer? _timer;
    private string? _qrAddress;
    private bool _disposed;

    public ConnectionViewModel(ICompanionGameBridge bridge)
    {
        _server = new CompanionServer(bridge);
        _server.StatusChanged += ServerStatusChanged;
        _dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (_dispatcher is not null)
        {
            _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
                (_, _) => UpdateStatus(), _dispatcher);
            _timer.Start();
        }
    }

    public ObservableCollection<LanInterface> Interfaces { get; } = [];
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private LanInterface? _selectedInterface;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isRunning;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isBusy;
    [ObservableProperty] private bool _shareCertificate = true;
    [ObservableProperty] private string _status = "Phone hosting is off. Choose a trusted Private network to begin.";
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _bootstrapAddress = "";
    [ObservableProperty] private string _ipAddress = "";
    [ObservableProperty] private string _pairingCode = "";
    [ObservableProperty] private string _fingerprint = "";
    [ObservableProperty] private string _publicCertificatePath = "";
    [ObservableProperty] private string _pendingIdentity = "No phone is waiting for approval.";
    [ObservableProperty] private string _controller = "No phone connected.";
    [ObservableProperty] private string _firewallCommand = "Start the host to see its exact firewall scope.";
    [ObservableProperty] private BitmapSource? _connectionQr;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ApproveCommand))]
    private bool _hasPendingApproval;

    public void RefreshInterfaces()
    {
        if (IsRunning) return;
        try
        {
            var previous = SelectedInterface?.Address;
            Interfaces.Clear();
            foreach (var item in CompanionServer.GetAvailableInterfaces()) Interfaces.Add(item);
            SelectedInterface = Interfaces.FirstOrDefault(item => item.Address.Equals(previous)) ?? Interfaces.FirstOrDefault();
            Problem = Interfaces.Count == 0
                ? "Windows has no eligible Private LAN connection. On a trusted home network, open network settings and set that connection to Private, then refresh. A public hotspot must stay Public."
                : null;
        }
        catch (Exception)
        {
            Problem = "The Windows network profile could not be read. Check Network & internet settings and try again.";
        }
    }

    [RelayCommand] private void Refresh() => RefreshInterfaces();
    private bool CanStart() => !_disposed && !IsRunning && !IsBusy && SelectedInterface is not null;
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!CanStart() || SelectedInterface is not { } selected) return;
        IsBusy = true;
        Problem = null;
        try
        {
            await _server.StartAsync(new CompanionHostOptions(selected.Address, EnableBootstrap: ShareCertificate));
            FirewallCommand = BuildFirewallCommand(selected, Environment.ProcessPath ?? "GoldenTicket.exe");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            Problem = "Phone hosting could not start. Check the Private network profile, port availability, and local certificate files. " + ex.Message;
        }
        catch (Exception)
        {
            Problem = "Phone hosting could not start. Check the selected network and restart the application if needed.";
        }
        finally { IsBusy = false; UpdateStatus(); }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await _server.StopAsync(); }
        catch (Exception) { Problem = "The phone host could not stop cleanly. Close the application before starting it again."; }
        finally { IsBusy = false; UpdateStatus(); }
    }

    [RelayCommand] private void NewCode() { if (IsRunning) _server.NewPairingCode(); }
    private bool CanApprove() => HasPendingApproval;
    [RelayCommand(CanExecute = nameof(CanApprove))]
    private void Approve()
    {
        if (!_server.ApprovePendingController()) Problem = "This request expired. Ask the phone to pair again.";
        UpdateStatus();
    }
    [RelayCommand] private void Revoke() { _server.RevokeController(); UpdateStatus(); }
    [RelayCommand] private async Task CloseCertificateSharingAsync()
    {
        try { await _server.CloseBootstrapAsync(); }
        catch (Exception) { Problem = "Certificate sharing could not close. Stop phone hosting and try again."; }
        UpdateStatus();
    }
    [RelayCommand] private void OpenNetworkSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true }); }
        catch (Exception) { Problem = "Windows Settings could not open. Open Settings → Network & internet manually."; }
    }
    [RelayCommand] private void CopyAddress() => CopyText(Address);
    [RelayCommand] private void CopyFirewallCommand() => CopyText(FirewallCommand);
    [RelayCommand] private void CopyCertificatePath() => CopyText(PublicCertificatePath);
    private void CopyText(string value)
    {
        try { if (!string.IsNullOrWhiteSpace(value)) Clipboard.SetText(value); }
        catch (Exception) { Problem = "Clipboard access is unavailable. Select and copy the displayed text instead."; }
    }
    public void InvalidatePrivateGrants() => _server.InvalidatePrivateGrants();

    private void ServerStatusChanged(object? sender, EventArgs args)
    {
        if (_disposed) return;
        if (_dispatcher is not null && !_dispatcher.CheckAccess())
        {
            if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(UpdateStatus);
        }
        else UpdateStatus();
    }
    private void UpdateStatus()
    {
        if (_disposed) return;
        var state = _server.Status;
        IsRunning = state.Running;
        Status = state.Message;
        Address = state.Address ?? "";
        BootstrapAddress = state.BootstrapAddress ?? "";
        IpAddress = state.IpAddress ?? "";
        PairingCode = state.PairingCode ?? "";
        Fingerprint = state.CertificateFingerprint ?? "";
        PublicCertificatePath = state.PublicCertificatePath ?? "";
        HasPendingApproval = state.PendingApproval is not null;
        PendingIdentity = state.PendingApproval is { } pending
            ? $"{pending.DeviceLabel} · identity {pending.Identity}. Compare this with the phone before approving."
            : "No phone is waiting for approval.";
        Controller = state.ControllerLabel is { } label ? $"Controller: {label}" : "No phone connected.";
        var landing = state.BootstrapAddress ?? state.Address;
        if (_qrAddress != landing)
        {
            _qrAddress = landing;
            ConnectionQr = landing is null ? null : DrawQr(landing);
        }
    }

    internal static BitmapSource DrawQr(string address)
    {
        var code = QrCode.Encode(address);
        var size = code.Size + 8;
        var bytes = new byte[size * size * 4];
        Array.Fill(bytes, (byte)255);
        for (var y = 0; y < code.Size; y++)
        for (var x = 0; x < code.Size; x++)
        {
            if (!code[x, y]) continue;
            var offset = ((y + 4) * size + x + 4) * 4;
            bytes[offset] = bytes[offset + 1] = bytes[offset + 2] = 0;
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, bytes, size * 4);
        bitmap.Freeze();
        return bitmap;
    }

    internal static string BuildFirewallCommand(LanInterface selected, string program)
    {
        static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
        return "New-NetFirewallRule -DisplayName 'GoldenTicket private LAN' -Direction Inbound -Action Allow " +
            "-Profile Private -Protocol TCP -LocalPort 8080,8443 -LocalAddress " + selected.Address +
            " -RemoteAddress LocalSubnet -InterfaceAlias " + Quote(selected.Name) + " -Program " + Quote(program);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Stop();
        _server.StatusChanged -= ServerStatusChanged;
        await _server.DisposeAsync();
    }
}
