using System.Security.Cryptography;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Persistence;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.Services;

internal enum SaveGameStage
{
    CheckingInventory,
    SavingCheckpoint,
    CapturingPhoto,
    CheckingAfterPhoto
}

internal enum SaveGameFailure
{
    None,
    InventoryTimedOut,
    GameChanged,
    CheckpointUnverified,
    CheckpointRejected,
    AfterPhotoTimedOut
}

internal sealed record SaveGameOutcome(SaveGameFailure Failure,
    string? Detail = null, BoardInventoryObservation? LastObservation = null)
{
    public bool Completed => Failure == SaveGameFailure.None;
    public bool RequiresRefresh => Failure == SaveGameFailure.CheckpointRejected;
}

internal sealed record SaveGameInventoryUpdate(GameTableAnalysis Analysis,
    BoardInventoryObservation Observation, bool AfterPhoto, bool AfterTimeout, bool Recovered);

/// <summary>
/// Owns the physical Save Game transaction. The coordinator is still the sole writer of game
/// state; this workflow validates the live board before that write, then binds a fresh photo
/// and post-photo inventory to its verified checkpoint. The caller supplies camera analyses and
/// receives typed observations, stages, and outcomes without sharing verifier state.
/// </summary>
internal sealed class SaveGameWorkflow
{
    private sealed record ConfirmedInventory(BoardInventoryObservation Observation,
        long FrameSequence, long CameraEpoch, long CropRevision);

    private readonly BoardManifest _manifest;
    private readonly CameraViewModel _camera;
    private readonly CheckpointPhotoStore _photoStore;
    private readonly TimeSpan _inventoryTimeout;
    private BoardInventoryVerifier? _verifier;
    private TaskCompletionSource<ConfirmedInventory>? _completion;
    private BoardInventoryObservation? _lastObservation;
    private long _minimumFrameSequence;
    private bool _observeAfterTimeout;

