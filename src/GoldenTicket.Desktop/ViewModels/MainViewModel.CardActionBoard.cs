using GoldenTicket.Application;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private sealed record CardBoardCheck(GameCoordinator Coordinator, long Version,
        DateTimeOffset StartedAt, long InitialEpoch, long InitialSequence,
        BoardInventoryVerifier Verifier)
    {
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GameTableAnalysis? ConfirmedAnalysis { get; set; }
    }

    private CardBoardCheck? _cardBoardCheck;
    private BoardInventoryVerifier? _cardBoardMonitor;
    private string? _cardBoardMonitorKey;
    private string? _cardActionBoardWarning;
    private string? _cardBoardGuidance;
    private bool _cardBoardCameraSeen;
    private string? _lastCardBoardProblemLogKey;
    private DateTimeOffset _lastCardBoardProblemLogAt;

    private bool UsesCameraForCardActions => _cardBoardCameraSeen ||
        _gameLayerVisible && Camera.IsGameTablePreviewRequested ||
        Camera.IsGameTablePreviewUpright || Camera.GameTableAnalysis is not null;

    private bool IsHumanCardPhase(GameCoordinator coordinator) =>
        coordinator.Public.Lifecycle == SessionLifecycle.Active &&
        coordinator.Public.SeatOf(coordinator.Public.ActiveSeatId).Kind == SeatKind.Human &&
        coordinator.Public.TurnPhase is TurnPhase.TurnStart or
            TurnPhase.AwaitingSecondTrainCard or TurnPhase.AwaitingTicketKeep;

    private bool CardBoardContextCurrent(GameCoordinator coordinator, long version) =>
        ReferenceEquals(coordinator, _coordinator) && coordinator.Public.StateVersion == version &&
        IsHumanCardPhase(coordinator) && !coordinator.StorageFaulted &&
        !_exitRequested && !_mustReload && !_toolsDisposed && _windowActive && _systemAvailable &&
        !IsGameInputPaused && !NeedsBoardReconciliation && _scoreMarkerStep is null &&
        IsGameplayScreenActive(Screen.Table);

    private BoardInventoryVerifier NewCardBoardVerifier(GameCoordinator coordinator) =>
        new(coordinator.Public.RouteOwners.Select(route => new BoardInventoryRoute(route.Key.Value,
            ToMarkerColor(coordinator.Public.SeatOf(route.Value).Color),
            _manifest.Route(route.Key).Length)).ToArray(), verifyClaimedRouteColors: false);

    private void ResetCardActionBoard()
    {
        _cardBoardCheck?.Completion.TrySetResult(false);
        _cardBoardCheck = null;
        _cardBoardMonitor = null;
        _cardBoardMonitorKey = null;
        SetCardBoardWarning(null);
    }

    private void SetCardBoardWarning(string? message)
    {
        _cardActionBoardWarning = message;
        if (message is null)
        {
            _lastCardBoardProblemLogKey = null;
            if (_cardBoardGuidance is not null && Game.GuidanceInstruction == _cardBoardGuidance)
                Game.ClearGuidance();
            _cardBoardGuidance = null;
        }
        else ShowCardBoardGuidance(message);
        OnPropertyChanged(nameof(CanKeepSoloTickets));
        NotifySoloDrawCommands();
    }

    private void ShowCardBoardGuidance(string message)
    {
        // A payable route's payment dialog and specific placement feedback take precedence.
        if (BoardFirstProposal is not null || _boardFirstInvalidMoveMessage is not null) return;
        _cardBoardGuidance = message;
        Game.ShowGuidance(Table.TurnText, _coordinator!.Public.SeatOf(
            _coordinator.Public.ActiveSeatId).DisplayName, message);
    }

    private string CardBoardProblem(BoardInventoryObservation observation, GameCoordinator coordinator)
    {
        if (observation.UnexpectedTrains is { } extra)
        {
            var route = _manifest.Describe(new RouteId(extra.RouteId));
            var color = extra.Color is { } c ? c.ToString().ToLowerInvariant() + " " : "";
            var seen = $"The camera sees {extra.Count} {color}train{(extra.Count == 1 ? "" : "s")} on {route}. ";
            return seen + (coordinator.Public.TurnPhase == TurnPhase.TurnStart
                ? "Claim this route if you can pay for it, or remove the trains before drawing cards."
                : "You already chose to draw cards this turn. Remove these unclaimed trains to continue.");
        }
        if (observation.RouteId is { } routeId)
        {
            var route = _manifest.Describe(new RouteId(routeId));
            return observation.State switch
            {
                BoardInventoryState.Ambiguous =>
                    $"The camera cannot clearly identify the train positions on {route}. " +
                    "The claim is still recorded. Keep the trains in their spaces and clear hands or glare " +
                    "while the camera checks again.",
                BoardInventoryState.MissingTrains =>
                    $"The camera cannot verify every train on {route}. The claim is still recorded. " +
                    "Make sure every train is visible in its space before drawing cards.",
                BoardInventoryState.WrongColor =>
                    $"The camera reads a different train color on {route}. The claim is still recorded. " +
                    "Check the pieces and lighting before drawing cards.",
                _ => $"The camera cannot verify the claimed route {route}. " +
                    "The claim is still recorded. Check the board view before drawing cards."
            };
        }
        return "Check for unclaimed or misplaced trains. The board must match the game before drawing cards.";
    }

    private void LogCardBoardProblem(GameTableAnalysis analysis, BoardInventoryObservation observation,
        GameCoordinator coordinator)
    {
        var routeId = observation.RouteId ?? observation.UnexpectedTrains?.RouteId;
        var key = $"{coordinator.SessionId.Value}/{coordinator.Public.StateVersion}/" +
            $"{analysis.Board.Epoch}/{analysis.CropRevision}/{analysis.ModelRevision}/" +
            $"{observation.State}/{routeId}/{observation.UnexpectedTrains}/{_cardBoardCheck is not null}";
        if (_lastCardBoardProblemLogKey == key &&
            analysis.Board.CapturedAt >= _lastCardBoardProblemLogAt &&
            analysis.Board.CapturedAt - _lastCardBoardProblemLogAt < TimeSpan.FromSeconds(1)) return;
        _lastCardBoardProblemLogKey = key;
        _lastCardBoardProblemLogAt = analysis.Board.CapturedAt;
        BoardInteractionLog.Write("card-action.board-problem", new
        {
            session = coordinator.SessionId.Value, version = coordinator.Public.StateVersion,
            turn = coordinator.Public.TurnNumber, phase = coordinator.Public.TurnPhase.ToString(),
            analysis.Board.Sequence, analysis.Board.Epoch, analysis.Board.CapturedAt,
            ageMs = analysis.Board.Age.TotalMilliseconds,
            analysis.CropRevision, analysis.ModelRevision,
            state = observation.State.ToString(), route = routeId, observation.UnexpectedTrains,
            cardCheckPending = _cardBoardCheck is not null,
            nearbyCandidates = routeId is null ? Array.Empty<object>() : DescribeNearbyPlacementCandidates(analysis, routeId)
        });
    }

    private static bool CardBoardHasProblem(BoardInventoryObservation observation) =>
        observation.State is BoardInventoryState.UnexpectedTrain or BoardInventoryState.MissingTrains or
            BoardInventoryState.WrongColor or BoardInventoryState.Ambiguous or BoardInventoryState.Unsupported;

    // Runs independently of the placement flow, including while an asynchronous card action waits.
    private void ObserveCardActionBoard()
    {
        if (_coordinator is not { } coordinator) return;
        if (Camera.IsGameTablePreviewUpright || Camera.GameTableAnalysis is not null)
            _cardBoardCameraSeen = true;
        if (!IsHumanCardPhase(coordinator))
        {
            ResetCardActionBoard();
            return;
        }
        if (!CardBoardContextCurrent(coordinator, coordinator.Public.StateVersion))
        {
            _cardBoardCheck?.Completion.TrySetResult(false);
            _cardBoardMonitor?.Reset();
            return;
        }
        if (Camera.GameTableAnalysis is not { } analysis || !Camera.IsGameTablePreviewUpright ||
            analysis.Board.Age > TimeSpan.FromSeconds(2))
        {
            _cardBoardMonitor?.Reset();
            _cardBoardCheck?.Verifier.Reset();
            return;
        }

        var key = $"{coordinator.SessionId.Value}/{coordinator.Public.StateVersion}";
        if (_cardBoardMonitorKey != key)
        {
            _cardBoardMonitorKey = key;
            _cardBoardMonitor = NewCardBoardVerifier(coordinator);
            SetCardBoardWarning(null);
        }
        var observation = _cardBoardMonitor!.Observe(analysis.Board, analysis.Candidates,
            analysis.CropRevision, analysis.ModelRevision);
        if (CardBoardHasProblem(observation))
        {
            LogCardBoardProblem(analysis, observation, coordinator);
            SetCardBoardWarning(CardBoardProblem(observation, coordinator));
        }
        else if (observation.Confirmed) SetCardBoardWarning(null);

        if (_cardBoardCheck is not { } check) return;
        if (!CardBoardContextCurrent(check.Coordinator, check.Version))
        {
            check.Completion.TrySetResult(false);
            return;
        }
        // A result published after the click can still belong to a capture from before it.
        if (analysis.Board.CapturedAt < check.StartedAt ||
            analysis.Board.Epoch == check.InitialEpoch && analysis.Board.Sequence <= check.InitialSequence)
            return;
        var fresh = check.Verifier.Observe(analysis.Board, analysis.Candidates,
            analysis.CropRevision, analysis.ModelRevision);
        if (CardBoardHasProblem(fresh)) check.Completion.TrySetResult(false);
        else if (fresh.Confirmed)
        {
            check.ConfirmedAnalysis = analysis;
            check.Completion.TrySetResult(true);
        }
    }

    private async Task<bool> CheckBoardBeforeCardActionAsync(GameCoordinator coordinator, long version,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Technical/manual sessions without a camera retain operator verification.
        if (!UsesCameraForCardActions || coordinator.Public.Lifecycle != SessionLifecycle.Active) return true;
        _cardBoardCameraSeen = true;
        if (!CardBoardContextCurrent(coordinator, version) || _cardActionBoardWarning is not null ||
            BoardFirstProposal is not null || _boardFirstInvalidMoveMessage is not null) return false;
        var current = Camera.GameTableAnalysis;
        var check = new CardBoardCheck(coordinator, version, DateTimeOffset.UtcNow,
            current?.Board.Epoch ?? -1, current?.Board.Sequence ?? -1, NewCardBoardVerifier(coordinator));
        _cardBoardCheck = check;
        ShowCardBoardGuidance("Checking the board before drawing cards…");
        BoardInteractionLog.Write("card-action.board-check-started", new
        {
            session = coordinator.SessionId.Value, version,
            phase = coordinator.Public.TurnPhase.ToString(),
            check.InitialSequence, check.InitialEpoch
        });
        var confirmed = false;
        try
        {
            confirmed = await check.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            confirmed = confirmed && CardBoardContextCurrent(coordinator, version) &&
                _cardActionBoardWarning is null && BoardFirstProposal is null &&
                _boardFirstInvalidMoveMessage is null && Camera.IsGameTablePreviewUpright &&
                Camera.GameTableAnalysis is { } latest && ReferenceEquals(latest, check.ConfirmedAnalysis) &&
                latest.Board.Age <= TimeSpan.FromSeconds(2);
            return confirmed;
        }
        catch (TimeoutException)
        {
            SetCardBoardWarning("The camera could not verify the board. Keep it clear and in focus, then try again.");
            return false;
        }
        finally
        {
            if (ReferenceEquals(_cardBoardCheck, check)) _cardBoardCheck = null;
            if (_cardActionBoardWarning is null) SetCardBoardWarning(null);
            BoardInteractionLog.Write("card-action.board-check-finished", new { version, confirmed });
        }
    }
}
