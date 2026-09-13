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

    [ObservableProperty] private bool _isCornerDetectionBusy;
    [ObservableProperty] private string _cornerDetectionStatus = "ML selects the board corners when the preview starts. You can adjust the handles afterwards.";

    public bool CanDetectBoardCorners => IsRunning && !IsBusy && !IsCornerDetectionBusy && !_disposed;
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

    private async Task DetectBoardCornersCoreAsync()
    {
        IsCornerDetectionBusy = true;
        var operationRevision = ++_cornerOperationRevision;
        var cropRevision = _cropRevision;
        var cameraEpoch = Capture.Epoch;
        var preferGpu = SelectedProcessor.Value != FrameComputeMode.Cpu;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _cornerCancellation = cancellation;
        CornerDetectionStatus = "Locating the four board corners with ML…";
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
                CornerDetectionStatus = "The camera image changed or became stale. Use Detect board corners to try again.";
                return;
            }
            if (!result.Accepted)
            {
                CornerDetectionStatus = "ML could not confidently locate all four corners. " +
                    result.RejectionReason + " Keep the whole board visible, then try again or select the corners manually.";
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
                CornerDetectionStatus = "Automatic corner selection unavailable: " + ex.Message +
                    " Use Select four board corners, or try detection again.";
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
