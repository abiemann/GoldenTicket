using System.Collections.Immutable;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoldenTicket.Application;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

public sealed record BoardFirstPaymentRow(PaymentOption Option, string Description);

public sealed class BoardFirstPaymentCardRow(HeldCard card) : ObservableObject
{
    private bool _isSelected;

    public CardId Id { get; } = card.Id;
    public TrainCardKind Kind { get; } = card.Kind;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>A camera suggestion, not an authorized or committed claim.</summary>
public sealed class BoardFirstClaimProposal(
    SessionId sessionId, SeatId seatId, string seatName, RouteId routeId, string routeText,
    long stateVersion, long cropRevision, long modelRevision, long cameraEpoch,
    IReadOnlyList<BoardFirstPaymentRow> payments, IReadOnlyList<HeldCard>? availableCards = null)
    : ObservableObject
{
    public string ProposalId { get; } = Guid.NewGuid().ToString("N");
    public SessionId SessionId { get; } = sessionId;
    public SeatId SeatId { get; } = seatId;
    public string SeatName { get; } = seatName;
    public RouteId RouteId { get; } = routeId;
    public string RouteText { get; } = routeText;
    internal SeatView? SeatView { get; init; }
    public long StateVersion { get; } = stateVersion;
    public long CropRevision { get; } = cropRevision;
    public long ModelRevision { get; } = modelRevision;
    public long CameraEpoch { get; } = cameraEpoch;
    public IReadOnlyList<BoardFirstPaymentRow> Payments { get; } = payments;
    public IReadOnlyList<BoardFirstPaymentCardRow> Cards { get; } = BuildCards(availableCards, payments);
    public int RequiredCards => Payments.Count == 0 ? 0 : Payments[0].Option.Total;
    public int SelectedCount => Cards.Count(card => card.IsSelected);
    public bool CanConfirmPayment => SelectedPayment is not null;

    public string SelectionFeedback => SelectedCount switch
    {
        0 => $"Select {RequiredCards} card{(RequiredCards == 1 ? "" : "s")}.",
        var count when count < RequiredCards =>
            $"Select {RequiredCards - count} more card{(RequiredCards - count == 1 ? "" : "s")}.",
        var count when count > RequiredCards =>
            $"Remove {count - RequiredCards} card{(count - RequiredCards == 1 ? "" : "s")}.",
        _ when CanConfirmPayment => "Ready to pay.",
        _ => "These cards cannot pay for this route. Use one color plus locomotives."
    };

    public BoardFirstPaymentRow? SelectedPayment
    {
        get
        {
            if (SelectedCount != RequiredCards || RequiredCards == 0) return null;
            var chosen = Cards.Where(card => card.IsSelected).ToArray();
            var locomotives = chosen.Count(card => card.Kind == TrainCardKind.Locomotive);
            var colors = chosen.Where(card => card.Kind != TrainCardKind.Locomotive)
                .Select(card => card.Kind).Distinct().ToArray();
            if (colors.Length > 1) return null;
            var option = new PaymentOption(colors.Length == 0 ? TrainCardKind.Locomotive : colors[0],
                chosen.Length - locomotives, locomotives);
            return Payments.FirstOrDefault(payment => payment.Option == option);
        }
    }

    public ImmutableArray<CardId> SelectedCardIds => Cards.Where(card => card.IsSelected)
        .Select(card => card.Id).ToImmutableArray();

    private static IReadOnlyList<BoardFirstPaymentCardRow> BuildCards(
        IReadOnlyList<HeldCard>? availableCards, IReadOnlyList<BoardFirstPaymentRow> payments)
    {
        if (availableCards is null) return [];
        var usableKinds = payments.SelectMany(payment =>
            payment.Option.Locomotives > 0
                ? payment.Option.ColorCards > 0
                    ? new[] { payment.Option.Color, TrainCardKind.Locomotive }
                    : [TrainCardKind.Locomotive]
                : [payment.Option.Color]).ToHashSet();
        return availableCards.Where(card => usableKinds.Contains(card.Kind))
            .Select(card => new BoardFirstPaymentCardRow(card)).ToArray();
    }

    internal void ObserveCardSelection()
    {
        foreach (var card in Cards) card.PropertyChanged += OnCardPropertyChanged;
    }

    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BoardFirstPaymentCardRow.IsSelected)) return;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanConfirmPayment));
        OnPropertyChanged(nameof(SelectionFeedback));
    }
}

