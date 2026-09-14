using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using GoldenTicket.Domain;

namespace GoldenTicket.Desktop.ViewModels;

public enum GameScreenStage { Welcome, CharacterSelection, Playing }
public enum CharacterRole { Unselected, Human, Computer }

public sealed partial class GameSeatChoice(int number) : ObservableObject
{
    private BitmapImage? _humanPortrait;
    private BitmapImage? _robotPortrait;
    private ImageSource? _grayPortrait;

    public int Number { get; } = number;
    public bool IsChosen => Role != CharacterRole.Unselected;
    public string Label => Role switch
    {
        CharacterRole.Human => $"Player {RoleNumber}",
        CharacterRole.Computer => $"Computer {RoleNumber}",
        _ => "Select",
    };
    public string AccessibleName => $"Character {Number}, {Label}";
    public string PortraitUri => $"/GoldenTicket;component/Assets/Characters/character-{Number:00}-{(Role == CharacterRole.Computer ? "robot" : "human")}-256.png";
    public ImageSource Portrait => Role switch
    {
        CharacterRole.Human => _humanPortrait ??= LoadPortrait(false),
        CharacterRole.Computer => _robotPortrait ??= LoadPortrait(true),
        _ => _grayPortrait ??= GrayPortrait(),
    };

    [ObservableProperty] private CharacterRole _role;
    [ObservableProperty] private int _roleNumber;
    [ObservableProperty] private bool _isSelected;

