using System.Collections.Immutable;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoldenTicket.Application;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Projections;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private bool _soloCanDrawBlind;
    private bool _soloCanDrawTickets;
    private readonly HashSet<int> _soloDrawableSlots = [];
    private long _soloDrawActionsVersion = -1;
    private long _soloTicketOfferVersion = -1;
    private int _soloTicketMinimumKeep;
    private IReadOnlyList<TicketChoiceRow> _soloTicketOffer = [];
    private IReadOnlyList<DestinationMarkerRow> _soloTicketOfferMarkers = [];
    private IReadOnlyList<DestinationLineRow> _soloTicketOfferLines = [];
    private string? _soloTicketOfferMessage;

    public IReadOnlyList<TicketChoiceRow> SoloTicketOffer => _soloTicketOffer;
    public IReadOnlyList<DestinationMarkerRow> SoloTicketOfferMarkers => _soloTicketOfferMarkers;
    public IReadOnlyList<DestinationLineRow> SoloTicketOfferLines => _soloTicketOfferLines;
    public bool ShowSoloTicketOffer => _soloTicketOffer.Count > 0;
    public int SoloTicketMinimumKeep => _soloTicketMinimumKeep;
    public bool CanKeepSoloTickets => ShowSoloTicketOffer && !_operationInProgress &&
        _windowActive && !IsGameExitMenuOpen && !NeedsBoardReconciliation &&
        _soloTicketOffer.Count(choice => choice.Keep) >= _soloTicketMinimumKeep;
    public string? SoloTicketOfferMessage
    {
        get => _soloTicketOfferMessage;
        private set => SetProperty(ref _soloTicketOfferMessage, value);
    }

    private bool CanUseSoloDrawPiles => _coordinator is { StorageFaulted: false } coordinator &&
        IsSingleHumanGame && IsSoloHumanTurn && IsGameplayScreenActive(Screen.Table) &&
        coordinator.Public.TurnPhase is TurnPhase.TurnStart or TurnPhase.AwaitingSecondTrainCard &&
        !_operationInProgress && !_exitRequested && !_mustReload && !_toolsDisposed &&
        _windowActive && _systemAvailable && !NeedsBoardReconciliation &&
        !IsGameExitMenuOpen && _scoreMarkerStep is null &&
        PrivateSeat is null && BoardFirstProposal is null && !ShowSoloTicketOffer &&
        _soloDrawActionsVersion == coordinator.Public.StateVersion;

    private bool CanDrawSoloBlind() => CanUseSoloDrawPiles && _soloCanDrawBlind;
    private bool CanDrawSoloTickets() => CanUseSoloDrawPiles && _soloCanDrawTickets;
    private bool CanDrawSoloFaceUp(MarketSlotRow? slot) =>
        slot is not null && CanUseSoloDrawPiles && _soloDrawableSlots.Contains(slot.Slot) &&
        slot.Slot >= 0 && slot.Slot < _coordinator!.Public.FaceUp.Length &&
        _coordinator!.Public.FaceUp[slot.Slot] == slot.Kind;

    private void NotifySoloDrawCommands()
    {
        DrawSoloBlindCommand.NotifyCanExecuteChanged();
        DrawSoloTicketsCommand.NotifyCanExecuteChanged();
        DrawSoloFaceUpCommand.NotifyCanExecuteChanged();
        KeepSoloTicketsCommand.NotifyCanExecuteChanged();
    }

    private async Task RefreshSoloDrawActionsAsync(PublicView publicView)
    {
        _soloCanDrawBlind = false;
        _soloCanDrawTickets = false;
        _soloDrawableSlots.Clear();
        _soloDrawActionsVersion = -1;

        if (!IsSingleHumanGame || publicView.Lifecycle != SessionLifecycle.Active ||
            publicView.SeatOf(publicView.ActiveSeatId).Kind != SeatKind.Human)
        {
            ClearSoloTicketOffer();
            NotifySoloDrawCommands();
            return;
        }

        if (publicView.TurnPhase == TurnPhase.AwaitingTicketKeep)
        {
            await RefreshSoloTicketOfferAsync(publicView);
            NotifySoloDrawCommands();
            return;
        }
        ClearSoloTicketOffer();
        if (publicView.TurnPhase is not (TurnPhase.TurnStart or TurnPhase.AwaitingSecondTrainCard))
        {
            NotifySoloDrawCommands();
            return;
        }

        var coordinator = _coordinator!;
        var seat = await coordinator.GetSeatViewAsync(publicView.ActiveSeatId);
        if (!ReferenceEquals(coordinator, _coordinator) ||
            coordinator.Public.StateVersion != publicView.StateVersion) return;
        var legal = _rules.GetLegalActions(seat);
        _soloCanDrawBlind = legal.CanDrawBlindTrainCard;
        _soloCanDrawTickets = legal.CanRequestTicketOffer;
        foreach (var slot in legal.DrawableFaceUpSlots) _soloDrawableSlots.Add(slot);
        _soloDrawActionsVersion = publicView.StateVersion;
        NotifySoloDrawCommands();
    }

    private async Task RefreshSoloTicketOfferAsync(PublicView publicView)
    {
        if (_soloTicketOfferVersion == publicView.StateVersion && ShowSoloTicketOffer) return;
        var coordinator = _coordinator!;
        var seat = await coordinator.GetSeatViewAsync(publicView.ActiveSeatId);
        if (!ReferenceEquals(coordinator, _coordinator) ||
            coordinator.Public.StateVersion != publicView.StateVersion || seat.Offer is null) return;

        ClearSoloTicketOffer();
        _soloTicketMinimumKeep = seat.Offer.MinimumKeep;
        _soloTicketOffer = seat.Offer.Offered.Select(ticketId =>
        {
            var ticket = _manifest.Ticket(ticketId);
            return new TicketChoiceRow(ticketId,
                $"{_manifest.City(ticket.CityA).DisplayName} - " +
                _manifest.City(ticket.CityB).DisplayName,
                ticket.Points, GoldenTicket.AI.RoutePlanner.EstimateCost(seat, _manifest, ticket));
        }).ToArray();
        foreach (var choice in _soloTicketOffer) choice.PropertyChanged += OnSoloTicketChoiceChanged;
        _soloTicketOfferMarkers = DestinationBoardOverlay.Build(_manifest, _soloTicketOffer);
        _soloTicketOfferLines = DestinationBoardOverlay.BuildLines(
            _manifest, _soloTicketOffer, _soloTicketOfferMarkers);
        _soloTicketOfferVersion = publicView.StateVersion;
        NotifySoloTicketOfferChanged();
    }

    private void OnSoloTicketChoiceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TicketChoiceRow.Keep))
        {
            OnPropertyChanged(nameof(CanKeepSoloTickets));
            KeepSoloTicketsCommand.NotifyCanExecuteChanged();
        }
    }

    private void NotifySoloTicketOfferChanged()
    {
        OnPropertyChanged(nameof(SoloTicketOffer));
        OnPropertyChanged(nameof(ShowSoloTicketOffer));
        OnPropertyChanged(nameof(SoloTicketMinimumKeep));
        OnPropertyChanged(nameof(CanKeepSoloTickets));
        OnPropertyChanged(nameof(SoloTicketOfferMarkers));
        OnPropertyChanged(nameof(SoloTicketOfferLines));
        OnPropertyChanged(nameof(ShowDestinationMarkersOnBoard));
        OnPropertyChanged(nameof(BoardDestinationMarkers));
        OnPropertyChanged(nameof(BoardDestinationLines));
        OnPropertyChanged(nameof(GameTableBoardTop));
        OnPropertyChanged(nameof(ShowDrawPanels));
        KeepSoloTicketsCommand.NotifyCanExecuteChanged();
        _lastDestinationAlignmentUtc = DateTime.MinValue;
        AlignDestinationMarkers();
    }

    private void ClearSoloTicketOffer()
    {
        if (_soloTicketOffer.Count == 0) return;
        foreach (var choice in _soloTicketOffer) choice.PropertyChanged -= OnSoloTicketChoiceChanged;
        _soloTicketOffer = [];
        _soloTicketOfferMarkers = [];
        _soloTicketOfferLines = [];
        _soloTicketOfferVersion = -1;
        _soloTicketMinimumKeep = 0;
        SoloTicketOfferMessage = null;
        NotifySoloTicketOfferChanged();
    }

    [RelayCommand(CanExecute = nameof(CanDrawSoloBlind))]
    private Task DrawSoloBlindAsync() => SubmitSoloDrawAsync(null);

    [RelayCommand(CanExecute = nameof(CanDrawSoloFaceUp))]
    private Task DrawSoloFaceUpAsync(MarketSlotRow? slot) =>
        slot is null ? Task.CompletedTask : SubmitSoloDrawAsync(slot.Slot);

    [RelayCommand(CanExecute = nameof(CanDrawSoloTickets))]
    private Task DrawSoloTicketsAsync() => SubmitSoloActionAsync(
        (coordinator, seat) => new RequestTicketOffer(coordinator.NewEnvelope(seat.SeatId)));

    [RelayCommand(CanExecute = nameof(CanKeepSoloTickets))]
    private async Task KeepSoloTicketsAsync()
    {
        if (!CanKeepSoloTickets || _coordinator is not { } coordinator ||
            coordinator.Public.TurnPhase != TurnPhase.AwaitingTicketKeep ||
            !IsGameplayScreenActive(Screen.Table) || !_windowActive || _operationInProgress ||
            IsGameExitMenuOpen || NeedsBoardReconciliation) return;

        var version = _soloTicketOfferVersion;
        var kept = _soloTicketOffer.Where(choice => choice.Keep)
            .Select(choice => choice.TicketId).ToImmutableArray();
        var returned = _soloTicketOffer.Where(choice => !choice.Keep)
            .Select(choice => choice.TicketId).ToImmutableArray();
        SetOperationInProgress(true);
        try
        {
            var seat = await coordinator.GetSeatViewAsync(coordinator.Public.ActiveSeatId);
            if (!ReferenceEquals(coordinator, _coordinator) ||
                coordinator.Public.StateVersion != version || seat.Offer is null ||
                !_rules.GetLegalActions(seat).MustCommitTicketSelection) return;
            var outcome = await coordinator.SubmitAsync(new CommitTicketSelection(
                coordinator.NewEnvelope(seat.SeatId), kept, returned));
            if (!outcome.IsAccepted)
            {
                SoloTicketOfferMessage = outcome.Result.Rejection?.Message ?? "Choose at least one destination.";
                if (outcome.Result.Rejection?.Code == "StorageFaulted") RequireReload();
                return;
            }
            await PumpAsync();
        }
        catch (Exception) { RequireReload(); }
        finally { SetOperationInProgress(false); }
    }

    private Task SubmitSoloDrawAsync(int? slot) => SubmitSoloActionAsync(
        (coordinator, seat) => slot is null
            ? _rules.GetLegalActions(seat).CanDrawBlindTrainCard
                ? new SelectTrainCard(coordinator.NewEnvelope(seat.SeatId), null) : null
            : _rules.GetLegalActions(seat).DrawableFaceUpSlots.Contains(slot.Value)
                ? new SelectTrainCard(coordinator.NewEnvelope(seat.SeatId), slot) : null,
        reopenTrainCards: true);

    private async Task SubmitSoloActionAsync(
        Func<GameCoordinator, SeatView, GameCommand?> build, bool reopenTrainCards = false)
    {
        if (!CanUseSoloDrawPiles || _coordinator is not { } coordinator) return;
        var active = coordinator.Public.ActiveSeatId;
        var version = coordinator.Public.StateVersion;
        SetOperationInProgress(true);
        var accepted = false;
        try
        {
            var seat = await coordinator.GetSeatViewAsync(active);
            if (!ReferenceEquals(coordinator, _coordinator) ||
                coordinator.Public.StateVersion != version ||
                !IsGameplayScreenActive(Screen.Table) || !_windowActive) return;
            var command = build(coordinator, seat);
            if (command is null) return;
            var outcome = await coordinator.SubmitAsync(command);
            if (!outcome.IsAccepted)
            {
                Status = outcome.Result.Rejection?.Message ?? "That card is unavailable now.";
                if (outcome.Result.Rejection?.Code == "StorageFaulted") RequireReload();
                return;
            }
            accepted = true;
            Status = null;
            await PumpAsync();
        }
        catch (Exception) { RequireReload(); }
        finally { SetOperationInProgress(false); }

        if (accepted && reopenTrainCards && IsSoloHumanTurn &&
            _coordinator?.Public.TurnPhase == TurnPhase.AwaitingSecondTrainCard)
        {
            var human = Game.TableSeats.FirstOrDefault(tile =>
                tile.Seat.SeatId == active && tile.Seat.Operator == "human");
            if (human is not null) await ToggleSoloCardsAsync(human, SoloCardPanelSelection.TrainCards);
        }
    }
}
