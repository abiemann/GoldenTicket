using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private long _savedMatchListGeneration;
    private bool _deletingSavedMatch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSavedMatchDeleteConfirmationOpen), nameof(SavedMatchDeleteDescription))]
    private SavedSessionRow? _savedMatchPendingDeletion;

    public bool IsSavedMatchDeleteConfirmationOpen => SavedMatchPendingDeletion is not null;
    public string? SavedMatchDeleteDescription => SavedMatchPendingDeletion?.Description;

    private bool CanRefreshSavedMatches => !_operationInProgress && !_exitRequested && !_toolsDisposed;
    private bool CanStartMatch => CanRefreshSavedMatches && IsGameplayScreenActive(Screen.Setup);
    private bool CanDeleteSelectedSavedMatch => CanRefreshSavedMatches && Screen == Screen.Setup &&
        IsGameplayScreenActive(Screen.Setup) &&
        Setup.SelectedSavedSession is { } selected && selected.SessionId != _coordinator?.SessionId &&
        Setup.SavedSessions.Any(row => row.SessionId == selected.SessionId);
    private bool CanRequestDeleteSavedMatch => CanDeleteSelectedSavedMatch && !IsSavedMatchDeleteConfirmationOpen;
    private bool CanConfirmDeleteSavedMatch => CanDeleteSelectedSavedMatch &&
        SavedMatchPendingDeletion is { } pending && pending.SessionId == Setup.SelectedSavedSession?.SessionId;
    private bool CanCancelDeleteSavedMatch => CanRefreshSavedMatches && IsSavedMatchDeleteConfirmationOpen;

    partial void OnSavedMatchPendingDeletionChanged(SavedSessionRow? value) => NotifySavedMatchCommands();

    private void NotifySavedMatchCommands()
    {
        RequestDeleteSavedMatchCommand.NotifyCanExecuteChanged();
        ConfirmDeleteSavedMatchCommand.NotifyCanExecuteChanged();
        CancelDeleteSavedMatchCommand.NotifyCanExecuteChanged();
        LoadSavedSessionsCommand.NotifyCanExecuteChanged();
        StartMatchCommand.NotifyCanExecuteChanged();
    }

    private void ClearSavedMatchDeletion() => SavedMatchPendingDeletion = null;

    [RelayCommand(CanExecute = nameof(CanRequestDeleteSavedMatch))]
    private void RequestDeleteSavedMatch()
    {
        if (!CanRequestDeleteSavedMatch) return;
        ClearEarlierSaveRecovery();
        Setup.SavedMatchMessage = null;
        SavedMatchPendingDeletion = Setup.SelectedSavedSession;
    }

    [RelayCommand(CanExecute = nameof(CanCancelDeleteSavedMatch))]
    private void CancelDeleteSavedMatch()
    {
        if (CanCancelDeleteSavedMatch) ClearSavedMatchDeletion();
    }

    [RelayCommand(CanExecute = nameof(CanConfirmDeleteSavedMatch))]
    private async Task ConfirmDeleteSavedMatchAsync()
    {
        if (!CanConfirmDeleteSavedMatch || SavedMatchPendingDeletion is not { } saved) return;

        _deletingSavedMatch = true;
        // A list request started before deletion must never restore a removed row afterwards.
        _savedMatchListGeneration++;
        SetOperationInProgress(true);
        Busy = "Deleting the selected saved match...";
        try
        {
            await _store.DeleteSessionAsync(saved.SessionId, CancellationToken.None);
            ClearSavedMatchDeletion();
            ClearEarlierSaveRecovery();
            Setup.RemoveSavedSession(saved.SessionId);
            _deletingSavedMatch = false;
            await LoadSavedSessionsAsync();
            if (Setup.SavedMatchMessage is null)
                Setup.SavedMatchMessage = "The selected saved match and its saved board photos were deleted.";
        }
        catch (Exception)
        {
            ClearSavedMatchDeletion();
            Setup.SavedMatchMessage = "The saved match could not be fully deleted. Check storage access and try again.";
        }
        finally
        {
            _deletingSavedMatch = false;
            Busy = null;
            SetOperationInProgress(false);
        }
    }
}
