using CommunityToolkit.Mvvm.Input;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private void PresentMultiHumanPhoneSetup()
    {
        ShowMultiHumanPhoneSetup = CanConnectPhone && IsGameplayScreenActive(Screen.Table);
        if (ShowMultiHumanPhoneSetup) Connection.RefreshInterfaces();
    }

    [RelayCommand]
    private void OpenMultiHumanPhoneSetup()
    {
        if (_coordinator is null || !CanConnectPhone ||
            !(IsGameplayScreenActive(Screen.Table) || IsGameplayScreenActive(Screen.FinalScore))) return;
        Connection.RefreshInterfaces();
        ShowMultiHumanPhoneSetup = true;
    }

    [RelayCommand]
    private void DismissMultiHumanPhoneSetup() => ShowMultiHumanPhoneSetup = false;
}
