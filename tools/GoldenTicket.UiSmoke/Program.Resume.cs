using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Manifest;

internal static partial class Program
{
    private static async Task VerifyResumeTurnAnnouncement()
    {
        BindingLog.Context = "resume-turn-announcement";
        var model = new MainViewModel(ManifestLoader.LoadClassicUs(), new InMemorySessionStore());
        var window = new GoldenTicket.Desktop.MainWindow(model, _ => true);
        var root = (Grid)window.FindName("Root");
        var content = (Grid)window.FindName("GameContent");
        var overlay = (Grid)window.FindName("ResumeTurnAnnouncementOverlay");
        var ok = (Button)window.FindName("ResumeTurnOkButton");
        var checks = new List<object>();
        try
        {
            model.Setup.ManualVerificationAccepted = true;
            await model.StartMatchAsync();
            model.HidePrivateSeat();
            // Seed only the display state. Save/reload integration tests exercise the actual
            // turn and the command; this fixture never loads user state or opens a camera.
            model.ResumeTurnAnnouncementText = "Computer 1 goes first.\nContinue turn 59.";
            model.IsResumeTurnAnnouncementOpen = true;
            foreach (var (width, height) in new[] { (1280, 800), (1000, 620) })
            {
                await Arrange(root, width, height);
                var bounds = ok.TransformToAncestor(root).TransformBounds(new Rect(ok.RenderSize));
                if (overlay.Visibility != Visibility.Visible || content.IsEnabled ||
                    !ReferenceEquals(ok.Command, model.AcknowledgeResumeTurnCommand) ||
                    !ok.IsEnabled || !ok.IsDefault || ok.FocusVisualStyle is not null ||
                    KeyboardNavigation.GetTabNavigation(overlay) != KeyboardNavigationMode.Cycle ||
                    !FocusManager.GetIsFocusScope(overlay) ||
                    ((SolidColorBrush)ok.Background).Color.ToString() != "#EC522D26" ||
                    ((SolidColorBrush)ok.BorderBrush).Color.ToString() != "#FFD4AC6B" ||
                    bounds.Left < 0 || bounds.Top < 0 || bounds.Right > width || bounds.Bottom > height ||
                    !Descendants<TextBlock>(overlay).Any(text => text.Text == model.ResumeTurnAnnouncementText))
                    throw new InvalidOperationException("The reload announcement must block the table and present its bound turn message with a contained brown/gold OK button and no dotted focus marks.");
                Save(root, $"resume-turn-announcement-{width}x{height}.png", width, height);
                checks.Add(new { width, height, ContentDisabled = true, ThemedOk = true,
                    DottedFocusMarks = false, TabContained = true });
            }

            var source = new FixturePresentationSource { RootVisual = window };
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(escape);
            if (!escape.Handled || !model.IsResumeTurnAnnouncementOpen || model.IsGameExitMenuOpen)
                throw new InvalidOperationException("Escape must leave the reload announcement open and must not expose another screen.");

            model.IsResumeTurnAnnouncementOpen = false;
            await Arrange(root, 1000, 620);
            if (overlay.Visibility != Visibility.Collapsed || !content.IsEnabled)
                throw new InvalidOperationException("Dismissing the reload announcement must restore the underlying interface.");
            await File.WriteAllTextAsync(Path.Combine(Output, "resume-turn-announcement.json"),
                JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Reload announcement: themed OK, turn message, content blocking, Escape containment, and both viewport sizes passed.");
        }
        finally
        {
            model.IsResumeTurnAnnouncementOpen = false;
            window.Close();
            await model.DisposeToolsAsync();
        }
    }
}
