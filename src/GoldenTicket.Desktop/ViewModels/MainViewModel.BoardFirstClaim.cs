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
    private BoardFirstClaimProposal? _boardFirstProposal;
    private string? _boardFirstInvalidMoveMessage;
    private DateTimeOffset? _boardFirstInvalidMoveAbsentSince;

    public BoardFirstClaimProposal? BoardFirstProposal
    {
        get => _boardFirstProposal;
        private set
        {
            if (!SetProperty(ref _boardFirstProposal, value)) return;
            value?.ObserveCardSelection();
            OnPropertyChanged(nameof(ShowBoardFirstClaimProposal));
            OnPropertyChanged(nameof(CanRevealPrivateSeat));
            NotifySoloDrawCommands();
        }
    }

    public bool ShowBoardFirstClaimProposal => BoardFirstProposal is not null;

    private void ResetBoardFirstClaimFlow()
    {
        _boardFirstRouteDetector.Reset();
        _boardFirstMoveFeedbackDetector.Reset();
        _boardFirstSeatView = null;
        _boardFirstLegalActions = null;
        BoardFirstProposal = null;
        _boardFirstInvalidMoveMessage = null;
        _boardFirstInvalidMoveAbsentSince = null;
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
        if (_coordinator is not { } coordinator || !IsSingleHumanGame ||
            coordinator.Public.Lifecycle != SessionLifecycle.Active ||
            coordinator.Public.TurnPhase != TurnPhase.TurnStart ||
            coordinator.Public.SeatOf(coordinator.Public.ActiveSeatId).Kind != SeatKind.Human ||
            _operationInProgress || IsGameExitMenuOpen || _boardFirstSubmitting || PrivateSeat is not null)
        {
            if (BoardFirstProposal is not null) ClearBoardFirstProposal();
            ClearBoardFirstInvalidMove();
            return;
        }

        var active = coordinator.Public.SeatOf(coordinator.Public.ActiveSeatId);
        if (BoardFirstProposal is { } proposal &&
            (proposal.SessionId != coordinator.SessionId ||
             proposal.SeatId != active.SeatId ||
             proposal.StateVersion != coordinator.Public.StateVersion ||
             proposal.CropRevision != analysis.CropRevision ||
             proposal.ModelRevision != analysis.ModelRevision ||
             proposal.CameraEpoch != analysis.Board.Epoch))
            ClearBoardFirstProposal();

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

        if (BoardFirstProposal is { } existing)
        {
            ClearBoardFirstInvalidMove();
            // Removing the pieces withdraws the camera proposal; no cards were reserved or spent.
            if (_boardFirstRouteDetector.ProposedRouteId != existing.RouteId.Value)
                ClearBoardFirstProposal();
            return;
        }
        if (observed is null)
        {
            ObserveBoardFirstInvalidMove(analysis, coordinator, active);
            return;
        }
        ClearBoardFirstInvalidMove();

        var claim = _boardFirstLegalActions.Claims.SingleOrDefault(candidate => candidate.RouteId.Value == observed);
        if (claim is null) return;
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
            _boardFirstInvalidMoveAbsentSince ??= analysis.Board.CapturedAt;
            if (analysis.Board.CapturedAt - _boardFirstInvalidMoveAbsentSince >= TimeSpan.FromSeconds(1))
                ClearBoardFirstInvalidMove();
            return;
        }

        _boardFirstInvalidMoveAbsentSince = null;
        var route = _manifest.Route(new RouteId(feedback.RouteId));
        var routeText = _manifest.Describe(route.RouteId);
        var explanation = feedback.DetectedTrains < feedback.RequiredTrains
            ? $"{routeText}: {feedback.DetectedTrains} of {feedback.RequiredTrains} train spaces detected. " +
              "Fill every space."
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
        }
        if (_boardFirstInvalidMoveMessage == explanation) return;
        _boardFirstInvalidMoveMessage = explanation;
        BoardInteractionLog.Write("board-first.invalid-move", new
        {
            route = feedback.RouteId,
            feedback.DetectedTrains,
            feedback.RequiredTrains,
            feedback.CanClaim,
            analysis.Board.Sequence,
            analysis.Board.Epoch
        });
        Game.ShowGuidance("Invalid Move", active.DisplayName, explanation);
    }

    private void ClearBoardFirstInvalidMove()
    {
        _boardFirstMoveFeedbackDetector.Reset();
        _boardFirstInvalidMoveAbsentSince = null;
        if (_boardFirstInvalidMoveMessage is null) return;
        BoardInteractionLog.Write("board-first.invalid-move-cleared", new
        {
            previous = _boardFirstInvalidMoveMessage
        });
        _boardFirstInvalidMoveMessage = null;
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

    private void ClearBoardFirstProposal()
    {
        if (BoardFirstProposal is null) return;
        BoardFirstProposal = null;
        Game.ClearGuidance();
    }

    [RelayCommand]
    private Task ConfirmBoardFirstClaimAsync() =>
        AuthorizeBoardFirstClaimAsync(BoardFirstProposal?.SelectedPayment);

    private async Task AuthorizeBoardFirstClaimAsync(BoardFirstPaymentRow? payment)
    {
        if (_boardFirstSubmitting || _operationInProgress || payment is null ||
            BoardFirstProposal is not { } proposal || !proposal.Payments.Contains(payment) ||
            !proposal.CanConfirmPayment || proposal.SelectedPayment != payment ||
            _coordinator is not { } coordinator || !ReferenceEquals(coordinator, _coordinator) ||
            coordinator.SessionId != proposal.SessionId ||
            coordinator.Public.StateVersion != proposal.StateVersion ||
            coordinator.Public.TurnPhase != TurnPhase.TurnStart ||
            coordinator.Public.ActiveSeatId != proposal.SeatId ||
            Camera.GameTableAnalysis is not { } current || current.Board.Age > TimeSpan.FromSeconds(2) ||
            current.Board.Epoch != proposal.CameraEpoch ||
            current.CropRevision != proposal.CropRevision ||
            current.ModelRevision != proposal.ModelRevision ||
            proposal.SeatView is not { } seatView ||
            IsGameExitMenuOpen || !_windowActive || !IsGameplayScreenActive(Screen.Table) ||
            _mustReload || NeedsBoardReconciliation)
            return;

        _boardFirstSubmitting = true;
        SetOperationInProgress(true);
        try
        {
            var cards = proposal.SelectedCardIds;
            if (cards.Length != payment.Option.Total ||
                cards.Any(cardId => !seatView.Available.Any(card => card.Id == cardId))) return;
            var command = new PlanClaim(
                new CommandEnvelope(coordinator.SessionId, CommandId.New(),
                    proposal.StateVersion, proposal.SeatId), proposal.RouteId, cards);
            var outcome = await coordinator.SubmitAsync(command);
            if (!outcome.IsAccepted)
            {
                Status = outcome.Result.Rejection?.Message ?? "That route could not be claimed.";
                if (outcome.Result.Rejection?.Code == "StorageFaulted") RequireReload();
                return;
            }

            ResetBoardFirstClaimFlow();
            Game.ClearGuidance();
            await PumpAsync();
            if (ReferenceEquals(coordinator, _coordinator) &&
                coordinator.Public.PendingClaim is { } pending &&
                pending.RouteId == proposal.RouteId && pending.SeatId == proposal.SeatId)
                Game.ShowGuidance(Table.TurnText, proposal.SeatName,
                    $"Keep your {TrainCountText.Format(pending.TrainCount)} on {proposal.RouteText}. " +
                    "The camera is checking every space before the claim is scored.");
        }
        catch (Exception) { RequireReload(); }
        finally
        {
            _boardFirstSubmitting = false;
            SetOperationInProgress(false);
        }
    }
}