public sealed partial class MainViewModel
{
    private readonly BoardFirstRouteDetector _boardFirstRouteDetector = new();
    private readonly BoardFirstMoveFeedbackDetector _boardFirstMoveFeedbackDetector = new();
    private SeatView? _boardFirstSeatView;
    private LegalActions? _boardFirstLegalActions;
    private bool _boardFirstLoading;
    private bool _boardFirstSubmitting;
    private bool _boardFirstAwaitingRemoval;
    private BoardFirstClaimProposal? _boardFirstProposal;
    private string? _boardFirstInvalidMoveMessage;
    private int _boardFirstInvalidMoveMask;
    private BoardInventoryVerifier? _boardFirstCorrectionVerifier;
    private BoardInventoryVerifier? _boardFirstPaymentVerifier;
    private string? _boardFirstPaymentVerificationKey;

    public BoardFirstClaimProposal? BoardFirstProposal
    {
        get => _boardFirstProposal;
        private set
        {
            if (!SetProperty(ref _boardFirstProposal, value)) return;
            value?.ObserveCardSelection();
            if (value is not null) Game.UpdateInventoryProblemMarkers("proposal", null);
            OnPropertyChanged(nameof(ShowBoardFirstClaimProposal));
            OnPropertyChanged(nameof(CanRevealPrivateSeat));
            NotifySoloDrawCommands();
        }
    }

    public bool ShowBoardFirstClaimProposal => IsSingleHumanGame && BoardFirstProposal is not null;

    private void ResetBoardFirstClaimFlow()
    {
        Game.UpdateInventoryProblemMarkers("proposal", null);
        _boardFirstRouteDetector.Reset();
        _boardFirstMoveFeedbackDetector.Reset();
        _boardFirstSeatView = null;
        _boardFirstLegalActions = null;
        BoardFirstProposal = null;
        _boardFirstAwaitingRemoval = false;
        _boardFirstInvalidMoveMessage = null;
        _boardFirstInvalidMoveMask = 0;
        _boardFirstCorrectionVerifier = null;
        _boardFirstPaymentVerifier = null;
        _boardFirstPaymentVerificationKey = null;
        Game.ClearUnverifiedTrainSpaces();
        NotifySoloDrawCommands();
    }

    private void ReconcileBoardFirstClaimFlow(PublicView view)
    {
        if (_boardFirstSeatView is null && BoardFirstProposal is null &&
            _boardFirstInvalidMoveMessage is null) return;
        if (view.Lifecycle == SessionLifecycle.Active && view.TurnPhase == TurnPhase.TurnStart &&
            _boardFirstSeatView?.Public.StateVersion == view.StateVersion &&
            _boardFirstSeatView.SeatId == view.ActiveSeatId) return;
        var hadGuidance = BoardFirstProposal is not null || _boardFirstInvalidMoveMessage is not null;
        ResetBoardFirstClaimFlow();
        if (hadGuidance) Game.ClearGuidance();
    }

