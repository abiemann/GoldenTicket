using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GoldenTicket.Desktop.ViewModels;

namespace GoldenTicket.Desktop.Views;

public partial class GameScreenView : UserControl
{
    private bool _faceFlipping;

    public GameScreenView() => InitializeComponent();

    private GameScreenViewModel? Game => (DataContext as MainViewModel)?.Game;

    public void FocusCurrentChoice() => _ = Dispatcher.BeginInvoke(DispatcherPriority.Input,
        new Action(() => { if (IsVisible && IsEnabled) Keyboard.Focus(this); }));

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var game = Game;
        if (game is null || game.IsPlaying || game.IsBusy) return;
        if (e.Key is Key.Up or Key.Left)
        {
            game.MoveSelection(-1);
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Right)
        {
            game.MoveSelection(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !e.IsRepeat)
        {
            if (Keyboard.FocusedElement is CheckBox ||
                Keyboard.FocusedElement is Button focused &&
                (Equals(focused.Tag, "NavigationBack") || ReferenceEquals(focused, PlayButton))) return;
            e.Handled = true;
            if (game.IsCharacterSelection && game.SeatSelection < game.SeatChoices.Count)
            {
                var index = Keyboard.FocusedElement is Button { DataContext: GameSeatChoice choice }
                    ? choice.Number - 1 : Math.Max(0, game.SeatSelection);
                game.SelectSeat(index);
                FlipFace(game.SeatChoices[index], FindSeatButton(index));
            }
            else
                await game.ActivateSelectedAsync();
            FocusCurrentChoice();
        }
    }

    private void Start_MouseEnter(object sender, MouseEventArgs e) => Game?.SelectWelcome(0);
    private void Reload_MouseEnter(object sender, MouseEventArgs e) => Game?.SelectWelcome(1);

    private async void Welcome_Click(object sender, RoutedEventArgs e)
    {
        var game = Game;
        if (game is null) return;
        game.SelectWelcome(ReferenceEquals(sender, ReloadButton) ? 1 : 0);
        await game.ActivateSelectedAsync();
        FocusCurrentChoice();
    }

    private void Seat_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button { DataContext: GameSeatChoice choice }) Game?.SelectSeat(choice.Number - 1);
    }

    private void Seat_Click(object sender, RoutedEventArgs e)
    {
        var game = Game;
        if (game is null || sender is not Button { DataContext: GameSeatChoice choice }) return;
        game.SelectSeat(choice.Number - 1);
        FlipFace(choice, (Button)sender);
        FocusCurrentChoice();
    }

    private Button? FindSeatButton(int index)
    {
        if (SeatItems.ItemContainerGenerator.ContainerFromIndex(index) is not DependencyObject container) return null;
        return FindButton(container);
    }

    private static Button? FindButton(DependencyObject parent)
    {
        if (parent is Button button) return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindButton(VisualTreeHelper.GetChild(parent, index)) is { } found) return found;
        return null;
    }

    private void FlipFace(GameSeatChoice choice, Button? button)
    {
        var game = Game;
        if (_faceFlipping || game is null || game.IsBusy) return;
        if (button is null || !SystemParameters.ClientAreaAnimation)
        {
            game.CycleSeat(choice);
            return;
        }

        _faceFlipping = true;
        game.IsFaceFlipping = true;
        var transform = new ScaleTransform(1, 1);
        button.RenderTransformOrigin = new Point(.5, .5);
        button.RenderTransform = transform;
        var close = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(115));
        close.Completed += (_, _) =>
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            transform.ScaleX = 0;
            game.CycleSeat(choice);
            var open = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(115));
            open.Completed += (_, _) =>
            {
                transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                transform.ScaleX = 1;
                _faceFlipping = false;
                game.IsFaceFlipping = false;
            };
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, open);
        };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, close);
    }

    private void Play_MouseEnter(object sender, MouseEventArgs e)
    {
        var game = Game;
        if (game is not null) game.SelectSeat(game.SeatChoices.Count);
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        var game = Game;
        if (game is null) return;
        game.SelectSeat(game.SeatChoices.Count);
        await game.ActivateSelectedAsync();
        FocusCurrentChoice();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        Game?.Back();
        FocusCurrentChoice();
    }
}
