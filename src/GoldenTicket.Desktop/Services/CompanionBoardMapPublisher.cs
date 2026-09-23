using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GoldenTicket.CompanionHost;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Manifest;

namespace GoldenTicket.Desktop.Services;

internal readonly record struct CompanionBoardMapContext(SessionId SessionId, SeatId SeatId);

/// <summary>
/// Publishes the accepted board crop for one session and seat. All inputs arrive as a
/// snapshot from the UI; encoding never calls back to read mutable game state.
/// </summary>
internal sealed class CompanionBoardMapPublisher
{
    private readonly Func<BitmapSource, Task<CompanionBoardImageEncoder.Preview>> _encode;
    private readonly Func<Dispatcher?> _dispatcherProvider;
    private readonly TimeSpan _minimumEncodeInterval;
    private CompanionBoardImage? _previousImage;
    private IReadOnlyList<CompanionMapCity> _cities = [];
    private BitmapSource? _lastSource;
    private bool _encoding;
    private long _generation;
    private long _lastEncodeAt;

    internal CompanionBoardMapPublisher(BoardManifest manifest,
        Func<BitmapSource, Task<CompanionBoardImageEncoder.Preview>>? encode = null,
        TimeSpan? minimumEncodeInterval = null, Func<Dispatcher?>? dispatcherProvider = null)
    {
        _encode = encode ?? (source => Task.Run(() => CompanionBoardImageEncoder.Encode(source, manifest)));
        _minimumEncodeInterval = minimumEncodeInterval ?? TimeSpan.FromSeconds(1);
        _dispatcherProvider = dispatcherProvider ?? (() => System.Windows.Application.Current?.Dispatcher);
    }

    internal event EventHandler? Changed;

    internal CompanionBoardMapContext? Context { get; private set; }

    internal CompanionBoardMap? BoardMap { get; private set; }

    internal CompanionBoardImage? CurrentImage { get; private set; }

    internal CompanionBoardImage? ReadImage(string id) => BoardMap is null ? null :
        CurrentImage?.Id == id ? CurrentImage :
        _previousImage?.Id == id ? _previousImage : null;

    internal void Update(CompanionBoardMapContext? context, BitmapSource? previewFrame,
        IReadOnlyList<CompanionMapPoint> targets)
    {
        if (Context != context)
        {
            Reset();
            Context = context;
        }
        if (context is null) return;

        if (previewFrame is null)
        {
            if (CurrentImage is not null || _lastSource is not null)
            {
                Reset();
                Context = context;
            }
            SetBoardMap(null, []);
            return;
        }
        if (!previewFrame.IsFrozen)
            throw new ArgumentException("The board frame must be frozen before encoding.", nameof(previewFrame));

        // Targets may change without a new frame, such as after a placement correction.
        SetBoardMap(CurrentImage?.Id, targets);
        if (_encoding || ReferenceEquals(previewFrame, _lastSource) ||
            _lastEncodeAt != 0 && Stopwatch.GetElapsedTime(_lastEncodeAt) < _minimumEncodeInterval)
            return;
        _encoding = true;
        _lastSource = previewFrame;
        _lastEncodeAt = Stopwatch.GetTimestamp();
        _ = PublishAsync(previewFrame, _generation);
    }

    internal void Reset()
    {
        if (Context is null && BoardMap is null && CurrentImage is null && _lastSource is null) return;
        _generation++;
        Context = null;
        BoardMap = null;
        CurrentImage = null;
        _previousImage = null;
        _cities = [];
        _lastSource = null;
        _lastEncodeAt = 0;
        // Keep the in-flight encoder serialized. Its completion will request a new
        // snapshot, then the latest context can start a fresh encode.
    }

    private void SetBoardMap(string? imageId, IReadOnlyList<CompanionMapPoint> targets)
    {
        if (BoardMap is { } map && map.ImageId == imageId && map.Targets.SequenceEqual(targets) &&
            map.Cities is not null && map.Cities.SequenceEqual(_cities)) return;
        BoardMap = new(imageId, targets, _cities);
    }

    private async Task PublishAsync(BitmapSource source, long generation)
    {
        CompanionBoardImageEncoder.Preview? preview = null;
        try { preview = await _encode(source).ConfigureAwait(false); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            NotSupportedException or IOException or COMException)
        {
            // A failed preview cannot interrupt gameplay. A later camera frame retries.
        }
        var dispatcher = _dispatcherProvider();
        if (dispatcher is { HasShutdownStarted: true } or { HasShutdownFinished: true }) return;
        void Publish()
        {
            _encoding = false;
            if (generation == _generation && Context is not null && BoardMap is not null &&
                preview is not null)
            {
                // One previous frame lets an authorized in-flight image request finish.
                _previousImage = CurrentImage;
                CurrentImage = new(Guid.NewGuid().ToString("N"), preview.Jpeg);
                _cities = preview.Cities;
                SetBoardMap(CurrentImage.Id, BoardMap.Targets);
            }
            // Even a discarded encode can unblock a newer context waiting to encode.
            Changed?.Invoke(this, EventArgs.Empty);
        }
        try
        {
            if (dispatcher is not null && !dispatcher.CheckAccess())
                await dispatcher.InvokeAsync(Publish, DispatcherPriority.Background);
            else Publish();
        }
        catch (OperationCanceledException) when (dispatcher?.HasShutdownStarted == true) { }
        catch (InvalidOperationException) when (dispatcher?.HasShutdownStarted == true) { }
    }
}