    private void ObserveBoardFirstClaim(GameTableAnalysis analysis)
    {
        if (_coordinator is not { } coordinator || !(IsSingleHumanGame || UsesCompanionCameraClaims) ||
            coordinator.Public.Lifecycle != SessionLifecycle.Active ||
            coordinator.Public.TurnPhase != TurnPhase.TurnStart ||
            coordinator.Public.SeatOf(coordinator.Public.ActiveSeatId).Kind != SeatKind.Human)
        {
            if (BoardFirstProposal is not null) ClearBoardFirstProposal("flow-inactive");
            Game.UpdateInventoryProblemMarkers("proposal", null);
            ClearBoardFirstInvalidMove();
            return;
        }

        // Opening a hand or menu does not remove physical trains. Keep an existing warning
        // until fresh board observations clear it, rather than enabling draws while paused.
        if (_operationInProgress || IsGameInputPaused || _boardFirstSubmitting || PrivateSeat is not null)
            return;

        var active = coordinator.Public.SeatOf(coordinator.Public.ActiveSeatId);
        if (_boardFirstAwaitingRemoval)
        {
            _boardFirstCorrectionVerifier ??= NewCardBoardVerifier(coordinator);
            if (_boardFirstCorrectionVerifier.Observe(analysis.Board, analysis.Candidates,
                analysis.CropRevision, analysis.ModelRevision).State is
                BoardInventoryState.Stabilizing or BoardInventoryState.Confirmed)
            {
                _boardFirstAwaitingRemoval = false;
                ClearBoardFirstInvalidMove();
            }
            return;
        }
        if (BoardFirstProposal is { } proposal)
        {
            if (proposal.SessionId != coordinator.SessionId ||
                proposal.SeatId != active.SeatId ||
                proposal.StateVersion != coordinator.Public.StateVersion)
                ClearBoardFirstProposal("turn-changed");
            else
            {
                // The whole board was verified before offering payment. Keep that
                // choice stable; fresh physical verification follows authorization.
                return;
            }
        }

        if (_boardFirstSeatView is null || _boardFirstLegalActions is null ||
            _boardFirstSeatView.Public.StateVersion != coordinator.Public.StateVersion ||
            _boardFirstSeatView.SeatId != active.SeatId)
        {
            if (!_boardFirstLoading) _ = LoadBoardFirstClaimsAsync(coordinator, active.SeatId);
            return;
        }

        var routes = _boardFirstLegalActions.Claims
            .Where(claim => RoutePlacementVerifier.Supports(claim.RouteId.Value, claim.Length))
            .Select(claim => (claim.RouteId.Value, claim.Length))
            .ToArray();
        var observed = _boardFirstRouteDetector.Observe(
            analysis.Board, analysis.Candidates, routes, ToMarkerColor(active.Color),
            $"{coordinator.SessionId.Value}/{coordinator.Public.StateVersion}",
            analysis.CropRevision, analysis.ModelRevision);

        var detectedRoute = observed ?? _boardFirstRouteDetector.ProposedRouteId;
        if (detectedRoute is null)
        {
            Game.UpdateInventoryProblemMarkers("proposal", null);
            ObserveBoardFirstInvalidMove(analysis, coordinator, active);
            return;
        }
        ClearBoardFirstInvalidMove();

        var claim = _boardFirstLegalActions.Claims.SingleOrDefault(candidate => candidate.RouteId.Value == detectedRoute);
        if (claim is null) return;
        var confirmation = ObserveBoardFirstPaymentInventory(analysis, coordinator,
            claim.RouteId, active.Color);
        if (!confirmation.Confirmed)
        {
            Game.ShowGuidance("Checking your route", active.DisplayName,
                confirmation.State == BoardInventoryState.UnexpectedTrain
                    ? "Check the yellow spheres for extra train pieces outside the claimed routes and " +
                      $"{_manifest.Describe(claim.RouteId)} before choosing payment."
                    : $"Verifying your trains on {_manifest.Describe(claim.RouteId)} before choosing payment.");
            return;
        }
        var payments = claim.Payments
            .OrderBy(payment => payment.Locomotives)
            .ThenBy(payment => payment.Color)
            .Select(payment => new BoardFirstPaymentRow(payment, payment.Describe()))
            .ToArray();
        if (payments.Length == 0) return;

        var routeText = _manifest.Describe(claim.RouteId);
        var newProposal = new BoardFirstClaimProposal(
            coordinator.SessionId, active.SeatId, active.DisplayName, claim.RouteId, routeText,
            _boardFirstSeatView.Public.StateVersion, analysis.CropRevision,
            analysis.ModelRevision, analysis.Board.Epoch, payments,
            _boardFirstSeatView.Available.ToArray())
        { SeatView = _boardFirstSeatView };
        BoardFirstProposal = newProposal;
        BoardInteractionLog.Write("board-first.proposal-shown", new
        {
            route = claim.RouteId.Value,
            analysis.Board.Sequence, analysis.Board.Epoch,
            analysis.CropRevision, analysis.ModelRevision
        });
        Game.ShowGuidance(Table.TurnText, active.DisplayName,
            $"Detected your train{(claim.Length == 1 ? "" : "s")} on {routeText}. " +
            "Choose which train cards to spend. " +
            "Claiming this route uses your turn.");
    }

