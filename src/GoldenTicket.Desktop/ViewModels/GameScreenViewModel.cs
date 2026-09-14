using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GoldenTicket.Desktop.ViewModels;

public enum GameScreenStage { Welcome, PlayerCount, AiSelection, Playing }

public sealed partial class PlayerCountChoice(int count) : ObservableObject
{
    public int Count { get; } = count;
    public IReadOnlyList<int> People { get; } = Enumerable.Range(0, count).ToArray();

    [ObservableProperty] private bool _isSelected;
}

public sealed partial class GameSeatChoice : ObservableObject, IDisposable
{
    private readonly SeatSetupRow _seat;

    public GameSeatChoice(SeatSetupRow seat, int number)
    {
        _seat = seat;
        Number = number;
        _seat.PropertyChanged += SeatChanged;
    }

    public int Number { get; }
    public bool IsComputer => _seat.IsComputer;
    public string Label => $"{(IsComputer ? "Computer" : "Player")} {Number}";
    public string PortraitUri => $"/GoldenTicket;component/Assets/Characters/character-{Number:00}-{(IsComputer ? "robot" : "human")}-256.png";

    [ObservableProperty] private bool _isSelected;

    public void Toggle()
    {
        _seat.IsComputer = !_seat.IsComputer;
        _seat.DisplayName = Label;
    }

    private void SeatChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SeatSetupRow.IsComputer)) return;
        OnPropertyChanged(nameof(IsComputer));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(PortraitUri));
    }

    public void Dispose() => _seat.PropertyChanged -= SeatChanged;
}

