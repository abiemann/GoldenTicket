using System.ComponentModel;
using GoldenTicket.CompanionHost;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    // Only public, browser-visible values belong here. Equality suppresses repeated camera
    // observations without relying on a timer or sending private hand contents to the stream.
    private sealed record CompanionPresentation(string? SessionId, long? StateVersion,
        bool CanControl, bool StorageFaulted, CompanionBoardInteraction? Board,
        CompanionGuidance? Guidance, string? ResultImageId, CompanionBoardMap? BoardMap);

    private CompanionPresentation? _lastCompanionPresentation;

    private void InitializeCompanionUpdates()
    {
        PropertyChanged += CompanionMainPropertyChanged;
        Game.PropertyChanged += CompanionGuidancePropertyChanged;
        Table.PropertyChanged += CompanionTablePropertyChanged;
        // Registered after the camera's board/placement observers, so their resulting
        // proposal, readiness and correction messages are included in the notification.
        Camera.PropertyChanged += CompanionCameraPropertyChanged;
        Connection.PropertyChanged += CompanionConnectionPropertyChanged;
    }

    private void DisposeCompanionUpdates()
    {
        PropertyChanged -= CompanionMainPropertyChanged;
        Game.PropertyChanged -= CompanionGuidancePropertyChanged;
        Table.PropertyChanged -= CompanionTablePropertyChanged;
        Camera.PropertyChanged -= CompanionCameraPropertyChanged;
        Connection.PropertyChanged -= CompanionConnectionPropertyChanged;
    }

    private void CompanionMainPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(BoardFirstProposal) or nameof(CanRevealPrivateSeat) or
            nameof(CanConnectPhone) or nameof(CanKeepSoloTickets) or nameof(Screen) or
            nameof(GameplayScreen) or nameof(IsGameExitMenuOpen) or nameof(IsResumeTurnAnnouncementOpen) or
            nameof(IsCheckingResumedGame) or nameof(NeedsBoardReconciliation) or nameof(ShowMultiHumanPhoneSetup))
            NotifyCompanionPresentationChanged();
    }

    private void CompanionGuidancePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(GameScreenViewModel.GuidanceSeat) or
            nameof(GameScreenViewModel.GuidanceInstruction) or nameof(GameScreenViewModel.PlacementTargets))
            NotifyCompanionPresentationChanged();
    }

    private void CompanionTablePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TableViewModel.Placement)) NotifyCompanionPresentationChanged();
    }

    private void CompanionCameraPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(CameraViewModel.GameTableAnalysis) or nameof(CameraViewModel.GameTablePreview) or
            nameof(CameraViewModel.IsGameTablePreviewUpright) or nameof(CameraViewModel.IsGameTablePreviewRequested) or
            nameof(CameraViewModel.IsRunning))
            NotifyCompanionPresentationChanged();
    }

    private void CompanionConnectionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ConnectionViewModel.UsePractical)) NotifyCompanionPresentationChanged();
    }

    private void NotifyCompanionPresentationChanged()
    {
        if (_toolsDisposed) return;
        RefreshCompanionMap();
        var presentation = new CompanionPresentation(_coordinator?.SessionId.Value,
            _coordinator?.Public.StateVersion, CanCompanionControl, _coordinator?.StorageFaulted == true,
            CurrentCompanionBoardInteraction(), CurrentCompanionGuidance(), CurrentFinalStandingsImage()?.Info.Id,
            _companionBoardMap);
        if (presentation == _lastCompanionPresentation) return;
        _lastCompanionPresentation = presentation;
        // A nonblocking invalidation: the SSE handler reads the public bridge on its own
        // dispatch. Never wait for a network client while processing a desktop command.
        Connection.NotifyGameChanged();
    }
}
