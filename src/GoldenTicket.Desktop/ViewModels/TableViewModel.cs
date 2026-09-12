using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Projections;

namespace GoldenTicket.Desktop.ViewModels;

public sealed record SeatRow(
    SeatId SeatId,
    string DisplayName,
    PlayerColor Color,
    string Symbol,
    string Operator,
    int Score,
    int TrainsRemaining,
    int CardCount,
    int TicketCount,
    int RoutesClaimed,
    bool IsActive);

public sealed record MarketSlotRow(int Slot, TrainCardKind? Kind, string Label);

public sealed record ClaimedRouteRow(string RouteText, int Length, string OwnerName, PlayerColor OwnerColor, string Symbol);

/// <summary>
/// The public table screen (DESIGN 4.1). It is built from <see cref="PublicView"/> alone; no hidden
/// hand is placed in this visual tree at any opacity, because no hidden hand reaches this view model.
/// </summary>
public sealed partial class TableViewModel : ObservableObject
{
    private readonly BoardManifest _manifest;

    public TableViewModel(BoardManifest manifest) => _manifest = manifest;

    public ObservableCollection<SeatRow> Seats { get; } = [];

    public ObservableCollection<MarketSlotRow> Market { get; } = [];

    public ObservableCollection<ClaimedRouteRow> ClaimedRoutes { get; } = [];

    public ObservableCollection<string> History { get; } = [];

    [ObservableProperty] private string _activeSeatName = "";
    [ObservableProperty] private PlayerColor _activeSeatColor;
    [ObservableProperty] private string _activeSeatSymbol = "";
    [ObservableProperty] private string _phaseText = "";
    [ObservableProperty] private string _instruction = "";
    [ObservableProperty] private string _turnText = "";
    [ObservableProperty] private string _supplyText = "";
    [ObservableProperty] private string _verificationText = "";
    [ObservableProperty] private string? _finalRoundText;
    [ObservableProperty] private string? _rulesDecisionText;

    /// <summary>Set while a claim is waiting for its physical trains (DESIGN 4.5).</summary>
    [ObservableProperty] private PlacementInstruction? _placement;
    [ObservableProperty] private bool _wholeBoardAcknowledged;

    public void Update(PublicView view, IReadOnlyList<PublicEventEntry> history)
    {
        WholeBoardAcknowledged = false;
        var active = view.SeatOf(view.ActiveSeatId);
        ActiveSeatName = active.DisplayName;
        ActiveSeatColor = active.Color;
        ActiveSeatSymbol = active.Symbol;

        TurnText = view.Lifecycle == SessionLifecycle.Setup
            ? "Setup"
            : $"Turn {view.TurnNumber}";

        PhaseText = DescribePhase(view);
        Instruction = DescribeInstruction(view, active);

        SupplyText = $"Draw pile {view.TrainDeckCount}  ·  discards {view.TrainDiscardCount}  ·  " +
                     $"destination tickets {view.TicketDeckCount}";

        VerificationText = view.VerificationMode == VerificationMode.Manual
            ? "Physical verification: operator confirms each placement. Camera verification is not available in this build."
            : "Physical verification: camera";

        FinalRoundText = view.FinalRound is { } round
            ? "Final round: " + string.Join(", ", round.RemainingTurnsBySeat
                .OrderBy(pair => pair.Key.Value)
                .Select(pair => $"{view.SeatOf(pair.Key).DisplayName} {pair.Value}"))
            : null;

        RulesDecisionText = view.RulesDecision is { } decision
            ? $"Paused - {decision.Code}: {decision.Explanation}"
            : null;

        Seats.Clear();
        foreach (var seat in view.Seats)
        {
            Seats.Add(new SeatRow(
                seat.SeatId,
                seat.DisplayName,
                seat.Color,
                seat.Symbol,
                seat.Kind == SeatKind.Computer ? "computer" : "human",
                seat.RouteScore,
                seat.TrainsRemaining,
                seat.TrainCardCount,
                seat.TicketCount,
                seat.ClaimedRoutes.Length,
                seat.SeatId == view.ActiveSeatId));
        }

        Market.Clear();
        for (var slot = 0; slot < view.FaceUp.Length; slot++)
        {
            var kind = view.FaceUp[slot];
            Market.Add(new MarketSlotRow(slot, kind, kind?.ToString() ?? "empty"));
        }

        ClaimedRoutes.Clear();
        foreach (var (routeId, seatId) in view.RouteOwners.OrderBy(pair => pair.Value.Value).ThenBy(pair => pair.Key.Value))
        {
            var owner = view.SeatOf(seatId);
            ClaimedRoutes.Add(new ClaimedRouteRow(
                _manifest.Describe(routeId),
                _manifest.Route(routeId).Length,
                owner.DisplayName,
                owner.Color,
                owner.Symbol));
        }

        History.Clear();
        foreach (var entry in history.TakeLast(40))
        {
            var who = entry.Seat is { } seat ? view.SeatOf(seat).DisplayName + ": " : "";
            History.Add(who + entry.Text);
        }

        Placement = view.PendingClaim is { } pending
            ? BuildPlacement(view, pending)
            : null;
    }