/// <summary>The player-facing setup uses the same seat rows and match commands as the technical setup.</summary>
public sealed partial class GameScreenViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public GameScreenViewModel(MainViewModel main)
    {
        _main = main;
        CountOptions = Enumerable.Range(2, 4).Select(count => new PlayerCountChoice(count)).ToArray();
        _main.Setup.SavedSessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPreviousGame));
        _main.Setup.Seats.CollectionChanged += (_, _) => SyncSeats();
        UpdateSelection();
    }

    public IReadOnlyList<PlayerCountChoice> CountOptions { get; }
    public ObservableCollection<GameSeatChoice> SeatChoices { get; } = [];
    public bool HasPreviousGame => _main.Setup.SavedSessions.Count > 0;

    [ObservableProperty] private GameScreenStage _stage = GameScreenStage.Welcome;
    [ObservableProperty] private int _welcomeSelection;
    [ObservableProperty] private int _countSelection = 2;
    [ObservableProperty] private int _seatSelection;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _message;

    public bool IsWelcome => Stage == GameScreenStage.Welcome;
    public bool IsPlayerCount => Stage == GameScreenStage.PlayerCount;
    public bool IsAiSelection => Stage == GameScreenStage.AiSelection;
    public bool IsPlaying => Stage == GameScreenStage.Playing;
    public bool IsStartSelected => WelcomeSelection == 0;
    public bool IsReloadSelected => WelcomeSelection == 1;
    public bool IsPlaySelected => SeatSelection == SeatChoices.Count;

    partial void OnStageChanged(GameScreenStage value)
    {
        OnPropertyChanged(nameof(IsWelcome));
        OnPropertyChanged(nameof(IsPlayerCount));
        OnPropertyChanged(nameof(IsAiSelection));
        OnPropertyChanged(nameof(IsPlaying));
        Message = null;
    }

    partial void OnWelcomeSelectionChanged(int value)
    {
        OnPropertyChanged(nameof(IsStartSelected));
        OnPropertyChanged(nameof(IsReloadSelected));
    }

    partial void OnCountSelectionChanged(int value) => UpdateSelection();
    partial void OnSeatSelectionChanged(int value) => UpdateSelection();

    private void UpdateSelection()
    {
        if (CountOptions is not null)
            foreach (var option in CountOptions) option.IsSelected = option.Count == CountSelection;
        foreach (var choice in SeatChoices) choice.IsSelected = choice.Number - 1 == SeatSelection;
        OnPropertyChanged(nameof(IsPlaySelected));
    }

    public void ShowPlaying() => Stage = GameScreenStage.Playing;

    public void Back()
    {
        if (IsBusy) return;
        Stage = Stage switch
        {
            GameScreenStage.AiSelection => GameScreenStage.PlayerCount,
            GameScreenStage.PlayerCount => GameScreenStage.Welcome,
            _ => Stage,
        };
    }

    public void SelectWelcome(int index)
    {
        if (index is 0 or 1 && (index == 0 || HasPreviousGame)) WelcomeSelection = index;
    }

    public void SelectCount(int count)
    {
        if (count is >= 2 and <= 5) CountSelection = count;
    }

    public void SelectSeat(int index)
    {
        if (index >= 0 && index <= SeatChoices.Count) SeatSelection = index;
    }

    public void MoveSelection(int delta)
    {
        if (IsBusy) return;
        switch (Stage)
        {
            case GameScreenStage.Welcome:
                SelectWelcome(Math.Clamp(WelcomeSelection + delta, 0, HasPreviousGame ? 1 : 0));
                break;
            case GameScreenStage.PlayerCount:
                SelectCount(Math.Clamp(CountSelection + delta, 2, 5));
                break;
            case GameScreenStage.AiSelection:
                SelectSeat(Math.Clamp(SeatSelection + delta, 0, SeatChoices.Count));
                break;
        }
    }

    public async Task ActivateSelectedAsync()
    {
        if (IsBusy) return;
        switch (Stage)
        {
            case GameScreenStage.Welcome:
                if (WelcomeSelection == 0) Stage = GameScreenStage.PlayerCount;
                else await ReloadPreviousAsync();
                break;
            case GameScreenStage.PlayerCount:
                ConfigureSeats(CountSelection);
                Stage = GameScreenStage.AiSelection;
                SyncSeats();
                break;
            case GameScreenStage.AiSelection:
                if (SeatSelection == SeatChoices.Count) await PlayAsync();
                else SeatChoices[SeatSelection].Toggle();
                break;
        }
    }

    private void ConfigureSeats(int count)
    {
        while (_main.Setup.Seats.Count > count) _main.Setup.RemoveSeat();
        while (_main.Setup.Seats.Count < count) _main.Setup.AddSeat();
        for (var index = 0; index < count; index++)
        {
            var seat = _main.Setup.Seats[index];
            seat.IsComputer = false;
            seat.DisplayName = $"Player {index + 1}";
        }
        _main.Setup.StartingSeatIndex = 0;
        SyncSeats();
        SeatSelection = 0;
        UpdateSelection();
    }

    public void SyncSeats()
    {
        if (Stage != GameScreenStage.AiSelection) return;
        foreach (var choice in SeatChoices) choice.Dispose();
        SeatChoices.Clear();
        for (var index = 0; index < _main.Setup.Seats.Count; index++)
            SeatChoices.Add(new GameSeatChoice(_main.Setup.Seats[index], index + 1));
        CountSelection = _main.Setup.Seats.Count;
        SeatSelection = Math.Clamp(SeatSelection, 0, SeatChoices.Count);
        UpdateSelection();
    }

    private async Task ReloadPreviousAsync()
    {
        if (!HasPreviousGame) return;
        IsBusy = true;
        try
        {
            _main.Setup.SelectedSavedSession = _main.Setup.SavedSessions[0];
            await _main.ResumeMatchAsync();
            if (Stage != GameScreenStage.Playing)
                Message = _main.Setup.SavedMatchMessage ?? "The previous game could not be opened.";
        }
        finally { IsBusy = false; }
    }

    private async Task PlayAsync()
    {
        IsBusy = true;
        try
        {
            await _main.StartMatchAsync();
            if (Stage != GameScreenStage.Playing)
                Message = _main.Setup.ValidationMessage ?? "The game could not be started.";
        }
        finally { IsBusy = false; }
    }
}
