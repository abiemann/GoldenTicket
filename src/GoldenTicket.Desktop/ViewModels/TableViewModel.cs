using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Vision;

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
    bool IsActive,
    string LastAction);

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

    /// <summary>The reviewed way forward for the paused position, when one exists (DESIGN 6.4).</summary>
    [ObservableProperty] private RulesContinuation? _rulesContinuation;

    [ObservableProperty] private bool _rulesContinuationAccepted;

    /// <summary>Set while a claim is waiting for its physical trains (DESIGN 4.5).</summary>
    [ObservableProperty] private PlacementInstruction? _placement;
    [ObservableProperty] private bool _wholeBoardAcknowledged;

    // ---- Save, pack away and rebuild (DESIGN 4.10, 19.8) -------------------------------------

    /// <summary>The name the operator is giving the next save.</summary>
    [ObservableProperty] private string _saveName = "";

    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _isPackedAway;
    [ObservableProperty] private bool _isRebuilding;

    /// <summary>What the save screen says. Only a validated checkpoint says it is safe to pack.</summary>
    [ObservableProperty] private string? _saveStatus;

    [ObservableProperty] private string? _saveProblem;

    /// <summary>The saved position, listed the way the rebuild instructions read it.</summary>
    public ObservableCollection<RebuildRouteRow> RebuildTarget { get; } = [];

    public ObservableCollection<RebuildStockRow> RebuildStock { get; } = [];

    [ObservableProperty] private string? _rebuildHeadline;
    [ObservableProperty] private string? _rebuildSuspendedAction;
    [ObservableProperty] private bool _rebuildAttested;
    [ObservableProperty] private bool _rebuildAcknowledged;

    public void Update(PublicView view, IReadOnlyList<PublicEventEntry> history)
    {
        WholeBoardAcknowledged = false;
        UpdatePackAway(view);
        var active = view.SeatOf(view.ActiveSeatId);
        var guidanceSeat = view.TurnPhase == TurnPhase.SetupTicketSelection &&
            view.Seats.Count(seat => seat.Kind == SeatKind.Human) == 1
                ? view.Seats.Single(seat => seat.Kind == SeatKind.Human)
                : active;
        ActiveSeatName = guidanceSeat.DisplayName;
        ActiveSeatColor = guidanceSeat.Color;
        ActiveSeatSymbol = guidanceSeat.Symbol;

        TurnText = view.Lifecycle == SessionLifecycle.Setup
            ? "Setup"
            : $"Turn {view.TurnNumber}";

        PhaseText = DescribePhase(view);
        Instruction = DescribeInstruction(view, active);

        SupplyText = $"Draw pile {view.TrainDeckCount}  ·  discards {view.TrainDiscardCount}  ·  " +
                     $"destinations {view.TicketDeckCount}";

        VerificationText = "The camera checks calibrated routes automatically. Manual whole-board confirmation remains available for other routes.";

        FinalRoundText = view.FinalRound is { } round
            ? "Final round: " + string.Join(", ", round.RemainingTurnsBySeat
                .OrderBy(pair => pair.Key.Value)
                .Select(pair => $"{view.SeatOf(pair.Key).DisplayName} {pair.Value}"))
            : null;

        RulesDecisionText = view.RulesDecision is { } decision
            ? $"Paused - {decision.Code}: {decision.Explanation}"
            : null;

        RulesContinuation = !view.IsGameplaySuspended && view.RulesDecision is { } paused
            ? RulesContinuations.For(paused.Code) : null;
        // Each newly published decision needs its own acknowledgement, including a policy raised
        // while accepting another one (for example, partial market followed by no second draw).
        RulesContinuationAccepted = false;

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
                seat.TicketCount + seat.PendingTicketOfferCount,
                seat.ClaimedRoutes.Length,
                seat.SeatId == view.ActiveSeatId,
                LastActionFor(seat.SeatId, history)));
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
            History.Add(who + entry.Text.Replace("destination ticket", "destination", StringComparison.Ordinal));
        }

        Placement = !view.IsGameplaySuspended && view.PendingClaim is { } pending
            ? BuildPlacement(view, pending)
            : null;
    }

    /// <summary>
    /// The rules engine has advanced to the next turn after committing a route, but the
    /// operator must finish moving the previous seat's physical score marker before the
    /// desktop starts that turn. Keep the engineering header honest about this handoff.
    /// </summary>
    public void ShowPendingScoreMarker(int nextTurn, string scoringSeatName,
        PlayerColor scoringColor, int targetPrintedScore)
    {
        TurnText = $"Up next: Turn {nextTurn}";
        PhaseText = "Waiting for scoring marker";
        Instruction = $"Waiting for {scoringSeatName}'s {scoringColor} scoring marker on " +
            $"{targetPrintedScore}. {ActiveSeatName} has not acted yet.";
    }

    private static string LastActionFor(SeatId seatId, IReadOnlyList<PublicEventEntry> history)
    {
        for (var index = history.Count - 1; index >= 0; index--)
        {
            var entry = history[index];
            if (entry.Seat != seatId) continue;

            switch (entry.Kind)
            {
                case "FaceUpCardTaken":
                case "BlindCardDrawn":
                    var faceUp = 0;
                    var blind = 0;
                    for (var draw = index; draw >= 0; draw--)
                    {
                        var prior = history[draw];
                        if (prior.Seat != seatId) continue;
                        if (prior.Kind == "TurnStarted") break;
                        if (prior.Kind == "FaceUpCardTaken") faceUp++;
                        if (prior.Kind == "BlindCardDrawn") blind++;
                    }
                    var count = faceUp + blind;
                    var source = faceUp > 0 && blind > 0 ? "face-up and blind" :
                        faceUp > 0 ? "face-up" : "blind";
                    return $"Drew {count} {source} train card{(count == 1 ? "" : "s")}.";
                case "TicketsKept":
                    return entry.Text.Replace("destination ticket", "destination", StringComparison.Ordinal);
                case "TicketOfferCreated":
                    return entry.Text.Replace("destination ticket", "destination", StringComparison.Ordinal);
                case "ClaimCommitted":
                case "ClaimCancelled":
                case "ClaimPlanned":
                    return entry.Text;
            }
        }

        return "Waiting for first action.";
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
            pending.RouteId,
            _manifest.Describe(pending.RouteId),
            route.DisplayLaneLabel,
            pending.TrainCount,
            pending.AwaitingRestore);
    }

    /// <summary>
    /// Mirrors the save lifecycle onto the screen. DESIGN 19.8 step 7: only a validated checkpoint
    /// may say the pieces can be cleared away, so a committed-but-unvalidated one says it is still
    /// being checked.
    /// </summary>
    private void UpdatePackAway(PublicView view)
    {
        IsSaving = view.Lifecycle == SessionLifecycle.PreparingPackAway;
        IsPackedAway = view.Lifecycle == SessionLifecycle.PackedAway;
        IsRebuilding = view.Lifecycle == SessionLifecycle.Rebuilding;
        RebuildAttested = view.RebuildAttested;
        SaveProblem = view.CheckpointFault;

        if (!IsRebuilding) RebuildAcknowledged = false;

        if (view.Checkpoint is not { } checkpoint)
        {
            SaveStatus = IsSaving ? "Saving. Clear your hands from the board." : null;
            RebuildTarget.Clear();
            RebuildStock.Clear();
            RebuildHeadline = null;
            RebuildSuspendedAction = null;
            return;
        }

        SaveStatus = view.Lifecycle switch
        {
            SessionLifecycle.Rebuilding => "Rebuilding the saved position. Finish the board check to resume.",
            SessionLifecycle.PreparingPackAway => "Saving a new checkpoint. Do not pack the game away yet.",
            SessionLifecycle.PackedAway => checkpoint.Status switch
            {
                CheckpointStatus.Verified =>
                    $"Saved as \"{checkpoint.Name}\" at {checkpoint.CreatedAt.ToLocalTime():HH:mm}. " +
                    "You can pack the game away.",
                CheckpointStatus.CommittedAwaitingReadback =>
                    "The save is written and is being checked. Do not pack the game away yet.",
                _ => "The save could not be validated. The game stays packed until this is resolved.",
            },
            _ => $"Previous checkpoint: \"{checkpoint.Name}\". Save again before packing away the current game.",
        };

        RebuildHeadline =
            $"\"{checkpoint.Name}\" - {checkpoint.RouteCount} route{(checkpoint.RouteCount == 1 ? "" : "s")}, " +
            $"{TrainCountText.Format(checkpoint.TotalTrainsOnBoard)} on the board" +
            (checkpoint.Provenance == TargetProvenance.LogicalStateOnly
                ? ". The saved route list is the rebuild target; an optional reference photo can help."
                : ".");

        RebuildSuspendedAction = checkpoint.SuspendedTurnPhase switch
        {
            TurnPhase.AwaitingSecondTrainCard =>
                "When you resume, the active seat still owes the second card of its draw.",
            TurnPhase.AwaitingTicketKeep =>
                "When you resume, the active seat is still choosing which destinations to keep.",
            TurnPhase.AwaitingPhysicalPlacement =>
                "A route claim was waiting for its trains. Those trains are NOT part of the saved board " +
                "below; after resuming, place them and confirm as usual. Nothing has been spent.",
            TurnPhase.RestoreBeforeState =>
                "A cancelled claim was waiting for its trains to come back off. Finish that after resuming.",
            _ => null,
        };

        var seatsByName = view.Seats.ToDictionary(seat => seat.SeatId);

        RebuildTarget.Clear();
        foreach (var route in checkpoint.PhysicalTarget
                     .OrderBy(route => route.SeatId.Value)
                     .ThenBy(route => _manifest.Describe(route.RouteId), StringComparer.Ordinal))
        {
            var owner = seatsByName[route.SeatId];
            RebuildTarget.Add(new RebuildRouteRow(
                owner.DisplayName,
                owner.Color,
                owner.Symbol,
                _manifest.Describe(route.RouteId),
                _manifest.Route(route.RouteId).DisplayLaneLabel,
                route.Length));
        }

        RebuildStock.Clear();
        var starting = _manifest.RulesConstants.StartingTrainsPerSeat;
        foreach (var seat in view.Seats)
        {
            var onBoard = checkpoint.PhysicalTarget
                .Where(route => route.SeatId == seat.SeatId)
                .Sum(route => route.Length);

            RebuildStock.Add(new RebuildStockRow(
                seat.DisplayName, seat.Color, seat.Symbol, onBoard, starting - onBoard));
        }
    }

    private static string DescribePhase(PublicView view) => view.TurnPhase switch
    {
        TurnPhase.SetupTicketSelection => "Choosing opening destinations",
        TurnPhase.TurnStart => "Choosing an action",
        TurnPhase.AwaitingSecondTrainCard => "Taking a second train card",
        TurnPhase.AwaitingTicketKeep => "Choosing which destinations to keep",
        TurnPhase.AwaitingPhysicalPlacement => "Waiting for trains to be placed",
        TurnPhase.RestoreBeforeState => "Waiting for the board to be put back",
        TurnPhase.FinalScoring => "Final scoring",
        TurnPhase.Finished => "Match complete",
        TurnPhase.RulesDecisionRequired => "Paused on an unresolved rules situation",
        _ => view.TurnPhase.ToString(),
    };

    private string DescribeInstruction(PublicView view, PublicSeatSummary active) => view.TurnPhase switch
    {
        _ when view.Lifecycle == SessionLifecycle.PreparingPackAway =>
            "Saving the current position. Wait for the completed save before removing any trains.",
        _ when view.Lifecycle == SessionLifecycle.PackedAway =>
            "This match is packed away. Use the saved-position rebuild workflow to continue.",
        _ when view.Lifecycle == SessionLifecycle.Rebuilding =>
            "Rebuild the saved target, including empty lanes, before resuming play.",
        TurnPhase.RulesDecisionRequired =>
            "Review the supply policy below before continuing the match.",
        TurnPhase.SetupTicketSelection when view.Seats.Count(seat => seat.Kind == SeatKind.Human) == 1 =>
            "Choose whether to keep all destinations or drop one.",
        TurnPhase.SetupTicketSelection =>
            "Each seat keeps at least " +
            $"{_manifest.RulesConstants.SetupTicketMinimumKeep} of its {_manifest.RulesConstants.SetupTicketOffer} " +
            "opening destinations.",

        TurnPhase.TurnStart when active.Kind == SeatKind.Human &&
                                 view.Seats.Count(seat => seat.Kind == SeatKind.Human) == 1 =>
            "Place trains on a route you can pay for, or draw from the train-card or destination piles below. " +
            "A route claim uses your turn.",
        TurnPhase.TurnStart when active.Kind == SeatKind.Human =>
            "Choose a route to claim, draw train cards, or draw destinations. " +
            "A route claim uses your turn.",
        TurnPhase.AwaitingSecondTrainCard when active.Kind == SeatKind.Human =>
            "Draw one more train card from the T pile or face-up cards. " +
            "A face-up locomotive cannot be the second card.",
        TurnPhase.AwaitingTicketKeep when active.Kind == SeatKind.Human =>
            $"Keep at least {_manifest.RulesConstants.InGameTicketMinimumKeep} " +
            "of the destinations you drew.",

        TurnPhase.AwaitingPhysicalPlacement when view.PendingClaim is { } pending =>
            $"Place {active.DisplayName}'s {pending.TrainCount} {active.Color} " +
            $"train{(pending.TrainCount == 1 ? "" : "s")} on " +
            $"{_manifest.Describe(pending.RouteId)}. " +
            (RoutePlacementVerifier.Supports(pending.RouteId.Value, pending.TrainCount)
                ? pending.TrainCount == 1
                    ? "The camera will check its position and continue automatically."
                    : "The camera will check their positions and continue automatically."
                : "Check the whole board, then confirm beside it."),

        TurnPhase.RestoreBeforeState when view.PendingClaim is { } pending =>
            $"Take {active.DisplayName}'s {TrainCountText.Format(pending.TrainCount)} back off " +
            $"{_manifest.Describe(pending.RouteId)}, then confirm.",

        TurnPhase.Finished => "Final scores are below.",

        _ when active.Kind == SeatKind.Computer => $"{active.DisplayName} is thinking.",

        _ => "Choose a legal action for your turn.",
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
    RouteId RouteId,
    string RouteText,
    string? LaneLabel,
    int TrainCount,
    bool AwaitingRestore)
{
    public string Headline => AwaitingRestore
        ? $"Remove {SeatName}'s {TrainCountText.Format(TrainCount)} from {RouteText}"
        : $"Place {SeatName}'s {TrainCountText.Format(TrainCount)} on {RouteText}";

    public string Detail => AwaitingRestore
        ? $"{TrainCount} {Color} train{(TrainCount == 1 ? "" : "s")} must come off before the claim is released. " +
          "No cards have been spent."
        : $"{TrainCount} {Color} train{(TrainCount == 1 ? "" : "s")}" +
          (LaneLabel is null ? "" : $", {LaneLabel}") +
          (TrainCount == 1
              ? ". Place it on the marked space; nothing is spent or scored until the placement is confirmed."
              : ". Place them in any order; nothing is spent or scored until the placement is confirmed.");
}

/// <summary>One route of the saved position, in the words DESIGN 19.8 asks the instructions to use.</summary>
public sealed record RebuildRouteRow(
    string OwnerName,
    PlayerColor OwnerColor,
    string Symbol,
    string RouteText,
    string? LaneLabel,
    int TrainCount);

/// <summary>How many trains each seat should have left over once the board is rebuilt.</summary>
public sealed record RebuildStockRow(
    string SeatName,
    PlayerColor Color,
    string Symbol,
    int OnBoard,
    int RemainingOffBoard);
