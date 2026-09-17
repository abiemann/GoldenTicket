using CommunityToolkit.Mvvm.Input;
using GoldenTicket.Application;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

public sealed record BoardFirstPaymentRow(PaymentOption Option, string Description);

/// <summary>A camera suggestion, not an authorized or committed claim.</summary>
public sealed class BoardFirstClaimProposal(
    SessionId sessionId, SeatId seatId, string seatName, RouteId routeId, string routeText,
    long stateVersion, long cropRevision, long modelRevision, long cameraEpoch,
    IReadOnlyList<BoardFirstPaymentRow> payments)
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
}

public sealed partial class MainViewModel
{
    private readonly BoardFirstRouteDetector _boardFirstRouteDetector = new();
    private SeatView? _boardFirstSeatView;
    private LegalActions? _boardFirstLegalActions;
    private bool _boardFirstLoading;
    private bool _boardFirstSubmitting;
    private BoardFirstClaimProposal? _boardFirstProposal;

    public BoardFirstClaimProposal? BoardFirstProposal
    {
        get => _boardFirstProposal;
        private set
        {
            if (!SetProperty(ref _boardFirstProposal, value)) return;
            OnPropertyChanged(nameof(ShowBoardFirstClaimProposal));
            OnPropertyChanged(nameof(CanRevealPrivateSeat));
        }
    }

    public bool ShowBoardFirstClaimProposal => BoardFirstProposal is not null;

    private void ResetBoardFirstClaimFlow()
    {
        _boardFirstRouteDetector.Reset();
        _boardFirstSeatView = null;
        _boardFirstLegalActions = null;
        BoardFirstProposal = null;
    }

    private void ReconcileBoardFirstClaimFlow(PublicView view)
    {
        if (_boardFirstSeatView is null && BoardFirstProposal is null) return;
        if (view.Lifecycle == SessionLifecycle.Active && view.TurnPhase == TurnPhase.TurnStart &&
            _boardFirstSeatView?.Public.StateVersion == view.StateVersion &&
            _boardFirstSeatView.SeatId == view.ActiveSeatId) return;
        var hadProposal = BoardFirstProposal is not null;
        ResetBoardFirstClaimFlow();
        if (hadProposal) Game.ClearGuidance();
    }

    private void ObserveBoardFirstClaim(GameTableAnalysis analysis)
    {
        if (_coordinator is not { } coordinator || !IsSingleHumanGame ||
            coordinator.Public.Lifecycle != SessionLifecycle.Active ||
            coordinator.Public.TurnPhase != TurnPhase.TurnStart ||
            coordinator.Public.SeatOf(coordinator.Public.ActiveSeatId).Kind != SeatKind.Human ||
            _operationInProgress || _boardFirstSubmitting || PrivateSeat is not null)
        {
            if (BoardFirstProposal is not null) ClearBoardFirstProposal();
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
            // Removing the pieces withdraws the camera proposal; no cards were reserved or spent.
            if (_boardFirstRouteDetector.ProposedRouteId != existing.RouteId.Value)
                ClearBoardFirstProposal();
            return;
        }
        if (observed is null) return;

        var claim = _boardFirstLegalActions.Claims.SingleOrDefault(candidate => candidate.RouteId.Value == observed);
        if (claim is null) return;
        var payments = claim.Payments
            .OrderBy(payment => payment.Locomotives)
            .ThenBy(payment => payment.Color)
            .Select(payment => new BoardFirstPaymentRow(payment, payment.Describe()))
            .ToArray();
        if (payments.Length == 0) return;

        var routeText = _manifest.Describe(claim.RouteId);
        BoardFirstProposal = new BoardFirstClaimProposal(
            coordinator.SessionId, active.SeatId, active.DisplayName, claim.RouteId, routeText,
            _boardFirstSeatView.Public.StateVersion, analysis.CropRevision,
            analysis.ModelRevision, analysis.Board.Epoch, payments)
        { SeatView = _boardFirstSeatView };
        Game.ShowGuidance(Table.TurnText, active.DisplayName,
            $"Detected your trains on {routeText}. Choose which train cards to spend. " +
            "Claiming this route uses your turn.");
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
    private async Task AuthorizeBoardFirstClaimAsync(BoardFirstPaymentRow? payment)
    {
        if (_boardFirstSubmitting || _operationInProgress || payment is null ||
            BoardFirstProposal is not { } proposal || !proposal.Payments.Contains(payment) ||
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
            !_windowActive || !IsGameplayScreenActive(Screen.Table) || _mustReload || NeedsBoardReconciliation)
            return;

        _boardFirstSubmitting = true;
        SetOperationInProgress(true);
        try
        {
            var cards = LegalActionCalculator.ResolveCards(seatView, payment.Option);
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
                    $"Keep your {pending.TrainCount} trains on {proposal.RouteText}. " +
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
