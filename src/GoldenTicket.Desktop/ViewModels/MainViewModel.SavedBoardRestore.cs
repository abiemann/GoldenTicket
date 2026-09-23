using GoldenTicket.Application;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Desktop.Services;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private SavedBoardRestoreSession? _savedBoardRestoreSession;
    private string? _savedBoardRestoreGuidance;
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
        _savedBoardRestoreSession = new SavedBoardRestoreSession(checkpoint.CheckpointId.Value,
            markers, routes, checkpoint.PhysicalTarget, pending);
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
        NotifyManualReloadCommands();
    }

    private void CancelSavedBoardRestore()
    {
        _savedBoardRestoreSession = null;
        _savedBoardRestoreGuidance = null;
        Game.UpdateInventoryProblemMarkers("restore", null);
        SetSavedBoardRestoreTarget(null);
        NotifyManualReloadCommands();
    }

    private void ObserveSavedBoardRestore()
    {
        if (_savedBoardRestoreSession is not { IsCompleting: false } session ||
            _coordinator is not { } coordinator || _operationInProgress || _mustReload ||
            _exitRequested || IsGameExitMenuOpen ||
            coordinator.Public.Lifecycle is not (SessionLifecycle.PackedAway or SessionLifecycle.Rebuilding) ||
            coordinator.Public.Checkpoint?.CheckpointId.Value != session.CheckpointId ||
            !Camera.IsGameTablePreviewUpright || Camera.GameTableAnalysis is not { } analysis)
            return;

        if (session.Observe(analysis) is not { } progress) return;
        var result = progress.Observation;
        BoardInteractionLog.Write("reload.board-check.frame", new
        {
            analysis.Board.Sequence, analysis.Board.Epoch,
            stage = result.Stage.ToString(),
            marker = result.Marker?.Color.ToString(),
            markerScore = result.Marker?.PrintedScore,
            markerState = result.MarkerState?.ToString(),
            markerProblemIndices = result.ProblemCandidateIndices,
            inventoryState = result.Inventory?.State.ToString(),
            inventoryRoute = result.Inventory?.RouteId,
            unexpectedDetections = result.Inventory?.UnexpectedDetections,
            // Include the actual marker readings during marker checks as well as train
            // conflicts, so an unclear object cannot hide behind a generic target prompt.
            scoreMarkers = result.Stage == SavedBoardRestoreStage.CheckingMarker ||
                result.Inventory?.State == BoardInventoryState.UnexpectedTrain
                ? analysis.Scores.Where(reading => reading.CandidateIndex >= 0 &&
                        reading.CandidateIndex < analysis.Candidates.Count)
                    .Select(reading => new
                    {
                        color = reading.Color?.ToString(), reading.Score,
                        status = reading.Status.ToString(), reading.Reason,
                        candidate = analysis.Candidates[reading.CandidateIndex]
                    }).ToArray() : null,
            nearbyCandidates = result.Inventory?.RouteId is { } failedRoute
                ? DescribeNearbyPlacementCandidates(analysis, failedRoute) : []
        });
        switch (result.Stage)
        {
            case SavedBoardRestoreStage.WaitingForCamera:
                return;
            case SavedBoardRestoreStage.CheckingMarker when result.Marker is { } marker:
                var hasMarkerTargets = Game.UpdateScoreMarkerProblemMarkers("restore", marker.Color,
                    analysis.Candidates, result.ProblemCandidateIndices);
                SetSavedBoardRestoreTarget(null);
                ShowSavedBoardRestoreGuidance(DescribeSavedMarkerCheck(result, analysis.Scores, hasMarkerTargets));
                return;
            case SavedBoardRestoreStage.CheckingTrains:
                if (result.Inventory is { } inventory)
                    Game.UpdateInventoryProblemMarkers("restore", inventory,
                        progress.PendingSlotMask);
                if (progress.ShouldUpdateTarget) SetSavedBoardRestoreTarget(progress.Target);
                ShowSavedBoardRestoreGuidance(DescribeSavedTrainCheck(result.Inventory));
                return;
            case SavedBoardRestoreStage.Confirmed:
                if (!session.TryBeginCompletion()) return;
                Game.UpdateInventoryProblemMarkers("restore", null);
                SetSavedBoardRestoreTarget(null);
                NotifyManualReloadCommands();
                ShowSavedBoardRestoreGuidance("Saved scoring markers and trains verified.");
                _ = CompleteSavedBoardRestoreAsync(coordinator, session);
                return;
        }
    }

    private static string DescribeSavedMarkerCheck(SavedBoardRestoreObservation observation,
        IReadOnlyList<ScoreMarkerReading> readings, bool hasTargets)
    {
        var marker = observation.Marker!;
        var name = marker.Color.ToString().ToUpperInvariant();
        var target = $"{name} scoring marker on {marker.PrintedScore}";
        var matching = readings.Where(reading => reading.Color == marker.Color).ToArray();
        var instruction = observation.MarkerState switch
        {
            ScoreMarkerMoveState.Missing =>
                $"The camera cannot find the {name} scoring marker. It should be on {marker.PrintedScore}. " +
                "Keep scoring markers side by side, never stacked, so each is visible from directly above.",
            ScoreMarkerMoveState.WrongPosition when matching.Length == 1 && matching[0].Score is { } score =>
                $"The camera reads the {name} scoring marker on {score}. The saved game expects {marker.PrintedScore}.",
            ScoreMarkerMoveState.WrongPosition => $"Place the {target}.",
            ScoreMarkerMoveState.Ambiguous when matching.Length > 1 =>
                $"The camera sees {matching.Length} possible {name} scoring markers. Check which object is the marker; it should be on {marker.PrintedScore}.",
            ScoreMarkerMoveState.Ambiguous when matching.Length == 1 && matching[0].Status == ScoreMarkerReadingStatus.OffTrack =>
                $"The {name} scoring marker appears off the score track. Place it on {marker.PrintedScore}.",
            ScoreMarkerMoveState.Ambiguous when matching.Length == 1 && matching[0].Status == ScoreMarkerReadingStatus.Read =>
                $"The camera cannot distinguish the {name} scoring marker from a nearby detection. It should be on {marker.PrintedScore}.",
            ScoreMarkerMoveState.Ambiguous =>
                $"The camera cannot clearly read the {name} scoring marker's position. Center it on {marker.PrintedScore}.",
            ScoreMarkerMoveState.Stabilizing => $"{target}. Waiting for a stable camera reading.",
            _ => $"Checking the {target}."
        };
        return hasTargets ? instruction + " Check the yellow spheres on the board." : instruction;
    }

    private string DescribeSavedTrainCheck(BoardInventoryObservation? observation)
    {
        if (observation is null || observation.State is BoardInventoryState.Stabilizing or
            BoardInventoryState.WaitingForFreshFrame)
            return "Saved trains.";
        if (observation.State == BoardInventoryState.Unsupported)
            return "The camera cannot verify this saved route automatically. Play stays paused until the saved board can be checked.";
        if (observation.RouteId is { } pendingRouteId && _savedBoardRestoreSession?.PendingPlacement is { } pending &&
            pending.RouteId.Value == pendingRouteId)
            return $"Unfinished {pending.Color} placement on {_manifest.Describe(pending.RouteId)} " +
                $"({pending.TrainCount} trains in the saved photo).";
        if (observation.State == BoardInventoryState.UnexpectedTrain)
        {
            if (observation.UnexpectedTrains is { } extra)
                return $"The camera sees {extra.Count} unexpected{(extra.Color is { } color ? " " + color : "")} " +
                    $"train{(extra.Count == 1 ? "" : "s")} on {_manifest.Describe(new RouteId(extra.RouteId))} " +
                    "outside the saved routes. Check the yellow spheres on the board.";
            return observation.UnexpectedDetections.Count > 0
                ? "The camera sees unexpected trains outside the saved routes. Check the yellow spheres on the board."
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

    private async Task CompleteSavedBoardRestoreAsync(GameCoordinator coordinator, SavedBoardRestoreSession session)
    {
        bool IsSameSession() => _coordinator == coordinator &&
            ReferenceEquals(_savedBoardRestoreSession, session);
        bool IsCurrentCheckpoint() => IsSameSession() &&
            coordinator.Public.Checkpoint?.CheckpointId.Value == session.CheckpointId &&
            coordinator.Public.Lifecycle is SessionLifecycle.PackedAway or SessionLifecycle.Rebuilding;
        if (!IsCurrentCheckpoint()) return;
        SetOperationInProgress(true);
        HidePrivateSeat();
        try
        {
            var checkpoint = coordinator.Public.Checkpoint!;
            if (coordinator.Public.Lifecycle == SessionLifecycle.PackedAway)
            {
                var begin = await coordinator.SubmitAsync(new BeginBoardRebuild(
                    coordinator.NewEnvelope(), checkpoint.CheckpointId));
                if (!IsCurrentCheckpoint()) return;
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
                if (!IsCurrentCheckpoint()) return;
                if (!attest.IsAccepted)
                {
                    Status = attest.Result.Rejection?.Message;
                    if (attest.Result.Rejection?.Code == "StorageFaulted") RequireReload();
                    return;
                }
            }
            var resume = await coordinator.SubmitAsync(new ResumePackedGame(
                coordinator.NewEnvelope(), checkpoint.CheckpointId));
            if (!IsSameSession()) return;
            if (!resume.IsAccepted)
            {
                Status = resume.Result.Rejection?.Message;
                if (resume.Result.Rejection?.Code == "StorageFaulted") RequireReload();
                return;
            }

            BoardInteractionLog.Write("reload.board-check.confirmed", new { checkpoint = session.CheckpointId });
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
            if (!_mustReload && ReferenceEquals(_savedBoardRestoreSession, session))
            {
                session.ResetAfterFailedCompletion();
                NotifyManualReloadCommands();
                _savedBoardRestoreGuidance = null;
                ShowSavedBoardRestoreGuidance("Saved board.");
            }
        }
    }
}