    private void ObserveBoardFirstInvalidMove(GameTableAnalysis analysis, GameCoordinator coordinator,
        PublicSeatSummary active)
    {
        if (_boardFirstSeatView is not { } seatView || _boardFirstLegalActions is not { } legal)
            return;
        var payable = legal.Claims.Select(claim => claim.RouteId.Value)
            .ToHashSet(StringComparer.Ordinal);
        var routes = _manifest.Routes
            .Where(route => !coordinator.Public.RouteOwners.ContainsKey(route.RouteId))
            .Select(route => new BoardFirstDiagnosticRoute(route.RouteId.Value, route.Length,
                payable.Contains(route.RouteId.Value)))
            .ToArray();
        var feedback = _boardFirstMoveFeedbackDetector.Observe(
            analysis.Board, analysis.Candidates, routes, ToMarkerColor(active.Color),
            $"{coordinator.SessionId.Value}/{coordinator.Public.StateVersion}",
            analysis.CropRevision, analysis.ModelRevision);
        if (feedback is null)
        {
            if (_boardFirstInvalidMoveMessage is null) return;
            _boardFirstCorrectionVerifier ??= new BoardInventoryVerifier(coordinator.Public.RouteOwners
                .Select(route => new BoardInventoryRoute(route.Key.Value,
                    ToMarkerColor(coordinator.Public.SeatOf(route.Value).Color),
                    _manifest.Route(route.Key).Length)).ToArray(), verifyClaimedRouteColors: false);
            // Losing a suggestion alone does not prove correction. Once a fresh observation
            // matches the committed board, remove the obsolete message and its markers.
            // Any subsequent draw or payment still requires its own stable camera proof.
            if (_boardFirstCorrectionVerifier.Observe(analysis.Board, analysis.Candidates,
                analysis.CropRevision, analysis.ModelRevision).State is
                BoardInventoryState.Stabilizing or BoardInventoryState.Confirmed)
                ClearBoardFirstInvalidMove();
            return;
        }

        _boardFirstCorrectionVerifier = null;
        var route = _manifest.Route(new RouteId(feedback.RouteId));
        var routeText = _manifest.Describe(route.RouteId);
        var unverifiedCount = feedback.RequiredTrains - feedback.DetectedTrains;
        var explanation = feedback.DetectedTrains < feedback.RequiredTrains
            ? $"{routeText}: camera verified {feedback.DetectedTrains} of {feedback.RequiredTrains} train spaces. " +
              $"Check the yellow marker{(unverifiedCount == 1 ? "" : "s")} and center the " +
              $"train{(unverifiedCount == 1 ? "" : "s")} there."
            : $"{routeText} cannot be claimed yet.";
        if (!feedback.CanClaim)
        {
            if (seatView.TrainsRemaining < route.Length)
                explanation += $" Only {TrainCountText.Format(seatView.TrainsRemaining)} remain.";
            else if (LegalActionCalculator.PaymentsFor(seatView, route.RequiredCardKind,
                         route.Length).IsEmpty)
                explanation += route.RequiredCardKind is { } color
                    ? $" Need {route.Length} {color} train cards (locomotives count)."
                    : $" Need {route.Length} same-color train cards (locomotives count).";
            else
                explanation += " This parallel lane is unavailable.";
            if (feedback.DetectedTrains == feedback.RequiredTrains)
                explanation += " Remove the trains before drawing cards.";
        }
        var messageChanged = _boardFirstInvalidMoveMessage != explanation;
        if (!messageChanged && _boardFirstInvalidMoveMask == feedback.UnverifiedSlotMask) return;
        _boardFirstInvalidMoveMessage = explanation;
        _boardFirstInvalidMoveMask = feedback.UnverifiedSlotMask;
        NotifySoloDrawCommands();
        Game.ShowUnverifiedTrainSpaces(route.RouteId, feedback.RequiredTrains,
            feedback.UnverifiedSlotMask);
        BoardInteractionLog.Write("board-first.invalid-move", new
        {
            route = feedback.RouteId,
            feedback.DetectedTrains,
            feedback.RequiredTrains,
            feedback.CanClaim,
            feedback.UnverifiedSlotMask,
            nearbyCandidates = DescribeNearbyPlacementCandidates(analysis, feedback.RouteId),
            analysis.Board.Sequence,
            analysis.Board.Epoch
        });
        if (messageChanged) Game.ShowGuidance(feedback.CanClaim ? "Check Train Placement" : "Invalid Move",
            active.DisplayName, explanation);
    }

