using GoldenTicket.Application;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    // Cards are already durably awarded when this gate starts. Only the following turn waits.
    private sealed record CardBoardCheck(GameCoordinator Coordinator, long Version,
        DateTimeOffset StartedAt, long InitialEpoch, long InitialSequence,
        BoardInventoryVerifier Verifier);

    private CardBoardCheck? _cardBoardCheck;
    private bool _finishingCardBoardCheck;
    private string? _cardActionBoardWarning;
    private string? _cardBoardGuidance;
    private bool _cardBoardCameraSeen;
    private string? _lastCardBoardProblemLogKey;
    private DateTimeOffset _lastCardBoardProblemLogAt;

    public bool IsCheckingBoardBeforeNextTurn => _cardBoardCheck is not null;

    private bool UsesCameraForCardActions => _cardBoardCameraSeen ||
        _gameLayerVisible && Camera.IsGameTablePreviewRequested ||
        Camera.IsGameTablePreviewUpright || Camera.GameTableAnalysis is not null;

    private bool IsHumanCardPhase(GameCoordinator coordinator) =>
        coordinator.Public.Lifecycle == SessionLifecycle.Active &&
        coordinator.Public.SeatOf(coordinator.Public.ActiveSeatId).Kind == SeatKind.Human &&
        coordinator.Public.TurnPhase is TurnPhase.TurnStart or
            TurnPhase.AwaitingSecondTrainCard or TurnPhase.AwaitingTicketKeep;

    private BoardInventoryVerifier NewCardBoardVerifier(GameCoordinator coordinator) =>
        new(coordinator.Public.RouteOwners.Select(route => new BoardInventoryRoute(route.Key.Value,
            ToMarkerColor(coordinator.Public.SeatOf(route.Value).Color),
            _manifest.Route(route.Key).Length)).ToArray(), verifyClaimedRouteColors: false);

    private void NotifyCardBoardCheckChanged()
    {
        OnPropertyChanged(nameof(IsCheckingBoardBeforeNextTurn));
        OnPropertyChanged(nameof(CanRevealPrivateSeat));
        OnPropertyChanged(nameof(IsGameTableHumanTurn));
        NotifySoloDrawCommands();
        UpdateTurnClock();
        NotifyCompanionPresentationChanged();
    }

    private void ResetCardActionBoard()
    {
        _cardBoardCheck = null;
        SetCardBoardWarning(null);
        NotifyCardBoardCheckChanged();
    }

    private void PauseCardBoardCheck()
    {
        if (_cardBoardCheck is not { } check) return;
        var latest = Camera.GameTableAnalysis;
        check.Verifier.Reset();
        _cardBoardCheck = check with { StartedAt = DateTimeOffset.UtcNow,
            InitialEpoch = latest?.Board.Epoch ?? -1, InitialSequence = latest?.Board.Sequence ?? -1 };
    }

    private void BeginCardTurnBoardCheck(PublicView before)
    {
        if (_coordinator is not { } coordinator || !UsesCameraForCardActions ||
            before.Lifecycle != SessionLifecycle.Active ||
            before.SeatOf(before.ActiveSeatId).Kind != SeatKind.Human ||
            before.TurnPhase is not (TurnPhase.TurnStart or TurnPhase.AwaitingSecondTrainCard or TurnPhase.AwaitingTicketKeep) ||
            coordinator.Public.TurnNumber == before.TurnNumber && coordinator.Public.Lifecycle != SessionLifecycle.Finished)
            return;

        _cardBoardCameraSeen = true;
        var latest = Camera.GameTableAnalysis;
        _cardBoardCheck = new(coordinator, coordinator.Public.StateVersion, DateTimeOffset.UtcNow,
            latest?.Board.Epoch ?? -1, latest?.Board.Sequence ?? -1, NewCardBoardVerifier(coordinator));
        HidePrivateSeat();
        ResetBoardFirstClaimFlow();
        SetCardBoardWarning(null);
        ShowCardBoardGuidance("Checking the board before the next turn. Keep every train visible in its space.");
        NotifyCardBoardCheckChanged();
        BoardInteractionLog.Write("card-turn.board-check-started", new
        {
            session = coordinator.SessionId.Value, version = coordinator.Public.StateVersion,
            completedTurn = before.TurnNumber, nextTurn = coordinator.Public.TurnNumber
        });
    }

    private void NotifyAcceptedLocalCardAction(SubmitOutcome outcome)
    {
        if (!outcome.IsAccepted || outcome.WasDuplicate || outcome.Result.Transition is not { } transition) return;
        foreach (var entry in transition.Events)
        {
            if (entry is FaceUpCardTaken faceUp)
                OnLocalTrainCardAccepted(faceUp.SeatId, faceUp.Slot, faceUp.Kind);
            else if (entry is BlindCardDrawn blind)
                OnLocalTrainCardAccepted(blind.SeatId, null, TrainCardKind.Locomotive);
            else if (entry is TicketOfferCreated tickets)
                OnLocalDestinationCardsAccepted(tickets.SeatId, tickets.Offered.Length);
        }
    }

    private void SetCardBoardWarning(string? message)
    {
        _cardActionBoardWarning = message;
        if (message is null)
        {
            Game.UpdateInventoryProblemMarkers("card", null);
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
        _cardBoardGuidance = message;
        Game.ShowGuidance(Table.TurnText, "Checking…", message);
    }

    private string CardBoardProblem(BoardInventoryObservation observation)
    {
        if (observation.UnexpectedTrains is { } extra)
        {
            var route = _manifest.Describe(new RouteId(extra.RouteId));
            var color = extra.Color is { } c ? c + " " : "";
            return $"The camera sees {extra.Count} unexpected {color}train{(extra.Count == 1 ? "" : "s")} on {route}. " +
                "Check the yellow spheres and remove these trains before the next turn.";
        }
        if (observation.RouteId is { } routeId)
        {
            var route = _manifest.Describe(new RouteId(routeId));
            return observation.State switch
            {
                BoardInventoryState.Ambiguous => $"The camera cannot clearly identify the trains on {route}. " +
                    "Check the yellow spheres and clear hands or glare while it checks again.",
                BoardInventoryState.MissingTrains => $"The camera cannot verify every train on {route}. " +
                    "The claim is still recorded. Check the yellow spheres and make every train visible in its space.",
                BoardInventoryState.WrongColor => $"The camera reads a different train color on {route}. " +
                    "Check the yellow spheres, pieces and lighting before the next turn.",
                _ => $"The camera cannot verify the claimed route {route}. " +
                    "The claim is still recorded. Check the yellow spheres before the next turn."
            };
        }
        return observation.UnexpectedDetections.Count > 0
            ? "The camera flagged trains outside the claimed routes. Check the yellow spheres on the board."
            : "The camera could not verify the board. Keep it clear while it checks again.";
    }

    private void LogCardBoardProblem(GameTableAnalysis analysis, BoardInventoryObservation observation,
        GameCoordinator coordinator)
    {
        var routeId = observation.RouteId ?? observation.UnexpectedTrains?.RouteId;
        var key = $"{coordinator.SessionId.Value}/{coordinator.Public.StateVersion}/" +
            $"{analysis.Board.Epoch}/{analysis.CropRevision}/{analysis.ModelRevision}/" +
            $"{observation.State}/{routeId}/{observation.UnexpectedTrains}";
        if (_lastCardBoardProblemLogKey == key &&
            analysis.Board.CapturedAt >= _lastCardBoardProblemLogAt &&
            analysis.Board.CapturedAt - _lastCardBoardProblemLogAt < TimeSpan.FromSeconds(1)) return;
        _lastCardBoardProblemLogKey = key;
        _lastCardBoardProblemLogAt = analysis.Board.CapturedAt;
        BoardInteractionLog.Write("card-turn.board-problem", new
        {
            session = coordinator.SessionId.Value, version = coordinator.Public.StateVersion,
            analysis.Board.Sequence, analysis.Board.Epoch, analysis.Board.CapturedAt,
            analysis.CropRevision, analysis.ModelRevision,
            state = observation.State.ToString(), route = routeId, observation.UnexpectedTrains,
            observation.UnexpectedDetections,
            nearbyCandidates = routeId is null ? Array.Empty<object>() : DescribeNearbyPlacementCandidates(analysis, routeId)
        });
    }

    private static bool CardBoardHasProblem(BoardInventoryObservation observation) =>
        observation.State is BoardInventoryState.UnexpectedTrain or BoardInventoryState.MissingTrains or
            BoardInventoryState.WrongColor or BoardInventoryState.Ambiguous or BoardInventoryState.Unsupported;

    private void ObserveCardActionBoard()
    {
        if (Camera.IsGameTablePreviewUpright || Camera.GameTableAnalysis is not null)
            _cardBoardCameraSeen = true;
        if (_cardBoardCheck is not { } check || _finishingCardBoardCheck) return;
        var coordinator = check.Coordinator;
        if (!ReferenceEquals(coordinator, _coordinator) || coordinator.Public.StateVersion != check.Version)
        {
            // A save/rebuild or another recovery transition supersedes this in-memory gate.
            ResetCardActionBoard();
            return;
        }
        if (_operationInProgress || IsGameInputPaused || !_windowActive || !_systemAvailable ||
            _exitRequested || _mustReload || _toolsDisposed || NeedsBoardReconciliation ||
            !IsGameplayScreenActive(Screen.Table) || coordinator.StorageFaulted)
        {
            check.Verifier.Reset();
            return;
        }
        if (Camera.GameTableAnalysis is not { } analysis || !Camera.IsGameTablePreviewUpright ||
            analysis.Board.Age > TimeSpan.FromSeconds(2))
        {
            check.Verifier.Reset();
            ShowCardBoardGuidance(_cardActionBoardWarning ??
                "Keep the whole board visible while the camera checks it before the next turn.");
            return;
        }
        if (analysis.Board.CapturedAt <= check.StartedAt ||
            analysis.Board.Epoch == check.InitialEpoch && analysis.Board.Sequence <= check.InitialSequence) return;
        var observation = check.Verifier.Observe(analysis.Board, analysis.Candidates,
            analysis.CropRevision, analysis.ModelRevision);
        if (observation.State == BoardInventoryState.WaitingForFreshFrame) return;
        Game.UpdateInventoryProblemMarkers("card", observation);
        if (CardBoardHasProblem(observation))
        {
            LogCardBoardProblem(analysis, observation, coordinator);
            SetCardBoardWarning(CardBoardProblem(observation));
        }
        else if (observation.State is BoardInventoryState.Stabilizing or BoardInventoryState.Confirmed)
        {
            SetCardBoardWarning(null);
            ShowCardBoardGuidance("The board matches. Confirming its positions before the next turn…");
            if (observation.Confirmed) _ = FinishCardTurnBoardCheckAsync(check);
        }
    }

    private async Task FinishCardTurnBoardCheckAsync(CardBoardCheck check)
    {
        if (_finishingCardBoardCheck || !ReferenceEquals(_cardBoardCheck, check) || _operationInProgress) return;
        _finishingCardBoardCheck = true;
        SetOperationInProgress(true);
        try
        {
            ResetCardActionBoard();
            BoardInteractionLog.Write("card-turn.board-check-finished", new { check.Version, confirmed = true });
            await PumpAsync();
        }
        catch (Exception) { RequireReload(); }
        finally
        {
            _finishingCardBoardCheck = false;
            SetOperationInProgress(false);
        }
    }
}
