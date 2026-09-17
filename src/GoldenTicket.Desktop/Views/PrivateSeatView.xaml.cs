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
        // Keep drives the board's line and city rings. Remove the rejected card at the
        // same time, then let the player see the updated board before the whole tile exits.
        card.Visibility = Visibility.Collapsed;
        try
        {
            if (SystemParameters.ClientAreaAnimation)
            {
                await Task.Delay(180);
                var move = new TranslateTransform();
                SoloCardsTile.RenderTransform = move;
                var duration = new Duration(TimeSpan.FromMilliseconds(500));
                move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 225, duration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                });
                await Task.Delay(duration.TimeSpan);
            }
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
            card.Visibility = Visibility.Visible;
            if (SoloCardsTile.RenderTransform is TranslateTransform tileMove)
                tileMove.BeginAnimation(TranslateTransform.YProperty, null);
            SoloCardsTile.RenderTransform = Transform.Identity;
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
