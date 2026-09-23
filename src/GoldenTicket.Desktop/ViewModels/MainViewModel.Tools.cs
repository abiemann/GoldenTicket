using CommunityToolkit.Mvvm.Input;
using GoldenTicket.Application;
using GoldenTicket.CompanionHost;
using GoldenTicket.Desktop.Services;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Persistence;
using GoldenTicket.Vision;
using System.ComponentModel;
using System.Security.Cryptography;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private Screen _gameScreen = Screen.Setup;
    private bool _handlingRemoteCommand;
    private GoldenTicket.Domain.Projections.PublicView? _remoteCommandStartingView;
    private bool _systemAvailable = true;
    private bool _toolsDisposed;
    private string? _loadedPhotoCheckpoint;

    public CameraViewModel Camera { get; private set; } = null!;
    public ConnectionViewModel Connection { get; private set; } = null!;
    public CheckpointPhotoViewModel CheckpointPhoto { get; private set; } = null!;
    internal ICompanionGameBridge CompanionBridge { get; private set; } = null!;

    private void InitializeTools(CameraViewModel? camera)
    {
        var settingsRoot = (_store as SqliteSessionStore)?.RootDirectory;
        Camera = camera ?? new CameraViewModel(settingsRoot is null ? null
            : System.IO.Path.Combine(settingsRoot, "camera-processing.json"),
            cameraSettingsPath: settingsRoot is null ? null : System.IO.Path.Combine(settingsRoot, "camera-device.json"));
        var inner = new CoordinatorCompanionBridge(() => _coordinator,
            async _ =>
            {
                if (_remoteCommandStartingView is { } before) BeginCardTurnBoardCheck(before);
                await PumpAsync();
            }, () => CanCompanionControl, resultImage: CurrentFinalStandingsImage);
        CompanionBridge = new DesktopCompanionBridge(inner,
            () => System.Windows.Application.Current?.Dispatcher,
            BeginRemoteCommand, EndRemoteCommand, () => { if (CanCompanionControl) HideLaptopPrivateViewOnly(); }, RequireReload,
            CurrentCompanionBoardInteraction, InterceptCompanionCommandAsync, CurrentCompanionGuidance,
            CurrentCompanionBoardMap, ReadCompanionBoardImage);
        Connection = new ConnectionViewModel(CompanionBridge);
        Connection.PropertyChanged += ConnectionPresentationChanged;
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(CanRevealPrivateSeat) or nameof(IsPrivateVisible) or
                nameof(ShowMultiHumanPhoneSetup) or nameof(GameplayScreen) or nameof(CanConnectPhone))
                OnPropertyChanged(nameof(ShowPracticalHandoff));
            if (args.PropertyName == nameof(ShowPracticalHandoff))
                TakePracticalTurnCommand.NotifyCanExecuteChanged();
        };
        CheckpointPhoto = new CheckpointPhotoViewModel(_checkpointPhotoStore,
            CaptureCheckpointPhotoAsync, Camera, ShowCameraCommand) { CaptureAllowed = false };
    }

    private async Task<CheckpointPhotoCaptureInput> CaptureCheckpointPhotoAsync(CancellationToken token)
    {
        var coordinator = _coordinator;
        if (coordinator?.Public.Lifecycle is not (SessionLifecycle.PackedAway or SessionLifecycle.Rebuilding) ||
            await coordinator.GetCheckpointAsync(token) is not { IsSafeToPackAway: true } checkpoint)
            throw new InvalidOperationException("Save and pack the game before attaching its reference photo.");

        var pending = checkpoint.SuspendedTurnPhase == TurnPhase.AwaitingPhysicalPlacement
            ? coordinator.Public.PendingClaim ?? throw new InvalidOperationException(
                "The unfinished route is missing from the saved game. Reload before capturing its photo.")
            : null;
        if (pending is not null && pending.OperationId != checkpoint.PendingOperationId)
            throw new InvalidOperationException("The unfinished route changed. Reload before capturing its photo.");

        BoardInventoryObservation? before = null;
        GameTableAnalysis? beforeAnalysis = null;
        if (pending is not null)
        {
            (before, beforeAnalysis) = await ConfirmCheckpointPhotoInventoryAsync(coordinator,
                checkpoint, pending, 0, null, token);
        }

        // For unfinished placements the game-table crop is the one used for the exact-slot
        // detections. Its capture API verifies epoch, registration and model revisions.
        var photo = pending is null
            ? await Camera.CapturePhotoAsync(token)
            : await Camera.CaptureGameTablePhotoAsync(token);
        try
        {
            if (coordinator != _coordinator || coordinator.Public.Checkpoint?.CheckpointId != checkpoint.CheckpointId ||
                coordinator.Public.Lifecycle is not (SessionLifecycle.PackedAway or SessionLifecycle.Rebuilding))
                throw new InvalidOperationException("The selected checkpoint changed while capturing. Check the board and try again.");

            CheckpointTrainInventory? observedInventory = null;
            CheckpointPendingPlacement? pendingPlacement = null;
            if (pending is not null && before is not null && beforeAnalysis is not null)
            {
                if (photo.CameraEpoch != beforeAnalysis.Board.Epoch ||
                    photo.BoardCropRevision != beforeAnalysis.CropRevision ||
                    photo.FrameSequence < beforeAnalysis.Board.Sequence)
                    throw new InvalidOperationException("The camera crop changed after checking the unfinished placement. Try the photo again.");
                var (after, afterAnalysis) = await ConfirmCheckpointPhotoInventoryAsync(coordinator,
                    checkpoint, pending, photo.FrameSequence, before.PendingSlotMask, token);
                if (afterAnalysis.Board.Epoch != photo.CameraEpoch ||
                    afterAnalysis.CropRevision != photo.BoardCropRevision ||
                    beforeAnalysis.ModelRevision != afterAnalysis.ModelRevision ||
                    !before.ConfirmedByColor.OrderBy(pair => pair.Key)
                        .SequenceEqual(after.ConfirmedByColor.OrderBy(pair => pair.Key)))
                    throw new InvalidOperationException("The board changed while capturing its reference photo. Try again.");

                var counts = after.ConfirmedByColor;
                int Count(MarkerColor color) => counts.TryGetValue(color, out var value) ? value : 0;
                observedInventory = new CheckpointTrainInventory(
                    Count(MarkerColor.Blue), Count(MarkerColor.Red), Count(MarkerColor.Green),
                    Count(MarkerColor.Yellow), Count(MarkerColor.Black),
                    CheckpointTrainInventoryProvenance.CameraObserved);
                pendingPlacement = new CheckpointPendingPlacement(pending.OperationId, pending.RouteId,
                    pending.SeatId, coordinator.Public.SeatOf(pending.SeatId).Color, pending.TrainCount,
                    after.PendingSlotMask ?? throw new InvalidOperationException(
                        "The unfinished route's exact train spaces could not be checked."));
            }
            return new CheckpointPhotoCaptureInput(photo.PngBytes,
                new CheckpointPhotoCapture(photo.CapturedAt, photo.CameraId,
                    photo.CameraEpoch, photo.BoardCropRevision, false), observedInventory, pendingPlacement);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(photo.PngBytes);
            throw;
        }
    }

    private async Task<(BoardInventoryObservation Observation, GameTableAnalysis Analysis)>
        ConfirmCheckpointPhotoInventoryAsync(GameCoordinator coordinator, PackAwayCheckpoint checkpoint,
            PublicPendingClaim pending, long minimumSequence, int? requiredPendingMask,
            CancellationToken token)
    {
        var routes = checkpoint.PhysicalTarget.Select(route => new BoardInventoryRoute(route.RouteId.Value,
            ToMarkerColor(coordinator.Public.SeatOf(route.SeatId).Color), route.Length)).ToArray();
        var pendingRoute = new BoardInventoryRoute(pending.RouteId.Value,
            ToMarkerColor(coordinator.Public.SeatOf(pending.SeatId).Color), pending.TrainCount);
        var verifier = new BoardInventoryVerifier(routes, pendingRoute, requiredPendingMask);
        var completion = new TaskCompletionSource<(BoardInventoryObservation, GameTableAnalysis)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        BoardInventoryObservation? last = null;
        void Observe(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(CameraViewModel.GameTableAnalysis) ||
                Camera.GameTableAnalysis is not { } analysis || !Camera.IsGameTablePreviewUpright ||
                analysis.Board.Sequence <= minimumSequence ||
                coordinator != _coordinator || coordinator.Public.Checkpoint?.CheckpointId != checkpoint.CheckpointId)
                return;
            try
            {
                last = verifier.Observe(analysis.Board, analysis.Candidates,
                    analysis.CropRevision, analysis.ModelRevision);
                if (last.State == BoardInventoryState.Unsupported)
                    completion.TrySetException(new InvalidOperationException(
                        "The camera cannot verify this unfinished route's exact train spaces."));
                else if (last.Confirmed)
                    completion.TrySetResult((last, analysis));
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }

        Camera.PropertyChanged += Observe;
        try
        {
            Observe(Camera, new PropertyChangedEventArgs(nameof(CameraViewModel.GameTableAnalysis)));
            try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), token); }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("The camera could not verify the unfinished placement. " +
                    DescribeGameExitInventoryIssue(last));
            }
        }
        finally { Camera.PropertyChanged -= Observe; }
    }

    private bool CanCompanionControl => CanConnectPhone && Connection.UseQuickPlay && !_toolsDisposed && !_exitRequested &&
        !IsGameInputPaused && _systemAvailable && !_mustReload && _scoreMarkerStep is null &&
        !IsCheckingBoardBeforeNextTurn &&
        (!_operationInProgress || _handlingRemoteCommand) && !NeedsBoardReconciliation &&
        IsGameplayScreenActive(Screen.Table) && _coordinator is { StorageFaulted: false };

    private bool BeginRemoteCommand()
    {
        if (_operationInProgress || !CanCompanionControl) return false;
        _remoteCommandStartingView = _coordinator?.Public;
        _handlingRemoteCommand = true;
        SetOperationInProgress(true);
        HideLaptopPrivateViewOnly();
        return true;
    }

    private void EndRemoteCommand()
    {
        _handlingRemoteCommand = false;
        _remoteCommandStartingView = null;
        SetOperationInProgress(false);
        if (_coordinator?.StorageFaulted == true) RequireReload();
    }

    private void HideLaptopPrivateViewOnly()
    {
        _revealGeneration++;
        PrivateSeat = null;
        CloseSoloCardPanel();
        ClearPracticalTurn();
    }

    [RelayCommand]
    private void ShowGame()
    {
        if (_operationInProgress || _exitRequested || CheckpointPhoto.IsBusy) return;
        Screen = _gameScreen;
    }

    private bool NavigateToTool(Screen screen)
    {
        if (_operationInProgress || _exitRequested || CheckpointPhoto.IsBusy) return false;
        if (Screen is Screen.Setup or Screen.Table or Screen.Rebuild or Screen.FinalScore)
            _gameScreen = Screen;
        Screen = screen;
        return true;
    }

    [RelayCommand]
    private async Task ShowCameraAsync()
    {
        if (!NavigateToTool(Screen.Camera)) return;
        await Camera.RefreshDevicesCommand.ExecuteAsync(null);
    }

    [RelayCommand(CanExecute = nameof(CanConnectPhone))]
    private void ShowConnection()
    {
        if (!CanConnectPhone) return;
        if (NavigateToTool(Screen.Connection)) Connection.RefreshInterfaces();
    }

    [RelayCommand]
    private async Task ShowCheckpointPhotoAsync()
    {
        if (!NavigateToTool(Screen.CheckpointPhoto)) return;
        _loadedPhotoCheckpoint = null;
        await RefreshCheckpointPhotoAsync();
    }

    private async Task RefreshCheckpointPhotoAsync()
    {
        // Historical checkpoints on a resumed game are reference-only; the photo view disables
        // capture unless the game is still frozen in PackedAway/Rebuilding (capture delegate).
        var coordinator = _coordinator;
        CheckpointPhoto.CaptureAllowed = coordinator?.Public.Lifecycle is SessionLifecycle.PackedAway or SessionLifecycle.Rebuilding;
        var checkpoint = coordinator is null ? null : await coordinator.GetCheckpointAsync();
        if (coordinator != _coordinator) return;
        var key = checkpoint is null ? null : $"{checkpoint.SessionId.Value}/{checkpoint.CheckpointId.Value}/{checkpoint.Status}";
        if (_loadedPhotoCheckpoint == key && key is not null && CheckpointPhoto.HasPhoto)
        {
            Camera.SetGameTableReference(CheckpointPhoto.PhotoImage);
            return;
        }
        _loadedPhotoCheckpoint = key;
        await CheckpointPhoto.LoadCheckpointAsync(checkpoint);
        Camera.SetGameTableReference(CheckpointPhoto.PhotoImage);
    }

    public async Task SetSystemAvailableAsync(bool available)
    {
        _systemAvailable = available;
        if (!available) PauseCardBoardCheck();
        UpdateTurnClock();
        Camera.SetGameTableCameraRecoveryEnabled(available);
        HidePrivateSeat();
        OnPropertyChanged(nameof(CanRevealPrivateSeat));
        if (!available) await Camera.StopCommand.ExecuteAsync(null);
    }

    public void PauseAfterUnhandledFault()
    {
        _systemAvailable = false;
        RequireReload();
    }

    public async Task DisposeToolsAsync()
    {
        if (_toolsDisposed) return;
        _toolsDisposed = true;
        DisposeCompanionUpdates();
        ResetCompanionMap();
        ResetFinalStandingsSharing();
        PauseCardBoardCheck();
        _turnClockTimer?.Stop();
        UpdateTurnClock();
        await PersistTurnClockAsync();
        HidePrivateSeat();
        Connection.PropertyChanged -= ConnectionPresentationChanged;
        try { await Connection.DisposeAsync(); }
        finally { await Camera.DisposeAsync(); }
    }
}
