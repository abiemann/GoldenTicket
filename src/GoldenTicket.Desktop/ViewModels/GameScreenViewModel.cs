using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using GoldenTicket.Domain;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

public enum GameScreenStage { Welcome, Settings, CharacterSelection, CameraSetup, Playing }
public enum CharacterRole { Unselected, Human, Computer }

public sealed partial class GameSeatChoice(int number) : ObservableObject
{
    private BitmapImage? _humanPortrait;
    private BitmapImage? _robotPortrait;
    private ImageSource? _unselectedPortrait;

    public int Number { get; } = number;
    public int PortraitNumber => Number switch
    {
        1 => 1,
        2 => 4,
        3 => 2,
        4 => 5,
        5 => 3,
        _ => throw new InvalidOperationException($"Character {Number} has no portrait."),
    };
    public PlayerColor TrainColor => PortraitNumber switch
    {
        1 => PlayerColor.Red,
        2 => PlayerColor.Green,
        3 => PlayerColor.Yellow,
        4 => PlayerColor.Blue,
        5 => PlayerColor.Black,
        _ => throw new InvalidOperationException($"Character {Number} has no train color."),
    };
    public bool IsChosen => Role != CharacterRole.Unselected;
    public string Label => Role switch
    {
        CharacterRole.Human => $"Player {RoleNumber}",
        CharacterRole.Computer => $"Computer {RoleNumber}",
        _ => "Select",
    };
    public string AccessibleName => $"Character {Number}, {Label}";
    public string PortraitUri => $"/GoldenTicket;component/Assets/Characters/character-{PortraitNumber:00}-{(Role == CharacterRole.Computer ? "robot" : "human")}-256.png";
    public ImageSource Portrait => Role switch
    {
        CharacterRole.Human => _humanPortrait ??= LoadPortrait(false),
        CharacterRole.Computer => _robotPortrait ??= LoadPortrait(true),
        _ => _unselectedPortrait ??= UnselectedPortrait(),
    };

    public ImageSource PortraitFor(bool computer) => computer
        ? _robotPortrait ??= LoadPortrait(true)
        : _humanPortrait ??= LoadPortrait(false);

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
        image.UriSource = new Uri($"pack://application:,,,/GoldenTicket;component/Assets/Characters/character-{PortraitNumber:00}-{(robot ? "robot" : "human")}-256.png");
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private ImageSource UnselectedPortrait()
    {
        var source = new FormatConvertedBitmap(_humanPortrait ??= LoadPortrait(false), PixelFormats.Bgra32, null, 0);
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = y * stride + x * 4;
            var blue = pixels[offset];
            var green = pixels[offset + 1];
            var red = pixels[offset + 2];
            if (IsJacketPixel(x, y, width, height, red, green, blue)) continue;
            var gray = (byte)Math.Clamp((int)Math.Round(.2126 * red + .7152 * green + .0722 * blue), 0, 255);
            pixels[offset] = gray;
            pixels[offset + 1] = gray;
            pixels[offset + 2] = gray;
        }
        var portrait = BitmapSource.Create(width, height, source.DpiX, source.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        portrait.Freeze();
        return portrait;
    }

    private bool IsJacketPixel(int x, int y, int width, int height, byte red, byte green, byte blue)
    {
        // The color check keeps scenery and skin gray even near the jacket's outer silhouette.
        var px = x * 256 / width;
        var py = y * 256 / height;
        var inJacket = py switch
        {
            < 160 => false,
            < 185 => px is >= 32 and <= 88 or >= 170 and <= 224,
            < 218 => px is >= 22 and <= 109 or >= 148 and <= 235,
            _ => px is <= 118 or >= 138,
        };
        if (!inJacket) return false;
        return TrainColor switch
        {
            PlayerColor.Red => red > green * 1.5 && red > blue * 1.5 && red > 50,
            PlayerColor.Green => green >= red * .85 && green > blue * 1.2 && red < 115 && green < 120,
            PlayerColor.Yellow => red > green * 1.12 && green > blue * 1.6 && green - blue > 80 && red > 100,
            PlayerColor.Blue => blue > red * 1.3 && blue > green * 1.2 && blue > 60,
            PlayerColor.Black => red < 95 && green < 90 && blue < 85,
            _ => false,
        };
    }
}

/// <summary>One visible layer of a public card stack. Both kinds fill the same 12 × 6 fan.</summary>
public sealed record CardStackLayer(double Left, double Top, string Fill, string Letter);