    public SaveGameWorkflow(BoardManifest manifest, CameraViewModel camera,
        CheckpointPhotoStore photoStore, TimeSpan inventoryTimeout)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(photoStore);
        if (inventoryTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(inventoryTimeout));
        _manifest = manifest;
        _camera = camera;
        _photoStore = photoStore;
        _inventoryTimeout = inventoryTimeout;
    }

    public event Action<SaveGameStage>? StageChanged;

    public SaveGameInventoryUpdate? Observe(GameTableAnalysis? analysis, bool upright)
    {
        if (_verifier is not { } verifier ||
            (!_observeAfterTimeout && (_completion is not { } waiting || waiting.Task.IsCompleted)) ||
            analysis is null || !upright || analysis.Board.Sequence <= _minimumFrameSequence)
            return null;

        var observation = verifier.Observe(analysis.Board, analysis.Candidates,
            analysis.CropRevision, analysis.ModelRevision);
        _lastObservation = observation;
        var afterPhoto = _minimumFrameSequence > 0;
        if (_observeAfterTimeout)
        {
            var recovered = observation.State is BoardInventoryState.Stabilizing or BoardInventoryState.Confirmed;
            if (recovered) Reset();
            return new(analysis, observation, afterPhoto, true, recovered);
        }
        if (observation.Confirmed)
            _completion!.TrySetResult(new(observation, analysis.Board.Sequence,
                analysis.Board.Epoch, analysis.CropRevision));
        return new(analysis, observation, afterPhoto, false, false);
    }

    public async Task<SaveGameOutcome> RunAsync(GameCoordinator coordinator, string? saveName,
        Func<bool> isCurrentSession, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(isCurrentSession);
        Reset();
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
            StageChanged?.Invoke(SaveGameStage.CheckingInventory);
            BeginInventoryCheck(expected, pendingRoute);
            ConfirmedInventory inventory;
            try
            {
                inventory = await _completion!.Task.WaitAsync(_inventoryTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                _observeAfterTimeout = true;
                return new(SaveGameFailure.InventoryTimedOut, LastObservation: _lastObservation);
            }
            // The view model owns menu/session identity. Recheck that one external gate before
            // the first durable write; this workflow owns the camera and checkpoint protocol.
            if (!isCurrentSession() || coordinator.Public.StateVersion != capturedVersion)
                return new(SaveGameFailure.GameChanged);

            var counts = inventory.Observation.ConfirmedByColor;
            var pendingPlacement = pending is null ? null : new CheckpointPendingPlacement(
                pending.OperationId, pending.RouteId, pending.SeatId,
                coordinator.Public.SeatOf(pending.SeatId).Color, pending.TrainCount,
                inventory.Observation.PendingSlotMask ??
                    throw new InvalidOperationException("The pending placement was not checked."));
            var confirmedInventory = new CheckpointTrainInventory(
                Count(MarkerColor.Blue), Count(MarkerColor.Red), Count(MarkerColor.Green),
                Count(MarkerColor.Yellow), Count(MarkerColor.Black),
                CheckpointTrainInventoryProvenance.CameraObserved);
            int Count(MarkerColor color) => counts.TryGetValue(color, out var count) ? count : 0;

            StageChanged?.Invoke(SaveGameStage.SavingCheckpoint);
            PackAwayCheckpoint? checkpoint;
            if (coordinator.Public.Lifecycle == SessionLifecycle.PackedAway)
            {
                checkpoint = await coordinator.GetCheckpointAsync(cancellationToken);
                if (checkpoint is not { IsSafeToPackAway: true })
                    return new(SaveGameFailure.CheckpointUnverified);
            }
            else
            {
                var outcome = coordinator.Public.Lifecycle == SessionLifecycle.PreparingPackAway
                    ? await coordinator.ContinuePackAwayAsync(cancellationToken)
                    : await coordinator.SaveAndPackAwayAsync(
                        string.IsNullOrWhiteSpace(saveName)
                            ? $"Game {DateTime.Now:yyyy-MM-dd HH:mm}"
                            : saveName.Trim(), cancellationToken);
                if (!outcome.SafeToPack)
                    return new(SaveGameFailure.CheckpointRejected,
                        outcome.Rejection?.Message ?? outcome.Problem ??
                        "The game save is not verified yet. Try again.");
                checkpoint = outcome.Checkpoint;
            }

            if (checkpoint is null) throw new InvalidOperationException("The saved checkpoint is missing.");
            if (confirmedInventory.Blue + confirmedInventory.Red + confirmedInventory.Green +
                confirmedInventory.Yellow + confirmedInventory.Black !=
                    checkpoint.TotalTrainsOnBoard + (pendingPlacement?.TrainCount ?? 0))
                throw new InvalidOperationException(
                    "The photographed board inventory does not match the saved route inventory.");

            StageChanged?.Invoke(SaveGameStage.CapturingPhoto);
            CameraPhoto photo;
            do
            {
                photo = await _camera.CaptureGameTablePhotoAsync();
                if (photo.CapturedAt >= checkpoint.CreatedAt) break;
                CryptographicOperations.ZeroMemory(photo.PngBytes);
                await Task.Delay(150, cancellationToken);
            } while (DateTimeOffset.UtcNow - checkpoint.CreatedAt < TimeSpan.FromSeconds(5));

            try
            {
                if (photo.CapturedAt < checkpoint.CreatedAt ||
                    photo.CameraEpoch != inventory.CameraEpoch ||
                    photo.BoardCropRevision != inventory.CropRevision ||
                    photo.FrameSequence < inventory.FrameSequence)
                    throw new InvalidOperationException(
                        "The camera changed after the inventory check. Keep the board still and try Save Game again.");

                // The checkpoint and photo are frozen. Require matching inventory from frames
                // newer than the photo so an intervening board change cannot be attached.
                StageChanged?.Invoke(SaveGameStage.CheckingAfterPhoto);
                BeginInventoryCheck(expected, pendingRoute, photo.FrameSequence,
                    pendingPlacement?.OccupiedSlotMask);
                ConfirmedInventory afterPhoto;
                try
                {
                    afterPhoto = await _completion!.Task.WaitAsync(_inventoryTimeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    _observeAfterTimeout = true;
                    return new(SaveGameFailure.AfterPhotoTimedOut, LastObservation: _lastObservation);
                }
                if (afterPhoto.CameraEpoch != photo.CameraEpoch ||
                    afterPhoto.CropRevision != photo.BoardCropRevision ||
                    !afterPhoto.Observation.ConfirmedByColor.OrderBy(pair => pair.Key)
                        .SequenceEqual(counts.OrderBy(pair => pair.Key)))
                    throw new InvalidOperationException("The board inventory changed during the save.");

                await _photoStore.SaveReferenceAsync(checkpoint, photo.PngBytes,
                    new CheckpointPhotoCapture(photo.CapturedAt, photo.CameraId,
                        photo.CameraEpoch, photo.BoardCropRevision, true),
                    cancellationToken, confirmedInventory, pendingPlacement);
            }
            finally { CryptographicOperations.ZeroMemory(photo.PngBytes); }

            return new(SaveGameFailure.None);
        }
        finally { FinishAttempt(); }
    }

    public void Reset()
    {
        _verifier = null;
        _completion = null;
        _lastObservation = null;
        _minimumFrameSequence = 0;
        _observeAfterTimeout = false;
    }

    private void BeginInventoryCheck(IReadOnlyList<BoardInventoryRoute> expected,
        BoardInventoryRoute? pendingRoute, long minimumFrameSequence = 0,
        int? requiredPendingMask = null)
    {
        _verifier = new BoardInventoryVerifier(expected, pendingRoute, requiredPendingMask);
        _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastObservation = null;
        _minimumFrameSequence = minimumFrameSequence;
    }

    private void FinishAttempt()
    {
        _completion = null;
        if (_observeAfterTimeout) return;
        _verifier = null;
        _lastObservation = null;
        _minimumFrameSequence = 0;
    }

    private static MarkerColor ToMarkerColor(PlayerColor color) => color switch
    {
        PlayerColor.Blue => MarkerColor.Blue,
        PlayerColor.Red => MarkerColor.Red,
        PlayerColor.Green => MarkerColor.Green,
        PlayerColor.Yellow => MarkerColor.Yellow,
        PlayerColor.Black => MarkerColor.Black,
        _ => throw new ArgumentOutOfRangeException(nameof(color), color, null)
    };
}
