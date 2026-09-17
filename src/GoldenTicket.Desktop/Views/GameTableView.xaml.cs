using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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

    private MainViewModel? _model;
    private bool _updateQueued;

    public GameTableView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachModel(DataContext as MainViewModel);
        PositionDrawPanels(animate: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => AttachModel(null);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachModel(e.NewValue as MainViewModel);
        PositionDrawPanels(animate: false);
    }

    private void AttachModel(MainViewModel? model)
    {
        if (ReferenceEquals(_model, model)) return;
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
            _model.Table.Seats.CollectionChanged -= OnSeatsChanged;
        }
        _model = model;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelPropertyChanged;
            _model.Table.Seats.CollectionChanged += OnSeatsChanged;
        }
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowSoloOpeningTicketsOnBoard))
            QueueDrawPanelPosition();
    }

    private void OnSeatsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueDrawPanelPosition();

    private void QueueDrawPanelPosition()
    {
        if (_updateQueued) return;
        _updateQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _updateQueued = false;
            if (_model is not null) PositionDrawPanels(animate: true);
        }, DispatcherPriority.Loaded);
    }

    private void PositionDrawPanels(bool animate)
    {
        // These coordinates leave an 18-pixel gap and center both panels as one group in
        // the 1440-pixel scene. The outer positions reserve the bottom seat at five players.
        var center = _model is { ShowSoloOpeningTicketsOnBoard: false } &&
                     _model.Table.Seats.Count is > 0 and < 5;
        MovePanel(DrawPilesPanel, center ? CenteredDrawPilesLeft : OuterDrawPilesLeft, animate);
        MovePanel(FaceUpMarketPanel, center ? CenteredFaceUpMarketLeft : OuterFaceUpMarketLeft, animate);
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
