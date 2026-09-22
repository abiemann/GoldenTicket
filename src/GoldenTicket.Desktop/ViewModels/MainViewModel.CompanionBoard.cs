using GoldenTicket.CompanionHost;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    // Camera use is latched for the match: losing a frame must not reopen manual claims.
    private bool UsesCompanionCameraClaims => CanConnectPhone && Connection.UseQuickPlay &&
        UsesCameraForCardActions;

    private CompanionGuidance? CurrentCompanionGuidance()
    {
        if (!CanConnectPhone || !Connection.UseQuickPlay || _toolsDisposed || _exitRequested ||
            IsGameInputPaused || !_systemAvailable || _mustReload || NeedsBoardReconciliation ||
            IsCheckingResumedGame || !IsGameplayScreenActive(Screen.Table) ||
            _coordinator is not { StorageFaulted: false } coordinator)
            return null;

        var view = coordinator.Public;
        if (view.IsGameplaySuspended || view.TurnPhase == TurnPhase.RulesDecisionRequired)
            return null;
        // A final claim can finish the digital match before its physical marker has moved.
        // Mirror the live laptop step, including its brief acknowledgment, until it completes.
        var scoring = _scoreMarkerStep is { } step && step.SessionId == coordinator.SessionId &&
            view.Lifecycle is SessionLifecycle.Active or SessionLifecycle.Finished;
        var placing = view.Lifecycle == SessionLifecycle.Active && Table.Placement is { } placement &&
            view.PendingClaim is { } pending && placement.OperationId == pending.OperationId &&
            placement.StateVersion == view.StateVersion && placement.SeatId == pending.SeatId;
        return scoring || placing || IsCheckingBoardBeforeNextTurn
            ? new(Game.GuidanceSeat, Game.GuidanceInstruction)
            : null;
    }

    private CompanionBoardInteraction? CurrentCompanionBoardInteraction()
    {
        if (!CanConnectPhone || !Connection.UseQuickPlay || _coordinator is not { } coordinator)
            return null;
        if (!UsesCompanionCameraClaims) return new(false, false, null);

        var proposal = BoardFirstProposal;
        if (proposal is not null && (proposal.SessionId != coordinator.SessionId ||
            proposal.SeatId != coordinator.Public.ActiveSeatId ||
            proposal.StateVersion != coordinator.Public.StateVersion)) proposal = null;
        var ready = proposal is not null &&
            !IsGameInputPaused && _windowActive && _systemAvailable &&
            !_mustReload && !NeedsBoardReconciliation && _scoreMarkerStep is null;
        CompanionDetectedRoute? detected = proposal is null ? null : new(proposal.ProposalId,
            proposal.RouteId.Value, proposal.RouteText, _manifest.Route(proposal.RouteId).Length, ready);
        var blocked = IsCheckingBoardBeforeNextTurn || IsHumanCardPhase(coordinator) && (proposal is not null ||
            _boardFirstRouteDetector.ProposedRouteId is not null ||
            _boardFirstInvalidMoveMessage is not null || _scoreMarkerStep is not null);
        var message = IsCheckingBoardBeforeNextTurn
            ? _cardActionBoardWarning ?? "The camera is checking the board before the next turn."
            : proposal is not null
            ? ready ? $"Your trains on {proposal.RouteText} are confirmed. Choose cards to pay."
                : "Resume the game to choose cards for your detected route."
            : _boardFirstInvalidMoveMessage ??
                (_boardFirstRouteDetector.ProposedRouteId is not null
                    ? "The camera is confirming your route before payment."
                    : null);
        return new(true, blocked, message, detected);
    }

    private async Task<CompanionCommandReceipt?> InterceptCompanionCommandAsync(SeatId seat,
        CompanionCommand command, CancellationToken cancellationToken)
    {
        CompanionCommandReceipt Refused(string code, string message) =>
            new(false, false, _coordinator?.Public.StateVersion ?? command.ExpectedStateVersion, code, message);
        cancellationToken.ThrowIfCancellationRequested();

        if (command.Kind == "planClaim" && UsesCompanionCameraClaims)
            return Refused("DetectedRouteRequired", "Place your trains on the board, then pay for the route the camera confirms.");

        if (command.Kind == "cancelDetectedRoute")
        {
            if (!UsesCompanionCameraClaims || !Guid.TryParseExact(command.CommandId, "N", out _) ||
                !Guid.TryParseExact(command.DetectedClaimId, "N", out _) ||
                BoardFirstProposal is not { } proposal || proposal.ProposalId != command.DetectedClaimId ||
                proposal.RouteId.Value != command.RouteId || proposal.SeatId != seat ||
                proposal.SessionId.Value != command.SessionId || proposal.StateVersion != command.ExpectedStateVersion ||
                !CancelBoardFirstProposal(proposal))
                return Refused("DetectedRouteChanged", "This route selection is no longer current.");
            return new(true, false, _coordinator!.Public.StateVersion, null, "Route selection cancelled.");
        }

        if (command.Kind == "payDetectedRoute")
        {
            if (!UsesCompanionCameraClaims || !Guid.TryParseExact(command.CommandId, "N", out _) ||
                !Guid.TryParseExact(command.DetectedClaimId, "N", out _) ||
                BoardFirstProposal is not { } proposal || proposal.ProposalId != command.DetectedClaimId ||
                proposal.RouteId.Value != command.RouteId || proposal.SeatId != seat ||
                proposal.SessionId.Value != command.SessionId || proposal.StateVersion != command.ExpectedStateVersion ||
                proposal.SeatView is not { } view || command.Payment is null ||
                proposal.Payments.FirstOrDefault(payment => payment.Option == command.Payment) is not { } payment)
                return Refused("DetectedRouteChanged", "The route or payment changed. Wait for the camera to confirm your trains again.");

            var cards = LegalActionCalculator.ResolveCards(view, payment.Option);
            var outcome = await CommitBoardFirstClaimAsync(proposal, payment, cards,
                new CommandId(command.CommandId), remote: true, cancellationToken);
            if (outcome is null)
                return Refused(_mustReload ? "StorageFaulted" : "DetectedRouteChanged", _mustReload
                    ? "Check the laptop before continuing."
                    : "This route selection is no longer current. Refresh the game before paying.");
            return new(outcome.IsAccepted, outcome.WasDuplicate, _coordinator!.Public.StateVersion,
                outcome.Result.Rejection?.Code, outcome.IsAccepted
                    ? "Payment saved. The camera is checking the board before the next turn."
                    : outcome.Result.Rejection!.Message);
        }

        if (command.Kind is "drawTrain" or "drawTickets" or "keepTickets" &&
            _coordinator is { } coordinator && IsHumanCardPhase(coordinator))
        {
            if (coordinator.SessionId.Value != command.SessionId ||
                coordinator.Public.StateVersion != command.ExpectedStateVersion || coordinator.Public.ActiveSeatId != seat)
                return Refused("RefreshRequired", "Hide and reveal again to use the current turn.");
            if (IsCheckingBoardBeforeNextTurn || BoardFirstProposal is not null ||
                _boardFirstInvalidMoveMessage is not null || _boardFirstRouteDetector.ProposedRouteId is not null)
                return Refused("BoardCheckRequired", CurrentCompanionBoardInteraction()?.Message ??
                    "Check the trains on the board before drawing cards.");
            cancellationToken.ThrowIfCancellationRequested();
        }
        return null;
    }
}
