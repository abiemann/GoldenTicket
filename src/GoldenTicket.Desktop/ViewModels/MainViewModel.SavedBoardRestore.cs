using GoldenTicket.Application;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private SavedBoardRestoreVerifier? _savedBoardRestoreVerifier;
    private string? _savedBoardRestoreCheckpoint;
    private string? _savedBoardRestoreGuidance;
    private bool _savedBoardRestoreCompleting;
    internal (RouteId RouteId, int TrainCount, int SlotMask)? SavedBoardRestoreTarget { get; private set; }

    private void StartSavedBoardRestore()
    {
        var view = _coordinator?.Public;
        if (view?.Checkpoint is not { IsSafeToPackAway: true } checkpoint ||
            view.Lifecycle is not (SessionLifecycle.PackedAway or SessionLifecycle.Rebuilding))
            return;

        var colors = new[] { PlayerColor.Blue, PlayerColor.Red, PlayerColor.Green,
            PlayerColor.Yellow, PlayerColor.Black };
        var markers = colors.SelectMany(color => view.Seats.Where(seat => seat.Color == color))
            .Select(seat => new SavedScoreMarker(ToMarkerColor(seat.Color), PrintedScore(seat.RouteScore)))
            .ToArray();
        var routes = checkpoint.PhysicalTarget.Select(route =>
            new BoardInventoryRoute(route.RouteId.Value,
                ToMarkerColor(view.SeatOf(route.SeatId).Color), route.Length)).ToArray();
        var pending = CheckpointPhoto.PendingPlacement;
        var pendingRoute = pending is null ? null : new BoardInventoryRoute(pending.RouteId.Value,
            ToMarkerColor(pending.Color), pending.RouteLength);
        _savedBoardRestoreVerifier = new SavedBoardRestoreVerifier(markers, routes,
            pendingRoute, pending?.OccupiedSlotMask);
        _savedBoardRestoreCheckpoint = checkpoint.CheckpointId.Value;
        _savedBoardRestoreCompleting = false;
        _savedBoardRestoreGuidance = null;
        Game.UpdateInventoryProblemMarkers("restore", null);
        SetSavedBoardRestoreTarget(null);
        ShowSavedBoardRestoreGuidance("Saved board.");
        BoardInteractionLog.Write("reload.board-check.started", new
        {
            checkpoint = checkpoint.CheckpointId.Value,
            markers = markers.Select(marker => new { color = marker.Color.ToString(), marker.PrintedScore }),
            routes = routes.Select(route => new { route.RouteId, color = route.Color.ToString(), route.TrainCount })
        });
    }

    private void CancelSavedBoardRestore()
    {
        _savedBoardRestoreVerifier = null;
        _savedBoardRestoreCheckpoint = null;
        _savedBoardRestoreGuidance = null;
        _savedBoardRestoreCompleting = false;
        Game.UpdateInventoryProblemMarkers("restore", null);
        SetSavedBoardRestoreTarget(null);
    }

    private void ObserveSavedBoardRestore()
    {
        if (_savedBoardRestoreVerifier is not { } verifier || _savedBoardRestoreCompleting ||
            _coordinator is not { } coordinator || _operationInProgress || _mustReload ||
            _exitRequested || IsGameExitMenuOpen ||
            coordinator.Public.Lifecycle is not (SessionLifecycle.PackedAway or SessionLifecycle.Rebuilding) ||
            coordinator.Public.Checkpoint?.CheckpointId.Value != _savedBoardRestoreCheckpoint ||
            !Camera.IsGameTablePreviewUpright || Camera.GameTableAnalysis is not { } analysis)
            return;

        var result = verifier.Observe(analysis.Board, analysis.Scores, analysis.Candidates,
            analysis.CropRevision, analysis.ModelRevision);
        BoardInteractionLog.Write("reload.board-check.frame", new
        {
            analysis.Board.Sequence, analysis.Board.Epoch,
            stage = result.Stage.ToString(),
            marker = result.Marker?.Color.ToString(),
            markerScore = result.Marker?.PrintedScore,
            markerState = result.MarkerState?.ToString(),
            inventoryState = result.Inventory?.State.ToString(),
            inventoryRoute = result.Inventory?.RouteId,
            unexpectedDetections = result.Inventory?.UnexpectedDetections,
            nearbyCandidates = result.Inventory?.RouteId is { } failedRoute
                ? DescribeNearbyPlacementCandidates(analysis, failedRoute) : []
        });
        switch (result.Stage)
        {
            case SavedBoardRestoreStage.WaitingForCamera:
                return;
            case SavedBoardRestoreStage.CheckingMarker when result.Marker is { } marker:
                Game.UpdateInventoryProblemMarkers("restore", null);
                SetSavedBoardRestoreTarget(null);
                ShowSavedBoardRestoreGuidance(
                    $"{marker.Color.ToString().ToUpperInvariant()} scoring marker on {marker.PrintedScore}.");
                return;
            case SavedBoardRestoreStage.CheckingTrains:
                if (result.Inventory is { } inventory)
                    Game.UpdateInventoryProblemMarkers("restore", inventory,
                        CheckpointPhoto.PendingPlacement is { } partial &&
                        inventory.RouteId == partial.RouteId.Value ? partial.OccupiedSlotMask : null);
                var route = result.Inventory?.RouteId is { } routeId &&
                    result.Inventory.State is BoardInventoryState.MissingTrains or
                        BoardInventoryState.WrongColor or BoardInventoryState.Ambiguous
                    ? coordinator.Public.Checkpoint?.PhysicalTarget
                        .FirstOrDefault(target => target.RouteId.Value == routeId)
                    : null;
                if (route is not null)
                    SetSavedBoardRestoreTarget((route.RouteId, route.Length, (1 << route.Length) - 1));
                else if (CheckpointPhoto.PendingPlacement is { } pending &&
                    result.Inventory?.RouteId == pending.RouteId.Value)
                    SetSavedBoardRestoreTarget((pending.RouteId, pending.RouteLength, pending.OccupiedSlotMask));
                else if (result.Inventory?.State != BoardInventoryState.WaitingForFreshFrame)
                    SetSavedBoardRestoreTarget(null);
                ShowSavedBoardRestoreGuidance(DescribeSavedTrainCheck(result.Inventory));
                return;
            case SavedBoardRestoreStage.Confirmed:
                Game.UpdateInventoryProblemMarkers("restore", null);
                SetSavedBoardRestoreTarget(null);
                _savedBoardRestoreCompleting = true;
                ShowSavedBoardRestoreGuidance("Saved scoring markers and trains verified.");
                _ = CompleteSavedBoardRestoreAsync(coordinator, _savedBoardRestoreCheckpoint!);
                return;
        }
    }

    private string DescribeSavedTrainCheck(BoardInventoryObservation? observation)
    {
        if (observation is null || observation.State is BoardInventoryState.Stabilizing or
            BoardInventoryState.WaitingForFreshFrame)
            return "Saved trains.";
        if (observation.State == BoardInventoryState.Unsupported)
            return "The camera cannot verify this saved route automatically. Play stays paused until the saved board can be checked.";
        if (observation.RouteId is { } pendingRouteId && CheckpointPhoto.PendingPlacement is { } pending &&
            pending.RouteId.Value == pendingRouteId)
            return $"Unfinished {pending.Color} placement on {_manifest.Describe(pending.RouteId)} " +
                $"({pending.TrainCount} trains in the saved photo).";
        if (observation.State == BoardInventoryState.UnexpectedTrain)
        {
            if (observation.UnexpectedTrains is { } extra)
                return $"The camera sees {extra.Count}{(extra.Color is { } color ? " " + color : "")} " +
                    $"train{(extra.Count == 1 ? "" : "s")} on {_manifest.Describe(new RouteId(extra.RouteId))} " +
                    "outside the saved routes. Check the yellow spheres on the board.";
            return observation.UnexpectedDetections.Count > 0
                ? "The camera sees trains outside the saved routes. Check the yellow spheres on the board."
                : "The camera returned a train detection without a usable position. Clear hands or glare while it checks again.";
        }
        if (observation.RouteId is { } routeId)
        {
            var route = _coordinator?.Public.Checkpoint?.PhysicalTarget
                .FirstOrDefault(target => target.RouteId.Value == routeId);
            if (route is not null && _coordinator is { } coordinator)
            {
                var color = coordinator.Public.SeatOf(route.SeatId).Color.ToString().ToUpperInvariant();
                var name = _manifest.Describe(route.RouteId);
                return $"{route.Length} {color} train{(route.Length == 1 ? "" : "s")} on {name}.";
            }
        }
        return "Saved train positions and colors.";
    }

    private void ShowSavedBoardRestoreGuidance(string instruction)
    {
        if (_savedBoardRestoreGuidance == instruction) return;
        _savedBoardRestoreGuidance = instruction;
        Game.ShowGuidance(Table.TurnText, Table.ActiveSeatName, instruction);
    }

    private void SetSavedBoardRestoreTarget((RouteId RouteId, int TrainCount, int SlotMask)? target)
    {
        if (SavedBoardRestoreTarget == target) return;
        SavedBoardRestoreTarget = target;
        OnPropertyChanged(nameof(SavedBoardRestoreTarget));
    }

    private async Task CompleteSavedBoardRestoreAsync(GameCoordinator coordinator, string checkpointId)
    {
        if (_coordinator != coordinator || coordinator.Public.Checkpoint?.CheckpointId.Value != checkpointId)
            return;
        SetOperationInProgress(true);
        HidePrivateSeat();
        try
        {
            var checkpoint = coordinator.Public.Checkpoint!;
            if (coordinator.Public.Lifecycle == SessionLifecycle.PackedAway)
            {
                var begin = await coordinator.SubmitAsync(new BeginBoardRebuild(
                    coordinator.NewEnvelope(), checkpoint.CheckpointId));
                if (!begin.IsAccepted)
                {
                    Status = begin.Result.Rejection?.Message;
                    if (begin.Result.Rejection?.Code == "StorageFaulted") RequireReload();
                    return;
                }
            }
            if (!coordinator.Public.RebuildAttested)
            {
                var attest = await coordinator.SubmitAsync(new AttestBoardRebuild(
                    coordinator.NewEnvelope(), checkpoint.CheckpointId,
                    checkpoint.PhysicalTargetHash, Environment.UserName + " (camera-verified)"));
                if (!attest.IsAccepted)
                {
                    Status = attest.Result.Rejection?.Message;
                    if (attest.Result.Rejection?.Code == "StorageFaulted") RequireReload();
                    return;
                }
            }
            var resume = await coordinator.SubmitAsync(new ResumePackedGame(
                coordinator.NewEnvelope(), checkpoint.CheckpointId));
            if (!resume.IsAccepted)
            {
                Status = resume.Result.Rejection?.Message;
                if (resume.Result.Rejection?.Code == "StorageFaulted") RequireReload();
                return;
            }

            BoardInteractionLog.Write("reload.board-check.confirmed", new { checkpoint = checkpointId });
            CancelSavedBoardRestore();
            Game.ClearGuidance();
            Status = null;
            ShowGameplayScreen(Screen.Table);
            await AnnounceResumedTurnAsync();
        }
        catch (Exception)
        {
            RequireReload();
        }
        finally
        {
            SetOperationInProgress(false);
            if (!_mustReload && _savedBoardRestoreVerifier is { } verifier)
            {
                verifier.Reset();
                _savedBoardRestoreCompleting = false;
                _savedBoardRestoreGuidance = null;
                ShowSavedBoardRestoreGuidance("Saved board.");
            }
        }
    }
}
