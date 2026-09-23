using GoldenTicket.Domain;
using GoldenTicket.Domain.Model;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Persistence;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.Services;

/// <summary>
/// One camera verification attempt for one saved checkpoint. The target and pending physical
/// placement are frozen at the start, so later UI changes cannot silently change what is checked.
/// </summary>
internal sealed class SavedBoardRestoreSession
{
    private readonly SavedBoardRestoreVerifier _verifier;
    private readonly IReadOnlyDictionary<string, TargetRoute> _routes;
    private readonly CheckpointPendingPlacement? _pending;
    private SavedBoardRestoreStage _lastStage = SavedBoardRestoreStage.WaitingForCamera;

    public SavedBoardRestoreSession(string checkpointId, IReadOnlyList<SavedScoreMarker> markers,
        IReadOnlyList<BoardInventoryRoute> routes, IReadOnlyList<TargetRoute> physicalTarget,
        CheckpointPendingPlacement? pending)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(physicalTarget);
        CheckpointId = checkpointId;
        _routes = physicalTarget.ToDictionary(route => route.RouteId.Value);
        _pending = pending;
        var pendingRoute = pending is null ? null : new BoardInventoryRoute(pending.RouteId.Value,
            ToMarkerColor(pending.Color), pending.RouteLength);
        _verifier = new SavedBoardRestoreVerifier(markers, routes,
            pendingRoute, pending?.OccupiedSlotMask);
    }

    public string CheckpointId { get; }
    public bool IsCompleting { get; private set; }
    public CheckpointPendingPlacement? PendingPlacement => _pending;

    public SavedBoardRestoreProgress? Observe(GameTableAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (IsCompleting) return null;
        var observation = _verifier.Observe(analysis.Board, analysis.Scores, analysis.Candidates,
            analysis.CropRevision, analysis.ModelRevision);
        _lastStage = observation.Stage;
        var inventory = observation.Inventory;
        if (observation.Stage == SavedBoardRestoreStage.WaitingForCamera)
            return new(observation, null, false, null);
        if (observation.Stage != SavedBoardRestoreStage.CheckingTrains)
            return new(observation, null, true, null);

        var routeId = inventory?.RouteId;
        var pendingMask = routeId is not null && _pending is { } pending &&
            routeId == pending.RouteId.Value ? pending.OccupiedSlotMask : (int?)null;
        if (routeId is not null && inventory?.State is (BoardInventoryState.MissingTrains or
            BoardInventoryState.WrongColor or BoardInventoryState.Ambiguous) &&
            _routes.TryGetValue(routeId, out var route))
            return new(observation, (route.RouteId, route.Length, (1 << route.Length) - 1),
                true, pendingMask);
        if (routeId is not null && _pending is { } pendingPlacement &&
            routeId == pendingPlacement.RouteId.Value)
            return new(observation,
                (pendingPlacement.RouteId, pendingPlacement.RouteLength,
                    pendingPlacement.OccupiedSlotMask),
                true, pendingMask);
        return new(observation, null,
            inventory?.State != BoardInventoryState.WaitingForFreshFrame, pendingMask);
    }

    public bool TryBeginCompletion()
    {
        if (IsCompleting || _lastStage != SavedBoardRestoreStage.Confirmed) return false;
        IsCompleting = true;
        return true;
    }

    public void ResetAfterFailedCompletion()
    {
        _verifier.Reset();
        _lastStage = SavedBoardRestoreStage.WaitingForCamera;
        IsCompleting = false;
    }

    private static MarkerColor ToMarkerColor(PlayerColor color) => color switch
    {
        PlayerColor.Blue => MarkerColor.Blue,
        PlayerColor.Red => MarkerColor.Red,
        PlayerColor.Green => MarkerColor.Green,
        PlayerColor.Yellow => MarkerColor.Yellow,
        PlayerColor.Black => MarkerColor.Black,
        _ => throw new ArgumentOutOfRangeException(nameof(color))
    };
}

internal sealed record SavedBoardRestoreProgress(SavedBoardRestoreObservation Observation,
    (RouteId RouteId, int TrainCount, int SlotMask)? Target, bool ShouldUpdateTarget,
    int? PendingSlotMask);
