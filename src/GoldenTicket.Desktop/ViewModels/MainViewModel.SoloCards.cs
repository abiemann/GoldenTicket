using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Scoring;
using System.IO;

namespace GoldenTicket.Desktop.ViewModels;

public enum SoloCardPanelSelection { None, TrainCards, Destinations }

public sealed record SoloTrainCardRow(TrainCardKind Kind, string Label, int Count)
{
    public bool ShowCount => Count > 1;
    public string Description => $"{Label}: {Count} {(Count == 1 ? "card" : "cards")}";
}

public sealed record SoloDestinationCardRow(string Origin, string Destination, int Points, bool Completed)
{
    public string Description => $"{Origin} - {Destination}";
}

public sealed partial class MainViewModel
{
    private long _soloCardsGeneration;
    private SoloCardPanelSelection _soloCardPanelKind;
    private string? _presentationSettingsPath;
    [ObservableProperty] private bool _showDestinationsWhenViewingTrainCards = true;

    partial void OnShowDestinationsWhenViewingTrainCardsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDestinationMarkersOnBoard));
        OnPropertyChanged(nameof(BoardDestinationMarkers));
        OnPropertyChanged(nameof(BoardDestinationLines));
        _lastDestinationAlignmentUtc = DateTime.MinValue;
        AlignDestinationMarkers();
        try { Services.PresentationPreferences.Save(_presentationSettingsPath, value); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "The display choice is active but could not be saved for next launch: " + ex.Message;
        }
    }

    public SoloCardPanelSelection SoloCardPanelKind
    {
        get => _soloCardPanelKind;
        private set
        {
            if (!SetProperty(ref _soloCardPanelKind, value)) return;
            OnPropertyChanged(nameof(ShowSoloCardPanel));
            OnPropertyChanged(nameof(ShowSoloTrainCards));
            OnPropertyChanged(nameof(ShowSoloDestinations));
            OnPropertyChanged(nameof(ShowDestinationMarkersOnBoard));
            OnPropertyChanged(nameof(BoardDestinationMarkers));
            OnPropertyChanged(nameof(BoardDestinationLines));
            _lastDestinationAlignmentUtc = DateTime.MinValue;
            AlignDestinationMarkers();
        }
    }

    public bool ShowSoloCardPanel => SoloCardPanelKind != SoloCardPanelSelection.None;
    public bool ShowSoloTrainCards => SoloCardPanelKind == SoloCardPanelSelection.TrainCards;
    public bool ShowSoloDestinations => SoloCardPanelKind == SoloCardPanelSelection.Destinations;
    public IReadOnlyList<SoloTrainCardRow> SoloTrainCards { get; private set; } = [];
    public IReadOnlyList<SoloDestinationCardRow> SoloDestinationCards { get; private set; } = [];
    public IReadOnlyList<DestinationMarkerRow> SoloDestinationMarkers { get; private set; } = [];
    public IReadOnlyList<DestinationLineRow> SoloDestinationLines { get; private set; } = [];

    [RelayCommand]
    private Task ToggleSoloTrainCardsAsync(GameTableSeat? tile) =>
        ToggleSoloCardsAsync(tile, SoloCardPanelSelection.TrainCards);

    [RelayCommand]
    private Task ToggleSoloDestinationsAsync(GameTableSeat? tile) =>
        ToggleSoloCardsAsync(tile, SoloCardPanelSelection.Destinations);

    private async Task ToggleSoloCardsAsync(GameTableSeat? tile, SoloCardPanelSelection kind)
    {
        if (_coordinator is not { StorageFaulted: false } coordinator || tile is null ||
            !IsSoloHumanTurn || !IsGameplayScreenActive(Screen.Table) ||
            ShowSoloOpeningTicketsOnBoard || _operationInProgress || IsGameInputPaused || _exitRequested ||
            _mustReload || NeedsBoardReconciliation || !_windowActive || !_systemAvailable ||
            coordinator.Public.ActiveSeatId != tile.Seat.SeatId ||
            !Table.Seats.Any(seat => seat.SeatId == tile.Seat.SeatId && seat.Operator == "human")) return;

        if (SoloCardPanelKind == kind)
        {
            CloseSoloCardPanel();
            return;
        }

        CloseSoloCardPanel();
        var generation = _soloCardsGeneration;
        var version = coordinator.Public.StateVersion;
        try
        {
            var view = await coordinator.GetSeatViewAsync(tile.Seat.SeatId);
            if (generation != _soloCardsGeneration || !ReferenceEquals(coordinator, _coordinator) ||
                coordinator.Public.StateVersion != version || !IsSoloHumanTurn ||
                coordinator.Public.ActiveSeatId != tile.Seat.SeatId ||
                !IsGameplayScreenActive(Screen.Table)) return;

            SoloDestinationMarkers = DestinationBoardOverlay.BuildKept(_manifest, view.Tickets);
            SoloDestinationLines = DestinationBoardOverlay.BuildKeptLines(
                _manifest, view.Tickets, SoloDestinationMarkers);
            OnPropertyChanged(nameof(SoloDestinationMarkers));
            OnPropertyChanged(nameof(SoloDestinationLines));

            if (kind == SoloCardPanelSelection.TrainCards)
            {
                SoloTrainCards = view.Hand.GroupBy(card => card.Kind).OrderBy(group => group.Key)
                    .Select(group => new SoloTrainCardRow(group.Key, group.Key.ToString(), group.Count()))
                    .ToArray();
                OnPropertyChanged(nameof(SoloTrainCards));
            }
            else
            {
                var connectivity = SeatConnectivity.Build(_manifest,
                    view.Public.SeatOf(view.SeatId).ClaimedRoutes);
                SoloDestinationCards = view.Tickets.Select(ticketId =>
                {
                    var ticket = _manifest.Ticket(ticketId);
                    return new SoloDestinationCardRow(
                        _manifest.City(ticket.CityA).DisplayName,
                        _manifest.City(ticket.CityB).DisplayName,
                        ticket.Points, connectivity.Completes(ticket));
                }).ToArray();
                OnPropertyChanged(nameof(SoloDestinationCards));
            }
            SoloCardPanelKind = kind;
        }
        catch (Exception)
        {
            CloseSoloCardPanel();
            Status = "Your cards could not be shown. Try the stack again.";
        }
    }

    private void CloseSoloCardPanel()
    {
        _soloCardsGeneration++;
        SoloCardPanelKind = SoloCardPanelSelection.None;
        SoloTrainCards = [];
        SoloDestinationCards = [];
        SoloDestinationMarkers = [];
        SoloDestinationLines = [];
        OnPropertyChanged(nameof(SoloTrainCards));
        OnPropertyChanged(nameof(SoloDestinationCards));
        OnPropertyChanged(nameof(SoloDestinationMarkers));
        OnPropertyChanged(nameof(SoloDestinationLines));
    }
}
