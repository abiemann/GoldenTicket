using System.IO;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Model;
using GoldenTicket.Persistence;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private BoardInventoryVerifier? _gameExitInventoryVerifier;
    private TaskCompletionSource<BoardInventoryObservation>? _gameExitInventoryCompletion;
    private BoardInventoryObservation? _lastGameExitInventoryObservation;
    private long _gameExitInventoryFrameSequence;
    private long _gameExitInventoryCameraEpoch;
    private long _gameExitInventoryCropRevision;
    private long _gameExitMinimumFrameSequence;

    [ObservableProperty] private bool _isGameExitMenuOpen;
    [ObservableProperty] private bool _isGameExitSaving;
    [ObservableProperty] private string _gameExitInventorySummary = "";
    [ObservableProperty] private string? _gameExitStatus;

    public void OpenGameExitMenu()
    {
        if (IsGameInputPaused || !Game.IsPlaying || !_gameLayerVisible || _exitRequested ||
            _toolsDisposed || _coordinator is null || _operationInProgress || Busy is not null) return;
        // An action must finish its durable write, computer turns and public-view refresh before
        // the menu can pause play. Otherwise PumpAsync would skip that continuation permanently.
        // The solo opening destination choice is deliberately persistent on the public table.
        // Cover it with this modal, then reveal the same choice when the player returns.
        if (!ShowSoloOpeningTicketsOnBoard) HidePrivateSeat();
        GameExitInventorySummary = DescribeExpectedTrainInventory();
        GameExitStatus = null;
        IsGameExitMenuOpen = true;
        OnPropertyChanged(nameof(CanRevealPrivateSeat));
    }

    public void CloseGameExitMenu()
    {
        if (!IsGameExitMenuOpen || IsGameExitSaving) return;
        IsGameExitMenuOpen = false;
        GameExitStatus = null;
        OnPropertyChanged(nameof(CanRevealPrivateSeat));
    }

    private string DescribeExpectedTrainInventory()
    {
        var view = _coordinator?.Public;
        if (view is null) return "No game is in progress.";
        var lines = view.Seats.Select(seat =>
        {
            var onBoard = view.RouteOwners
                .Where(route => route.Value == seat.SeatId)
                .Sum(route => _manifest.Route(route.Key).Length);
            return $"{seat.DisplayName} ({seat.Color}): {onBoard} on board, " +
                   $"{seat.TrainsRemaining} remaining";
        });
        var summary = "Expected from confirmed routes: " + string.Join(" · ", lines);
        if (view.PendingClaim is { AwaitingRestore: false } pending)
            summary += $". Any trains already placed for {_manifest.Describe(pending.RouteId)} " +
                       "will be saved as an unfinished move.";
        return summary;
    }

    private void ObserveGameExitInventory()
    {
        if (_gameExitInventoryVerifier is not { } verifier ||
            _gameExitInventoryCompletion is not { } completion || completion.Task.IsCompleted ||
            Camera.GameTableAnalysis is not { } analysis ||
            !Camera.IsGameTablePreviewUpright ||
            analysis.Board.Sequence <= _gameExitMinimumFrameSequence) return;

        var observation = verifier.Observe(analysis.Board, analysis.Candidates,
            analysis.CropRevision, analysis.ModelRevision);
        _lastGameExitInventoryObservation = observation;
        BoardInteractionLog.Write("save.board-check.frame", new
        {
            analysis.Board.Sequence, analysis.Board.Epoch,
            analysis.CropRevision, analysis.ModelRevision,
            state = observation.State.ToString(),
            observation.RouteId, observation.UnexpectedTrains,
            afterPhoto = _gameExitMinimumFrameSequence > 0
        });
        if (observation.Confirmed)
        {
            _gameExitInventoryFrameSequence = analysis.Board.Sequence;
            _gameExitInventoryCameraEpoch = analysis.Board.Epoch;
            _gameExitInventoryCropRevision = analysis.CropRevision;
            completion.TrySetResult(observation);
        }
        else
        {
            GameExitStatus = observation.State == BoardInventoryState.Stabilizing
                ? "Keep the board still while the camera confirms the train positions and colors…"
                : DescribeGameExitInventoryIssue(observation) + " Checking again…";
        }
    }

    private string DescribeGameExitInventoryIssue(BoardInventoryObservation? observation)
    {
        if (observation is { State: BoardInventoryState.UnexpectedTrain, UnexpectedTrains: { } extra })
        {
            var color = extra.Color is { } detected ? " " + detected.ToString().ToLowerInvariant() : "";
            return $"The camera sees {extra.Count}{color} train{(extra.Count == 1 ? "" : "s")} on " +
                $"{_manifest.Describe(new RouteId(extra.RouteId))}, an unclaimed route. " +
                "Return to the game to resolve the placement, or remove " +
                $"{(extra.Count == 1 ? "it" : "them")} before saving.";
        }

        var route = observation?.RouteId is { } routeId
            ? _manifest.Describe(new RouteId(routeId)) : null;
        return observation?.State switch
        {
            BoardInventoryState.MissingTrains when route is not null =>
                $"The camera cannot verify every train on {route}. Check that each train is visible and in its space.",
            BoardInventoryState.WrongColor when route is not null =>
                $"The camera sees a train of the wrong color on {route}. Check the pieces there.",
            BoardInventoryState.Ambiguous when route is not null =>
                $"The camera cannot clearly identify the trains or their colors on {route}. Check their positions and lighting.",
            BoardInventoryState.UnexpectedTrain when route is not null =>
                $"The camera sees extra trains on {route}. Keep this route's placement unchanged while saving.",
            BoardInventoryState.UnexpectedTrain =>
                "The camera sees an extra or misplaced train but cannot identify its route confidently. " +
                "Check for pieces outside the claimed routes and any unfinished placement.",
            BoardInventoryState.MissingTrains =>
                "The camera cannot verify all the expected trains. Check that every claimed route is still occupied.",
            BoardInventoryState.WrongColor =>
                "The camera sees a train of the wrong color. Check the pieces on the claimed routes.",
            BoardInventoryState.Ambiguous =>
                "The camera cannot clearly identify every train. Check the positions, crop and lighting.",
            BoardInventoryState.Unsupported =>
                "The camera cannot verify one of this game's routes. Return to the game to check the board.",
            BoardInventoryState.Stabilizing or BoardInventoryState.Confirmed =>
                "Keep the board still until the camera confirms the same train positions and colors in fresh views.",
            _ => "The camera needs a fresh, upright view of the whole board. Check the crop and lighting."
        };
    }

    [RelayCommand]
    private async Task SaveGameToMenuAsync()
    {
        if (!IsGameExitMenuOpen || IsGameExitSaving || _operationInProgress ||
            _exitRequested || _mustReload || _coordinator is not { StorageFaulted: false } coordinator)
            return;
        if (Busy is not null || _scoreMarkerStep is not null ||
            coordinator.Public.PendingClaim is { AwaitingRestore: true })
        {
            GameExitStatus = "Finish the scoring-marker move or restore the cancelled placement before saving.";
            return;
        }
        if (coordinator.Public.Lifecycle is not (SessionLifecycle.Active or
            SessionLifecycle.PreparingPackAway or SessionLifecycle.PackedAway))
        {
            GameExitStatus = "Finish the opening destinations before saving this game.";
            return;
        }
        if (!Camera.CanCaptureGameTablePhoto || !Camera.IsGameTablePreviewUpright ||
            Camera.GameTableAnalysis is not { Board.Age: var age } || age > TimeSpan.FromSeconds(2))
        {
            GameExitStatus = "Start the overhead camera, frame the entire board, " +
                "and wait for a fresh, upright view before saving.";
            return;
        }

        IsGameExitSaving = true;
        SetOperationInProgress(true);
        GameExitStatus = "Checking every train on the board by route and color…";
        var capturedVersion = coordinator.Public.StateVersion;
        var pending = coordinator.Public.PendingClaim;
        var pendingRoute = pending is null ? null : new BoardInventoryRoute(pending.RouteId.Value,
            ToMarkerColor(coordinator.Public.SeatOf(pending.SeatId).Color), pending.TrainCount);
        try
        {
            var expected = coordinator.Public.RouteOwners
                .Select(route => new BoardInventoryRoute(route.Key.Value,
                    ToMarkerColor(coordinator.Public.SeatOf(route.Value).Color),
                    _manifest.Route(route.Key).Length))
                .ToArray();
            _gameExitInventoryVerifier = new BoardInventoryVerifier(expected, pendingRoute);
            _gameExitInventoryCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _lastGameExitInventoryObservation = null;
            _gameExitMinimumFrameSequence = 0;
            ObserveGameExitInventory();
            BoardInventoryObservation inventory;
            try
            {
                inventory = await _gameExitInventoryCompletion.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException)
            {
                GameExitStatus = DescribeGameExitInventoryIssue(_lastGameExitInventoryObservation) +
                    " Then try Save Game again.";
                return;
            }
            if (!ReferenceEquals(coordinator, _coordinator) ||
                coordinator.Public.StateVersion != capturedVersion || !IsGameExitMenuOpen)
            {
                GameExitStatus = "The game changed while checking the board. Try Save Game again.";
                return;
            }

            var counts = inventory.ConfirmedByColor;
            var pendingPlacement = pending is null ? null : new CheckpointPendingPlacement(
                pending.OperationId, pending.RouteId, pending.SeatId,
                coordinator.Public.SeatOf(pending.SeatId).Color, pending.TrainCount,
                inventory.PendingSlotMask ?? throw new InvalidOperationException("The pending placement was not checked."));
            var inventoryEpoch = _gameExitInventoryCameraEpoch;
            var inventoryCrop = _gameExitInventoryCropRevision;
            var inventorySequence = _gameExitInventoryFrameSequence;
            var confirmedInventory = new CheckpointTrainInventory(
                Count(MarkerColor.Blue), Count(MarkerColor.Red), Count(MarkerColor.Green),
                Count(MarkerColor.Yellow), Count(MarkerColor.Black),
                CheckpointTrainInventoryProvenance.CameraObserved);
            int Count(MarkerColor color) => counts.TryGetValue(color, out var count) ? count : 0;

            GameExitStatus = "Saving the game and checking its stored copy…";
            PackAwayCheckpoint? checkpoint;
            if (coordinator.Public.Lifecycle == SessionLifecycle.PackedAway)
            {
                checkpoint = await coordinator.GetCheckpointAsync();
                if (checkpoint is not { IsSafeToPackAway: true })
                {
                    GameExitStatus = "The saved game has not passed readback verification. Try again after it is verified.";
                    return;
                }
            }
            else
            {
                var outcome = coordinator.Public.Lifecycle == SessionLifecycle.PreparingPackAway
                    ? await coordinator.ContinuePackAwayAsync()
                    : await coordinator.SaveAndPackAwayAsync(
                        string.IsNullOrWhiteSpace(Table.SaveName)
                            ? $"Game {DateTime.Now:yyyy-MM-dd HH:mm}"
                            : Table.SaveName.Trim());
                if (!outcome.SafeToPack)
                {
                    // A digital checkpoint can exist even when Save Game has not completed its
                    // mandatory photo and inventory. Quit must preserve the earlier save instead.
                    GameExitStatus = outcome.Rejection?.Message ?? outcome.Problem ??
                        "The game save is not verified yet. Try again.";
                    await RefreshAsync();
                    return;
                }
                checkpoint = outcome.Checkpoint;
            }

            if (checkpoint is null) throw new InvalidOperationException("The saved checkpoint is missing.");
            if (confirmedInventory.Blue + confirmedInventory.Red + confirmedInventory.Green +
                confirmedInventory.Yellow + confirmedInventory.Black !=
                    checkpoint.TotalTrainsOnBoard + (pendingPlacement?.TrainCount ?? 0))
                throw new InvalidOperationException("The photographed board inventory does not match the saved route inventory.");

            GameExitStatus = "Taking and checking a fresh photo of the saved board…";
            CameraPhoto photo;
            do
            {
                photo = await Camera.CaptureGameTablePhotoAsync();
                if (photo.CapturedAt >= checkpoint.CreatedAt) break;
                CryptographicOperations.ZeroMemory(photo.PngBytes);
                await Task.Delay(150);
            } while (DateTimeOffset.UtcNow - checkpoint.CreatedAt < TimeSpan.FromSeconds(5));

            try
            {
                if (photo.CapturedAt < checkpoint.CreatedAt ||
                    photo.CameraEpoch != inventoryEpoch ||
                    photo.BoardCropRevision != inventoryCrop ||
                    photo.FrameSequence < inventorySequence)
                    throw new InvalidOperationException("The camera changed after the inventory check. Keep the board still and try Save Game again.");

                // The photo is captured from the same crop after the checkpoint is frozen.
                // Recheck fresh frames after its source frame so a changed board cannot be
                // attached merely because the pre-save inventory was correct.
                GameExitStatus = "Checking the board again after its photo…";
                _gameExitInventoryVerifier = new BoardInventoryVerifier(expected, pendingRoute,
                    pendingPlacement?.OccupiedSlotMask);
                _gameExitInventoryCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _lastGameExitInventoryObservation = null;
                _gameExitMinimumFrameSequence = photo.FrameSequence;
                BoardInventoryObservation afterPhoto;
                try
                {
                    afterPhoto = await _gameExitInventoryCompletion.Task.WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (TimeoutException)
                {
                    throw new InvalidOperationException(
                        "The camera could not confirm that the board still matches the photo. " +
                        DescribeGameExitInventoryIssue(_lastGameExitInventoryObservation));
                }
                if (_gameExitInventoryCameraEpoch != photo.CameraEpoch ||
                    _gameExitInventoryCropRevision != photo.BoardCropRevision ||
                    !afterPhoto.ConfirmedByColor.OrderBy(pair => pair.Key)
                        .SequenceEqual(counts.OrderBy(pair => pair.Key)))
                    throw new InvalidOperationException("The board inventory changed during the save.");

                await _checkpointPhotoStore.SaveReferenceAsync(checkpoint, photo.PngBytes,
                    new CheckpointPhotoCapture(photo.CapturedAt, photo.CameraId,
                    photo.CameraEpoch, photo.BoardCropRevision, true),
                    CancellationToken.None, confirmedInventory, pendingPlacement);
            }
            finally { CryptographicOperations.ZeroMemory(photo.PngBytes); }

            // The photo and camera-observed inventory have both passed durable readback.
            // This checkpoint is now a completed Save Game even if menu navigation fails.
            await PersistTurnClockAsync();
            Table.SaveName = "";
            await LeaveGameForMenuAsync();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
            InvalidOperationException or UnauthorizedAccessException or CryptographicException or
            NotSupportedException)
        {
            GameExitStatus = "Save Game could not complete: " + exception.Message +
                " Keep the board in place and try again.";
        }
        finally
        {
            _gameExitInventoryVerifier = null;
            _gameExitInventoryCompletion = null;
            _lastGameExitInventoryObservation = null;
            _gameExitInventoryFrameSequence = 0;
            _gameExitInventoryCameraEpoch = 0;
            _gameExitInventoryCropRevision = 0;
            _gameExitMinimumFrameSequence = 0;
            IsGameExitSaving = false;
            SetOperationInProgress(false);
        }
    }

    [RelayCommand]
    private async Task QuitToMenuAsync()
    {
        if (!IsGameExitMenuOpen || IsGameExitSaving || _operationInProgress || Busy is not null ||
            _exitRequested || _coordinator is not { } coordinator) return;
        SetOperationInProgress(true);
        try
        {
            // Gameplay is auto-journaled, so merely returning to the menu would reload unsaved
            // moves. Keep an earlier verified checkpoint and atomically discard newer play.
            // A checkpoint created by a failed Save Game attempt is not a completed save.
            var prior = await FindLatestCompletedGameSaveAsync(coordinator.SessionId);
            if (prior is null)
                await _store.DeleteSessionAsync(coordinator.SessionId, CancellationToken.None);
            else
                await _store.RewindToVerifiedCheckpointAsync(coordinator.SessionId,
                    prior.CheckpointId, _manifest, _catalog, CancellationToken.None);
            await LeaveGameForMenuAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or NotSupportedException or GoldenTicket.Application.SessionIntegrityException)
        {
            GameExitStatus = "The game could not be discarded: " + exception.Message;
        }
        finally { SetOperationInProgress(false); }
    }

    private async Task LeaveGameForMenuAsync()
    {
        // Quit may already have rewound or deleted this save; stop without writing discarded time.
        _coordinator?.SetGameTimingRunning(false);
        _coordinator = null;
        _driver = null;
        ResetAutomaticPhysicalFlow();
        HidePrivateSeat();
        CloseSoloCardPanel();
        NeedsBoardReconciliation = false;
        BoardReconciliationAcknowledged = false;
        ShowMultiHumanPhoneSetup = false;
        _mustReload = false;
        Status = null;
        await Connection.StopCommand.ExecuteAsync(null);
        Camera.EndGameTablePreview();
        await Camera.StopCommand.ExecuteAsync(null);
        ShowGameplayScreen(Screen.Setup);
        Game.ShowWelcome();
        IsGameExitMenuOpen = false;
        GameExitStatus = null;
        NotifyHumanPresentation();
        await LoadSavedSessionsAsync();
    }
}
