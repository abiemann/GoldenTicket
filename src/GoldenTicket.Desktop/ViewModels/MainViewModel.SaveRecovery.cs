using CommunityToolkit.Mvvm.Input;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private PackAwayCheckpoint? _earlierCompletedSave;
    public bool HasEarlierCompletedSave => _earlierCompletedSave is not null;

    private void ClearEarlierSaveRecovery()
    {
        _earlierCompletedSave = null;
        OnPropertyChanged(nameof(HasEarlierCompletedSave));
    }

    private async Task OfferEarlierSaveRecoveryAsync(SessionId sessionId)
    {
        var earlier = await FindLatestCompletedGameSaveAsync(sessionId);
        if (earlier is null || Setup.SelectedSavedSession?.SessionId != sessionId) return;
        _earlierCompletedSave = earlier;
        Setup.SavedMatchMessage += $" You can restore the earlier completed save '{earlier.Name}'. " +
            "Restoring it will discard all actions after that save.";
        OnPropertyChanged(nameof(HasEarlierCompletedSave));
    }

    [RelayCommand]
    private async Task RestoreEarlierCompletedSaveAsync()
    {
        if (!CanResumeMatch || _earlierCompletedSave is not { } earlier ||
            Setup.SelectedSavedSession?.SessionId != earlier.SessionId) return;
        SetOperationInProgress(true);
        var recovered = false;
        try
        {
            // Revalidate at the explicit recovery click; a deleted image must not authorize
            // rewinding a journal just because it was present when the error was displayed.
            var latest = await FindLatestCompletedGameSaveAsync(earlier.SessionId);
            if (latest?.CheckpointId != earlier.CheckpointId)
                throw new InvalidOperationException("The earlier completed save is no longer available.");
            await _store.RewindToVerifiedCheckpointAsync(earlier.SessionId, earlier.CheckpointId,
                _manifest, _catalog, CancellationToken.None);
            _coordinator = null;
            _driver = null;
            _loadedPhotoCheckpoint = null;
            ClearEarlierSaveRecovery();
            await LoadSavedSessionsAsync();
            Setup.SelectedSavedSession = Setup.SavedSessions.FirstOrDefault(row => row.SessionId == earlier.SessionId);
            recovered = true;
        }
        catch (Exception)
        {
            Setup.SavedMatchMessage = "The earlier save could not be restored. Check storage access and its required board image, then retry loading the game.";
            Game.Message = Setup.SavedMatchMessage;
        }
        finally { SetOperationInProgress(false); }
        if (recovered)
        {
            await ResumeMatchAsync();
            if (!Game.IsPlaying) Game.Message = Setup.SavedMatchMessage;
        }
    }
}
