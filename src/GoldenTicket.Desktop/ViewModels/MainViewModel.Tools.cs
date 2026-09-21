using CommunityToolkit.Mvvm.Input;
using GoldenTicket.CompanionHost;
using GoldenTicket.Desktop.Services;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Model;
using GoldenTicket.Persistence;
using System.Security.Cryptography;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private Screen _gameScreen = Screen.Setup;
    private bool _handlingRemoteCommand;
    private bool _systemAvailable = true;
    private bool _toolsDisposed;
    private string? _loadedPhotoCheckpoint;

    public CameraViewModel Camera { get; private set; } = null!;
    public ConnectionViewModel Connection { get; private set; } = null!;
    public CheckpointPhotoViewModel CheckpointPhoto { get; private set; } = null!;
    internal ICompanionGameBridge CompanionBridge { get; private set; } = null!;

    private void InitializeTools(CameraViewModel? camera)
    {
        Camera = camera ?? new CameraViewModel(_store is SqliteSessionStore localStore
            ? System.IO.Path.Combine(localStore.RootDirectory, "camera-processing.json") : null);
        var inner = new CoordinatorCompanionBridge(() => _coordinator,
            async _ => await PumpAsync(), () => CanCompanionControl, resultImage: CurrentFinalStandingsImage);
        CompanionBridge = new DesktopCompanionBridge(inner,
            () => System.Windows.Application.Current?.Dispatcher,
            BeginRemoteCommand, EndRemoteCommand, () => { if (CanCompanionControl) HideLaptopPrivateViewOnly(); }, RequireReload,
            CurrentCompanionBoardInteraction, InterceptCompanionCommandAsync);
        Connection = new ConnectionViewModel(CompanionBridge);
        Connection.PropertyChanged += ConnectionPresentationChanged;
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(CanRevealPrivateSeat) or nameof(IsPrivateVisible) or
                nameof(ShowMultiHumanPhoneSetup) or nameof(GameplayScreen) or nameof(CanConnectPhone))
                OnPropertyChanged(nameof(ShowPracticalHandoff));
        };
        CheckpointPhoto = new CheckpointPhotoViewModel(_checkpointPhotoStore, async token =>
        {
            var coordinator = _coordinator;
            var checkpointId = coordinator?.Public.Checkpoint?.CheckpointId;
            if (coordinator?.Public.Lifecycle is not
                (SessionLifecycle.PackedAway or SessionLifecycle.Rebuilding))
                throw new InvalidOperationException("Save and pack the game before attaching its reference photo.");
            var photo = await Camera.CapturePhotoAsync(token);
            if (coordinator != _coordinator || coordinator.Public.Checkpoint?.CheckpointId != checkpointId ||
                coordinator.Public.Lifecycle is not (SessionLifecycle.PackedAway or SessionLifecycle.Rebuilding))
            {
                CryptographicOperations.ZeroMemory(photo.PngBytes);
                throw new InvalidOperationException("The selected checkpoint changed while capturing. Check the board and try again.");
            }
            return new CheckpointPhotoCaptureInput(photo.PngBytes,
                new CheckpointPhotoCapture(photo.CapturedAt, photo.CameraId,
                    photo.CameraEpoch, photo.BoardCropRevision, false));
        }, Camera, ShowCameraCommand) { CaptureAllowed = false };
    }

    private bool CanCompanionControl => CanConnectPhone && Connection.UseQuickPlay && !_toolsDisposed && !_exitRequested &&
        !IsGameInputPaused && _systemAvailable && !_mustReload && _scoreMarkerStep is null &&
        (!_operationInProgress || _handlingRemoteCommand) && !NeedsBoardReconciliation &&
        IsGameplayScreenActive(Screen.Table) && _coordinator is { StorageFaulted: false };

    private bool BeginRemoteCommand()
    {
        if (_operationInProgress || !CanCompanionControl) return false;
        _handlingRemoteCommand = true;
        SetOperationInProgress(true);
        HideLaptopPrivateViewOnly();
        return true;
    }

    private void EndRemoteCommand()
    {
        _handlingRemoteCommand = false;
        SetOperationInProgress(false);
        if (_coordinator?.StorageFaulted == true) RequireReload();
    }

    private void HideLaptopPrivateViewOnly()
    {
        _revealGeneration++;
        PrivateSeat = null;
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
        if (!available) _cardBoardCheck?.Completion.TrySetResult(false);
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
        ResetFinalStandingsSharing();
        _cardBoardCheck?.Completion.TrySetResult(false);
        _turnClockTimer?.Stop();
        UpdateTurnClock();
        await PersistTurnClockAsync();
        HidePrivateSeat();
        Connection.PropertyChanged -= ConnectionPresentationChanged;
        try { await Connection.DisposeAsync(); }
        finally { await Camera.DisposeAsync(); }
    }
}
