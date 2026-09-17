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
    private CheckpointId? _incompleteGameExitSaveCheckpointId;

    [ObservableProperty] private bool _isGameExitMenuOpen;
    [ObservableProperty] private bool _isGameExitSaving;
    [ObservableProperty] private string _gameExitInventorySummary = "";
    [ObservableProperty] private string? _gameExitStatus;

    public void OpenGameExitMenu()
    {
        if (IsGameExitMenuOpen || !Game.IsPlaying || !_gameLayerVisible || _exitRequested ||
            _toolsDisposed || _coordinator is null) return;
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
        return "Expected from confirmed routes: " + string.Join(" · ", lines);
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
        if (observation.Confirmed)
        {
            _gameExitInventoryFrameSequence = analysis.Board.Sequence;
            _gameExitInventoryCameraEpoch = analysis.Board.Epoch;
            _gameExitInventoryCropRevision = analysis.CropRevision;
            completion.TrySetResult(observation);
        }
    }

    [RelayCommand]
    private async Task SaveGameToMenuAsync()
    {
        if (!IsGameExitMenuOpen || IsGameExitSaving || _operationInProgress ||
            _exitRequested || _mustReload || _coordinator is not { StorageFaulted: false } coordinator)
            return;
        if (Busy is not null || _scoreMarkerStep is not null || coordinator.Public.PendingClaim is not null)
        {
            GameExitStatus = "Finish the current train placement and scoring-marker move before saving.";
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
        var photoStore = new CheckpointPhotoStore((_store as SqliteSessionStore)?.RootDirectory ??
            SqliteSessionStore.DefaultRoot);
        try
        {
            var expected = coordinator.Public.RouteOwners
                .Select(route => new BoardInventoryRoute(route.Key.Value,
                    ToMarkerColor(coordinator.Public.SeatOf(route.Value).Color),
                    _manifest.Route(route.Key).Length))
                .ToArray();
            _gameExitInventoryVerifier = new BoardInventoryVerifier(expected);
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
                var issue = _lastGameExitInventoryObservation is { } last
                    ? $" Last reading: {last.State}" +
                      (last.RouteId is null ? "." : $" on {_manifest.Describe(new RouteId(last.RouteId))}.")
                    : "";
                GameExitStatus = "The camera could not verify every train and color on the board. " +
                    "Keep the board still, check the crop and lighting, then try Save Game again." + issue;
                return;
            }
            if (!ReferenceEquals(coordinator, _coordinator) ||
                coordinator.Public.StateVersion != capturedVersion || !IsGameExitMenuOpen)
            {
                GameExitStatus = "The game changed while checking the board. Try Save Game again.";
                return;
            }

            var counts = inventory.ConfirmedByColor;
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
                    _incompleteGameExitSaveCheckpointId = coordinator.Public.Checkpoint?.CheckpointId;
                    GameExitStatus = outcome.Rejection?.Message ?? outcome.Problem ??
                        "The game save is not verified yet. Try again.";
                    await RefreshAsync();
                    return;
                }
                checkpoint = outcome.Checkpoint;
                _incompleteGameExitSaveCheckpointId = checkpoint?.CheckpointId;
            }

            if (checkpoint is null) throw new InvalidOperationException("The saved checkpoint is missing.");
            if (confirmedInventory.Blue + confirmedInventory.Red + confirmedInventory.Green +
                confirmedInventory.Yellow + confirmedInventory.Black != checkpoint.TotalTrainsOnBoard)
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
                _gameExitInventoryVerifier = new BoardInventoryVerifier(expected);
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
                        "The camera could not confirm that the board still matches the photo.");
                }
                if (_gameExitInventoryCameraEpoch != photo.CameraEpoch ||
                    _gameExitInventoryCropRevision != photo.BoardCropRevision ||
                    !afterPhoto.ConfirmedByColor.OrderBy(pair => pair.Key)
                        .SequenceEqual(counts.OrderBy(pair => pair.Key)))
                    throw new InvalidOperationException("The board inventory changed during the save.");

                await photoStore.SaveReferenceAsync(checkpoint, photo.PngBytes,
                    new CheckpointPhotoCapture(photo.CapturedAt, photo.CameraId,
                        photo.CameraEpoch, photo.BoardCropRevision, true),
                    CancellationToken.None, confirmedInventory);
            }
            finally { CryptographicOperations.ZeroMemory(photo.PngBytes); }

            // The photo and camera-observed inventory have both passed durable readback.
            // This checkpoint is now a completed Save Game even if menu navigation fails.
            _incompleteGameExitSaveCheckpointId = null;
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
            var prior = await _store.FindLatestVerifiedCheckpointAsync(coordinator.SessionId,
                _manifest, _catalog, _incompleteGameExitSaveCheckpointId, CancellationToken.None);
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
        _coordinator = null;
        _driver = null;
        ResetAutomaticPhysicalFlow();
        HidePrivateSeat();
        CloseSoloCardPanel();
        NeedsBoardReconciliation = false;
        BoardReconciliationAcknowledged = false;
        ShowMultiHumanPhoneSetup = false;
        _mustReload = false;
        _incompleteGameExitSaveCheckpointId = null;
        Status = null;
        await Connection.StopCommand.ExecuteAsync(null);
        await Camera.StopCommand.ExecuteAsync(null);
        ShowGameplayScreen(Screen.Setup);
        Game.ShowWelcome();
        IsGameExitMenuOpen = false;
        GameExitStatus = null;
        NotifyHumanPresentation();
        await LoadSavedSessionsAsync();
    }
}
