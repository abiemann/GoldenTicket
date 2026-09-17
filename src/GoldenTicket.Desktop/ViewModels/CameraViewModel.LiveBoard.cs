using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class CameraViewModel
{
    private BoardOrientationReference? _gameTableReference;
    private BoardOrientationReference? _acceptedSetupReference;
    private BitmapSource? _gameTableReferencePhoto;
    private bool _liveBoardCheckBusy;
    private Task _liveBoardCheckWork = Task.CompletedTask;
    private DateTimeOffset _lastLiveBoardCheckAt = DateTimeOffset.MinValue;

    /// <summary>Use an upright checkpoint photo as the orientation anchor when resuming a game.</summary>
    public void SetGameTableReference(BitmapSource? uprightPhoto)
    {
        if (uprightPhoto is null || _disposed) return;
        if (ReferenceEquals(_gameTableReferencePhoto, uprightPhoto)) return;
        var resized = new TransformedBitmap(uprightPhoto,
            new ScaleTransform((double)BoardOrientationReference.Width / uprightPhoto.PixelWidth,
                (double)BoardOrientationReference.Height / uprightPhoto.PixelHeight));
        var bgra = new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0);
        if (bgra.PixelWidth != BoardOrientationReference.Width || bgra.PixelHeight != BoardOrientationReference.Height)
            throw new InvalidDataException("The saved board photo could not be prepared for orientation checking.");
        var pixels = new byte[BoardOrientationReference.Width * BoardOrientationReference.Height * 4];
        bgra.CopyPixels(pixels, BoardOrientationReference.Width * 4, 0);
        _gameTableReference = new BoardOrientationReference(CameraFrame.CopyFromBgra32(
            BoardOrientationReference.Width, BoardOrientationReference.Height, pixels));
        _gameTableReferencePhoto = uprightPhoto;
        _lastLiveBoardCheckAt = DateTimeOffset.MinValue;
        if (_gameTablePreviewRequested && Capture.LatestFrame is { } frame)
        {
            if (_registration is { } manual && manual.Matches(frame)) AdoptTechnicalBoardCrop(frame, manual);
            QueueLiveBoardCheck(frame);
        }
    }

    private void SetAcceptedSetupReference(CameraFrame frame, BoardRegistration uprightRegistration)
    {
        _acceptedSetupReference = new BoardOrientationReference(uprightRegistration.Rectify(frame,
            BoardOrientationReference.Width, BoardOrientationReference.Height));
    }

    /// <summary>Fail closed before the next public preview or piece analysis can use a rotated crop.</summary>
    private void CheckLiveBoardAlignment(CameraFrame frame)
    {
        if (!_gameTablePreviewRequested) return;
        if (_gameTableReference is null)
        {
            HoldLiveBoardAlignment("Board orientation is unverified. Recheck the board with the camera setup or load a saved board photo.");
            QueueLiveBoardCheck(frame);
            return;
        }
        if (_gameTableRegistration is { } registration && IsGameTablePreviewUpright)
        {
            if (!registration.Matches(frame))
            {
                HoldLiveBoardAlignment("Camera format changed. Checking the board corners again…");
            }
            else
            {
                try
                {
                    var crop = registration.Rectify(frame, BoardOrientationReference.Width,
                        BoardOrientationReference.Height);
                    if (!_gameTableReference.IsAligned(crop))
                        HoldLiveBoardAlignment("The board moved or rotated. Checking its corners and orientation again…");
                }
                catch (ArgumentException)
                {
                    HoldLiveBoardAlignment("The board crop changed. Checking its corners again…");
                }
            }
        }
        QueueLiveBoardCheck(frame);
    }

    private void HoldLiveBoardAlignment(string status)
    {
        if (IsGameTablePreviewUpright || GameTablePreviewStatus != status)
            BoardInteractionLog.Write("camera.board.alignment-held", new { status });
        if (IsGameTablePreviewUpright) IsGameTablePreviewUpright = false;
        ClearGameTableAnalysis();
        GameTablePreview = null;
        GameTablePreviewStatus = status;
        OnPropertyChanged(nameof(CanCaptureGameTablePhoto));
    }

    private void QueueLiveBoardCheck(CameraFrame frame)
    {
        if (!_gameTablePreviewRequested || _gameTableReference is null || _liveBoardCheckBusy ||
            IsCornerDetectionBusy || !IsRunning || !Capture.IsRunning || frame.Age > TimeSpan.FromSeconds(2)) return;
        var interval = IsGameTablePreviewUpright ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(800);
        if (DateTimeOffset.UtcNow - _lastLiveBoardCheckAt < interval) return;
        _lastLiveBoardCheckAt = DateTimeOffset.UtcNow;
        _liveBoardCheckWork = DetectLiveBoardAsync(frame);
    }

    private async Task DetectLiveBoardAsync(CameraFrame source)
    {
        _liveBoardCheckBusy = true;
        OnPropertyChanged(nameof(CanDetectBoardCorners));
        var reference = _gameTableReference;
        var epoch = source.Epoch;
        var preferGpu = SelectedProcessor.Value != FrameComputeMode.Cpu;
        try
        {
            if (reference is null) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var token = linked.Token;
            var detected = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (_boardCornerModel is null || _cornerPreferGpu != preferGpu)
                {
                    _boardCornerModel?.Dispose();
                    _boardCornerModel = _boardCornerModelFactory(_boardCornerModelDirectory, preferGpu);
                    _cornerPreferGpu = preferGpu;
                }
                return _boardCornerModel.Detect(source, token);
            }, token);
            if (!_gameTablePreviewRequested || !ReferenceEquals(reference, _gameTableReference) ||
                !Capture.IsRunning || Capture.Epoch != epoch) return;
            if (!detected.Accepted)
            {
                if (!IsGameTablePreviewUpright)
                    BoardInteractionLog.Write("camera.board.corners-rejected", new
                    {
                        source.Sequence, source.Epoch,
                        detected.Confidences,
                        detected.RejectionReason
                    });
                if (!IsGameTablePreviewUpright)
                    GameTablePreviewStatus = "Waiting for all four outer board corners to be visible.";
                return;
            }
            var padded = BoardCropPadding.Expand(source, detected.Corners);
            var registrations = GameBoardOrientations.Enumerate(padded.Corners)
                .Select(corners => BoardRegistration.Create(source, corners)).ToArray();
            var crops = await Task.Run(() => registrations.Select(registration =>
                registration.Rectify(source, BoardOrientationReference.Width,
                    BoardOrientationReference.Height)).ToArray(), token);
            var orientation = reference.ChooseOrientation(crops);
            if (!_gameTablePreviewRequested || !ReferenceEquals(reference, _gameTableReference) ||
                Capture.LatestFrame is not { } current || current.Age > TimeSpan.FromSeconds(2) ||
                current.Epoch != epoch || current.Width != source.Width || current.Height != source.Height) return;
            if (orientation is null)
            {
                if (!IsGameTablePreviewUpright)
                    GameTablePreviewStatus = "Board orientation is unclear. Keep Miami in view and clear the board edges.";
                return;
            }
            var selected = registrations[orientation.Value];
            if (!reference.IsAligned(selected.Rectify(current, BoardOrientationReference.Width,
                    BoardOrientationReference.Height)))
            {
                if (!IsGameTablePreviewUpright)
                    GameTablePreviewStatus = "Hold the board steady while its orientation is checked.";
                return;
            }
            if (_gameTableRegistration is { } previous && IsGameTablePreviewUpright &&
                previous.Matches(current) && SameCorners(previous.Corners, selected.Corners)) return;
            ClearGameTableAnalysis();
            _gameTableRegistration = selected;
            _gameTableCropRevision++;
            BoardInteractionLog.Write("camera.board.aligned", new
            {
                source.Sequence, source.Epoch,
                cropRevision = _gameTableCropRevision,
                orientation = orientation.Value,
                corners = selected.Corners.Select(point => new
                {
                    x = Math.Round(point.X, 5), y = Math.Round(point.Y, 5)
                }).ToArray()
            });
            _lastGameTableCropAt = DateTimeOffset.MinValue;
            GameTablePreview = null;
            IsGameTablePreviewUpright = true;
            GameTablePreviewStatus = "Updating the upright live board view…";
            QueueGameTablePreview(current);
            QueueGameTableAnalysis(current);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_gameTablePreviewRequested && !IsGameTablePreviewUpright)
                GameTablePreviewStatus = "Board orientation check unavailable: " + ex.Message;
        }
        finally
        {
            _liveBoardCheckBusy = false;
            OnPropertyChanged(nameof(CanDetectBoardCorners));
        }
    }

    private static bool SameCorners(IReadOnlyList<NormalizedPoint> left, IReadOnlyList<NormalizedPoint> right) =>
        left.Count == 4 && right.Count == 4 && Enumerable.Range(0, 4).All(index =>
            Math.Abs(left[index].X - right[index].X) < .002 &&
            Math.Abs(left[index].Y - right[index].Y) < .002);
}
