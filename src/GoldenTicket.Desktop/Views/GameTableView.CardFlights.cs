using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GoldenTicket.Desktop.ViewModels;

namespace GoldenTicket.Desktop.Views;

public partial class GameTableView
{
    private readonly Dictionary<Border, Storyboard> _cardFlights = [];

    private void OnCardDrawn(object? sender, CardFlightEventArgs flight)
    {
        if (_model is null || _model.GameplayScreen != Screen.Table || !_model.CanPresentCardFlights ||
            !SystemParameters.ClientAreaAnimation) return;
        var owner = _model.Game.TableSeats.FirstOrDefault(tile => tile.Seat.SeatId == flight.SeatId);
        var source = flight.Source switch
        {
            CardFlightSource.TrainPile => BlindTrainDrawButton,
            CardFlightSource.DestinationPile => DestinationDrawButton,
            _ => FlightDescendants<Button>(FaceUpMarketPanel).FirstOrDefault(button =>
                button.CommandParameter is MarketSlotRow slot && slot.Slot == flight.MarketSlot),
        };
        if (owner is null || source is null || source.ActualWidth <= 0 || source.ActualHeight <= 0) return;

        // Capture both positions before the public rows refresh or the next seat becomes active.
        var from = source.TranslatePoint(new Point(source.ActualWidth / 2, source.ActualHeight / 2), TableScene);
        var to = new Point(owner.Left + 125, owner.Top + 80);
        for (var index = 0; index < Math.Clamp(flight.Count, 1, 3); index++)
            FlyCard(flight, from, to, source.ActualWidth, source.ActualHeight, index);
    }

    private void FlyCard(CardFlightEventArgs flight, Point from, Point to, double width, double height, int index)
    {
        var card = CreateFlyingCard(flight, width, height);
        card.Tag = flight;
        card.RenderTransformOrigin = new Point(.5, .5);
        var scale = new ScaleTransform(1, 1);
        var rotation = new RotateTransform();
        var movement = new TranslateTransform();
        var transforms = new TransformGroup();
        transforms.Children.Add(scale);
        transforms.Children.Add(rotation);
        transforms.Children.Add(movement);
        card.RenderTransform = transforms;
        Canvas.SetLeft(card, from.X - width / 2);
        Canvas.SetTop(card, from.Y - height / 2);
        DrawCardFlightLayer.Children.Add(card);

        var duration = new Duration(TimeSpan.FromMilliseconds(540));
        var delay = TimeSpan.FromMilliseconds(index * 45);
        var storyboard = new Storyboard { BeginTime = delay, FillBehavior = FillBehavior.Stop };
        var arc = new PathGeometry();
        var path = new PathFigure { StartPoint = new Point(0, 0) };
        path.Segments.Add(new QuadraticBezierSegment(
            new Point((to.X - from.X) * .45, (to.Y - from.Y) * .45 - 110),
            new Point(to.X - from.X, to.Y - from.Y), true));
        arc.Figures.Add(path);
        arc.Freeze();
        AddFlightAnimation(storyboard, card, FlightTransformProperty(2, TranslateTransform.XProperty),
            new DoubleAnimationUsingPath { PathGeometry = arc, Source = PathAnimationSource.X, Duration = duration });
        AddFlightAnimation(storyboard, card, FlightTransformProperty(2, TranslateTransform.YProperty),
            new DoubleAnimationUsingPath { PathGeometry = arc, Source = PathAnimationSource.Y, Duration = duration });
        AddFlightAnimation(storyboard, card, FlightTransformProperty(1, RotateTransform.AngleProperty),
            new DoubleAnimation(0, to.X < from.X ? -360 : 360, duration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        AddFlightAnimation(storyboard, card, FlightTransformProperty(0, ScaleTransform.ScaleXProperty), new DoubleAnimation(1, 36 / width, duration));
        AddFlightAnimation(storyboard, card, FlightTransformProperty(0, ScaleTransform.ScaleYProperty), new DoubleAnimation(1, 35 / height, duration));
        AddFlightAnimation(storyboard, card, new PropertyPath(OpacityProperty),
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(90)) { BeginTime = TimeSpan.FromMilliseconds(450) });
        _cardFlights[card] = storyboard;
        storyboard.Completed += (_, _) =>
        {
            storyboard.Remove(this);
            _cardFlights.Remove(card);
            DrawCardFlightLayer.Children.Remove(card);
            PulseOwnerStack(flight);
        };
        storyboard.Begin(this, isControllable: true);
    }

    private static PropertyPath FlightTransformProperty(int child, DependencyProperty property) =>
        new($"(0).(1)[{child}].(2)", RenderTransformProperty, TransformGroup.ChildrenProperty, property);

    private static void AddFlightAnimation(Storyboard storyboard, Border target,
        PropertyPath property, AnimationTimeline animation)
    {
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private Border CreateFlyingCard(CardFlightEventArgs flight, double width, double height)
    {
        var faceUp = flight.Source == CardFlightSource.FaceUpTrain;
        var destination = flight.Source == CardFlightSource.DestinationPile;
        var gold = new SolidColorBrush(Color.FromRgb(240, 215, 160));
        var card = new Border
        {
            Width = width, Height = height, CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(2), BorderBrush = gold,
            Background = faceUp ? TryFindResource($"Card.{flight.VisibleKind}") as Brush ?? Brushes.Gray
                : new SolidColorBrush(destination ? Color.FromRgb(100, 138, 167) : Color.FromRgb(155, 97, 60)),
            IsHitTestVisible = false,
        };
        var label = new TextBlock
        {
            Text = faceUp ? flight.VisibleKind?.ToString() : destination ? "D" : "T",
            FontSize = faceUp ? 11 : 27, FontWeight = FontWeights.Bold,
            Foreground = faceUp ? new SolidColorBrush(Color.FromRgb(26, 37, 49)) : gold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        card.Child = faceUp ? new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(251, 238, 213)),
            VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(0, 0, 3, 3),
            Padding = new Thickness(2), Child = label,
        } : label;
        return card;
    }

    private void PulseOwnerStack(CardFlightEventArgs flight)
    {
        var stack = FlightDescendants<Button>(TableScene).FirstOrDefault(button =>
            button.DataContext is GameTableSeat tile && tile.Seat.SeatId == flight.SeatId &&
            ReferenceEquals(button.Command, flight.Source == CardFlightSource.DestinationPile
                ? _model?.ToggleSoloDestinationsCommand : _model?.ToggleSoloTrainCardsCommand));
        if (stack is null) return;
        var scale = new ScaleTransform(1, 1);
        stack.RenderTransformOrigin = new Point(.5, .5);
        stack.RenderTransform = scale;
        var pulse = new DoubleAnimation(1, 1.15, TimeSpan.FromMilliseconds(100))
            { AutoReverse = true, FillBehavior = FillBehavior.Stop };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }

    private void ClearCardFlights()
    {
        foreach (var storyboard in _cardFlights.Values) storyboard.Remove(this);
        _cardFlights.Clear();
        DrawCardFlightLayer?.Children.Clear();
    }

    private static IEnumerable<T> FlightDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in FlightDescendants<T>(child)) yield return nested;
        }
    }
}
