using CommunityToolkit.Mvvm.Input;
using GoldenTicket.Domain;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public bool ShowManualSavedBoardCheck => IsCheckingResumedGame &&
        IsGameplayScreenActive(Screen.Table) && _savedBoardRestoreVerifier is not null &&
        _coordinator?.Public is { VerificationMode: VerificationMode.Manual } view &&
        view.Lifecycle is SessionLifecycle.PackedAway or SessionLifecycle.Rebuilding &&
        view.Checkpoint?.CheckpointId.Value == _savedBoardRestoreCheckpoint;

    public bool CanCheckSavedBoardMyself => ShowManualSavedBoardCheck &&
        !_savedBoardRestoreCompleting && !_operationInProgress && Busy is null &&
        !IsGameInputPaused && !_exitRequested && !_mustReload && !_toolsDisposed &&
        _windowActive && _systemAvailable && _coordinator is { StorageFaulted: false };

    public string ManualReloadMarkerInstructions => _coordinator is { } coordinator
        ? string.Join(Environment.NewLine, coordinator.Public.Seats.Select(seat =>
            $"{seat.Color} scoring marker: {PrintedScore(seat.RouteScore)}"))
        : "";

    private void InitializeManualReload()
    {
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(CanRevealPrivateSeat) or nameof(Busy) or
                nameof(IsCheckingResumedGame) or nameof(GameplayScreen))
                NotifyManualReloadCommands();
        };
    }

    private void NotifyManualReloadCommands()
    {
        OnPropertyChanged(nameof(ShowManualSavedBoardCheck));
        OnPropertyChanged(nameof(CanCheckSavedBoardMyself));
        OnPropertyChanged(nameof(ManualReloadMarkerInstructions));
        CheckSavedBoardMyselfCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCheckSavedBoardMyself))]
    public async Task CheckSavedBoardMyselfAsync()
    {
        // The operator must still inspect and explicitly attest to the saved position.
        // Entering this screen is neither a confirmation nor camera evidence.
        if (!CanCheckSavedBoardMyself) return;
        await BeginRebuildAsync();
    }
}
