using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Desktop.Views;
using GoldenTicket.Vision;

internal static partial class Program
{
    private static async Task VerifyCameraCompatibilityPresentation()
    {
        foreach (var (width, height, tier) in new[]
        {
            (3840, 2160, "4k"), (1920, 1080, "1080p"),
            (1280, 720, "720p"), (640, 480, "incompatible")
        })
        {
            // This fixture reports native source modes without opening a physical camera.
            await using var camera = new CameraViewModel(getCameraFormats: (_, _) =>
                Task.FromResult<IReadOnlyList<CameraFormat>>([new(width, height, 30, "MJPG")]));
            camera.SelectedDevice = new CameraDevice($"synthetic-{tier}", $"Synthetic {tier} webcam");
            await camera.RefreshSelectedCameraCapabilitiesAsync();
            var supports4K = width == 3840;
            var hasWarning = height < 1080;
            if (camera.HasNative4K != supports4K ||
                camera.Preferences.Any(option => option.Value == CameraCapturePreference.HighDetail2160p) != supports4K ||
                camera.HasCameraCompatibilityMessage != hasWarning)
                throw new InvalidOperationException($"The {tier} fixture must expose only supported capture options and its quality warning.");
            await RenderSizes($"camera-quality-{tier}", () => new CameraView { DataContext = camera }, view =>
            {
                var warning = (TextBlock)view.FindName("CameraCompatibilityMessage");
                if (IsElementShown(warning) != hasWarning ||
                    warning.Text != camera.CameraCompatibilityMessage ||
                    warning.TextWrapping != TextWrapping.Wrap ||
                    AutomationProperties.GetLiveSetting(warning) != AutomationLiveSetting.Polite)
                    throw new InvalidOperationException($"The {tier} camera compatibility message must reflect the selected webcam.");
                if (!Descendants<CheckBox>(view).Any(box =>
                    box.Content as string == "Enhanced preview" && IsElementShown(box)))
                    throw new InvalidOperationException("The technical enhancement comparison must not claim native 4K capture.");
            }, [(1280, 800)]);
        }
        Console.WriteLine("Camera quality presentation: native 4K conditional, 1080p clear, 720p warning, and sub-720p incompatibility passed without hardware access.");
    }
}
