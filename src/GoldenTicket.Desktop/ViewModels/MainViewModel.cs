using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoldenTicket.AI;
using GoldenTicket.Application;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Persistence;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

public enum Screen
{
    Setup,
    Table,

    /// <summary>Guided reconstruction against a saved checkpoint's target (DESIGN 19.8).</summary>
    Rebuild,
    FinalScore,
    Camera,
    Connection,
    CheckpointPhoto,
}

public enum DisplayMode { Resizable, FullScreen }

/// <summary>
/// Composition root and navigation. It owns the coordinator, drives computer seats between human
/// actions, and is the only place that decides when a private view may be revealed.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly BoardManifest _manifest;
    private readonly CardCatalog _catalog;
    private readonly GameRules _rules;
    private readonly ISessionStore _store;
    private readonly CheckpointPhotoStore _checkpointPhotoStore;

    private GameCoordinator? _coordinator;
    private ComputerSeatDriver? _driver;
    private bool _operationInProgress;
    private bool _windowActive = true;
    private bool _mustReload;
    private bool _gameLayerVisible;
    private long _revealGeneration;
    private DateTime _lastDestinationAlignmentUtc = DateTime.MinValue;

    public MainViewModel()
        : this(ManifestLoader.LoadClassicUs(), SqliteSessionStore.CreateDefault())
    {
    }

    public MainViewModel(BoardManifest manifest, ISessionStore store, CheckpointPhotoStore? photoStore = null,
        TimeProvider? timeProvider = null, CameraViewModel? camera = null)
    {
        _turnTimeProvider = timeProvider ?? TimeProvider.System;
        _manifest = manifest;
        _catalog = CardCatalog.FromManifest(manifest);
        _rules = new GameRules(manifest, _catalog);
        _store = store;
        _checkpointPhotoStore = photoStore ?? new CheckpointPhotoStore(
            (store as SqliteSessionStore)?.RootDirectory ?? SqliteSessionStore.DefaultRoot);
        _presentationSettingsPath = store is SqliteSessionStore localStore
            ? Path.Combine(localStore.RootDirectory, "presentation-settings.json") : null;
        _showDestinationsWhenViewingTrainCards = Services.PresentationPreferences
            .LoadShowDestinationsWithTrainCards(_presentationSettingsPath);
        _displayMode = Services.PresentationPreferences.LoadDisplayMode(_presentationSettingsPath);
        SavedWindowPresentation = Services.PresentationPreferences.LoadWindowPresentation(_presentationSettingsPath);

        Setup = new SetupViewModel(manifest);
        Table = new TableViewModel(manifest);
        InitializeTools(camera);
        Game = new GameScreenViewModel(this);
        InitializeManualReload();
        InitializeTurnClock();
        Camera.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CameraViewModel.GameTablePreview))
                AlignDestinationMarkers();
            if (args.PropertyName == nameof(CameraViewModel.GameTableAnalysis))
            {
                ObserveSavedBoardRestore();
                ObserveGameTableAnalysis();
                ObserveCardActionBoard();
                ObserveGameExitInventory();
            }
        };
        Setup.PropertyChanged += (_, args) =>
        {
            if (_coordinator is null && args.PropertyName == nameof(SetupViewModel.HumanSeatCount)) NotifyHumanPresentation();
            if (args.PropertyName == nameof(SetupViewModel.SelectedSavedSession))
            {
                ClearEarlierSaveRecovery();
                ClearSavedMatchDeletion();
                NotifySavedMatchCommands();
                ResumeMatchCommand.NotifyCanExecuteChanged();
            }
        };
        InitializeCompanionUpdates();
    }

    public SetupViewModel Setup { get; }

    internal BoardManifest Manifest => _manifest;

    public TableViewModel Table { get; }

    public ObservableCollection<string> EngineeringHistory { get; } = [];

    public GameScreenViewModel Game { get; }

    public ObservableCollection<FinalScoreRow> FinalScores { get; } = [];

    [ObservableProperty] private Screen _screen = Screen.Setup;
    [ObservableProperty] private DisplayMode _displayMode = DisplayMode.Resizable;
    [ObservableProperty] private bool _showMultiHumanPhoneSetup;

    /// <summary>
    /// The revealed private view, or null for the privacy curtain. DESIGN 4.7: hiding discards this
    /// object; the hand is never left in the tree with an opacity of zero.
    /// </summary>
    [ObservableProperty] private PrivateSeatViewModel? _privateSeat;

    [ObservableProperty] private string? _status;

    [ObservableProperty] private string? _busy;

    [ObservableProperty] private string _finalSummary = "";
    [ObservableProperty] private bool _needsBoardReconciliation;
    [ObservableProperty] private bool _boardReconciliationAcknowledged;

    public bool IsPrivateVisible => PrivateSeat is not null;
    public bool ShowSoloOpeningTicketsOnBoard => CanUseGameTableControls &&
        PrivateSeat is { IsSetupOffer: true, MustChooseTickets: true };
    private bool ShowSoloKeptDestinationsOnBoard => CanUseGameTableControls &&
        (ShowSoloDestinations || (ShowSoloTrainCards && ShowDestinationsWhenViewingTrainCards));
    public bool ShowDestinationMarkersOnBoard => ShowSoloOpeningTicketsOnBoard || ShowSoloTicketOffer ||
        ShowSoloKeptDestinationsOnBoard && SoloDestinationMarkers.Count > 0;
    public IReadOnlyList<DestinationMarkerRow> BoardDestinationMarkers =>
        ShowSoloOpeningTicketsOnBoard ? PrivateSeat?.DestinationMarkers ?? [] :
        ShowSoloTicketOffer ? SoloTicketOfferMarkers :
        ShowSoloKeptDestinationsOnBoard ? SoloDestinationMarkers : [];
    public IReadOnlyList<DestinationLineRow> BoardDestinationLines =>
        ShowSoloOpeningTicketsOnBoard ? PrivateSeat?.DestinationLines ?? [] :
        ShowSoloTicketOffer ? SoloTicketOfferLines :
        ShowSoloKeptDestinationsOnBoard ? SoloDestinationLines : [];
    public double GameTableBoardTop => ShowSoloOpeningTicketsOnBoard || ShowSoloTicketOffer ? 120 : 190;
    public bool ShowDrawPanels => !ShowSoloOpeningTicketsOnBoard && !ShowSoloTicketOffer;

    private int HumanSeatCount => _coordinator?.Public.Seats.Count(seat => seat.Kind == SeatKind.Human) ?? Setup.HumanSeatCount;
    public bool IsSingleHumanGame => HumanSeatCount == 1;
    public bool IsSoloHumanTurn => _coordinator?.Public is { Lifecycle: SessionLifecycle.Active } view &&
        IsSingleHumanGame && view.SeatOf(view.ActiveSeatId).Kind == SeatKind.Human;
    public bool CanConnectPhone => HumanSeatCount > 1;
    public Screen GameplayScreen => _gameScreen;
    private bool IsGameplayScreenActive(Screen screen) =>
        _gameLayerVisible ? _gameScreen == screen : Screen == screen;
    public bool CanResumeMatch => !_operationInProgress && !_exitRequested && IsGameplayScreenActive(Screen.Setup) &&
        Setup.SelectedSavedSession is not null;

    public void SetGameLayerVisible(bool visible)
    {
        if (_gameLayerVisible == visible) return;
        _gameLayerVisible = visible;
        ClearSavedMatchDeletion();
        NotifySavedMatchCommands();
        UpdateTurnClock();
        HidePrivateSeat();
        OnPropertyChanged(nameof(CanRevealPrivateSeat));
        ResumeMatchCommand.NotifyCanExecuteChanged();
    }

    private void ShowGameplayScreen(Screen screen)
    {
        _gameScreen = screen;
        OnPropertyChanged(nameof(GameplayScreen));
        if (!_gameLayerVisible || Screen is Screen.Setup or Screen.Table or Screen.Rebuild or Screen.FinalScore)
            Screen = screen;
        NotifyFinalStandingsSharing();
    }

    private void NotifyHumanPresentation()
    {
        ResetFinalStandingsSharing();
        OnPropertyChanged(nameof(IsSingleHumanGame));
        OnPropertyChanged(nameof(IsSoloHumanTurn));
        NotifyPracticalTurnChanged();
        OnPropertyChanged(nameof(ShowSoloOpeningTicketsOnBoard));
        OnPropertyChanged(nameof(ShowDestinationMarkersOnBoard));
        OnPropertyChanged(nameof(BoardDestinationMarkers));
        OnPropertyChanged(nameof(BoardDestinationLines));
        OnPropertyChanged(nameof(GameTableBoardTop));
        OnPropertyChanged(nameof(ShowDrawPanels));
        OnPropertyChanged(nameof(CanConnectPhone));
        OnPropertyChanged(nameof(RevealPrompt));
        ShowConnectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The human seat that currently needs the screen. Recomputed on every refresh.</summary>
    private (SeatId SeatId, string Name)? _revealable;

    public bool CanRevealPrivateSeat => _revealable is not null && !_operationInProgress && !_exitRequested
        && !IsGameInputPaused && _windowActive && _systemAvailable && !_toolsDisposed &&
        IsGameplayScreenActive(Screen.Table) && !NeedsBoardReconciliation && !_mustReload
        && _scoreMarkerStep is null && !IsCheckingBoardBeforeNextTurn
        && BoardFirstProposal is null
        && _coordinator is { StorageFaulted: false }
        && _coordinator.Public.Lifecycle is SessionLifecycle.Setup or SessionLifecycle.Active
        && _coordinator.Public.TurnPhase != TurnPhase.RulesDecisionRequired;

    public string RevealPrompt => _revealable is { } seat
        ? Connection.UsePractical && CanConnectPhone
            ? $"{seat.Name}, it's your turn. Everyone else, look away."
            : IsSingleHumanGame
            ? $"{seat.Name}, your cards are shown on this laptop. Open your cards when you are ready."
            : $"Pass the laptop to {seat.Name}, then reveal their private view."
        : "No human seat needs the screen right now.";

    partial void OnPrivateSeatChanged(PrivateSeatViewModel? value)
    {
        OnPropertyChanged(nameof(IsPrivateVisible));
        OnPropertyChanged(nameof(ShowSoloOpeningTicketsOnBoard));
        OnPropertyChanged(nameof(ShowDestinationMarkersOnBoard));
        OnPropertyChanged(nameof(BoardDestinationMarkers));
        OnPropertyChanged(nameof(BoardDestinationLines));
        OnPropertyChanged(nameof(GameTableBoardTop));
        OnPropertyChanged(nameof(ShowDrawPanels));
        _lastDestinationAlignmentUtc = DateTime.MinValue;
        AlignDestinationMarkers();
    }

    private void AlignDestinationMarkers()
    {
        if (!ShowDestinationMarkersOnBoard || Camera.GameTablePreview is not { } preview) return;
        var markers = BoardDestinationMarkers;
        if (markers.Count == 0) return;
        var now = DateTime.UtcNow;
        if (now - _lastDestinationAlignmentUtc < TimeSpan.FromMilliseconds(500)) return;
        _lastDestinationAlignmentUtc = now;
        DestinationBoardOverlay.AlignToPreview(preview, markers);
    }

    partial void OnScreenChanged(Screen value)
    {
        ClearSavedMatchDeletion();
        if (value is Screen.Setup or Screen.Table or Screen.Rebuild or Screen.FinalScore)
        {
            _gameScreen = value;
            OnPropertyChanged(nameof(GameplayScreen));
        }
        HidePrivateSeat();
        OnPropertyChanged(nameof(CanRevealPrivateSeat));
        ResumeMatchCommand.NotifyCanExecuteChanged();
        NotifySavedMatchCommands();
        NotifyFinalStandingsSharing();
    }

    // ---- Setup -----------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanRefreshSavedMatches))]
    public async Task LoadSavedSessionsAsync()
    {
        if (_deletingSavedMatch) return;
        ClearSavedMatchDeletion();
        var generation = ++_savedMatchListGeneration;
        try
        {
            var saved = await _store.ListSessionsAsync(CancellationToken.None);
            if (generation == _savedMatchListGeneration) Setup.LoadSavedSessions(saved);
        }
        catch (Exception)
        {
            if (generation == _savedMatchListGeneration)
                Setup.SavedMatchMessage = "Saved matches could not be read. Check storage access and choose Refresh saved matches to retry.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartMatch))]
    public async Task StartMatchAsync()
    {
        if (_operationInProgress || _exitRequested || !IsGameplayScreenActive(Screen.Setup) || Setup.TryBuildSetup() is not { } setup) return;

        ClearSavedMatchDeletion();
        ClearEarlierSaveRecovery();
        ResetAutomaticPhysicalFlow();
        SetOperationInProgress(true);
        HidePrivateSeat();
        Busy = "Shuffling and dealing...";
        var generation = _revealGeneration;
        try
        {
            _coordinator = await GameCoordinator.CreateAsync(
                _rules, _store, setup, DeterministicRandom.SeedFromOperatingSystem(), timeProvider: _turnTimeProvider);

            _driver = new ComputerSeatDriver(
                _coordinator, new HeuristicAiPolicy(), DeterministicRandom.SeedFromOperatingSystem().S0);

            var revealUnchanged = generation == _revealGeneration;
            ShowGameplayScreen(Screen.Table);
            if (revealUnchanged) generation = _revealGeneration;
            NotifyHumanPresentation();
            Status = null;
            await PumpAsync();
            Game.ShowPlaying();
            PresentMultiHumanPhoneSetup();
        }
        catch (Exception)
        {
            Setup.ValidationMessage = "The match could not be started. Reopen the application and check its saved matches.";
            if (_coordinator is not null) RequireReload();
        }
        finally
        {
            Busy = null;
            SetOperationInProgress(false);
        }
        await ShowSoloOpeningDestinationsAsync(generation);
    }

    [RelayCommand(CanExecute = nameof(CanResumeMatch))]
    public async Task ResumeMatchAsync()
    {
        if (_operationInProgress || _exitRequested || !IsGameplayScreenActive(Screen.Setup)) return;
        ClearSavedMatchDeletion();
        if (Setup.SelectedSavedSession is not { } saved)
        {
            Setup.SavedMatchMessage = "Check a saved match in the list before choosing Resume selected match.";
            return;
        }

        ClearEarlierSaveRecovery();
        ResetAutomaticPhysicalFlow();
        SetOperationInProgress(true);
        HidePrivateSeat();
        Setup.SavedMatchMessage = null;
        Busy = "Restoring and verifying the saved match...";
        try
        {
            var restored = await GameCoordinator.RestoreAsync(_rules, _store, saved.SessionId,
                timeProvider: _turnTimeProvider);
            if (restored.Public.VerificationMode != VerificationMode.Manual)
                throw new NotSupportedException("This build can only resume matches that use manual verification.");
            // A journal checkpoint alone is not a completed Save Game. Validate its required
            // image before publishing a coordinator or starting the camera restore workflow.
            if (restored.Public.Lifecycle is SessionLifecycle.PreparingPackAway or
                SessionLifecycle.PackedAway or SessionLifecycle.Rebuilding)
            {
                var checkpoint = await restored.GetCheckpointAsync();
                await CheckpointPhoto.LoadCheckpointAsync(checkpoint);
                if (checkpoint is not { IsSafeToPackAway: true } ||
                    !CheckpointPhoto.HasPhotoFor(saved.SessionId, checkpoint.CheckpointId))
                {
                    Setup.SavedMatchMessage = CheckpointPhoto.ReferenceUnavailable
                        ? "This saved game cannot be opened because its required board image is damaged or unreadable. " +
                          "Restore the matching image from a backup, then try again. The saved game has not been changed."
                        : "This save is incomplete: its required board image has not been saved and verified. " +
                          "Restore the matching image from a backup, or use another completed save. The saved game has not been changed.";
                    await OfferEarlierSaveRecoveryAsync(saved.SessionId);
                    return;
                }
                if (!SavedPendingPlacementMatches(restored))
                    throw new InvalidDataException("The saved board photo does not match the unfinished placement.");
            }
            IsCheckingResumedGame = restored.Public.Lifecycle is SessionLifecycle.Setup or
                SessionLifecycle.Active or SessionLifecycle.PackedAway or SessionLifecycle.Rebuilding;
            _coordinator = restored;
            NotifyHumanPresentation();
            _driver = new ComputerSeatDriver(
                _coordinator, new HeuristicAiPolicy(), DeterministicRandom.SeedFromOperatingSystem().S0);

            ShowGameplayScreen(Screen.Table);

            // Packed checkpoints remain suspended until the camera has checked their saved board.
            var lifecycle = _coordinator.Public.Lifecycle;
            NeedsBoardReconciliation = lifecycle == SessionLifecycle.Setup || lifecycle == SessionLifecycle.Active;
            BoardReconciliationAcknowledged = false;

            Status = lifecycle switch
            {
                SessionLifecycle.PreparingPackAway => "This match was in the middle of being saved. Finish or cancel the save.",
                SessionLifecycle.PackedAway => "Checking the saved scoring markers and train positions.",
                SessionLifecycle.Rebuilding => "Checking the saved scoring markers and train positions.",
                _ when NeedsBoardReconciliation =>
                    "Saved digital state verified. Check every claimed route before continuing; any pending claim stays uncommitted.",
                _ => "Saved match restored and verified against its journal.",
            };

            await RefreshAsync();
            Game.ShowPlaying(reconnectCamera: true);
            StartSavedBoardRestore();
            PresentMultiHumanPhoneSetup();
        }
        catch (Exception exception)
        {
            Setup.SavedMatchMessage = exception is NotSupportedException
                ? "This build can only resume matches that use manual verification."
                : "The saved match could not be verified. Check storage access and the installed board-data version.";
            if (_coordinator is not null) RequireReload();
        }
        finally
        {
            Busy = null;
            SetOperationInProgress(false);
        }
    }

    // ---- Privacy ---------------------------------------------------------------------------

    /// <summary>Works out which human seat needs the screen, without revealing anything private.</summary>
    private async Task<(SeatId SeatId, string Name)?> FindRevealableSeatAsync()
    {
        if (_coordinator is null || NeedsBoardReconciliation) return null;

        var view = _coordinator.Public;
        if (view.Lifecycle is not (SessionLifecycle.Setup or SessionLifecycle.Active) ||
            view.TurnPhase == TurnPhase.RulesDecisionRequired) return null;

        if (view.Lifecycle == SessionLifecycle.Setup)
        {
            var awaiting = await _coordinator.SeatsAwaitingSetupSelectionAsync();
            foreach (var seatId in awaiting)
            {
                var seat = view.SeatOf(seatId);
                if (seat.Kind == SeatKind.Human) return (seatId, seat.DisplayName);
            }

            return null;
        }

        if (view.TurnPhase is TurnPhase.AwaitingPhysicalPlacement
            or TurnPhase.RestoreBeforeState
            or TurnPhase.RulesDecisionRequired)
        {
            return null;
        }

        var active = view.SeatOf(view.ActiveSeatId);
        return active.Kind == SeatKind.Human ? (active.SeatId, active.DisplayName) : null;
    }

    [RelayCommand]
    public async Task RevealPrivateSeatAsync()
    {
        if (_gameLayerVisible && Connection.UsePractical && CanConnectPhone)
        {
            await TakePracticalTurnAsync();
            return;
        }
        if (_coordinator is not { } coordinator || !CanRevealPrivateSeat || _revealable is not { } seat) return;

        Connection.InvalidatePrivateGrants();

        var generation = _revealGeneration;
        var view = await coordinator.GetSeatViewAsync(seat.SeatId);
        if (generation != _revealGeneration || !CanRevealPrivateSeat ||
            !ReferenceEquals(coordinator, _coordinator) || _revealable?.SeatId != seat.SeatId ||
            view.Public.StateVersion != coordinator.Public.StateVersion) return;

        var legal = _rules.GetLegalActions(view);
        var summary = view.Public.SeatOf(seat.SeatId);

        CloseSoloCardPanel();
        PrivateSeat = new PrivateSeatViewModel(view, legal, _manifest, summary.DisplayName, summary.Symbol);
    }

    private async Task ShowSoloOpeningDestinationsAsync(long generation)
    {
        // The opening destination choice is the only automatic private presentation. Later turns
        // stay on the game table until a player deliberately opens their cards.
        if (!IsSingleHumanGame || generation != _revealGeneration || !CanRevealPrivateSeat ||
            _coordinator?.Public.Lifecycle != SessionLifecycle.Setup) return;
        try { await RevealPrivateSeatAsync(); }
        catch (Exception) { RequireReload(); }
    }

    /// <summary>
    /// DESIGN 4.7: hide on seat changes, ordinary private-view deactivation, recovery dialogs
    /// and entry into public mode. The solo opening destination choice stays visible on focus loss.
    /// Dropping the view model clears the hand, its tickets and its pending choices together.
    /// </summary>
    [RelayCommand]
    public void HidePrivateSeat()
    {
        _revealGeneration++;
        PrivateSeat = null;
        CloseSoloCardPanel();
        ClearPracticalTurn();
        Connection.InvalidatePrivateGrants();
    }

    public void SetWindowActive(bool active)
    {
        _windowActive = active;
        if (!active) PauseCardBoardCheck();
        UpdateTurnClock();
        if (!active && !(IsSingleHumanGame && ShowSoloOpeningTicketsOnBoard)) HidePrivateSeat();
        OnPropertyChanged(nameof(CanRevealPrivateSeat));
        NotifySoloDrawCommands();
    }

    // ---- Human actions ---------------------------------------------------------------------

    [RelayCommand]
    public Task DrawBlindCardAsync() => SubmitPrivateAsync(
        (envelope, _) => new SelectTrainCard(envelope, null));

    [RelayCommand]
    public Task DrawFaceUpCardAsync(MarketSlotRow? slot) => slot is null
        ? Task.CompletedTask
        : SubmitPrivateAsync((envelope, _) => new SelectTrainCard(envelope, slot.Slot));

    [RelayCommand]
    public Task DrawTicketsAsync() => SubmitPrivateAsync(
        (envelope, _) => new RequestTicketOffer(envelope));

    [RelayCommand]
    public Task CommitTicketsAsync() => SubmitPrivateAsync((envelope, seat) =>
        new CommitTicketSelection(envelope, seat.KeptTickets, []));

    [RelayCommand]
    public Task PlanClaimAsync() => SubmitPrivateAsync((envelope, seat) =>
        seat.SelectedClaim is null || seat.SelectedPayment is null
            ? null
            : new PlanClaim(envelope, seat.SelectedClaim.RouteId, seat.ResolveSelectedPayment()));

    /// <summary>
    /// Submits a command on behalf of the revealed seat, addressed to the exact version its choices
    /// were computed from, then hides the private view before the public screen updates.
    /// </summary>
    private async Task SubmitPrivateAsync(Func<CommandEnvelope, PrivateSeatViewModel, GameCommand?> build)
    {
        if (_coordinator is not { } coordinator || _operationInProgress || IsGameInputPaused ||
            BoardFirstProposal is not null || IsCheckingBoardBeforeNextTurn ||
            _exitRequested || !_windowActive || !_systemAvailable || _toolsDisposed || _mustReload || _scoreMarkerStep is not null ||
            NeedsBoardReconciliation || PrivateSeat is not { } seat) return;

        var envelope = new CommandEnvelope(
            coordinator.SessionId, CommandId.New(), seat.StateVersion, seat.SeatId);

        if (build(envelope, seat) is not { } command) return;
        if (_boardFirstInvalidMoveMessage is not null &&
            command is SelectTrainCard or RequestTicketOffer or CommitTicketSelection)
        {
            seat.Message = "Correct or remove the trains from the unclaimed route before drawing cards.";
            return;
        }

        var beforeAction = coordinator.Public;
        var practicalTurn = _practicalTurn;
        SetOperationInProgress(true);
        HidePrivateSeat();
        var generation = _revealGeneration;
        var accepted = false;
        string? privateRejection = null;
        try
        {
            var outcome = await coordinator.SubmitAsync(command);
            accepted = outcome.IsAccepted;
            privateRejection = outcome.Result.Rejection?.Message;
            Status = accepted ? null : "That action was not accepted. Reveal your private view to review the choice.";
            if (outcome.Result.Rejection?.Code == "StorageFaulted")
            {
                RequireReload();
                return;
            }
            if (accepted)
            {
                NotifyAcceptedLocalCardAction(outcome);
                BeginCardTurnBoardCheck(beforeAction);
            }
            await PumpAsync();
        }
        catch (Exception)
        {
            accepted = false;
            RequireReload();
        }
        finally
        {
            SetOperationInProgress(false);
        }

        if (!accepted && privateRejection is not null && generation == _revealGeneration &&
            CanRevealPrivateSeat && ReferenceEquals(coordinator, _coordinator) &&
            coordinator.Public.StateVersion == seat.StateVersion && _revealable?.SeatId == seat.SeatId)
        {
            seat.Message = privateRejection;
            if (practicalTurn is not null)
            {
                _practicalTurn = practicalTurn;
                NotifyPracticalTurnChanged();
            }
            PrivateSeat = seat;
            return;
        }

        // Keep the public board visible after every action; private cards reopen only by request.
    }

    // ---- Operator actions ------------------------------------------------------------------

    [RelayCommand]
    public async Task ConfirmPlacementAsync()
    {
        if (_coordinator is null || !CanSubmitOperator() || !Table.WholeBoardAcknowledged ||
            Table.Placement is not { AwaitingRestore: false } placement) return;

        Table.WholeBoardAcknowledged = false;
        await AcceptPhysicalPlacementAsync(placement, EvidenceKind.ManualAttestation,
            Environment.UserName, "Operator confirmed the whole board matches the expected placement.");
    }

    [RelayCommand]
    public async Task CancelClaimAsync()
    {
        if (_coordinator is null || !CanSubmitOperator() || Table.Placement is not { AwaitingRestore: false } placement) return;

        HidePrivateSeat();

        // The operator says whether trains are already on the board; DESIGN 8.4 keeps the
        // reservation until the before-state is restored when they are.
        await SubmitOperatorAsync(new CancelPendingClaim(
            new CommandEnvelope(_coordinator.SessionId, CommandId.New(), placement.StateVersion, placement.SeatId),
            placement.OperationId, TrainsWerePlaced: true));
    }

    [RelayCommand]
    public async Task ConfirmRestoredAsync()
    {
        if (_coordinator is null || !CanSubmitOperator() || !Table.WholeBoardAcknowledged ||
            Table.Placement is not { AwaitingRestore: true } placement) return;

        await SubmitOperatorAsync(new ConfirmBeforeStateRestored(
            new CommandEnvelope(_coordinator.SessionId, CommandId.New(), placement.StateVersion, placement.SeatId),
            placement.OperationId));
    }

    private async Task SubmitOperatorAsync(GameCommand command)
    {
        if (!CanSubmitOperator()) return;
        SetOperationInProgress(true);
        HidePrivateSeat();
        Table.WholeBoardAcknowledged = false;
        try
        {
            var outcome = await _coordinator!.SubmitAsync(command);
            Status = outcome.IsAccepted ? null : outcome.Result.Rejection?.Message;
            if (outcome.Result.Rejection?.Code == "StorageFaulted")
            {
                RequireReload();
                return;
            }
            await PumpAsync();
        }
        catch (Exception)
        {
            RequireReload();
        }
        finally
        {
            SetOperationInProgress(false);
        }
    }

    private bool CanSubmitOperator() => !_operationInProgress && !IsGameInputPaused &&
        _scoreMarkerStep is null && !IsCheckingBoardBeforeNextTurn && !_exitRequested && !_mustReload &&
        !NeedsBoardReconciliation && IsGameplayScreenActive(Screen.Table);

    /// <summary>
    /// Accepts the reviewed continuation for a paused supply position (DESIGN 6.4). The operator has
    /// to tick the acknowledgement first, so a policy is never applied by a stray click.
    /// </summary>
    [RelayCommand]
    public async Task ResolveRulesDecisionAsync()
    {
        if (_coordinator is null || !CanSubmitOperator() || Table.RulesContinuation is not { } policy) return;

        if (!Table.RulesContinuationAccepted)
        {
            Status = "Read the policy and tick the box before applying it.";
            return;
        }

        await SubmitLifecycleAsync(new ResolveRulesDecision(
            _coordinator.NewEnvelope(), policy.Code, policy.PolicyId, Environment.UserName));
    }

    // ---- Save, pack away and rebuild (DESIGN 4.10, 19.8) --------------------------------------

    /// <summary>
    /// Runs the whole save. The result is reported exactly as DESIGN 19.8 allows: the pieces may be
    /// cleared away only once the checkpoint has been read back and validated.
    /// </summary>
    [RelayCommand]
    public async Task SaveAndPackAwayAsync()
    {
        if (_coordinator is null || !CanSubmitOperator()) return;

        var name = Table.SaveName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Status = "Give the saved game a name first.";
            return;
        }

        SetOperationInProgress(true);
        HidePrivateSeat();
        try
        {
            var outcome = await _coordinator.SaveAndPackAwayAsync(name);

            Status = outcome switch
            {
                { SafeToPack: true } => null,
                { Rejection: { } rejection } => rejection.Message,
                { AwaitingValidation: true } => "The save is written but still being checked.",
                _ => outcome.Problem,
            };

            if (outcome.SafeToPack) Table.SaveName = "";
            await RefreshAsync();
        }
        catch (Exception)
        {
            RequireReload();
        }
        finally
        {
            SetOperationInProgress(false);
        }
    }

    [RelayCommand]
    public async Task CancelSaveAsync()
    {
        if (_coordinator is null || _operationInProgress || IsGameInputPaused || _exitRequested || _mustReload) return;

        await SubmitLifecycleAsync(new CancelPackAwayPreparation(
            _coordinator.NewEnvelope(), "cancelled by the operator"));
    }

    [RelayCommand]
    public async Task BeginRebuildAsync()
    {
        if (_coordinator is null || Public?.Checkpoint is not { } checkpoint) return;

        CancelSavedBoardRestore();
        Game.ClearGuidance();
        await SubmitLifecycleAsync(new BeginBoardRebuild(_coordinator.NewEnvelope(), checkpoint.CheckpointId));
        if (_coordinator.Public.Lifecycle == SessionLifecycle.Rebuilding) ShowGameplayScreen(Screen.Rebuild);
    }

    /// <summary>
    /// DESIGN 19.8: the operator attests to the whole saved target. The echoed target hash means an
    /// attestation cannot be applied to a different saved arrangement.
    /// </summary>
    [RelayCommand]
    public async Task AttestRebuildAsync()
    {
        if (_coordinator is null || Public?.Checkpoint is not { } checkpoint) return;
        if (!Table.RebuildAcknowledged)
        {
            Status = "Tick the confirmation after checking the saved routes, empty lanes, and scoring markers.";
            return;
        }

        await SubmitLifecycleAsync(new AttestBoardRebuild(
            _coordinator.NewEnvelope(), checkpoint.CheckpointId, checkpoint.PhysicalTargetHash, Environment.UserName));
    }

    [RelayCommand]
    public async Task ResumePackedGameAsync()
    {
        if (_operationInProgress || Busy is not null || _exitRequested || _mustReload ||
            IsGameInputPaused || _coordinator is not { } coordinator ||
            coordinator.Public.Checkpoint is not { } checkpoint) return;
        if (!Table.RebuildAcknowledged || !Table.RebuildAttested)
        {
            Status = "Confirm the whole saved board before resuming.";
            return;
        }

        SetOperationInProgress(true);
        try
        {
            // Re-read the durable image: a previously displayed photo may have since gone missing
            // or become unreadable. Manual board confirmation cannot replace this required file.
            await CheckpointPhoto.LoadCheckpointAsync(await coordinator.GetCheckpointAsync());
            if (!ReferenceEquals(coordinator, _coordinator) || _exitRequested || _mustReload ||
                coordinator.Public.Checkpoint?.CheckpointId != checkpoint.CheckpointId) return;
            if (!CheckpointPhoto.HasPhotoFor(coordinator.SessionId, checkpoint.CheckpointId))
            {
                Status = CheckpointPhoto.ReferenceUnavailable
                    ? "The required board image is damaged or unreadable. Restore the matching image from a backup before resuming."
                    : "Save the required board image before resuming. Open Saved board photo to capture and verify it.";
                return;
            }
            if (!SavedPendingPlacementMatches(coordinator))
            {
                Status = "The saved board image does not match the unfinished placement.";
                return;
            }
            if (!Table.RebuildAcknowledged || !Table.RebuildAttested)
            {
                Status = "Confirm the whole saved board before resuming.";
                return;
            }
            Camera.SetGameTableReference(CheckpointPhoto.PhotoImage);
        }
        catch (Exception)
        {
            Status = "The required board image could not be verified. Check storage access and try again before resuming.";
            return;
        }
        finally { SetOperationInProgress(false); }

        await SubmitLifecycleAsync(new ResumePackedGame(coordinator.NewEnvelope(), checkpoint.CheckpointId));
    }

    /// <summary>The current public projection, or null before a match is open.</summary>
    private PublicView? Public => _coordinator?.Public;

    private async Task SubmitLifecycleAsync(GameCommand command)
    {
        if (_coordinator is null || _operationInProgress || IsGameInputPaused || _exitRequested || _mustReload) return;

        SetOperationInProgress(true);
        HidePrivateSeat();
        var generation = _revealGeneration;
        try
        {
            var outcome = await _coordinator.SubmitAsync(command);
            Status = outcome.IsAccepted ? null : outcome.Result.Rejection?.Message;

            if (outcome.Result.Rejection?.Code == "StorageFaulted")
            {
                RequireReload();
                return;
            }

            if (outcome.IsAccepted && command is ResolveRulesDecision or CancelPackAwayPreparation or ResumePackedGame)
            {
                if (_coordinator.Public.Lifecycle == SessionLifecycle.Active)
                {
                    var revealUnchanged = generation == _revealGeneration;
                    ShowGameplayScreen(Screen.Table);
                    if (revealUnchanged) generation = _revealGeneration;
                }
                if (command is ResumePackedGame)
                    await AnnounceResumedTurnAsync();
                else await PumpAsync();
            }
            else
            {
                await RefreshAsync();
            }
        }
        catch (Exception)
        {
            RequireReload();
        }
        finally
        {
            SetOperationInProgress(false);
        }
    }

    [RelayCommand]
    public async Task ConfirmBoardReconciledAsync()
    {
        if (_coordinator is null || _operationInProgress || IsGameInputPaused || _exitRequested || _mustReload || !NeedsBoardReconciliation ||
            !BoardReconciliationAcknowledged) return;

        SetOperationInProgress(true);
        HidePrivateSeat();
        NeedsBoardReconciliation = false;
        BoardReconciliationAcknowledged = false;
        Status = "Board reconciliation confirmed by the operator. Manual verification remains active.";
        try
        {
            await AnnounceResumedTurnAsync();
        }
        catch (Exception)
        {
            NeedsBoardReconciliation = true;
            RequireReload();
            await RefreshAsync();
        }
        finally
        {
            SetOperationInProgress(false);
        }
    }

    private void SetOperationInProgress(bool value)
    {
        _operationInProgress = value;
        NotifySavedMatchCommands();
        OnPropertyChanged(nameof(CanRevealPrivateSeat));
        NotifySoloDrawCommands();
        ResumeMatchCommand.NotifyCanExecuteChanged();
        AcknowledgeResumeTurnCommand.NotifyCanExecuteChanged();
        BackToMenuCommand.NotifyCanExecuteChanged();
    }

    private void RequireReload()
    {
        _mustReload = true;
        UpdateTurnClock();
        HidePrivateSeat();
        Status = "Play is paused after an error. Reopen the saved match to verify its last durable state.";
        OnPropertyChanged(nameof(CanRevealPrivateSeat));
    }

    // ---- Refresh ---------------------------------------------------------------------------

    /// <summary>
    /// Lets computer seats play whatever is available, then republishes the public screen. It stops
    /// at any point that needs a human or the operator (DESIGN 4.5).
    /// </summary>
    private async Task PumpAsync(bool preserveTrainCardPanel = false)
    {
        UpdateTurnClock();
        if (_coordinator is null || _driver is null || IsGameInputPaused || _scoreMarkerStep is not null ||
            NeedsBoardReconciliation || _mustReload) return;

        if (IsCheckingBoardBeforeNextTurn)
        {
            await RefreshAsync();
            return;
        }

        Busy = "Computer seats are playing...";
        try
        {
            await Task.Run(() => _driver.AdvanceAsync());
        }
        finally
        {
            Busy = null;
        }

        await RefreshAsync(preserveTrainCardPanel);
    }

    private async Task RefreshAsync(bool preserveTrainCardPanel = false)
    {
        if (_coordinator is null) return;
        UpdateTurnClock();
        if (!(preserveTrainCardPanel && ShowSoloTrainCards && IsGameTableHumanTurn &&
            _coordinator.Public.TurnPhase == TurnPhase.AwaitingSecondTrainCard))
            CloseSoloCardPanel();

        var view = _coordinator.Public;
        OnPropertyChanged(nameof(IsSoloHumanTurn));
        if (_practicalTurn is not null && !HasAcceptedPracticalTurn) ClearPracticalTurn();
        NotifyPracticalTurnChanged();
        ReconcileBoardFirstClaimFlow(view);
        Table.Update(view, _coordinator.PublicHistory);
        EngineeringHistory.Clear();
        foreach (var entry in _coordinator.EngineeringHistory.TakeLast(40))
        {
            var who = entry.Seat is { } seat ? view.SeatOf(seat).DisplayName + ": " : "";
            EngineeringHistory.Add(who + entry.Text.Replace("destination ticket", "destination", StringComparison.Ordinal));
        }
        if (_scoreMarkerStep is { } scoreStep)
            Table.ShowPendingScoreMarker(view.TurnNumber, scoreStep.SeatName,
                scoreStep.Color, scoreStep.ToPrintedScore);
        await RefreshSoloDrawActionsAsync(view);
        await RefreshCheckpointPhotoAsync();
        if (NeedsBoardReconciliation)
        {
            Table.Placement = null;
            Table.Instruction = "Rebuild and check the saved claimed routes below before resuming play.";
        }

        _revealable = await FindRevealableSeatAsync();
        OnPropertyChanged(nameof(CanRevealPrivateSeat));
        OnPropertyChanged(nameof(RevealPrompt));

        // A final claim can finish the digital match while its physical score marker still needs
        // to move. Keep the public board visible until that last movement has been observed.
        if (view.FinalResult is { } result && !_claimCompletionInProgress && _scoreMarkerStep is null &&
            !IsCheckingBoardBeforeNextTurn)
        {
            BuildFinalScores(result);
            ShowGameplayScreen(Screen.FinalScore);
            PrivateSeat = null;
        }
        if (IsCheckingBoardBeforeNextTurn)
            ShowCardBoardGuidance(_cardActionBoardWarning ??
                "Checking the board before the next turn. Keep every train visible in its space.");
        NotifyCompanionPresentationChanged();
    }

    private void BuildFinalScores(FinalResult result)
    {
        var view = _coordinator!.Public;
        FinalScores.Clear();

        var standings = result.Scores.OrderByDescending(score => score.Total)
            .ThenByDescending(score => score.CompletedTicketCount)
            .ThenByDescending(score => score.HoldsLongestRouteBonus).ToArray();
        foreach (var score in standings)
        {
            var seat = view.SeatOf(score.SeatId);
            var trail = score.LongestTrailWitness.IsEmpty
                ? "none"
                : string.Join(" → ", score.LongestTrailWitness.Select(_manifest.Describe));

            var row = new FinalScoreRow(
                seat.DisplayName,
                seat.Color,
                seat.Symbol,
                score.RoutePoints,
                score.TicketPointsGained,
                score.TicketPointsLost,
                score.LongestRouteBonusPoints,
                score.Total,
                $"{score.CompletedTicketCount} completed, {score.IncompleteTickets.Length} missed",
                score.LongestTrailLength,
                trail,
                result.Winners.Contains(score.SeatId))
            {
                Portrait = Game.SeatChoices.First(choice => choice.TrainColor == seat.Color)
                    .PortraitFor(seat.Kind == SeatKind.Computer),
                RankLabel = $"#{Array.FindIndex(standings, other => other.Total == score.Total &&
                    other.CompletedTicketCount == score.CompletedTicketCount &&
                    other.HoldsLongestRouteBonus == score.HoldsLongestRouteBonus) + 1}"
            };
            FinalScores.Add(WithTurnTiming(row, score.SeatId));
        }

        var winners = string.Join(" and ", result.Winners.Select(seat => view.SeatOf(seat).DisplayName));
        FinalSummary = result.SharedVictory
            ? $"Shared victory: {winners}. Tied on points, completed tickets and the longest continuous route."
            : $"{winners} wins. {result.TieBreakExplanation}";
    }
}

public sealed record FinalScoreRow(
    string SeatName,
    PlayerColor Color,
    string Symbol,
    int RoutePoints,
    int TicketsGained,
    int TicketsLost,
    int LongestBonus,
    int Total,
    string TicketSummary,
    int LongestTrailLength,
    string WitnessTrail,
    bool IsWinner)
{
    public System.Windows.Media.ImageSource? Portrait { get; init; }
    public string RankLabel { get; init; } = "";
    public string TotalTurnTimeText { get; init; } = "—";
    public string AverageTurnTimeText { get; init; } = "—";
    public string TimingNote { get; init; } = "Timing was not recorded for this game.";
    public int CompletedTurns { get; init; }
}
