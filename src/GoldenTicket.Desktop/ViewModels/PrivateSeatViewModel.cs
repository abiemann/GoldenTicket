using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Scoring;

namespace GoldenTicket.Desktop.ViewModels;

public sealed record HandGroupRow(TrainCardKind Kind, int Count, string Label);

public sealed record TicketRow(TicketId TicketId, string Description, int Points, bool Completed);

public sealed partial class TicketChoiceRow(TicketId ticketId, string description, int points, int estimatedTrains)
    : ObservableObject
{
    public TicketId TicketId { get; } = ticketId;

    public string Description { get; } = description;

    public int Points { get; } = points;

    public string Estimate { get; } = estimatedTrains == int.MaxValue
        ? "no route available"
        : $"about {estimatedTrains} more train{(estimatedTrains == 1 ? "" : "s")}";

    [ObservableProperty] private bool _keep = true;
}

public sealed record ClaimOptionRow(
    RouteId RouteId,
    string RouteText,
    int Length,
    int Points,
    string ColorRequirement,
    ImmutableArray<PaymentOption> Payments);

public sealed record PaymentRow(PaymentOption Option, string Description);

/// <summary>
/// One human seat's private view (DESIGN 4.2-4.3). It is built from a <see cref="SeatView"/>, which
/// carries the public state plus this seat's own cards and choices and nothing else. Discarding this
/// object is what hides the hand: the data is not merely made invisible.
/// </summary>
public sealed partial class PrivateSeatViewModel : ObservableObject
{
    private readonly BoardManifest _manifest;
    private readonly SeatView _view;

    public PrivateSeatViewModel(SeatView view, LegalActions legal, BoardManifest manifest, string seatName, string symbol)
    {
        _manifest = manifest;
        _view = view;
        SeatId = view.SeatId;
        SeatName = seatName;
        Symbol = symbol;
        StateVersion = view.Public.StateVersion;

        var connectivity = SeatConnectivity.Build(
            manifest, view.Public.Seats.First(s => s.SeatId == view.SeatId).ClaimedRoutes);

        foreach (var group in view.Hand.GroupBy(card => card.Kind).OrderBy(group => (int)group.Key))
        {
            var reserved = group.Count(card => view.ReservedCards.Contains(card.Id));
            var label = reserved > 0 ? $"{group.Count()} ({reserved} reserved)" : group.Count().ToString();
            Hand.Add(new HandGroupRow(group.Key, group.Count(), label));
        }

        foreach (var ticketId in view.Tickets)
        {
            var ticket = manifest.Ticket(ticketId);
            Tickets.Add(new TicketRow(
                ticketId,
                $"{manifest.City(ticket.CityA).DisplayName} - {manifest.City(ticket.CityB).DisplayName}",
                ticket.Points,
                connectivity.Completes(ticket)));
        }

        var offered = !view.SetupOffer.IsEmpty ? view.SetupOffer : view.Offer?.Offered ?? [];
        IsSetupOffer = !view.SetupOffer.IsEmpty;
        MinimumKeep = IsSetupOffer
            ? manifest.RulesConstants.SetupTicketMinimumKeep
            : view.Offer?.MinimumKeep ?? manifest.RulesConstants.InGameTicketMinimumKeep;

        foreach (var ticketId in offered)
        {
            var ticket = manifest.Ticket(ticketId);
            Offer.Add(new TicketChoiceRow(
                ticketId,
                $"{manifest.City(ticket.CityA).DisplayName} - {manifest.City(ticket.CityB).DisplayName}",
                ticket.Points,
                GoldenTicket.AI.RoutePlanner.EstimateCost(view, manifest, ticket)));
        }

        if (IsSetupOffer)
        {
            foreach (var marker in DestinationBoardOverlay.Build(manifest, Offer))
                DestinationMarkers.Add(marker);
            foreach (var line in DestinationBoardOverlay.BuildLines(manifest, Offer, DestinationMarkers))
                DestinationLines.Add(line);
        }

        MustChooseTickets = legal.MustCommitTicketSelection && Offer.Count > 0;
        CanDrawBlind = legal.CanDrawBlindTrainCard;
        MustResolvePendingClaim = legal.MustResolvePendingClaim;
        IsSecondDraw = view.Public.TurnPhase == TurnPhase.AwaitingSecondTrainCard;
        CanDrawTickets = legal.CanRequestTicketOffer;

        foreach (var slot in legal.DrawableFaceUpSlots)
        {
            if (view.Public.FaceUp[slot] is not { } kind) continue;
            DrawableSlots.Add(new MarketSlotRow(slot, kind, kind.ToString()));
        }

        foreach (var claim in legal.Claims
                     .OrderByDescending(claim => manifest.RulesConstants.ScoreForLength(claim.Length))
                     .ThenBy(claim => manifest.Describe(claim.RouteId)))
        {
            ClaimOptions.Add(new ClaimOptionRow(
                claim.RouteId,
                manifest.Describe(claim.RouteId),
                claim.Length,
                manifest.RulesConstants.ScoreForLength(claim.Length),
                claim.RequiredCardKind?.ToString() ?? "any single colour (grey)",
                claim.Payments));
        }
    }

