using CommunityToolkit.Mvvm.Input;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private sealed record PracticalTurn(SessionId SessionId, SeatId SeatId, int TurnNumber,
        SessionLifecycle Lifecycle);

    private PracticalTurn? _practicalTurn;

    public bool HasAcceptedPracticalTurn => _practicalTurn is { } turn &&
        _gameLayerVisible && Connection.UsePractical && CanConnectPhone &&
        IsGameplayScreenActive(Screen.Table) && _coordinator is { } coordinator &&
        coordinator.SessionId == turn.SessionId && coordinator.Public.Lifecycle == turn.Lifecycle &&
        coordinator.Public.TurnNumber == turn.TurnNumber &&
        (turn.Lifecycle == SessionLifecycle.Setup
            ? _revealable?.SeatId == turn.SeatId
            : coordinator.Public.ActiveSeatId == turn.SeatId);

    public bool CanUseGameTableControls => IsSingleHumanGame || HasAcceptedPracticalTurn;
    public bool IsGameTableHumanTurn => CanUseGameTableControls &&
        _coordinator?.Public is { Lifecycle: SessionLifecycle.Active } view &&
        view.SeatOf(view.ActiveSeatId).Kind == SeatKind.Human;

    private bool CanTakePracticalTurn() => _gameLayerVisible && ShowPracticalHandoff;

    [RelayCommand(CanExecute = nameof(CanTakePracticalTurn))]
    public async Task TakePracticalTurnAsync()
    {
        if (!CanTakePracticalTurn() || _coordinator is not { } coordinator ||
            _revealable is not { } seat) return;
        var generation = _revealGeneration;
        var view = await coordinator.GetSeatViewAsync(seat.SeatId);
        if (generation != _revealGeneration || !CanTakePracticalTurn() ||
            !ReferenceEquals(coordinator, _coordinator) || _revealable?.SeatId != seat.SeatId ||
            view.Public.StateVersion != coordinator.Public.StateVersion) return;

        Connection.InvalidatePrivateGrants();
        CloseSoloCardPanel();
        _practicalTurn = new(coordinator.SessionId, seat.SeatId,
            view.Public.TurnNumber, view.Public.Lifecycle);
        NotifyPracticalTurnChanged();
        if (view.Public.Lifecycle == SessionLifecycle.Setup)
        {
            var summary = view.Public.SeatOf(seat.SeatId);
            PrivateSeat = new PrivateSeatViewModel(view, _rules.GetLegalActions(view), _manifest,
                summary.DisplayName, summary.Symbol);
            return;
        }

        await RefreshSoloDrawActionsAsync(view.Public);
        if (generation != _revealGeneration || !HasAcceptedPracticalTurn || ShowSoloTicketOffer) return;
        var tile = Game.TableSeats.FirstOrDefault(item => item.Seat.SeatId == seat.SeatId);
        if (tile is not null) await ToggleSoloCardsAsync(tile, SoloCardPanelSelection.TrainCards);
    }

    private void ClearPracticalTurn()
    {
        if (_practicalTurn is null) return;
        _practicalTurn = null;
        ClearSoloTicketOffer();
        ResetBoardFirstClaimFlow();
        NotifyPracticalTurnChanged();
    }

    private void NotifyPracticalTurnChanged()
    {
        OnPropertyChanged(nameof(HasAcceptedPracticalTurn));
        OnPropertyChanged(nameof(CanUseGameTableControls));
        OnPropertyChanged(nameof(IsGameTableHumanTurn));
        OnPropertyChanged(nameof(ShowPracticalHandoff));
        OnPropertyChanged(nameof(ShowSoloOpeningTicketsOnBoard));
        OnPropertyChanged(nameof(ShowDestinationMarkersOnBoard));
        OnPropertyChanged(nameof(BoardDestinationMarkers));
        OnPropertyChanged(nameof(BoardDestinationLines));
        OnPropertyChanged(nameof(GameTableBoardTop));
        OnPropertyChanged(nameof(ShowDrawPanels));
        OnPropertyChanged(nameof(ShowBoardFirstClaimProposal));
        TakePracticalTurnCommand.NotifyCanExecuteChanged();
        NotifySoloDrawCommands();
    }
}
