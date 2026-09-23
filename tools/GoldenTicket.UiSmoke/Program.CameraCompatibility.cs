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
            var hasCompatibilityError = height < 720;
            CameraPreferenceOption[] expectedOptions = tier switch
            {
                "4k" => [new(CameraCapturePreference.AutoBest, "Auto (2160p)"),
                    new(CameraCapturePreference.SharedCurrent, "Shared · current Windows format")],
                "1080p" => [new(CameraCapturePreference.AutoBest, "Auto (1080p)"),
                    new(CameraCapturePreference.SharedCurrent, "Shared · current Windows format")],
                "720p" => [new(CameraCapturePreference.AutoBest, "Auto (720p)"),
                    new(CameraCapturePreference.SharedCurrent, "Shared · current Windows format")],
                _ => [new(CameraCapturePreference.AutoBest, "Auto"),
                    new(CameraCapturePreference.SharedCurrent, "Shared · current Windows format")]
            };
            if (camera.HasNative4K != supports4K ||
                !camera.Preferences.SequenceEqual(expectedOptions) ||
                camera.HasCameraCompatibilityMessage != hasCompatibilityError)
                throw new InvalidOperationException($"The {tier} fixture must expose only supported capture options and show an error only below 720p.");
            await RenderSizes($"camera-quality-{tier}", () => new CameraView { DataContext = camera }, view =>
            {
                var warning = (TextBlock)view.FindName("CameraCompatibilityMessage");
                var quality = Descendants<ComboBox>(view).Single(combo =>
                    AutomationProperties.GetName(combo) == "Camera resolution preference");
                if (IsElementShown(warning) != hasCompatibilityError ||
                    warning.Text != camera.CameraCompatibilityMessage ||
                    warning.TextWrapping != TextWrapping.Wrap ||
                    AutomationProperties.GetLiveSetting(warning) != AutomationLiveSetting.Polite ||
                    !ReferenceEquals(quality.ItemsSource, camera.Preferences) ||
                    !ReferenceEquals(quality.SelectedItem, camera.SelectedPreference))
                    throw new InvalidOperationException($"The {tier} camera compatibility message must appear only for an incompatible webcam.");
                if (!Descendants<CheckBox>(view).Any(box =>
                    box.Content as string == "Enhanced preview" && IsElementShown(box)))
                    throw new InvalidOperationException("The technical enhancement comparison must not claim native 4K capture.");
            }, [(1280, 800)]);
        }
        await using var fullHdAndHd = new CameraViewModel(getCameraFormats: (_, _) =>
            Task.FromResult<IReadOnlyList<CameraFormat>>(
                [new(1920, 1080, 30, "MJPG"), new(1280, 720, 30, "MJPG")]));
        fullHdAndHd.SelectedDevice = new CameraDevice("synthetic-full-hd", "Synthetic 1080p and 720p webcam");
        await fullHdAndHd.RefreshSelectedCameraCapabilitiesAsync();
        CameraPreferenceOption[] fullHdAndHdExpected =
        [
            new(CameraCapturePreference.AutoBest, "Auto (1080p)"),
            new(CameraCapturePreference.Native720p, "720p"),
            new(CameraCapturePreference.SharedCurrent, "Shared · current Windows format")
        ];
        if (!fullHdAndHd.Preferences.SequenceEqual(fullHdAndHdExpected))
            throw new InvalidOperationException("A 1080p webcam with native 720p must offer only the lower explicit resolution.");
        await RenderSizes("camera-quality-1080p-and-720p", () => new CameraView { DataContext = fullHdAndHd }, view =>
        {
            var quality = Descendants<ComboBox>(view).Single(combo =>
                AutomationProperties.GetName(combo) == "Camera resolution preference");
            if (!ReferenceEquals(quality.ItemsSource, fullHdAndHd.Preferences) ||
                !ReferenceEquals(quality.SelectedItem, fullHdAndHd.SelectedPreference))
                throw new InvalidOperationException("The camera quality picker must show the 1080p webcam's lower native mode.");
        }, [(1280, 800)]);
        await using var allModes = new CameraViewModel(getCameraFormats: (_, _) =>
            Task.FromResult<IReadOnlyList<CameraFormat>>(
                [new(3840, 2160, 15, "MJPG"), new(1920, 1080, 30, "MJPG"), new(1280, 720, 30, "MJPG")]));
        allModes.SelectedDevice = new CameraDevice("synthetic-all", "Synthetic all-mode webcam");
        await allModes.RefreshSelectedCameraCapabilitiesAsync();
        CameraPreferenceOption[] allExpected =
        [
            new(CameraCapturePreference.AutoBest, "Auto (2160p)"),
            new(CameraCapturePreference.Balanced1080p, "1080p"),
            new(CameraCapturePreference.Native720p, "720p"),
            new(CameraCapturePreference.SharedCurrent, "Shared · current Windows format")
        ];
        if (!allModes.Preferences.SequenceEqual(allExpected))
            throw new InvalidOperationException("A webcam advertising all three native modes must offer Auto, 1080p, and 720p in that order.");
        await RenderSizes("camera-quality-all-modes", () => new CameraView { DataContext = allModes }, view =>
        {
            var quality = Descendants<ComboBox>(view).Single(combo =>
                AutomationProperties.GetName(combo) == "Camera resolution preference");
            if (!ReferenceEquals(quality.ItemsSource, allModes.Preferences) ||
                !ReferenceEquals(quality.SelectedItem, allModes.SelectedPreference))
                throw new InvalidOperationException("The camera quality picker must show the discovered native modes.");
        }, [(1280, 800)]);
        Console.WriteLine("Camera quality presentation: Auto tier labels, native option filtering, no 720p warning, and sub-720p incompatibility passed without hardware access.");
    }
}
