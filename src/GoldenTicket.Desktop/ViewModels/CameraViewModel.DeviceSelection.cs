using System.IO;
using GoldenTicket.Desktop.Services;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class CameraViewModel
{
    private readonly string? _cameraSettingsPath;
    private CameraDevice? _preferredCamera;
    private bool _refreshingDeviceList;

    private void RememberCamera(CameraDevice device)
    {
        if (_preferredCamera == device) return;
        _preferredCamera = device;
        try { CameraDevicePreferences.Save(_cameraSettingsPath, device); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = "The webcam choice is active but could not be saved for next launch: " + ex.Message;
        }
    }

    private CameraDevice? FindPreferredCamera()
    {
        if (_preferredCamera is not { } preferred) return Devices.FirstOrDefault();
        var exact = Devices.FirstOrDefault(device => device.Id == preferred.Id);
        if (exact is not null) return exact;
        // USB mode changes can assign a new Windows ID. Only use the remembered name
        // when it identifies a single connected camera; never choose an arbitrary twin.
        var named = Devices.Where(device => device.Name == preferred.Name).Take(2).ToArray();
        return named.Length == 1 ? named[0] : null;
    }

    private string GameTableCameraConnectionMessage => _preferredCamera is { } preferred
        ? Devices.Count(device => device.Name == preferred.Name) > 1 &&
            !Devices.Any(device => device.Id == preferred.Id)
            ? $"More than one webcam is named {preferred.Name}. Select your overhead webcam in Settings."
            : $"Waiting for {preferred.Name} to reconnect. The game will check the board when it returns."
        : "Please connect a webcam. The game will find it and check the board automatically.";
}