    private void ClearBoardFirstInvalidMove()
    {
        _boardFirstMoveFeedbackDetector.Reset();
        _boardFirstCorrectionVerifier = null;
        Game.ClearUnverifiedTrainSpaces();
        if (_boardFirstInvalidMoveMessage is null) return;
        BoardInteractionLog.Write("board-first.invalid-move-cleared", new
        {
            previous = _boardFirstInvalidMoveMessage
        });
        _boardFirstInvalidMoveMessage = null;
        _boardFirstInvalidMoveMask = 0;
        NotifySoloDrawCommands();
        Game.ClearGuidance();
    }

    private async Task LoadBoardFirstClaimsAsync(GameCoordinator coordinator, SeatId seatId)
    {
        _boardFirstLoading = true;
        try
        {
            var view = await coordinator.GetSeatViewAsync(seatId);
            if (!ReferenceEquals(coordinator, _coordinator) ||
                coordinator.Public.StateVersion != view.Public.StateVersion ||
                coordinator.Public.TurnPhase != TurnPhase.TurnStart ||
                coordinator.Public.ActiveSeatId != seatId) return;
            _boardFirstSeatView = view;
            _boardFirstLegalActions = _rules.GetLegalActions(view);
            _boardFirstRouteDetector.Reset();
            ClearBoardFirstInvalidMove();
        }
        catch (Exception)
        {
            // The next fresh camera analysis retries. No visual observation becomes authority.
        }
        finally { _boardFirstLoading = false; }
    }

    private void ClearBoardFirstProposal(string reason = "flow-reset")
    {
        if (BoardFirstProposal is not { } proposal) return;
        BoardInteractionLog.Write("board-first.proposal-cleared", new
        {
            route = proposal.RouteId.Value,
            reason
        });
        BoardFirstProposal = null;
        _boardFirstPaymentVerifier = null;
        _boardFirstPaymentVerificationKey = null;
        Game.ClearGuidance();
    }

    private BoardInventoryVerifier CreateBoardFirstPaymentVerifier(GameCoordinator coordinator,
        RouteId routeId, PlayerColor color) => new(coordinator.Public.RouteOwners
            .Select(route => new BoardInventoryRoute(route.Key.Value,
                ToMarkerColor(coordinator.Public.SeatOf(route.Value).Color),
                _manifest.Route(route.Key).Length)).ToArray(),
            new BoardInventoryRoute(routeId.Value, ToMarkerColor(color), _manifest.Route(routeId).Length),
            (1 << _manifest.Route(routeId).Length) - 1, verifyClaimedRouteColors: false);

    private BoardInventoryObservation ObserveBoardFirstPaymentInventory(GameTableAnalysis analysis,
        GameCoordinator coordinator, RouteId routeId, PlayerColor color)
    {
        var key = $"{coordinator.SessionId.Value}/{coordinator.Public.StateVersion}/{routeId.Value}";
        if (_boardFirstPaymentVerificationKey != key)
        {
            _boardFirstPaymentVerifier = CreateBoardFirstPaymentVerifier(coordinator, routeId, color);
            _boardFirstPaymentVerificationKey = key;
        }
        var observation = _boardFirstPaymentVerifier!.Observe(analysis.Board, analysis.Candidates,
            analysis.CropRevision, analysis.ModelRevision);
        Game.UpdateInventoryProblemMarkers("proposal", observation);
        return observation;
    }

    [RelayCommand]
    private void CancelBoardFirstClaim()
    {
        if (BoardFirstProposal is { } proposal) CancelBoardFirstProposal(proposal);
    }

    private bool CancelBoardFirstProposal(BoardFirstClaimProposal proposal)
    {
        if (_boardFirstSubmitting || (_operationInProgress && !_handlingRemoteCommand) ||
            !BoardFirstProposalContextCurrent(proposal)) return false;
        ClearBoardFirstProposal("cancelled");
        _boardFirstRouteDetector.Reset();
        _boardFirstAwaitingRemoval = true;
        _boardFirstCorrectionVerifier = NewCardBoardVerifier(_coordinator!);
        _boardFirstInvalidMoveMessage = $"Remove your trains from {proposal.RouteText} to choose another action.";
        var length = _manifest.Route(proposal.RouteId).Length;
        Game.ShowUnverifiedTrainSpaces(proposal.RouteId, length, (1 << length) - 1);
        Game.ShowGuidance("Remove trains", proposal.SeatName, _boardFirstInvalidMoveMessage);
        NotifySoloDrawCommands();
        return true;
    }