    partial void OnRoleChanged(CharacterRole value)
    {
        OnPropertyChanged(nameof(IsChosen));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(AccessibleName));
        OnPropertyChanged(nameof(PortraitUri));
        OnPropertyChanged(nameof(Portrait));
    }

    partial void OnRoleNumberChanged(int value)
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(AccessibleName));
    }

    public void Cycle() => Role = Role switch
    {
        CharacterRole.Unselected => CharacterRole.Human,
        CharacterRole.Human => CharacterRole.Computer,
        _ => CharacterRole.Unselected,
    };

    private BitmapImage LoadPortrait(bool robot)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri($"pack://application:,,,/GoldenTicket;component/Assets/Characters/character-{Number:00}-{(robot ? "robot" : "human")}-256.png");
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private ImageSource GrayPortrait()
    {
        var image = new FormatConvertedBitmap();
        image.BeginInit();
        image.Source = _humanPortrait ??= LoadPortrait(false);
        image.DestinationFormat = PixelFormats.Gray8;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

/// <summary>Five character choices become the shared setup seats only when a new match starts.</summary>
public sealed partial class GameScreenViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public GameScreenViewModel(MainViewModel main)
    {
        _main = main;
        SeatChoices = Enumerable.Range(1, 5).Select(number => new GameSeatChoice(number)).ToArray();
        _main.Setup.SavedSessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPreviousGame));
        UpdateSelection();
    }

    public IReadOnlyList<GameSeatChoice> SeatChoices { get; }
    public bool HasPreviousGame => _main.Setup.SavedSessions.Count > 0;
    public int SelectedSeatCount => SeatChoices.Count(choice => choice.IsChosen);
    public bool CanPlay => SelectedSeatCount >= _main.Setup.MinPlayers && !IsFaceFlipping;

    [ObservableProperty] private GameScreenStage _stage = GameScreenStage.Welcome;
    [ObservableProperty] private int _welcomeSelection;
    [ObservableProperty] private int _seatSelection = -1;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isFaceFlipping;
    [ObservableProperty] private string? _message;

    public bool IsWelcome => Stage == GameScreenStage.Welcome;
    public bool IsCharacterSelection => Stage == GameScreenStage.CharacterSelection;
    public bool IsPlaying => Stage == GameScreenStage.Playing;
    public bool IsStartSelected => WelcomeSelection == 0;
    public bool IsReloadSelected => WelcomeSelection == 1;
    public bool IsPlaySelected => SeatSelection == SeatChoices.Count;

    partial void OnStageChanged(GameScreenStage value)
    {
        OnPropertyChanged(nameof(IsWelcome));
        OnPropertyChanged(nameof(IsCharacterSelection));
        OnPropertyChanged(nameof(IsPlaying));
        Message = null;
    }

    partial void OnWelcomeSelectionChanged(int value)
    {
        OnPropertyChanged(nameof(IsStartSelected));
        OnPropertyChanged(nameof(IsReloadSelected));
    }

    partial void OnSeatSelectionChanged(int value) => UpdateSelection();
    partial void OnIsFaceFlippingChanged(bool value) => OnPropertyChanged(nameof(CanPlay));

    private void UpdateSelection()
    {
        foreach (var choice in SeatChoices) choice.IsSelected = choice.Number - 1 == SeatSelection;
        OnPropertyChanged(nameof(IsPlaySelected));
    }

    public void ShowPlaying() => Stage = GameScreenStage.Playing;

    public void Back()
    {
        if (!IsBusy && IsCharacterSelection) Stage = GameScreenStage.Welcome;
    }

    public void SelectWelcome(int index)
    {
        if (index is 0 or 1 && (index == 0 || HasPreviousGame)) WelcomeSelection = index;
    }

    public void SelectSeat(int index)
    {
        if (index >= 0 && index <= SeatChoices.Count) SeatSelection = index;
    }

    public void CycleSeat(GameSeatChoice choice)
    {
        if (IsBusy || !IsCharacterSelection || !SeatChoices.Contains(choice)) return;
        choice.Cycle();
        var human = 0;
        var computer = 0;
        foreach (var seat in SeatChoices)
            seat.RoleNumber = seat.Role switch
            {
                CharacterRole.Human => ++human,
                CharacterRole.Computer => ++computer,
                _ => 0,
            };
        OnPropertyChanged(nameof(SelectedSeatCount));
        OnPropertyChanged(nameof(CanPlay));
        Message = null;
    }

    public void MoveSelection(int delta)
    {
        if (IsBusy) return;
        switch (Stage)
        {
            case GameScreenStage.Welcome:
                SelectWelcome(Math.Clamp(WelcomeSelection + delta, 0, HasPreviousGame ? 1 : 0));
                break;
            case GameScreenStage.CharacterSelection:
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
                if (WelcomeSelection == 0)
                {
                    foreach (var choice in SeatChoices) { choice.Role = CharacterRole.Unselected; choice.RoleNumber = 0; }
                    SeatSelection = -1;
                    _main.Setup.ManualVerificationAccepted = false;
                    OnPropertyChanged(nameof(SelectedSeatCount));
                    OnPropertyChanged(nameof(CanPlay));
                    Stage = GameScreenStage.CharacterSelection;
                }
                else await ReloadPreviousAsync();
                break;
            case GameScreenStage.CharacterSelection:
                if (SeatSelection == SeatChoices.Count) await PlayAsync();
                else
                {
                    if (SeatSelection < 0) SeatSelection = 0;
                    CycleSeat(SeatChoices[SeatSelection]);
                }
                break;
        }
    }

    private void ConfigureSeats()
    {
        var chosen = SeatChoices.Where(choice => choice.IsChosen).ToArray();
        while (_main.Setup.Seats.Count > chosen.Length) _main.Setup.RemoveSeat();
        while (_main.Setup.Seats.Count < chosen.Length) _main.Setup.AddSeat();
        var colors = Enum.GetValues<PlayerColor>();
        for (var index = 0; index < chosen.Length; index++)
        {
            var seat = _main.Setup.Seats[index];
            seat.IsComputer = chosen[index].Role == CharacterRole.Computer;
            seat.DisplayName = chosen[index].Label;
            seat.Color = colors[index];
        }
        _main.Setup.StartingSeatIndex = 0;
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
        if (IsFaceFlipping) return;
        if (!CanPlay)
        {
            Message = $"Choose at least {_main.Setup.MinPlayers} characters to play.";
            return;
        }
        IsBusy = true;
        try
        {
            ConfigureSeats();
            await _main.StartMatchAsync();
            if (Stage != GameScreenStage.Playing)
                Message = _main.Setup.ValidationMessage ?? "The game could not be started.";
        }
        finally { IsBusy = false; }
    }
}
