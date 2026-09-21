using CommunityToolkit.Mvvm.Input;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public bool ShowPracticalHandoff => Connection.UsePractical && CanConnectPhone &&
        IsGameplayScreenActive(Screen.Table) && !ShowMultiHumanPhoneSetup && !IsPrivateVisible && CanRevealPrivateSeat;

    private void ConnectionPresentationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ConnectionViewModel.UsePractical))
        {
            HidePrivateSeat();
            OnPropertyChanged(nameof(RevealPrompt));
            OnPropertyChanged(nameof(ShowPracticalHandoff));
        }
        if (args.PropertyName is nameof(ConnectionViewModel.IsBusy) or nameof(ConnectionViewModel.UsePractical) or
            nameof(ConnectionViewModel.HasApprovedController))
            DismissMultiHumanPhoneSetupCommand.NotifyCanExecuteChanged();
    }

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

    private bool CanDismissMultiHumanPhoneSetup() => !Connection.IsBusy &&
        (Connection.UsePractical || Connection.HasApprovedController);

    [RelayCommand(CanExecute = nameof(CanDismissMultiHumanPhoneSetup))]
    private void DismissMultiHumanPhoneSetup() => ShowMultiHumanPhoneSetup = false;
}
