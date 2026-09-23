using System.Windows.Media.Imaging;
using GoldenTicket.CompanionHost;
using GoldenTicket.Desktop.Services;
using GoldenTicket.Domain;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private CompanionBoardMapPublisher _companionBoardMapPublisher = null!;

    private void InitializeCompanionMapPublisher()
    {
        _companionBoardMapPublisher = new(_manifest);
        _companionBoardMapPublisher.Changed += (_, _) => NotifyCompanionPresentationChanged();
    }

    private CompanionBoardMapContext? CompanionMapContext()
    {
        if (!CanConnectPhone || !Connection.UseQuickPlay || _toolsDisposed || _exitRequested ||
            IsGameInputPaused || !_systemAvailable || _mustReload || NeedsBoardReconciliation ||
            IsCheckingResumedGame || !IsGameplayScreenActive(Screen.Table) ||
            _coordinator is not { StorageFaulted: false } coordinator)
            return null;
        var view = coordinator.Public;
        if (view.IsGameplaySuspended || view.TurnPhase == TurnPhase.RulesDecisionRequired) return null;
        // Completing a claim can advance the digital active seat before the computer's
        // public acknowledgment and scoring-marker instruction have finished.
        var scoring = _scoreMarkerStep is { } step && step.SessionId == coordinator.SessionId &&
            CurrentCompanionGuidance() is not null;
        var previous = _companionBoardMapPublisher.Context;
        var completing = _claimCompletionInProgress && previous is { } prior &&
            prior.SessionId == coordinator.SessionId;
        if (view.Lifecycle != SessionLifecycle.Active &&
            !(view.Lifecycle == SessionLifecycle.Finished &&
                (scoring || completing || IsCheckingBoardBeforeNextTurn))) return null;
        var seat = scoring ? _scoreMarkerStep!.SeatId : completing ? previous!.Value.SeatId : view.ActiveSeatId;
        return view.SeatOf(seat).Kind == SeatKind.Computer || CanCompanionControl || IsCheckingBoardBeforeNextTurn
            ? new(coordinator.SessionId, seat) : null;
    }

    private BitmapSource? CurrentCompanionPreview()
    {
        var frame = Camera.GameTablePreview;
        return Camera.IsGameTablePreviewRequested && Camera.IsRunning &&
            Camera.IsGameTablePreviewUpright && frame is { IsFrozen: true } ? frame : null;
    }

    private CompanionBoardMap? CurrentCompanionBoardMap()
    {
        RefreshCompanionMap();
        return _companionBoardMapPublisher.BoardMap;
    }

    private CompanionBoardImage? ReadCompanionBoardImage(string id)
    {
        RefreshCompanionMap();
        return CurrentCompanionPreview() is null ? null : _companionBoardMapPublisher.ReadImage(id);
    }

    private void RefreshCompanionMap()
    {
        var context = CompanionMapContext();
        var preview = context is null ? null : CurrentCompanionPreview();
        // The laptop's exact placement targets include any correction subset. The
        // publisher sees only these public dots and the accepted camera crop.
        IReadOnlyList<CompanionMapPoint> targets = preview is null ? [] :
            Game.PlacementTargets.Select(point => new CompanionMapPoint(point.X, point.Y, point.Number)).ToArray();
        _companionBoardMapPublisher.Update(context, preview, targets);
    }

    private void ResetCompanionMap() => _companionBoardMapPublisher.Reset();
}
