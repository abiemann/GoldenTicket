using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GoldenTicket.Desktop.ViewModels;

namespace GoldenTicket.Desktop.Views;

public partial class GameTableView : UserControl
{
    private const double CenteredDrawPilesLeft = 368;
    private const double CenteredFaceUpMarketLeft = 622;
    private const double OuterDrawPilesLeft = 14;
    private const double OuterFaceUpMarketLeft = 975;
    private const double PlayerStationHeight = 160;

    private MainViewModel? _model;
    private bool _updateQueued;

    public GameTableView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += (_, args) => { if (args.NewValue is false) ClearCardFlights(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachModel(DataContext as MainViewModel);
        PositionDrawPanels(animate: false);
        PositionSoloCardPanel();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => AttachModel(null);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachModel(e.NewValue as MainViewModel);
        PositionDrawPanels(animate: false);
        PositionSoloCardPanel();
    }

    private void AttachModel(MainViewModel? model)
    {
        if (ReferenceEquals(_model, model)) return;
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
            _model.Table.Seats.CollectionChanged -= OnSeatsChanged;
            _model.CardDrawn -= OnCardDrawn;
        }
        ClearCardFlights();
        _model = model;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelPropertyChanged;
            _model.Table.Seats.CollectionChanged += OnSeatsChanged;
            _model.CardDrawn += OnCardDrawn;
        }
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.GameplayScreen) or nameof(MainViewModel.IsGameExitMenuOpen) or
            nameof(MainViewModel.ShowMultiHumanPhoneSetup))
            ClearCardFlights();
        if (e.PropertyName == nameof(MainViewModel.ShowSoloOpeningTicketsOnBoard))
            QueueDrawPanelPosition();
        if (e.PropertyName == nameof(MainViewModel.ShowSoloCardPanel))
            PositionSoloCardPanel();
    }

    private void OnSeatsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueDrawPanelPosition();

    private void QueueDrawPanelPosition()
    {
        if (_updateQueued) return;
        _updateQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _updateQueued = false;
            if (_model is not null)
            {
                PositionDrawPanels(animate: true);
                PositionSoloCardPanel();
            }
        }, DispatcherPriority.Loaded);
    }

    private void PositionDrawPanels(bool animate)
    {
        // These coordinates leave an 18-pixel gap and center both panels as one group in
        // the 1440-pixel scene. Every player count leaves this space clear after setup.
        var center = _model is { ShowSoloOpeningTicketsOnBoard: false } &&
                     _model.Table.Seats.Count > 0;
        MovePanel(DrawPilesPanel, center ? CenteredDrawPilesLeft : OuterDrawPilesLeft, animate);
        MovePanel(FaceUpMarketPanel, center ? CenteredFaceUpMarketLeft : OuterFaceUpMarketLeft, animate);
    }

    private void PositionSoloCardPanel()
    {
        var station = _model?.Game.TableSeats.FirstOrDefault(tile =>
            tile.Seat.Operator == "human" && tile.Seat.IsActive);
        if (station is null) return;

        Canvas.SetLeft(SoloCardPanel, Math.Clamp(station.Left, 8, TableScene.Width - SoloCardPanel.Width - 8));
        var estimatedPanelHeight = _model?.ShowSoloDestinations == true ? 188 : 132;
        var column = _model!.Game.TableSeats
            .Where(tile => Math.Abs(tile.Left - station.Left) < SoloCardPanel.Width).ToArray();
        // Prefer beside the owner's station, then another free gap in the same column.
        // The third station must remain visible when the solo player opens their cards.
        var candidates = new[] { station.Top + PlayerStationHeight + 6,
            station.Top - estimatedPanelHeight - 8, 8d }
            .Concat(column.Select(tile => tile.Top + PlayerStationHeight + 6));
        var top = candidates.First(candidate => candidate >= 8 &&
            candidate + estimatedPanelHeight <= TableScene.Height - 8 &&
            column.All(tile => candidate + estimatedPanelHeight + 6 <= tile.Top ||
                candidate >= tile.Top + PlayerStationHeight + 6));
        Canvas.SetTop(SoloCardPanel, top);
    }

    private void OnSoloCardPanelVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        PositionSoloCardPanel();

        if (SoloCardPanel.RenderTransform is not System.Windows.Media.TranslateTransform transform) return;
        transform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        SoloCardPanel.BeginAnimation(OpacityProperty, null);
        if (!SystemParameters.ClientAreaAnimation)
        {
            transform.Y = 0;
            SoloCardPanel.Opacity = 1;
            return;
        }

        transform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(-32, 0, new Duration(TimeSpan.FromMilliseconds(280)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
        SoloCardPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220)))
            {
                FillBehavior = FillBehavior.Stop,
            });
    }

    private void OnHorizontalScrollWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer || viewer.ScrollableWidth <= 0) return;

        viewer.ScrollToHorizontalOffset(Math.Clamp(
            viewer.HorizontalOffset - e.Delta / Mouse.MouseWheelDeltaForOneLine * 48,
            0, viewer.ScrollableWidth));
        e.Handled = true;
    }

    private static void MovePanel(Border panel, double destination, bool animate)
    {
        var current = Canvas.GetLeft(panel);
        panel.BeginAnimation(Canvas.LeftProperty, null);
        Canvas.SetLeft(panel, destination);
        if (!animate || !SystemParameters.ClientAreaAnimation || Math.Abs(current - destination) < 0.5) return;

        panel.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(current, destination,
            new Duration(TimeSpan.FromMilliseconds(650)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });
    }
}
