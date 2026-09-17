using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class CameraViewModel
{
    private readonly string _boardCornerModelDirectory;
    private readonly Func<string, bool, IBoardCornerDetector> _boardCornerModelFactory;
    private IBoardCornerDetector? _boardCornerModel;
    private bool? _cornerPreferGpu;
    private Task _cornerWork = Task.CompletedTask;
    private CancellationTokenSource? _cornerCancellation;
    private (long Epoch, int Width, int Height)? _autoCornerCapture;
    private long _cornerOperationRevision;
    private bool _gameBoardFramingActive;
    private long _gameBoardFramingRevision;
    private DateTimeOffset _lastGameBoardCheckAt = DateTimeOffset.MinValue;
    private DateTimeOffset _gameBoardAcceptedAt = DateTimeOffset.MinValue;
    private (long Epoch, int Width, int Height)? _gameBoardCapture;
    private IReadOnlyList<NormalizedPoint> _gameBoardCorners = [];
    private (long Epoch, int Width, int Height, long Sequence, DateTimeOffset CapturedAt)? _firstGameBoardMiss;
    private MarkerColor[] _gameSetupColors = [];
    private DateTimeOffset _gameMarkersAcceptedAt = DateTimeOffset.MinValue;
    private (long Epoch, int Width, int Height)? _gameMarkersCapture;
    private bool _gameMarkersReady;
    private int? _gameBoardOrientationIndex;

    private static readonly TimeSpan GameBoardCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GameBoardResultLifetime = TimeSpan.FromSeconds(2.5);
    private const double GameBoardEdgeMargin = .005;

    [ObservableProperty] private bool _isCornerDetectionBusy;
    [ObservableProperty] private string _cornerDetectionStatus = "ML selects the board corners when the preview starts. You can adjust the handles afterwards.";
    [ObservableProperty] private string _gameBoardFramingStatus = "Waiting for the camera to find all four board corners.";
    public bool ShowGameBoardNotice => IsRunning && !string.IsNullOrWhiteSpace(GameBoardFramingStatus);
    partial void OnGameBoardFramingStatusChanged(string value) => OnPropertyChanged(nameof(ShowGameBoardNotice));

    /// <summary>Current ML predictions in full-camera coordinates, never manual crop handles.</summary>
    public IReadOnlyList<NormalizedPoint> GameBoardCorners
    {
        get => _gameBoardCorners;
        private set
        {
            if (SetProperty(ref _gameBoardCorners, value))
            {
                OnPropertyChanged(nameof(HasFreshGameBoardCorners));
                OnPropertyChanged(nameof(CanStartGameWithBoard));
            }
        }
    }

    public bool HasFreshGameBoardCorners => _gameBoardFramingActive && GameBoardCorners.Count == 4 &&
        IsRunning && Capture.IsRunning && _gameBoardCapture is { } capture &&
        Capture.LatestFrame is { } frame && frame.Age <= TimeSpan.FromSeconds(2) &&
        (frame.Epoch, frame.Width, frame.Height) == capture &&
        DateTimeOffset.UtcNow - _gameBoardAcceptedAt <= GameBoardResultLifetime;

    public bool CanStartGameWithBoard => HasFreshGameBoardCorners && _gameMarkersReady &&
        _gameMarkersCapture == _gameBoardCapture &&
        DateTimeOffset.UtcNow - _gameMarkersAcceptedAt <= GameBoardResultLifetime;

    public void BeginGameBoardFraming(IReadOnlyCollection<MarkerColor>? selectedColors = null)
    {
        EndGameTablePreview();
        _gameBoardFramingActive = true;
        _gameBoardFramingRevision++;
        _gameSetupColors = selectedColors?.Distinct().ToArray() ?? [];
        _lastGameBoardCheckAt = DateTimeOffset.MinValue;
        _firstGameBoardMiss = null;
        ClearGameBoardFraming();
        GameBoardFramingStatus = "Checking the live image for all four board corners…";
    }

    public void EndGameBoardFraming()
    {
        _gameBoardFramingActive = false;
        _gameBoardFramingRevision++;
        _firstGameBoardMiss = null;
        _gameSetupColors = [];
        ClearGameBoardFraming();
    }

    private void ClearGameMarkerCheck()
    {
        _gameMarkersReady = false;
        _gameBoardOrientationIndex = null;
        _acceptedSetupReference = null;
        _gameMarkersCapture = null;
        _gameMarkersAcceptedAt = DateTimeOffset.MinValue;
        OnPropertyChanged(nameof(CanStartGameWithBoard));
    }

    private void ClearGameBoardFraming()
    {
        _gameBoardCapture = null;
        _gameBoardAcceptedAt = DateTimeOffset.MinValue;
        ClearGameMarkerCheck();
        if (GameBoardCorners.Count != 0) GameBoardCorners = [];
        else
        {
            OnPropertyChanged(nameof(HasFreshGameBoardCorners));
            OnPropertyChanged(nameof(CanStartGameWithBoard));
        }
    }

    private bool ConfirmGameBoardMiss(CameraFrame frame)
    {
        var first = _firstGameBoardMiss;
        if (first is null || (first.Value.Epoch, first.Value.Width, first.Value.Height) !=
            (frame.Epoch, frame.Width, frame.Height) || frame.CapturedAt < first.Value.CapturedAt)
        {
            _firstGameBoardMiss = (frame.Epoch, frame.Width, frame.Height, frame.Sequence, frame.CapturedAt);
            return false;
        }
        if (frame.Sequence == first.Value.Sequence ||
            frame.CapturedAt - first.Value.CapturedAt < GameBoardCheckInterval) return false;
        _firstGameBoardMiss = null;
        return true;
    }

    private void ExpireGameBoardFraming(CameraFrame? frame)
    {
        if (!_gameBoardFramingActive || _gameBoardCapture is not { } capture) return;
        if (frame is null ||
            (frame.Epoch, frame.Width, frame.Height) != capture ||
            frame.Age > TimeSpan.FromSeconds(2) ||
            DateTimeOffset.UtcNow - _gameBoardAcceptedAt > GameBoardResultLifetime)
        {
            ClearGameBoardFraming();
            GameBoardFramingStatus = "The live board view changed. Checking all four corners again…";
        }
        else
        {
            OnPropertyChanged(nameof(HasFreshGameBoardCorners));
            OnPropertyChanged(nameof(CanStartGameWithBoard));
        }
    }

    private void QueueGameBoardFraming(CameraFrame frame)
    {
        if (!_gameBoardFramingActive || !CanDetectBoardCorners ||
            DateTimeOffset.UtcNow - _lastGameBoardCheckAt < GameBoardCheckInterval) return;
        _lastGameBoardCheckAt = DateTimeOffset.UtcNow;
        _cornerWork = DetectBoardCornersCoreAsync(forGameBoard: true);
    }

    public bool CanDetectBoardCorners => IsRunning && !IsBusy && !IsCornerDetectionBusy && !_liveBoardCheckBusy && !_disposed;
    partial void OnIsCornerDetectionBusyChanged(bool value) => OnPropertyChanged(nameof(CanDetectBoardCorners));

    private void QueueAutomaticCornerDetection(CameraFrame frame)
    {
        var capture = (frame.Epoch, frame.Width, frame.Height);
        if (!CanDetectBoardCorners || HasBoardCrop || SelectingCorners || SelectedCorners.Count != 0 ||
            _autoCornerCapture == capture) return;
        // Attempt once per camera/format, even when the model is missing or rejects the view.
        // Manual selection and the retry button remain available; no repeated background churn.
        _autoCornerCapture = capture;
        _cornerWork = DetectBoardCornersCoreAsync();
    }

    [RelayCommand]
    private async Task DetectBoardCornersAsync()
    {
        if (!CanDetectBoardCorners) return;
        if (Capture.LatestFrame is { } frame) _autoCornerCapture = (frame.Epoch, frame.Width, frame.Height);
        _cornerWork = DetectBoardCornersCoreAsync();
        await _cornerWork;
    }

    private async Task DetectBoardCornersCoreAsync(bool forGameBoard = false)
    {
        IsCornerDetectionBusy = true;
        var operationRevision = ++_cornerOperationRevision;
        var gameBoardRevision = _gameBoardFramingRevision;
        var cropRevision = _cropRevision;
        var cameraEpoch = Capture.Epoch;
        var preferGpu = SelectedProcessor.Value != FrameComputeMode.Cpu;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _cornerCancellation = cancellation;
        if (!forGameBoard) CornerDetectionStatus = "Locating the four board corners with ML…";
        try
        {
            // Model initialization is separate from the preview worker and never holds the UI thread.
            // Take the source frame after loading, so loading time cannot make the input stale.
            await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (_boardCornerModel is not null && _cornerPreferGpu == preferGpu) return;
                _boardCornerModel?.Dispose();
                _boardCornerModel = null;
                _boardCornerModel = _boardCornerModelFactory(_boardCornerModelDirectory, preferGpu);
                _cornerPreferGpu = preferGpu;
            }, cancellation.Token);
            if (!CornerOperationIsCurrent(operationRevision, cropRevision, cameraEpoch)) return;
            var frame = Capture.GetFreshFrame(TimeSpan.FromSeconds(1));
            var result = await Task.Run(() => _boardCornerModel!.Detect(frame, cancellation.Token), cancellation.Token);
            if (!CornerOperationIsCurrent(operationRevision, cropRevision, cameraEpoch)) return;
            var current = Capture.GetFreshFrame(TimeSpan.FromSeconds(1));
            if (frame.Width != current.Width || frame.Height != current.Height || frame.Epoch != current.Epoch ||
                frame.Age > TimeSpan.FromSeconds(2))
            {
                if (forGameBoard && _gameBoardFramingActive && gameBoardRevision == _gameBoardFramingRevision)
                {
                    _firstGameBoardMiss = null;
                    ClearGameBoardFraming();
                    GameBoardFramingStatus = "The camera image changed. Hold it steady while the board is checked again.";
                }
                else if (!forGameBoard)
                    CornerDetectionStatus = "The camera image changed or became stale. Use Detect board corners to try again.";
                return;
            }
            if (!result.Accepted)
            {
                if (forGameBoard && _gameBoardFramingActive && gameBoardRevision == _gameBoardFramingRevision)
                {
                    if (ConfirmGameBoardMiss(current))
                    {
                        ClearGameBoardFraming();
                        GameBoardFramingStatus = "Move the camera until all four board corners are visible.";
                    }
                }
                else if (!forGameBoard)
                    CornerDetectionStatus = "ML could not confidently locate all four corners. " +
                        result.RejectionReason + " Keep the whole board visible, then try again or select the corners manually.";
                return;
            }
            if (forGameBoard)
            {
                if (!_gameBoardFramingActive || gameBoardRevision != _gameBoardFramingRevision) return;
                _ = BoardRegistration.Create(current, result.Corners);
                if (result.Corners.Any(point => point.X <= GameBoardEdgeMargin ||
                    point.X >= 1 - GameBoardEdgeMargin || point.Y <= GameBoardEdgeMargin ||
                    point.Y >= 1 - GameBoardEdgeMargin))
                {
                    if (ConfirmGameBoardMiss(current))
                    {
                        ClearGameBoardFraming();
                        GameBoardFramingStatus = "Move the camera back so every board corner has room inside the image.";
                    }
                    return;
                }
                _firstGameBoardMiss = null;
                _gameBoardCapture = (current.Epoch, current.Width, current.Height);
                _gameBoardAcceptedAt = DateTimeOffset.UtcNow;
                GameBoardCorners = result.Corners.ToArray();
                if (GameBoardFramingStatus.StartsWith("Checking the live image", StringComparison.Ordinal) ||
                    GameBoardFramingStatus.StartsWith("The live board view changed", StringComparison.Ordinal))
                    GameBoardFramingStatus = "Checking for trains and scoring markers…";
                await CheckGameSetupMarkersAsync(current, gameBoardRevision, operationRevision,
                    cropRevision, cameraEpoch, cancellation.Token);
                return;
            }
            // Validate and render before replacing any existing crop. Failure leaves it untouched.
            var padding = BoardCropPadding.Expand(current, result.Corners);
            var corners = padding.Corners.ToArray();
            var registration = BoardRegistration.Create(current, corners);
            var preview = ToBitmap(registration.Rectify(current, 480, 300));
            InvalidateCrop();
            _cornerCapture = (current.Epoch, current.Width, current.Height);
            SelectingCorners = false;
            SelectedCorners.Clear();
            foreach (var point in corners) SelectedCorners.Add(point);
            _registration = registration;
            BoardPreview = preview;
            HasBoardCrop = true;
            AdoptTechnicalBoardCrop(current, registration);
            _previewSequence = -1;
            Problem = null;
            CropText = "ML selected the board with a small outer margin to protect score pieces. Zoom in and drag any numbered handle to adjust the crop." +
                (padding.LimitedByFrame ? " Some padding reaches the camera edge; widen the camera view for more room." : "");
            CornerDetectionStatus = $"Corners selected · {result.Backend} · {result.Elapsed.TotalMilliseconds:0} ms. " +
                (_boardCornerModel!.FallbackReason is { Length: > 0 } reason ? "CPU fallback: " + reason : "");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (CornerOperationIsCurrent(operationRevision, cropRevision, cameraEpoch))
            {
                if (forGameBoard && _gameBoardFramingActive && gameBoardRevision == _gameBoardFramingRevision)
                {
                    _firstGameBoardMiss = null;
                    ClearGameBoardFraming();
                    GameBoardFramingStatus = "Board corner check unavailable: " + ex.Message;
                }
                else if (!forGameBoard)
                    CornerDetectionStatus = "Automatic corner selection unavailable: " + ex.Message +
                        " Use Select four board corners, or try detection again.";
            }
        }
        finally
        {
            if (!_disposed && _cornerOperationRevision == operationRevision &&
                CornerDetectionStatus == "Locating the four board corners with ML…")
                CornerDetectionStatus = "The camera or crop changed. Use Detect board corners to try again.";
            if (ReferenceEquals(_cornerCancellation, cancellation)) _cornerCancellation = null;
            IsCornerDetectionBusy = false;
        }
    }

    private async Task CheckGameSetupMarkersAsync(CameraFrame frame, long gameBoardRevision,
        long operationRevision, long cropRevision, long cameraEpoch, CancellationToken token)
    {
        try
        {
            await EnsurePieceModelAsync();
            if (!GameMarkerOperationIsCurrent(gameBoardRevision, operationRevision, cropRevision, cameraEpoch)) return;
            if (_pieceModel is null)
            {
                ClearGameMarkerCheck();
                GameBoardFramingStatus = "Piece detection unavailable. " + ModelStatus;
                return;
            }
            var check = await Task.Run(() =>
            {
                var observations = new List<GameSetupBoardObservation>(4);
                foreach (var corners in GameBoardOrientations.Enumerate(GameBoardCorners))
                {
                    token.ThrowIfCancellationRequested();
                    var board = BoardRegistration.Create(frame, corners)
                        .Rectify(frame, LearnedPieceDetector.BoardWidth, LearnedPieceDetector.BoardHeight);
                    LearnedPieceDetection detection;
                    lock (_modelGate) detection = _pieceModel!.Detect(board, token);
                    observations.Add(new(detection.Candidates,
                        ScoreMarkerReader.Read(board, detection.Candidates)));
                }
                return GameSetupBoardValidator.Check(_gameSetupColors, observations);
            }, token);
            if (!GameMarkerOperationIsCurrent(gameBoardRevision, operationRevision, cropRevision, cameraEpoch)) return;
            var latest = Capture.LatestFrame;
            if (latest is null || latest.Age > TimeSpan.FromSeconds(2) ||
                (latest.Epoch, latest.Width, latest.Height) != (frame.Epoch, frame.Width, frame.Height) ||
                frame.Age > TimeSpan.FromSeconds(2))
            {
                ClearGameMarkerCheck();
                GameBoardFramingStatus = "The camera image changed. Checking the board again…";
                return;
            }
            _gameMarkersReady = check.Ready;
            _gameBoardOrientationIndex = check.Ready ? check.OrientationIndex : null;
            if (check.Ready && check.OrientationIndex is { } orientation)
            {
                var padded = BoardCropPadding.Expand(frame, GameBoardCorners);
                var upright = BoardRegistration.Create(frame,
                    GameBoardOrientations.Enumerate(padded.Corners)[orientation]);
                SetAcceptedSetupReference(frame, upright);
            }
            else _acceptedSetupReference = null;
            _gameMarkersCapture = (frame.Epoch, frame.Width, frame.Height);
            _gameMarkersAcceptedAt = DateTimeOffset.UtcNow;
            GameBoardFramingStatus = check.Message;
            OnPropertyChanged(nameof(CanStartGameWithBoard));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!GameMarkerOperationIsCurrent(gameBoardRevision, operationRevision, cropRevision, cameraEpoch)) return;
            ClearGameMarkerCheck();
            GameBoardFramingStatus = "Board piece check unavailable: " + ex.Message;
        }
    }

    private bool GameMarkerOperationIsCurrent(long gameBoardRevision, long operationRevision,
        long cropRevision, long cameraEpoch) => _gameBoardFramingActive &&
        gameBoardRevision == _gameBoardFramingRevision &&
        CornerOperationIsCurrent(operationRevision, cropRevision, cameraEpoch);

    private bool CornerOperationIsCurrent(long operationRevision, long cropRevision, long cameraEpoch) =>
        !_disposed && !IsBusy && Capture.IsRunning && Capture.Epoch == cameraEpoch &&
        _cropRevision == cropRevision && _cornerOperationRevision == operationRevision;

    private void CancelCornerDetection()
    {
        _cornerOperationRevision++;
        if (IsCornerDetectionBusy && !_disposed)
            CornerDetectionStatus = "Corner detection cancelled. Adjust the handles or use Detect board corners to try again.";
        _cornerCancellation?.Cancel();
    }

    private async Task DisposeCornerDetectionAsync()
    {
        CancelCornerDetection();
        await _cornerWork;
        _boardCornerModel?.Dispose();
        _boardCornerModel = null;
    }
}