/// <summary>Public seat information placed around the live board, with no private card contents.</summary>
public sealed record GameTableSeat(SeatRow Seat, ImageSource Portrait, double Left, double Top)
{
    public IReadOnlyList<CardStackLayer> TrainCardLayers => BuildStack(Seat.CardCount, true);
    public IReadOnlyList<CardStackLayer> DestinationLayers => BuildStack(Seat.TicketCount, false);

    private static IReadOnlyList<CardStackLayer> BuildStack(int count, bool train)
    {
        if (count <= 0) return [];
        var layers = new CardStackLayer[count];
        for (var index = 0; index < count; index++)
        {
            var fraction = count == 1 ? .5 : (double)index / (count - 1);
            var fill = index == count - 1
                ? train ? "#FF9B613C" : "#FF648AA7"
                : index == 0 ? train ? "#FF69412F" : "#FF426481"
                : train ? "#FF865337" : "#FF527797";
            layers[index] = new CardStackLayer(12 * (1 - fraction), 6 * fraction,
                fill, index == count - 1 ? train ? "T" : "D" : "");
        }
        return layers;
    }
}

/// <summary>One pulsing cue centered on one requested physical train space.</summary>
public sealed record PlacementTargetRow(double X, double Y, int Number)
{
    private const double Diameter = 32;
    public double Left => X - Diameter / 2;
    public double Top => Y - Diameter / 2;
    public string Description => $"Train space {Number}";
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
        _main.Table.Seats.CollectionChanged += TableSeatsChanged;
        _main.Table.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TableViewModel.Placement))
            {
                RefreshPlacementTarget();
                OnPropertyChanged(nameof(ShowManualPlacementControls));
            }
            if (args.PropertyName is nameof(TableViewModel.TurnText) or
                nameof(TableViewModel.ActiveSeatName) or nameof(TableViewModel.Instruction))
                NotifyGuidanceChanged();
        };
        _main.Camera.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(CameraViewModel.GameTablePreview) or
                nameof(CameraViewModel.IsGameTablePreviewUpright)) RefreshPlacementTarget();
        };
        _main.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.ShowSoloOpeningTicketsOnBoard))
                TableSeatsChanged(null, null);
        };
        UpdateSelection();
        RefreshPlacementTarget();
    }

    public IReadOnlyList<GameSeatChoice> SeatChoices { get; }
    public IReadOnlyList<GameTableSeat> TableSeats { get; private set; } = [];

    private (string Turn, string Seat, string Instruction)? _guidanceOverride;
    public string GuidanceTurn => _guidanceOverride?.Turn ?? _main.Table.TurnText;
    public string GuidanceSeat => _guidanceOverride?.Seat ?? _main.Table.ActiveSeatName;
    public string GuidanceInstruction => _guidanceOverride?.Instruction ?? _main.Table.Instruction;
    public bool ShowManualPlacementControls => _main.Table.Placement is { AwaitingRestore: false } placement &&
        !RoutePlacementVerifier.Supports(placement.RouteId.Value, placement.TrainCount);

    internal void ShowGuidance(string turn, string seat, string instruction)
    {
        _guidanceOverride = (turn, seat, instruction);
        NotifyGuidanceChanged();
    }

    internal void ClearGuidance()
    {
        _guidanceOverride = null;
        NotifyGuidanceChanged();
    }

    private void NotifyGuidanceChanged()
    {
        OnPropertyChanged(nameof(GuidanceTurn));
        OnPropertyChanged(nameof(GuidanceSeat));
        OnPropertyChanged(nameof(GuidanceInstruction));
    }

    [ObservableProperty] private bool _showPlacementTarget;
    [ObservableProperty] private double _placementTargetX;
    [ObservableProperty] private double _placementTargetY;
    public IReadOnlyList<PlacementTargetRow> PlacementTargets { get; private set; } = [];

    public double PlacementTargetLeft => PlacementTargetX - 23;
    public double PlacementTargetTop => PlacementTargetY - 23;

    partial void OnPlacementTargetXChanged(double value) => OnPropertyChanged(nameof(PlacementTargetLeft));
    partial void OnPlacementTargetYChanged(double value) => OnPropertyChanged(nameof(PlacementTargetTop));

    private void RefreshPlacementTarget()
    {
        if (_main.Table.Placement is not { } placement ||
            _main.Camera.GameTablePreview is null ||
            !_main.Camera.IsGameTablePreviewUpright ||
            !PlacementBoardOverlay.TryGetTargets(_main.Manifest, placement.RouteId, placement.TrainCount,
                out var targets))
        {
            PlacementTargets = [];
            OnPropertyChanged(nameof(PlacementTargets));
            ShowPlacementTarget = false;
            return;
        }

        PlacementTargets = targets.Select((point, index) =>
            new PlacementTargetRow(point.X, point.Y, index + 1)).ToArray();
        OnPropertyChanged(nameof(PlacementTargets));
        PlacementTargetX = PlacementTargets.Average(point => point.X);
        PlacementTargetY = PlacementTargets.Average(point => point.Y);
        ShowPlacementTarget = true;
    }

    private void TableSeatsChanged(object? sender, NotifyCollectionChangedEventArgs? e)
    {
        // The first two seats face one another. Up to four seats flank the board; only a fifth
        // sits below it. The solo opening cards temporarily use that bottom space.
        var extraSeatPosition = _main.ShowSoloOpeningTicketsOnBoard ? (14d, 18d) : (595d, 735d);
        (double Left, double Top)[] positions = _main.Table.Seats.Count switch
        {
            2 => [(10, 330), (1180, 330)],
            3 => [(10, 330), (1180, 330), _main.ShowSoloOpeningTicketsOnBoard ? (14, 18) : (10, 530)],
            4 => [(10, 265), (1180, 265), (10, 530), (1180, 530)],
            _ => [(10, 265), (1180, 265), (10, 530), (1180, 530), extraSeatPosition],
        };
        TableSeats = _main.Table.Seats.Select((seat, index) =>
        {
            var choice = SeatChoices.SingleOrDefault(candidate => candidate.TrainColor == seat.Color);
            var portrait = choice?.PortraitFor(seat.Operator == "computer") ?? SeatChoices[index].PortraitFor(seat.Operator == "computer");
            return new GameTableSeat(seat, portrait, positions[index].Left, positions[index].Top);
        }).ToArray();
        OnPropertyChanged(nameof(TableSeats));
    }
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
    public bool IsSettings => Stage == GameScreenStage.Settings;
    public bool IsCharacterSelection => Stage == GameScreenStage.CharacterSelection;
    public bool IsCameraSetup => Stage == GameScreenStage.CameraSetup;
    public bool IsPlaying => Stage == GameScreenStage.Playing;
    public bool IsStartSelected => WelcomeSelection == 0;
    public bool IsReloadSelected => WelcomeSelection == 1;
    public bool IsPlaySelected => SeatSelection == SeatChoices.Count;

    partial void OnStageChanged(GameScreenStage value)
    {
        OnPropertyChanged(nameof(IsWelcome));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsCharacterSelection));
        OnPropertyChanged(nameof(IsCameraSetup));
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

    public void ShowPlaying()
    {
        Stage = GameScreenStage.Playing;
        _main.Camera.RequestGameTablePreview();
    }

    public void Back()
    {
        if (IsBusy) return;
        if (IsCameraSetup) CancelCameraSetup();
        else if (IsCharacterSelection || IsSettings) Stage = GameScreenStage.Welcome;
    }

    public void OpenSettings()
    {
        if (!IsBusy && IsWelcome) Stage = GameScreenStage.Settings;
    }

    public void SelectWelcome(int index)
    {
        if (index is 0 or 1 && (index == 0 || HasPreviousGame)) WelcomeSelection = index;
    }

    public void SelectSeat(int index)
    {
        if (index >= -1 && index <= SeatChoices.Count) SeatSelection = index;
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
                if (SeatSelection == SeatChoices.Count) OpenCameraSetup();
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
        for (var index = 0; index < chosen.Length; index++)
        {
            var seat = _main.Setup.Seats[index];
            seat.IsComputer = chosen[index].Role == CharacterRole.Computer;
            seat.DisplayName = chosen[index].Label;
            seat.Color = chosen[index].TrainColor;
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

    private void OpenCameraSetup()
    {
        if (IsFaceFlipping) return;
        if (!CanPlay)
        {
            Message = $"Choose at least {_main.Setup.MinPlayers} characters to play.";
            return;
        }
        Stage = GameScreenStage.CameraSetup;
    }

    private async Task StartMatchAsync()
    {
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

    public void CancelCameraSetup()
    {
        if (!IsCameraSetup || IsBusy) return;
        SelectSeat(-1);
        Stage = GameScreenStage.CharacterSelection;
    }

    public async Task ConfirmCameraSetupAndPlayAsync()
    {
        if (!IsCameraSetup || IsBusy) return;
        _main.Setup.ManualVerificationAccepted = true;
        await StartMatchAsync();
    }
}
