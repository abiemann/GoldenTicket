using System.IO;
using GoldenTicket.Desktop.Services;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public WindowPresentation? SavedWindowPresentation { get; private set; }

    public void SaveWindowPresentation(WindowPresentation value)
    {
        if (!PresentationPreferences.IsValid(value)) return;
        SavedWindowPresentation = value;
        OnPropertyChanged(nameof(SavedWindowPresentation));
        try { PresentationPreferences.SaveWindowPresentation(_presentationSettingsPath, value); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "The window size is active but could not be saved for next launch: " + ex.Message;
        }
    }

    partial void OnDisplayModeChanged(DisplayMode value)
    {
        try { PresentationPreferences.SaveDisplayMode(_presentationSettingsPath, value); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "The display mode is active but could not be saved for next launch: " + ex.Message;
        }
    }
}