    private PlacementInstruction BuildPlacement(PublicView view, PublicPendingClaim pending)
    {
        var seat = view.SeatOf(pending.SeatId);
        var route = _manifest.Route(pending.RouteId);

        return new PlacementInstruction(
            pending.OperationId,
            view.StateVersion,
            seat.SeatId,
            seat.DisplayName,
            seat.Color,
            seat.Symbol,
            _manifest.Describe(pending.RouteId),
            route.DisplayLaneLabel,
            pending.TrainCount,
            pending.AwaitingRestore);
    }

    private static string DescribePhase(PublicView view) => view.TurnPhase switch
    {
        TurnPhase.SetupTicketSelection => "Choosing opening destination tickets",
        TurnPhase.TurnStart => "Choosing an action",
        TurnPhase.AwaitingSecondTrainCard => "Taking a second train card",
        TurnPhase.AwaitingTicketKeep => "Choosing which destination tickets to keep",
        TurnPhase.AwaitingPhysicalPlacement => "Waiting for trains to be placed",
        TurnPhase.RestoreBeforeState => "Waiting for the board to be put back",
        TurnPhase.FinalScoring => "Final scoring",
        TurnPhase.Finished => "Match complete",
        TurnPhase.RulesDecisionRequired => "Paused on an unresolved rules situation",
        _ => view.TurnPhase.ToString(),
    };

    private string DescribeInstruction(PublicView view, PublicSeatSummary active) => view.TurnPhase switch
    {
        TurnPhase.SetupTicketSelection =>
            "Each seat keeps at least " +
            $"{_manifest.RulesConstants.SetupTicketMinimumKeep} of its {_manifest.RulesConstants.SetupTicketOffer} " +
            "opening tickets. Hand the laptop to each human in turn.",

        TurnPhase.AwaitingPhysicalPlacement when view.PendingClaim is { } pending =>
            $"Place {active.DisplayName}'s {pending.TrainCount} {active.Color} trains on " +
            $"{_manifest.Describe(pending.RouteId)}, then confirm.",

        TurnPhase.RestoreBeforeState when view.PendingClaim is { } pending =>
            $"Take {active.DisplayName}'s trains back off {_manifest.Describe(pending.RouteId)}, then confirm.",

        TurnPhase.Finished => "Final scores are below.",

        _ when active.Kind == SeatKind.Computer => $"{active.DisplayName} is thinking.",

        _ => $"{active.DisplayName}: open your private view to take your turn.",
    };
}

/// <summary>
/// What the operator must physically do now (DESIGN 4.5): the acting seat, its colour and redundant
/// symbol, both endpoint cities, the exact lane, and how many trains to place.
/// </summary>
public sealed record PlacementInstruction(
    OperationId OperationId,
    long StateVersion,
    SeatId SeatId,
    string SeatName,
    PlayerColor Color,
    string Symbol,
    string RouteText,
    string? LaneLabel,
    int TrainCount,
    bool AwaitingRestore)
{
    public string Headline => AwaitingRestore
        ? $"Remove {SeatName}'s trains from {RouteText}"
        : $"Place {SeatName}'s trains on {RouteText}";

    public string Detail => AwaitingRestore
        ? $"{TrainCount} {Color} train{(TrainCount == 1 ? "" : "s")} must come off before the claim is released. " +
          "No cards have been spent."
        : $"{TrainCount} {Color} train{(TrainCount == 1 ? "" : "s")}" +
          (LaneLabel is null ? "" : $", {LaneLabel}") +
          ". Place them in any order; nothing is spent or scored until the placement is confirmed.";
}
