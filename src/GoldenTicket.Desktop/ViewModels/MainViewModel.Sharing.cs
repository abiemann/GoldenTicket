using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoldenTicket.CompanionHost;
using GoldenTicket.Desktop.Services;
using GoldenTicket.Domain;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private CompanionResultImage? _finalStandingsImage;
    [ObservableProperty] private string _finalStandingsShareStatus = "";

    public bool CanShareFinalStandings => !_toolsDisposed && CanConnectPhone &&
        _coordinator?.Public.Lifecycle == SessionLifecycle.Finished &&
        IsGameplayScreenActive(Screen.FinalScore) && FinalScores.Count > 0;

    private CompanionResultImage? CurrentFinalStandingsImage() => !_toolsDisposed &&
        _coordinator is { } game && _finalStandingsImage is { } image &&
        image.SessionId == game.SessionId.Value && image.StateVersion == game.Public.StateVersion
            ? image : null;

    private void ResetFinalStandingsSharing()
    {
        _finalStandingsImage = null;
        FinalStandingsShareStatus = "";
        NotifyFinalStandingsSharing();
    }

    private void NotifyFinalStandingsSharing()
    {
        NotifyCompanionPresentationChanged();
        OnPropertyChanged(nameof(CanShareFinalStandings));
        SendFinalStandingsToPhoneCommand.NotifyCanExecuteChanged();
        BackToMenuCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanShareFinalStandings))]
    private async Task SendFinalStandingsToPhoneAsync()
    {
        if (!CanShareFinalStandings || _coordinator is not { } game) return;
        Connection.UseQuickPlay = true;
        if (!Connection.IsRunning || !Connection.HasApprovedController)
        {
            FinalStandingsShareStatus = "Connect and approve the shared phone, then select Share to phone.";
            OpenMultiHumanPhoneSetup();
            return;
        }
        var version = game.Public.StateVersion;
        FinalStandingsShareStatus = "Preparing the standings image…";
        try
        {
            var png = await FinalStandingsImageRenderer.RenderAsync(FinalSummary, FinalScores.ToArray(), Camera.GameTablePreview);
            if (_coordinator != game || game.Public.StateVersion != version || !CanShareFinalStandings) return;
            if (!Connection.IsRunning || !Connection.HasApprovedController)
            {
                FinalStandingsShareStatus = "Reconnect the shared phone, then select Share to phone again.";
                return;
            }
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
            _finalStandingsImage = new CompanionResultImage(game.SessionId.Value, version,
                new CompanionResultImageInfo(Guid.NewGuid().ToString("N"),
                    $"golden-ticket-final-standings-{timestamp}.png"), png);
            FinalStandingsShareStatus = "Image ready on the shared phone. Open it to share or save.";
            NotifyCompanionPresentationChanged();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FinalStandingsShareStatus = "The image could not be prepared. Please try Share to phone again.";
            BoardInteractionLog.Write("final-standings.image-failed", new { errorType = error.GetType().Name });
        }
    }
}
