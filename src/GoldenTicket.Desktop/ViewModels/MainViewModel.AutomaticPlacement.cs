using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private sealed record ScoreMarkerStep(
        SessionId SessionId, OperationId OperationId, SeatId SeatId, string SeatName,
        PlayerColor Color, int FromPrintedScore, int ToPrintedScore, int Points)
    {
        public bool ThankYouFinished { get; set; }
    }

    private readonly RoutePlacementVerifier _routePlacementVerifier = new();
    private readonly ScoreMarkerMoveVerifier _scoreMarkerMoveVerifier = new();
    private ScoreMarkerStep? _scoreMarkerStep;
    private bool _claimCompletionInProgress;
    private bool _scoreCompletionInProgress;
    private long _automaticFlowGeneration;

    private void ResetAutomaticPhysicalFlow()
    {
        _automaticFlowGeneration++;
        _scoreMarkerStep = null;
        _claimCompletionInProgress = false;
        _scoreCompletionInProgress = false;
        _routePlacementVerifier.Reset();
        _scoreMarkerMoveVerifier.Reset();
        Game.ClearGuidance();
        OnPropertyChanged(nameof(CanRevealPrivateSeat));
    }

    private void ObserveGameTableAnalysis()
    {
        if (Camera.GameTableAnalysis is not { } analysis || !Camera.IsGameTablePreviewUpright ||
            analysis.Board.Age > TimeSpan.FromSeconds(2))
        {
            _routePlacementVerifier.Reset();
            _scoreMarkerMoveVerifier.Reset();
            return;
        }

        if (_coordinator is not { } coordinator || !IsGameplayScreenActive(Screen.Table) ||
            _mustReload || NeedsBoardReconciliation) return;

        if (_scoreMarkerStep is { ThankYouFinished: true } scoreStep)
        {
            if (_scoreCompletionInProgress || scoreStep.SessionId != coordinator.SessionId) return;
            var scoreObservation = _scoreMarkerMoveVerifier.Observe(
                analysis.Board, analysis.Scores, ToMarkerColor(scoreStep.Color),
                scoreStep.ToPrintedScore, scoreStep.OperationId.Value,
                analysis.CropRevision, analysis.ModelRevision);
            if (scoreObservation.Confirmed)
                _ = FinishScoreMarkerStepAsync(scoreStep);
            return;
        }
        if (_scoreMarkerStep is not null || _claimCompletionInProgress || _operationInProgress ||
            Table.Placement is not { AwaitingRestore: false } placement ||
            coordinator.Public.PendingClaim is not { } pending ||
            pending.OperationId != placement.OperationId ||
            pending.RouteId != placement.RouteId ||
            pending.SeatId != placement.SeatId ||
            !RoutePlacementVerifier.Supports(placement.RouteId.Value, placement.TrainCount))
        {
            _routePlacementVerifier.Reset();
            return;
        }

        var placementObservation = _routePlacementVerifier.Observe(
            analysis.Board, analysis.Candidates, placement.RouteId.Value,
            ToMarkerColor(placement.Color), placement.TrainCount,
            placement.OperationId.Value, analysis.CropRevision, analysis.ModelRevision);
        if (placementObservation.Confirmed)
            _ = AcceptCameraPlacementAsync(placement, analysis);
    }

    private Task AcceptCameraPlacementAsync(PlacementInstruction placement, GameTableAnalysis analysis) =>
        AcceptPhysicalPlacementAsync(placement, EvidenceKind.CameraAutomatic, analysis.ModelId,
            $"{placement.TrainCount} {placement.Color} train pieces matched every measured slot of " +
            $"{placement.RouteId.Value} in distinct upright frames at least one second apart; " +
            $"camera epoch {analysis.Board.Epoch}, frame {analysis.Board.Sequence}, " +
            $"crop {analysis.CropRevision}, model revision {analysis.ModelRevision}.");

    private async Task AcceptPhysicalPlacementAsync(PlacementInstruction placement,
        EvidenceKind evidence, string source, string summary)
    {
        if (_claimCompletionInProgress || _scoreMarkerStep is not null || _coordinator is not { } coordinator ||
            !CanSubmitOperator() || coordinator.Public.PendingClaim?.OperationId != placement.OperationId ||
            coordinator.Public.StateVersion != placement.StateVersion) return;

        _claimCompletionInProgress = true;
        SetOperationInProgress(true);
        HidePrivateSeat();
        var generation = _automaticFlowGeneration;
        var beforeScore = coordinator.Public.SeatOf(placement.SeatId).RouteScore;
        try
        {
            var command = new SubmitClaimEvidence(
                new CommandEnvelope(coordinator.SessionId, CommandId.New(),
                    placement.StateVersion, placement.SeatId),
                placement.OperationId, evidence, source, summary);
            var outcome = await coordinator.SubmitAsync(command);
            if (!outcome.IsAccepted)
            {
                Status = outcome.Result.Rejection?.Message ?? "The claim was not accepted.";
                if (outcome.Result.Rejection?.Code == "StorageFaulted") RequireReload();
                _routePlacementVerifier.Reset();
                return;
            }

            await RefreshAsync();
            if (!ReferenceEquals(coordinator, _coordinator) || generation != _automaticFlowGeneration) return;
            var points = _manifest.RulesConstants.ScoreForLength(
                _manifest.Route(placement.RouteId).Length);
            if (coordinator.Public.SeatOf(placement.SeatId).RouteScore != beforeScore + points)
            {
                RequireReload();
                return;
            }

            var step = new ScoreMarkerStep(coordinator.SessionId, placement.OperationId,
                placement.SeatId, placement.SeatName, placement.Color,
                PrintedScore(beforeScore), PrintedScore(beforeScore + points), points);
            _scoreMarkerStep = step;
            _scoreMarkerMoveVerifier.Reset();
            OnPropertyChanged(nameof(CanRevealPrivateSeat));
            Game.ShowGuidance("Scoring", placement.SeatName, "Thank you");
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (_scoreMarkerStep != step || generation != _automaticFlowGeneration ||
                !ReferenceEquals(coordinator, _coordinator)) return;
            step.ThankYouFinished = true;
            Game.ShowGuidance("Scoring", step.SeatName,
                $"Move {step.SeatName}'s {step.Color} scoring marker {step.Points} spaces " +
                $"from {step.FromPrintedScore} to {step.ToPrintedScore}. " +
                $"The camera will continue when it sees the marker on {step.ToPrintedScore}.");
        }
        catch (Exception)
        {
            RequireReload();
        }
        finally
        {
            _claimCompletionInProgress = false;
            SetOperationInProgress(false);
        }
    }

    private async Task FinishScoreMarkerStepAsync(ScoreMarkerStep step)
    {
        if (_scoreCompletionInProgress || _scoreMarkerStep != step || !step.ThankYouFinished ||
            _coordinator?.SessionId != step.SessionId) return;
        _scoreCompletionInProgress = true;
        SetOperationInProgress(true);
        var generation = _revealGeneration;
        try
        {
            _scoreMarkerStep = null;
            _scoreMarkerMoveVerifier.Reset();
            Game.ClearGuidance();
            await PumpAsync();
        }
        catch (Exception)
        {
            RequireReload();
        }
        finally
        {
            _scoreCompletionInProgress = false;
            SetOperationInProgress(false);
        }
        await ShowSingleHumanCardsAsync(generation);
    }

    private static int PrintedScore(int routeScore) => routeScore % 100 + 1;

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
