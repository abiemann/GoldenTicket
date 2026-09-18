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
    private BoardInventoryVerifier? _placementInventoryVerifier;
    private string? _placementInventoryKey;
    private bool _showingPlacementInventoryCorrection;
    private readonly ScoreMarkerMoveVerifier _scoreMarkerMoveVerifier = new();
    private ScoreMarkerStep? _scoreMarkerStep;
    private bool _claimCompletionInProgress;
    private bool _scoreCompletionInProgress;
    private long _automaticFlowGeneration;
    private string? _placementVerificationBlock;

    public bool ShowScoreMarkerDetectionPrompt => _scoreMarkerStep is { ThankYouFinished: true } &&
        !_mustReload && !NeedsBoardReconciliation && IsGameplayScreenActive(Screen.Table);

    private void NotifyScoreMarkerDetectionPromptChanged() =>
        OnPropertyChanged(nameof(ShowScoreMarkerDetectionPrompt));

    private void ResetAutomaticPhysicalFlow()
    {
        BoardInteractionLog.Write("placement.flow-reset", new { hasPlacement = Table.Placement is not null });
        _automaticFlowGeneration++;
        _scoreMarkerStep = null;
        _claimCompletionInProgress = false;
        _scoreCompletionInProgress = false;
        _placementVerificationBlock = null;
        _routePlacementVerifier.Reset();
        ResetPlacementInventory();
        _scoreMarkerMoveVerifier.Reset();
        ResetBoardFirstClaimFlow();
        Game.ClearGuidance();
        NotifyScoreMarkerDetectionPromptChanged();
        OnPropertyChanged(nameof(CanRevealPrivateSeat));
    }

    private void ObserveGameTableAnalysis()
    {
        if (Camera.GameTableAnalysis is not { } analysis || !Camera.IsGameTablePreviewUpright ||
            analysis.Board.Age > TimeSpan.FromSeconds(2))
        {
            NotePlacementVerificationBlock(Camera.GameTableAnalysis is null ? "camera-analysis-unavailable" :
                !Camera.IsGameTablePreviewUpright ? "board-orientation-unverified" : "camera-analysis-stale");
            _routePlacementVerifier.Reset();
            ResetPlacementInventory();
            _scoreMarkerMoveVerifier.Reset();
            return;
        }

        if (_coordinator is not { } coordinator || IsGameExitMenuOpen || !IsGameplayScreenActive(Screen.Table) ||
            _mustReload || NeedsBoardReconciliation)
        {
            NotePlacementVerificationBlock(_coordinator is null ? "no-game" : IsGameExitMenuOpen ? "exit-menu-open" :
                !IsGameplayScreenActive(Screen.Table) ? "table-not-active" : _mustReload ? "reload-required" :
                "board-reconciliation-required");
            return;
        }

        if (_scoreMarkerStep is { ThankYouFinished: true } scoreStep)
        {
            NotePlacementVerificationBlock("score-marker-step");
            if (_scoreCompletionInProgress || scoreStep.SessionId != coordinator.SessionId) return;
            var scoreObservation = _scoreMarkerMoveVerifier.Observe(
                analysis.Board, analysis.Scores, ToMarkerColor(scoreStep.Color),
                scoreStep.ToPrintedScore, scoreStep.OperationId.Value,
                analysis.CropRevision, analysis.ModelRevision);
            BoardInteractionLog.Write("score-marker.frame", new
            {
                analysis.Board.Sequence, analysis.Board.Epoch,
                target = scoreStep.ToPrintedScore,
                color = scoreStep.Color.ToString(),
                state = scoreObservation.State.ToString(),
                readings = analysis.Scores.Select(reading => new
                {
                    color = reading.Color?.ToString(), reading.Score,
                    status = reading.Status.ToString()
                }).ToArray()
            });
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
            var visiblePlacement = Table.Placement;
            var visiblePending = coordinator.Public.PendingClaim;
            NotePlacementVerificationBlock(_scoreMarkerStep is not null ? "score-marker-step" :
                _claimCompletionInProgress ? "claim-completion-in-progress" :
                _operationInProgress ? "operation-in-progress" :
                visiblePlacement is null ? "no-placement-request" :
                visiblePlacement.AwaitingRestore ? "awaiting-restore" :
                visiblePending is null ? "no-pending-claim" :
                visiblePending.OperationId != visiblePlacement.OperationId ||
                visiblePending.RouteId != visiblePlacement.RouteId ||
                visiblePending.SeatId != visiblePlacement.SeatId ? "placement-and-claim-mismatch" :
                "unsupported-route");
            _routePlacementVerifier.Reset();
            ResetPlacementInventory();
            if (_scoreMarkerStep is null && !_claimCompletionInProgress && !_operationInProgress &&
                Table.Placement is null)
                ObserveBoardFirstClaim(analysis);
            return;
        }

        NotePlacementVerificationBlock(null);
        var inventoryKey = $"{coordinator.SessionId.Value}/{placement.StateVersion}/{placement.OperationId.Value}";
        if (_placementInventoryKey != inventoryKey)
        {
            var expected = coordinator.Public.RouteOwners
                .Select(route => new BoardInventoryRoute(route.Key.Value,
                    ToMarkerColor(coordinator.Public.SeatOf(route.Value).Color),
                    _manifest.Route(route.Key).Length))
                .Append(new BoardInventoryRoute(placement.RouteId.Value,
                    ToMarkerColor(placement.Color), placement.TrainCount))
                .ToArray();
            _placementInventoryVerifier = new BoardInventoryVerifier(expected);
            _placementInventoryKey = inventoryKey;
            _showingPlacementInventoryCorrection = false;
        }
        var placementObservation = _routePlacementVerifier.Observe(
            analysis.Board, analysis.Candidates, placement.RouteId.Value,
            ToMarkerColor(placement.Color), placement.TrainCount,
            placement.OperationId.Value, analysis.CropRevision, analysis.ModelRevision);
        var inventory = _placementInventoryVerifier!.Observe(analysis.Board, analysis.Candidates,
            analysis.CropRevision, analysis.ModelRevision);
        UpdatePlacementInventoryGuidance(placement, inventory);
        BoardInteractionLog.Write("placement.frame", new
        {
            analysis.Board.Sequence, analysis.Board.Epoch,
            ageMs = analysis.Board.Age.TotalMilliseconds,
            route = placement.RouteId.Value,
            color = placement.Color.ToString(),
            expected = placement.TrainCount,
            state = placementObservation.State.ToString(),
            matched = placementObservation.MatchedCount,
            inventoryState = inventory.State.ToString(),
            inventoryRoute = inventory.RouteId,
            nearbyCandidates = DescribeNearbyPlacementCandidates(analysis, placement.RouteId.Value)
        });
        // A requested route alone is insufficient: pieces from any earlier claim may have
        // been moved into its spaces. The entire committed board plus this route must agree
        // in fresh, stable frames before submitting claim evidence.
        if (placementObservation.Confirmed && inventory.Confirmed)
            _ = AcceptCameraPlacementAsync(placement, analysis);
    }

    private void ResetPlacementInventory()
    {
        if (_showingPlacementInventoryCorrection) Game.ClearGuidance();
        _placementInventoryVerifier = null;
        _placementInventoryKey = null;
        _showingPlacementInventoryCorrection = false;
    }

    private void UpdatePlacementInventoryGuidance(PlacementInstruction placement,
        BoardInventoryObservation inventory)
    {
        string? correction = inventory.RouteId is { } routeId &&
            routeId != placement.RouteId.Value &&
            inventory.State is BoardInventoryState.MissingTrains or BoardInventoryState.WrongColor or
                BoardInventoryState.Ambiguous
            ? $"Put the trains back on {_manifest.Describe(new RouteId(routeId))}. " +
              "Previously claimed routes must stay occupied before this claim can continue."
            : inventory.State == BoardInventoryState.UnexpectedTrain
                ? "Check for train pieces outside the claimed routes and the new route. " +
                  "The whole board must match before this claim can continue."
                : inventory.State == BoardInventoryState.Unsupported
                    ? "The camera cannot verify every claimed route. Check the board before continuing."
                    : null;
        if (correction is not null)
        {
            if (_showingPlacementInventoryCorrection && Game.GuidanceInstruction == correction) return;
            Game.ShowGuidance(Table.TurnText, placement.SeatName, correction);
            _showingPlacementInventoryCorrection = true;
        }
        else if (_showingPlacementInventoryCorrection)
        {
            Game.ClearGuidance();
            _showingPlacementInventoryCorrection = false;
        }
    }

    private void NotePlacementVerificationBlock(string? reason)
    {
        if (_placementVerificationBlock == reason) return;
        BoardInteractionLog.Write(reason is null ? "placement.verification-resumed" : "placement.verification-blocked",
            new { reason, previous = _placementVerificationBlock });
        _placementVerificationBlock = reason;
    }

    private static object[] DescribeNearbyPlacementCandidates(GameTableAnalysis analysis, string routeId)
    {
        try { return DescribeNearbyPlacementCandidatesCore(analysis, routeId); }
        catch (Exception error)
        {
            BoardInteractionLog.Write("placement.candidate-audit-error", new
            {
                errorType = error.GetType().Name, errorCode = error.HResult
            });
            return [];
        }
    }

    private static object[] DescribeNearbyPlacementCandidatesCore(GameTableAnalysis analysis, string routeId)
    {
        if (!ClassicUsRouteGeometry.TryGetSlots(routeId, out var slots)) return [];
        return analysis.Candidates
            .Where(candidate => candidate.Outline.Count >= 4 && candidate.Outline.All(point =>
                double.IsFinite(point.X) && double.IsFinite(point.Y) &&
                point.X is >= 0 and <= 1 && point.Y is >= 0 and <= 1))
            .Select(candidate =>
            {
                var x = candidate.Outline.Average(point => point.X) * ClassicUsRouteGeometry.ReferenceWidth;
                var y = candidate.Outline.Average(point => point.Y) * ClassicUsRouteGeometry.ReferenceHeight;
                var nearest = slots.Select((slot, index) => new
                {
                    index,
                    slot,
                    distance = Math.Sqrt(Math.Pow(x - slot.ReferenceX, 2) +
                                         Math.Pow(y - slot.ReferenceY, 2))
                }).MinBy(slot => slot.distance)!;
                var dx = x - nearest.slot.ReferenceX;
                var dy = y - nearest.slot.ReferenceY;
                return new
                {
                    x = Math.Round(x, 1), y = Math.Round(y, 1),
                    kind = candidate.Kind.ToString(),
                    confidence = Math.Round(candidate.Confidence, 3),
                    width = Math.Round((candidate.Outline.Max(point => point.X) -
                                        candidate.Outline.Min(point => point.X)) * ClassicUsRouteGeometry.ReferenceWidth, 1),
                    height = Math.Round((candidate.Outline.Max(point => point.Y) -
                                         candidate.Outline.Min(point => point.Y)) * ClassicUsRouteGeometry.ReferenceHeight, 1),
                    color = candidate.Kind == PieceCandidateKind.Train
                        ? RoutePlacementVerifier.ReadCandidateColor(analysis.Board, candidate)?.ToString() : null,
                    nearestSlot = nearest.index,
                    distance = Math.Round(nearest.distance, 1),
                    along = Math.Round(dx * nearest.slot.TangentX + dy * nearest.slot.TangentY, 1),
                    across = Math.Round(-dx * nearest.slot.TangentY + dy * nearest.slot.TangentX, 1)
                };
            })
            .Where(candidate => candidate.distance <= 90)
            .OrderBy(candidate => candidate.nearestSlot)
            .ThenBy(candidate => candidate.distance)
            .Cast<object>()
            .ToArray();
    }

    private Task AcceptCameraPlacementAsync(PlacementInstruction placement, GameTableAnalysis analysis) =>
        AcceptPhysicalPlacementAsync(placement, EvidenceKind.CameraAutomatic, analysis.ModelId,
            $"{placement.TrainCount} {placement.Color} train pieces matched every measured slot of " +
            $"{placement.RouteId.Value}, and every earlier claimed route retained its trains and color " +
            "in distinct upright frames at least one second apart; " +
            $"camera epoch {analysis.Board.Epoch}, frame {analysis.Board.Sequence}, " +
            $"crop {analysis.CropRevision}, model revision {analysis.ModelRevision}.");

    private async Task AcceptPhysicalPlacementAsync(PlacementInstruction placement,
        EvidenceKind evidence, string source, string summary)
    {
        if (_claimCompletionInProgress || _scoreMarkerStep is not null || _coordinator is not { } coordinator ||
            !CanSubmitOperator() || coordinator.Public.PendingClaim?.OperationId != placement.OperationId ||
            coordinator.Public.StateVersion != placement.StateVersion)
        {
            BoardInteractionLog.Write("placement.accept-deferred", new
            {
                route = placement.RouteId.Value,
                operation = placement.OperationId.Value,
                evidence = evidence.ToString(),
                claimBusy = _claimCompletionInProgress,
                scorePending = _scoreMarkerStep is not null,
                operatorReady = CanSubmitOperator(),
                pendingOperation = _coordinator?.Public.PendingClaim?.OperationId.Value,
                stateVersion = _coordinator?.Public.StateVersion,
                expectedVersion = placement.StateVersion
            });
            if (evidence == EvidenceKind.CameraAutomatic) _routePlacementVerifier.Reset();
            return;
        }

        BoardInteractionLog.Write("placement.accept-started", new
        {
            route = placement.RouteId.Value,
            operation = placement.OperationId.Value,
            evidence = evidence.ToString(),
            placement.StateVersion
        });
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
                BoardInteractionLog.Write("placement.accept-rejected", new
                {
                    route = placement.RouteId.Value,
                    operation = placement.OperationId.Value,
                    code = outcome.Result.Rejection?.Code
                });
                Status = outcome.Result.Rejection?.Message ?? "The claim was not accepted.";
                if (outcome.Result.Rejection?.Code == "StorageFaulted") RequireReload();
                _routePlacementVerifier.Reset();
                return;
            }

            Status = null;
            BoardInteractionLog.Write("placement.accepted", new
            {
                route = placement.RouteId.Value,
                operation = placement.OperationId.Value,
                placement.TrainCount,
                color = placement.Color.ToString()
            });
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
            Table.ShowPendingScoreMarker(coordinator.Public.TurnNumber,
                step.SeatName, step.Color, step.ToPrintedScore);
            _scoreMarkerMoveVerifier.Reset();
            NotifyScoreMarkerDetectionPromptChanged();
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
            NotifyScoreMarkerDetectionPromptChanged();
        }
        catch (Exception error)
        {
            BoardInteractionLog.Write("placement.accept-error", new
            {
                route = placement.RouteId.Value,
                errorType = error.GetType().Name, errorCode = error.HResult
            });
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
        try
        {
            _scoreMarkerStep = null;
            _scoreMarkerMoveVerifier.Reset();
            NotifyScoreMarkerDetectionPromptChanged();
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
