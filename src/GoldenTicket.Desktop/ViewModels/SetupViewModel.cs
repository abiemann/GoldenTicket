using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GoldenTicket.Application;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Desktop.ViewModels;

/// <summary>One seat being configured before the match starts (DESIGN 3.3 step 9).</summary>
public sealed partial class SeatSetupRow : ObservableObject
{
    [ObservableProperty] private string _displayName = "Player";
    [ObservableProperty] private PlayerColor _color = PlayerColor.Blue;
    [ObservableProperty] private bool _isComputer;
    [ObservableProperty] private AiDifficulty _difficulty = AiDifficulty.Standard;

    public IReadOnlyList<PlayerColor> AvailableColors { get; } = Enum.GetValues<PlayerColor>();

    public IReadOnlyList<AiDifficulty> AvailableDifficulties { get; } = Enum.GetValues<AiDifficulty>();
}

/// <summary>A saved match offered for resumption (DESIGN 19.4 step 1: no private state shown).</summary>
public sealed record SavedSessionRow(SessionId SessionId, string Description);

/// <summary>
/// Table configuration and the saved-match list. Onboarding here covers seats, colours, operators,
/// clockwise order and the starting player. Camera and companion setup have separate desktop
/// screens; automatic train verification and the story layer remain later work.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly BoardManifest _manifest;
    private readonly HashSet<SeatSetupRow> _observedSeats = [];

    public SetupViewModel(BoardManifest manifest)
    {
        _manifest = manifest;

        Seats.Add(new SeatSetupRow { DisplayName = "Alex", Color = PlayerColor.Blue, IsComputer = false });
        Seats.Add(new SeatSetupRow { DisplayName = "Conductor", Color = PlayerColor.Red, IsComputer = true });
        Seats.Add(new SeatSetupRow { DisplayName = "Brakeman", Color = PlayerColor.Green, IsComputer = true });

        StartingSeatIndex = 0;
        Seats.CollectionChanged += (_, _) => ObserveSeats();
        ObserveSeats();
    }

    public ObservableCollection<SeatSetupRow> Seats { get; } = [];

    public ObservableCollection<SavedSessionRow> SavedSessions { get; } = [];

    [ObservableProperty] private int _startingSeatIndex;

    [ObservableProperty] private SavedSessionRow? _selectedSavedSession;
    [ObservableProperty] private string? _savedMatchMessage;

    public string SavedMatchSelectionHint => SavedSessions.Count == 0
        ? "No saved matches found."
        : SelectedSavedSession is null
            ? "Check one match below, then choose Resume selected match."
            : "The checked match is selected. Choose Resume selected match to open it.";

    partial void OnSelectedSavedSessionChanged(SavedSessionRow? value)
    {
        SavedMatchMessage = null;
        OnPropertyChanged(nameof(SavedMatchSelectionHint));
    }

    [ObservableProperty] private string? _validationMessage;
    [ObservableProperty] private bool _manualVerificationAccepted;

    public string EditionSummary =>
        $"{_manifest.Edition.DisplayName}  ·  {_manifest.Cities.Length} cities, " +
        $"{_manifest.Routes.Length} routes, {_manifest.Tickets.Length} destination tickets";

    /// <summary>
    /// DESIGN 6.3 keeps this honest: until a reviewer has compared the data package with the physical
    /// board, the application says so rather than implying the map has been verified.
    /// </summary>
    public string DataAuditWarning => _manifest.DataAudit.IsAudited
        ? $"Board data audited by {_manifest.DataAudit.Reviewer} on {_manifest.DataAudit.ReviewedOn}."
        : "Board data has NOT yet been audited against the physical board. Check any route or ticket " +
          "that looks wrong before trusting a result.";

    public bool DataIsAudited => _manifest.DataAudit.IsAudited;

    public int MinPlayers => _manifest.RulesConstants.MinPlayers;

    public int MaxPlayers => _manifest.RulesConstants.MaxPlayers;

    public bool CanAddSeat => Seats.Count < MaxPlayers;

    public bool CanRemoveSeat => Seats.Count > MinPlayers;

    public int HumanSeatCount => Seats.Count(seat => !seat.IsComputer);

    private void ObserveSeats()
    {
        foreach (var seat in _observedSeats) seat.PropertyChanged -= SeatPropertyChanged;
        _observedSeats.Clear();
        foreach (var seat in Seats)
            if (_observedSeats.Add(seat)) seat.PropertyChanged += SeatPropertyChanged;
        OnPropertyChanged(nameof(HumanSeatCount));
    }

    private void SeatPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SeatSetupRow.IsComputer)) OnPropertyChanged(nameof(HumanSeatCount));
    }

    public void AddSeat()
    {
        if (!CanAddSeat) return;

        var used = Seats.Select(seat => seat.Color).ToHashSet();
        var free = Enum.GetValues<PlayerColor>().FirstOrDefault(color => !used.Contains(color));

        Seats.Add(new SeatSetupRow { DisplayName = $"Player {Seats.Count + 1}", Color = free, IsComputer = true });
        NotifySeatCountChanged();
    }

    public void RemoveSeat()
    {
        if (!CanRemoveSeat) return;

        Seats.RemoveAt(Seats.Count - 1);
        if (StartingSeatIndex >= Seats.Count) StartingSeatIndex = Seats.Count - 1;
        NotifySeatCountChanged();
    }

    private void NotifySeatCountChanged()
    {
        OnPropertyChanged(nameof(CanAddSeat));
        OnPropertyChanged(nameof(CanRemoveSeat));
    }

    /// <summary>Validates the table and builds the session setup, or reports why it cannot.</summary>
    public SessionSetup? TryBuildSetup()
    {
        ValidationMessage = null;

        if (!ManualVerificationAccepted)
        {
            ValidationMessage = "Select manual verification and agree to check the whole physical board before starting.";
            return null;
        }

        if (Seats.Count < MinPlayers || Seats.Count > MaxPlayers)
        {
            ValidationMessage = $"This edition supports {MinPlayers} to {MaxPlayers} seats.";
            return null;
        }

        if (Seats.Select(seat => seat.Color).Distinct().Count() != Seats.Count)
        {
            ValidationMessage = "Each seat needs a different physical train colour.";
            return null;
        }

        if (Seats.Any(seat => string.IsNullOrWhiteSpace(seat.DisplayName)))
        {
            ValidationMessage = "Every seat needs a name.";
            return null;
        }

        var built = ImmutableArray.CreateBuilder<Seat>(Seats.Count);
        for (var index = 0; index < Seats.Count; index++)
        {
            var row = Seats[index];
            built.Add(new Seat(
                new SeatId(index + 1),
                row.DisplayName.Trim(),
                row.Color,
                row.IsComputer ? SeatKind.Computer : SeatKind.Human,
                row.Difficulty));
        }

        var seats = built.ToImmutable();
        var starting = seats[Math.Clamp(StartingSeatIndex, 0, seats.Length - 1)];

        // DESIGN 23.1: this build verifies physical placement by operator attestation.
        return new SessionSetup(SessionId.New(), seats, starting.SeatId, VerificationMode.Manual);
    }

    public void LoadSavedSessions(IReadOnlyList<SessionSummary> summaries)
    {
        var selectedId = SelectedSavedSession?.SessionId;
        SelectedSavedSession = null;
        SavedSessions.Clear();
        foreach (var summary in summaries)
        {
            SavedSessions.Add(new SavedSessionRow(
                summary.SessionId,
                summary.UnavailableReason is { } reason
                    ? $"Unavailable saved match · {summary.SessionId} · {reason} Select Resume to retry verification."
                    : SavedMatchDescription(summary)));
        }
        SelectedSavedSession = SavedSessions.FirstOrDefault(row => row.SessionId == selectedId)
            ?? (SavedSessions.Count == 1 ? SavedSessions[0] : null);
        SavedMatchMessage = null;
        OnPropertyChanged(nameof(SavedMatchSelectionHint));
    }

    private static string SavedMatchDescription(SessionSummary summary)
    {
        var name = string.IsNullOrWhiteSpace(summary.LatestCheckpointName)
            ? "" : $"{summary.LatestCheckpointName}  ·  ";
        var status = summary.Lifecycle switch
        {
            SessionLifecycle.Setup => "Setting up",
            SessionLifecycle.Active => "In progress",
            SessionLifecycle.Finished => "Finished",
            SessionLifecycle.PreparingPackAway => "Preparing to pack away",
            SessionLifecycle.PackedAway => "Packed away",
            SessionLifecycle.Rebuilding => "Rebuilding the board",
            _ => "Saved match",
        };
        return $"{name}{summary.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}  ·  turn {summary.TurnNumber}  ·  " +
               $"{status}  ·  {string.Join(", ", summary.SeatNames)}";
    }
}
