using System.IO;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoldenTicket.Desktop.Services;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Model;
using GoldenTicket.Persistence;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private SaveGameWorkflow? _saveGameWorkflow;
    internal TimeSpan GameSaveInventoryTimeout { get; set; } = TimeSpan.FromSeconds(15);

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
        if (!(IsSingleHumanGame && ShowSoloOpeningTicketsOnBoard)) HidePrivateSeat();
        ClearGameExitInventoryCheck();
        GameExitInventorySummary = DescribeExpectedTrainInventory();
        GameExitStatus = null;
        IsGameExitMenuOpen = true;
        OnPropertyChanged(nameof(CanRevealPrivateSeat));
    }

    public void CloseGameExitMenu()
    {
        if (!IsGameExitMenuOpen || IsGameExitSaving) return;
        IsGameExitMenuOpen = false;
        ClearGameExitInventoryCheck();
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
        if (!IsGameExitMenuOpen || _saveGameWorkflow?.Observe(Camera.GameTableAnalysis,
            Camera.IsGameTablePreviewUpright) is not { } update) return;

        var analysis = update.Analysis;
        var observation = update.Observation;
        UpdateGameExitEvidence(analysis, observation);
        BoardInteractionLog.Write("save.board-check.frame", new
        {
            analysis.Board.Sequence, analysis.Board.Epoch, analysis.Board.CapturedAt,
            analysis.CropRevision, analysis.ModelRevision,
            state = observation.State.ToString(),
            observation.RouteId, observation.UnexpectedTrains, observation.UnexpectedDetections,
            afterPhoto = update.AfterPhoto
        });
        if (update.AfterTimeout)
        {
            if (update.Recovered)
            {
                ClearGameExitInventoryCheck();
                GameExitStatus = "The board matches again. Select Save Game to save it.";
            }
            else if (observation.State != BoardInventoryState.WaitingForFreshFrame)
                GameExitStatus = DescribeGameExitInventoryIssue(observation) + " Then try Save Game again.";
            return;
        }
        if (!observation.Confirmed)
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
            BoardInventoryState.UnexpectedTrain when observation.UnexpectedDetections.Count > 0 =>
                $"The camera flagged {observation.UnexpectedDetections.Count} possible extra or misplaced " +
                $"train{(observation.UnexpectedDetections.Count == 1 ? "" : "s")}. " +
                "Check the numbered boxes in the board image; a highlighted detection may be mistaken.",
            BoardInventoryState.UnexpectedTrain =>
                "The camera returned a train detection with no usable position. " +
                "Check the camera crop, then retry the board check.",
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
        if (BoardFirstProposal is { } proposal)
        {
            GameExitStatus = $"Your detected route, {proposal.RouteText}, has not been paid for. " +
                "Return to Game to finish payment, or remove those trains and cancel the route before saving.";
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
        ClearGameExitInventoryCheck();
        SetOperationInProgress(true);
        GameExitStatus = "Checking every train on the board by route and color…";
        try
        {
            var workflow = new SaveGameWorkflow(_manifest, Camera, _checkpointPhotoStore,
                GameSaveInventoryTimeout);
            workflow.StageChanged += OnSaveGameStageChanged;
            _saveGameWorkflow = workflow;
            var save = workflow.RunAsync(coordinator, Table.SaveName,
                () => ReferenceEquals(coordinator, _coordinator) && IsGameExitMenuOpen);
            ObserveGameExitInventory();
            var outcome = await save;
            switch (outcome.Failure)
            {
                case SaveGameFailure.InventoryTimedOut:
                    GameExitStatus = DescribeGameExitInventoryIssue(outcome.LastObservation) +
                        " Then try Save Game again.";
                    return;
                case SaveGameFailure.GameChanged:
                    GameExitStatus = "The game changed while checking the board. Try Save Game again.";
                    return;
                case SaveGameFailure.CheckpointUnverified:
                    GameExitStatus = "The saved game has not passed readback verification. Try again after it is verified.";
                    return;
                case SaveGameFailure.CheckpointRejected:
                    GameExitStatus = outcome.Detail;
                    await RefreshAsync();
                    return;
                case SaveGameFailure.AfterPhotoTimedOut:
                    GameExitStatus = "Save Game could not complete: The camera could not confirm that the board still matches the photo. " +
                        DescribeGameExitInventoryIssue(outcome.LastObservation) +
                        " Keep the board in place and try again.";
                    return;
            }

            if (!ReferenceEquals(coordinator, _coordinator) || !IsGameExitMenuOpen)
            {
                GameExitStatus = "The game changed while checking the board. Try Save Game again.";
                return;
            }
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
            IsGameExitSaving = false;
            SetOperationInProgress(false);
        }
    }

    private void OnSaveGameStageChanged(SaveGameStage stage) => GameExitStatus = stage switch
    {
        SaveGameStage.CheckingInventory => "Checking every train on the board by route and color…",
        SaveGameStage.SavingCheckpoint => "Saving the game and checking its stored copy…",
        SaveGameStage.CapturingPhoto => "Taking and checking a fresh photo of the saved board…",
        SaveGameStage.CheckingAfterPhoto => "Checking the board again after its photo…",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
    };

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

    public bool CanLeaveFinalStandings => !_toolsDisposed && !_exitRequested && !_operationInProgress &&
        !IsGameExitSaving && Busy is null && _scoreMarkerStep is null &&
        _coordinator?.Public.Lifecycle == SessionLifecycle.Finished && IsGameplayScreenActive(Screen.FinalScore);

    [RelayCommand(CanExecute = nameof(CanLeaveFinalStandings))]
    private async Task BackToMenuAsync()
    {
        if (!CanLeaveFinalStandings) return;
        SetOperationInProgress(true);
        try
        {
            // Completed results are already journaled. Returning to the menu must preserve
            // them, unlike the in-game Quit action that intentionally discards unsaved play.
            await PersistTurnClockAsync();
            await LeaveGameForMenuAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            FinalStandingsShareStatus = "Could not return to the menu. Please try again.";
            BoardInteractionLog.Write("final-standings.menu-failed", new { errorType = exception.GetType().Name });
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
        ClearGameExitInventoryCheck();
        GameExitStatus = null;
        NotifyHumanPresentation();
        await LoadSavedSessionsAsync();
    }
}