    private bool BoardFirstProposalContextCurrent(BoardFirstClaimProposal proposal) =>
        ReferenceEquals(BoardFirstProposal, proposal) &&
        _coordinator is { StorageFaulted: false } coordinator &&
        coordinator.SessionId == proposal.SessionId &&
        coordinator.Public.Lifecycle == SessionLifecycle.Active &&
        coordinator.Public.StateVersion == proposal.StateVersion &&
        coordinator.Public.TurnPhase == TurnPhase.TurnStart &&
        coordinator.Public.ActiveSeatId == proposal.SeatId &&
        !IsGameInputPaused && _windowActive && _systemAvailable &&
        IsGameplayScreenActive(Screen.Table) && !_exitRequested && !_toolsDisposed &&
        !_mustReload && !NeedsBoardReconciliation && _scoreMarkerStep is null;

    [RelayCommand]
    private Task ConfirmBoardFirstClaimAsync() =>
        AuthorizeBoardFirstClaimAsync(BoardFirstProposal?.SelectedPayment);

    private async Task AuthorizeBoardFirstClaimAsync(BoardFirstPaymentRow? payment)
    {
        if (payment is null || BoardFirstProposal is not { } proposal ||
            !proposal.CanConfirmPayment || proposal.SelectedPayment != payment) return;
        await CommitBoardFirstClaimAsync(proposal, payment, proposal.SelectedCardIds, CommandId.New());
    }

    private async Task<SubmitOutcome?> CommitBoardFirstClaimAsync(BoardFirstClaimProposal proposal,
        BoardFirstPaymentRow payment, ImmutableArray<CardId> cards, CommandId commandId,
        bool remote = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_boardFirstSubmitting || (_operationInProgress && !(remote && _handlingRemoteCommand)) ||
            !BoardFirstProposalContextCurrent(proposal) || !proposal.Payments.Contains(payment) ||
            _coordinator is not { } coordinator || proposal.SeatView is not { } seatView)
            return null;

        _boardFirstSubmitting = true;
        var wasBusy = _operationInProgress;
        SetOperationInProgress(true);
        try
        {
            if (cards.Length != payment.Option.Total ||
                cards.Distinct().Count() != cards.Length ||
                cards.Any(cardId => !seatView.Available.Any(card => card.Id == cardId))) return null;
            var command = new PlanClaim(
                new CommandEnvelope(coordinator.SessionId, commandId,
                    proposal.StateVersion, proposal.SeatId), proposal.RouteId, cards);
            var outcome = await coordinator.SubmitAsync(command, cancellationToken);
            if (!outcome.IsAccepted)
            {
                Status = outcome.Result.Rejection?.Message ?? "That route could not be claimed.";
                if (outcome.Result.Rejection?.Code == "StorageFaulted") RequireReload();
                return outcome;
            }

            // Payment is now durably reserved. Start a separate physical check, using
            // only captures after this boundary, before spending cards or handing off.
            if (coordinator.Public.PendingClaim is { } authorized)
            {
                var latest = Camera.GameTableAnalysis;
                _boardFirstPaymentCheck = new(authorized.OperationId, DateTimeOffset.UtcNow,
                    latest?.Board.Epoch, latest?.Board.Sequence);
                _routePlacementVerifier.Reset();
                ResetPlacementInventory();
            }
            await RefreshAsync();
            if (ReferenceEquals(coordinator, _coordinator) &&
                coordinator.SessionId == proposal.SessionId &&
                coordinator.Public.StateVersion == outcome.StateVersion &&
                coordinator.Public.ActiveSeatId == proposal.SeatId &&
                coordinator.Public.TurnPhase == TurnPhase.AwaitingPhysicalPlacement &&
                coordinator.Public.PendingClaim is { } pending &&
                pending.RouteId == proposal.RouteId && pending.SeatId == proposal.SeatId &&
                Table.Placement is { AwaitingRestore: false } placement &&
                placement.OperationId == pending.OperationId &&
                !_mustReload && !NeedsBoardReconciliation && _windowActive)
            {
                Game.ShowGuidance(Table.TurnText, proposal.SeatName,
                    "Payment accepted. Keep the board clear while the camera checks it before the next turn.");
            }
            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { RequireReload(); return null; }
        finally
        {
            _boardFirstSubmitting = false;
            SetOperationInProgress(wasBusy);
        }
    }
}
