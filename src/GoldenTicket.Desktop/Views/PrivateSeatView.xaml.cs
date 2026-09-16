using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GoldenTicket.Desktop.ViewModels;

namespace GoldenTicket.Desktop.Views;

public partial class PrivateSeatView : UserControl
{
    private TicketChoiceRow? _pendingDrop;
    private Button? _pendingCard;
    private bool _committing;

    public PrivateSeatView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) ResetDropChoice();
        };
    }

    private void SoloDestination_Click(object sender, RoutedEventArgs e)
    {
        if (_committing || DataContext is not MainViewModel { ShowSoloOpeningTicketsOnBoard: true, PrivateSeat: { } seat } ||
            sender is not Button { DataContext: TicketChoiceRow ticket } card || !seat.Offer.Contains(ticket)) return;

        _pendingDrop = ticket;
        _pendingCard = card;
        SoloDropName.Text = ticket.Description;
        SoloDropConfirmation.Visibility = Visibility.Visible;
    }

    private void SoloDropNo_Click(object sender, RoutedEventArgs e) => ResetDropChoice();

    private async void SoloDropYes_Click(object sender, RoutedEventArgs e)
    {
        if (_committing || DataContext is not MainViewModel { ShowSoloOpeningTicketsOnBoard: true, PrivateSeat: { } seat } model ||
            _pendingDrop is not { } ticket || _pendingCard is not { } card || !seat.Offer.Contains(ticket)) return;

        _committing = true;
        SoloDropConfirmation.Visibility = Visibility.Collapsed;
        SoloDestinationCards.IsEnabled = false;
        SoloKeepAllButton.IsEnabled = false;
        foreach (var choice in seat.Offer) choice.Keep = !ReferenceEquals(choice, ticket);
        seat.RefreshKeepValidity();
        try
        {
            var move = new TranslateTransform();
            card.RenderTransform = move;
            var duration = new Duration(TimeSpan.FromMilliseconds(440));
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -900, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            });
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration));
            await Task.Delay(duration.TimeSpan);
            if (ReferenceEquals(model.PrivateSeat, seat)) await model.CommitTicketsAsync();
        }
        finally
        {
            // A rejected save can restore this same private view. Make its card selectable again.
            if (ReferenceEquals(model.PrivateSeat, seat))
            {
                ticket.Keep = true;
                seat.RefreshKeepValidity();
            }
            card.BeginAnimation(OpacityProperty, null);
            card.Opacity = 1;
            card.RenderTransform = Transform.Identity;
            _committing = false;
            SoloDestinationCards.IsEnabled = true;
            SoloKeepAllButton.IsEnabled = true;
            ResetDropChoice();
        }
    }

    private async void SoloKeepAll_Click(object sender, RoutedEventArgs e)
    {
        if (_committing || DataContext is not MainViewModel { ShowSoloOpeningTicketsOnBoard: true, PrivateSeat: { } seat } model) return;
        _committing = true;
        SoloKeepAllButton.IsEnabled = false;
        SoloDestinationCards.IsEnabled = false;
        try
        {
            foreach (var ticket in seat.Offer) ticket.Keep = true;
            seat.RefreshKeepValidity();
            await model.CommitTicketsAsync();
        }
        finally
        {
            _committing = false;
            SoloKeepAllButton.IsEnabled = true;
            SoloDestinationCards.IsEnabled = true;
        }
    }

    private void ResetDropChoice()
    {
        _pendingDrop = null;
        _pendingCard = null;
        SoloDropConfirmation.Visibility = Visibility.Collapsed;
    }
}
