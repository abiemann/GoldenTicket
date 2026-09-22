using System.Diagnostics;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GoldenTicket.CompanionHost;
using GoldenTicket.Desktop.Services;
using GoldenTicket.Domain;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private CompanionBoardMap? _companionBoardMap;
    private CompanionBoardImage? _companionBoardImage;
    private CompanionBoardImage? _previousCompanionBoardImage;
    private IReadOnlyList<CompanionMapCity> _companionMapCities = [];
    private BitmapSource? _lastCompanionMapSource;
    private (SessionId SessionId, SeatId SeatId)? _companionMapContext;
    private bool _companionMapEncoding;
    private long _companionMapGeneration;
    private long _lastCompanionMapEncodeAt;

    private (SessionId SessionId, SeatId SeatId)? CompanionMapContext()
    {
        if (!CanConnectPhone || !Connection.UseQuickPlay || _toolsDisposed || _exitRequested ||
            IsGameInputPaused || !_systemAvailable || _mustReload || NeedsBoardReconciliation ||
            IsCheckingResumedGame || !IsGameplayScreenActive(Screen.Table) ||
            _coordinator is not { StorageFaulted: false } coordinator)
            return null;
        var view = coordinator.Public;
        if (view.IsGameplaySuspended || view.TurnPhase == TurnPhase.RulesDecisionRequired) return null;
        // Completing a claim can advance the digital active seat before the computer's
        // public acknowledgment and scoring-marker instruction have finished.
        var scoring = _scoreMarkerStep is { } step && step.SessionId == coordinator.SessionId &&
            CurrentCompanionGuidance() is not null;
        var completing = _claimCompletionInProgress && _companionMapContext is { } previous &&
            previous.SessionId == coordinator.SessionId;
        if (view.Lifecycle != SessionLifecycle.Active &&
            !(view.Lifecycle == SessionLifecycle.Finished &&
                (scoring || completing || IsCheckingBoardBeforeNextTurn))) return null;
        var seat = scoring ? _scoreMarkerStep!.SeatId : completing ? _companionMapContext!.Value.SeatId : view.ActiveSeatId;
        return view.SeatOf(seat).Kind == SeatKind.Computer || CanCompanionControl || IsCheckingBoardBeforeNextTurn
            ? (coordinator.SessionId, seat) : null;
    }

    private bool HasCompanionMapFrame => Camera.IsGameTablePreviewRequested && Camera.IsRunning &&
        Camera.IsGameTablePreviewUpright && Camera.GameTablePreview is { IsFrozen: true };

    private CompanionBoardMap? CurrentCompanionBoardMap()
    {
        RefreshCompanionMap();
        return _companionBoardMap;
    }

    private CompanionBoardImage? ReadCompanionBoardImage(string id)
    {
        RefreshCompanionMap();
        if (_companionBoardMap is null || !HasCompanionMapFrame) return null;
        return _companionBoardImage?.Id == id ? _companionBoardImage :
            _previousCompanionBoardImage?.Id == id ? _previousCompanionBoardImage : null;
    }

    private void RefreshCompanionMap()
    {
        var context = CompanionMapContext();
        if (context is null)
        {
            ResetCompanionMap();
            return;
        }
        if (_companionMapContext != context)
        {
            ResetCompanionMap();
            _companionMapContext = context;
        }
        if (!HasCompanionMapFrame)
        {
            if (_companionBoardImage is not null || _lastCompanionMapSource is not null)
            {
                ResetCompanionMap();
                _companionMapContext = context;
            }
            SetCompanionMap(null, []);
            return;
        }

        // These are the very same targets shown on the laptop, including correction
        // subsets. Destination cities below cover the whole board; the browser alone
        // combines them with the revealed player's private tickets.
        var targets = Game.PlacementTargets.Select(point => new CompanionMapPoint(point.X, point.Y, point.Number)).ToArray();
        SetCompanionMap(_companionBoardImage?.Id, targets);
        var source = Camera.GameTablePreview!;
        if (_companionMapEncoding || ReferenceEquals(source, _lastCompanionMapSource) ||
            _lastCompanionMapEncodeAt != 0 && Stopwatch.GetElapsedTime(_lastCompanionMapEncodeAt) < TimeSpan.FromSeconds(1))
            return;
        _companionMapEncoding = true;
        _lastCompanionMapSource = source;
        _lastCompanionMapEncodeAt = Stopwatch.GetTimestamp();
        _ = PublishCompanionMapAsync(source, _companionMapGeneration);
    }

    private void SetCompanionMap(string? imageId, IReadOnlyList<CompanionMapPoint> targets)
    {
        if (_companionBoardMap is { } map && map.ImageId == imageId && map.Targets.SequenceEqual(targets) &&
            map.Cities is not null && map.Cities.SequenceEqual(_companionMapCities)) return;
        _companionBoardMap = new(imageId, targets, _companionMapCities);
    }

    private void ResetCompanionMap()
    {
        if (_companionMapContext is null && _companionBoardMap is null && _lastCompanionMapSource is null) return;
        _companionMapGeneration++;
        _companionMapContext = null;
        _companionBoardMap = null;
        _companionBoardImage = null;
        _previousCompanionBoardImage = null;
        _companionMapCities = [];
        _lastCompanionMapSource = null;
        _lastCompanionMapEncodeAt = 0;
    }

    private async Task PublishCompanionMapAsync(BitmapSource source, long generation)
    {
        CompanionBoardImageEncoder.Preview? preview = null;
        try { preview = await Task.Run(() => CompanionBoardImageEncoder.Encode(source, _manifest)).ConfigureAwait(false); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            NotSupportedException or System.IO.IOException or System.Runtime.InteropServices.COMException)
        {
            // A failed preview must not interrupt gameplay. A subsequent camera frame
            // can retry; the browser keeps its current image or its waiting message.
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is { HasShutdownStarted: true } or { HasShutdownFinished: true }) return;
        void Publish()
        {
            _companionMapEncoding = false;
            if (_toolsDisposed || generation != _companionMapGeneration ||
                CompanionMapContext() != _companionMapContext || !HasCompanionMapFrame) return;
            if (preview is not null)
            {
                // Keep one previous frame so an in-flight authorized request survives
                // the next publish, with a fixed two-frame memory bound and no history.
                _previousCompanionBoardImage = _companionBoardImage;
                _companionBoardImage = new(Guid.NewGuid().ToString("N"), preview.Jpeg);
                _companionMapCities = preview.Cities;
            }
            NotifyCompanionPresentationChanged();
        }
        try
        {
            if (dispatcher is not null && !dispatcher.CheckAccess())
                await dispatcher.InvokeAsync(Publish, DispatcherPriority.Background);
            else Publish();
        }
        catch (OperationCanceledException) when (_toolsDisposed || dispatcher?.HasShutdownStarted == true) { }
        catch (InvalidOperationException) when (_toolsDisposed || dispatcher?.HasShutdownStarted == true) { }
    }
}