    public SeatId SeatId { get; }

    public string SeatName { get; }

    public string Symbol { get; }

    /// <summary>
    /// The version these choices were computed against. Every command carries it, so a selection made
    /// before something else happened is refused rather than applied to a changed table (DESIGN 6.2).
    /// </summary>
    public long StateVersion { get; }

    public ObservableCollection<HandGroupRow> Hand { get; } = [];

    public ObservableCollection<TicketRow> Tickets { get; } = [];

    public ObservableCollection<TicketChoiceRow> Offer { get; } = [];

    /// <summary>Opening-destination city rings, positioned over the upright live board crop.</summary>
    public ObservableCollection<DestinationMarkerRow> DestinationMarkers { get; } = [];

    /// <summary>One connection between the two highlighted cities on each opening destination.</summary>
    public ObservableCollection<DestinationLineRow> DestinationLines { get; } = [];

    public ObservableCollection<MarketSlotRow> DrawableSlots { get; } = [];

    public ObservableCollection<ClaimOptionRow> ClaimOptions { get; } = [];

    public ObservableCollection<PaymentRow> Payments { get; } = [];

    public bool IsSetupOffer { get; }

    public int MinimumKeep { get; }

    public bool MustChooseTickets { get; }

    public bool CanDrawBlind { get; }

    public bool CanDrawTickets { get; }

    public bool IsSecondDraw { get; }

    public bool MustResolvePendingClaim { get; }

    public bool CanClaim => ClaimOptions.Count > 0;

    [ObservableProperty] private ClaimOptionRow? _selectedClaim;

    [ObservableProperty] private PaymentRow? _selectedPayment;

    [ObservableProperty] private string? _message;

    public int KeptCount => Offer.Count(row => row.Keep);

    public bool KeepSelectionIsValid => KeptCount >= MinimumKeep;

    partial void OnSelectedClaimChanged(ClaimOptionRow? value)
    {
        Payments.Clear();
        SelectedPayment = null;
        if (value is null) return;

        foreach (var option in value.Payments.OrderBy(option => option.Locomotives).ThenBy(option => option.Color))
            Payments.Add(new PaymentRow(option, option.Describe()));

        SelectedPayment = Payments.FirstOrDefault();
    }

    public ImmutableArray<TicketId> KeptTickets => [.. Offer.Where(row => row.Keep).Select(row => row.TicketId)];

    /// <summary>
    /// The exact card instances the chosen payment spends. Resolved from this seat's own view, so no
    /// other seat's information is touched and the same option always spends the same cards.
    /// </summary>
    public ImmutableArray<CardId> ResolveSelectedPayment() =>
        SelectedPayment is null ? [] : LegalActionCalculator.ResolveCards(_view, SelectedPayment.Option);

    public void RefreshKeepValidity()
    {
        OnPropertyChanged(nameof(KeptCount));
        OnPropertyChanged(nameof(KeepSelectionIsValid));
    }

    public string TicketInstruction => IsSetupOffer
        ? $"Keep at least {MinimumKeep} of these {Offer.Count} opening destinations. " +
          "An unfinished destination costs its value at the end."
        : $"Keep at least {MinimumKeep} of these {Offer.Count} destinations. " +
          "The rest go under the deck in the order shown.";
}
