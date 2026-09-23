using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoldenTicket.Application;
using GoldenTicket.Domain;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty] private bool _isResumeTurnAnnouncementOpen;
    [ObservableProperty] private string _resumeTurnAnnouncementText = "";
    // Presentation spans verification and its acknowledgment, even after the verifier is cleared.
    [ObservableProperty] private bool _isCheckingResumedGame;

    private bool IsGameInputPaused => IsGameExitMenuOpen || IsResumeTurnAnnouncementOpen;

    private async Task AnnounceResumedTurnAsync()
    {
        ShowResumeTurnAnnouncement();
        if (IsResumeTurnAnnouncementOpen) await RefreshAsync();
        else
        {
            IsCheckingResumedGame = false;
            await PumpAsync();
        }
    }

    partial void OnIsResumeTurnAnnouncementOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRevealPrivateSeat));
        NotifySoloDrawCommands();
        AcknowledgeResumeTurnCommand.NotifyCanExecuteChanged();
    }

    private void ShowResumeTurnAnnouncement()
    {
        if (_coordinator?.Public is not { Lifecycle: SessionLifecycle.Active } view) return;
        IsCheckingResumedGame = true;
        HidePrivateSeat();
        CloseSoloCardPanel();
        var seat = view.SeatOf(view.ActiveSeatId);
        ResumeTurnAnnouncementText = $"{seat.DisplayName} goes first. Resume turn {view.TurnNumber}." +
            (view.PendingClaim is not null ? " Their unfinished train placement will continue." : "");
        IsResumeTurnAnnouncementOpen = true;
    }

    private bool CanAcknowledgeResumeTurn() => IsResumeTurnAnnouncementOpen &&
        !_operationInProgress && !_mustReload && !_exitRequested && !_toolsDisposed &&
        _coordinator is { StorageFaulted: false };

    [RelayCommand(CanExecute = nameof(CanAcknowledgeResumeTurn))]
    private async Task AcknowledgeResumeTurnAsync()
    {
        if (!CanAcknowledgeResumeTurn()) return;
        SetOperationInProgress(true);
        IsResumeTurnAnnouncementOpen = false;
        IsCheckingResumedGame = false;
        try { await PumpAsync(); }
        catch (Exception) { RequireReload(); }
        finally { SetOperationInProgress(false); }
    }

    private bool SavedPendingPlacementMatches(GameCoordinator coordinator)
    {
        // Only format 1 attachments may lack a pending-slot record. New attachments must
        // carry the exact physical progress before either camera or manual rebuild proceeds.
        if (coordinator.Public.Checkpoint?.SuspendedTurnPhase == TurnPhase.AwaitingPhysicalPlacement &&
            CheckpointPhoto.PhotoFormatVersion >= 2 && CheckpointPhoto.PendingPlacement is null)
            return false;
        return CheckpointPhoto.PendingPlacement is not { } pending ||
            coordinator.Public.PendingClaim is { } claim &&
            pending.Matches(claim, coordinator.Public.SeatOf(claim.SeatId).Color);
    }
}
