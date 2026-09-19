using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class CameraViewModel
{
    private BoardOrientationReference? _gameTableReference;
    private BoardOrientationReference? _acceptedSetupReference;
    private BitmapSource? _gameTableReferencePhoto;
    private BoardPhotoAlignmentReference? _gameTablePhotoAlignment;
    private Task _gameTableAlignmentWork = Task.CompletedTask;
    private long? _gameTableAlignmentPendingRevision;
    private bool _liveBoardCheckBusy;
    private Task _liveBoardCheckWork = Task.CompletedTask;
    private DateTimeOffset _lastLiveBoardCheckAt = DateTimeOffset.MinValue;
    private DispatcherTimer? _gameTableHandoffTimer;
    private DateTimeOffset _gameTableHandoffStartedAt;
    private long _gameTableHandoffRevision = -1;
    private bool _gameTableHandoffBlackedOut;
    private long _lastLoggedGameTablePreviewRevision = -1;

    private static BoardRegistration AlignGameTableCrop(CameraFrame source, BoardRegistration initial,
        BoardPhotoAlignmentReference? savedPhoto, CancellationToken token,
        BoardRegistration? previous = null) =>
        ClassicUsBoardAlignment.Reference.TryRefine(source, initial, previous, token) ??
        savedPhoto?.Refine(source, initial, token) ?? initial;

    private void QueueGameTableAlignment(CameraFrame frame, BoardRegistration registration)
    {
        var revision = _gameTableCropRevision;
        _gameTableAlignmentPendingRevision = revision;
        _gameTableAlignmentWork = Task.WhenAll(_gameTableAlignmentWork,
            AlignInitialGameTableCropAsync(frame, registration, revision));
    }

    private async Task AlignInitialGameTableCropAsync(CameraFrame source,
        BoardRegistration initial, long revision)
    {
        var reference = _gameTableReference;
        var savedPhoto = _gameTablePhotoAlignment;
        try
        {
            var aligned = await Task.Run(() => AlignGameTableCrop(source, initial, savedPhoto,
                _lifetime.Token), _lifetime.Token);
            if (_disposed || !_gameTablePreviewRequested || !IsGameTablePreviewUpright ||
                revision != _gameTableCropRevision || !ReferenceEquals(reference, _gameTableReference) ||
                !ReferenceEquals(initial, _gameTableRegistration) || !Capture.IsRunning ||
                source.Age > TimeSpan.FromSeconds(2) || Capture.LatestFrame is not { } current ||
                current.Age > TimeSpan.FromSeconds(2) || !aligned.Matches(current)) return;
            if (reference is null || !reference.IsAligned(aligned.Rectify(current,
                BoardOrientationReference.Width, BoardOrientationReference.Height))) return;
            _gameTableAlignmentPendingRevision = null;
            if (!ReferenceEquals(initial, aligned))
            {
                ClearGameTableAnalysis();
                _gameTableRegistration = aligned;
                _gameTableCropRevision++;
                _lastGameTableCropAt = DateTimeOffset.MinValue;
                BeginGameTablePreviewHandoff();
                QueueGameTablePreview(current);
            }
            QueueGameTableAnalysis(current);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed && revision == _gameTableCropRevision)
                GameTablePreviewStatus = "Board alignment check unavailable: " + ex.Message;
        }
        finally
        {
            if (!_disposed && _gameTableAlignmentPendingRevision == revision)
                _lastLiveBoardCheckAt = DateTimeOffset.MinValue;
        }
    }

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
        var alignmentImage = new FormatConvertedBitmap(new TransformedBitmap(uprightPhoto,
            new ScaleTransform(320d / uprightPhoto.PixelWidth, 200d / uprightPhoto.PixelHeight)),
            PixelFormats.Bgra32, null, 0);
        var alignmentPixels = new byte[320 * 200 * 4];
        alignmentImage.CopyPixels(alignmentPixels, 320 * 4, 0);
        _gameTablePhotoAlignment = new BoardPhotoAlignmentReference(
            CameraFrame.CopyFromBgra32(320, 200, alignmentPixels));
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
        CancelGameTablePreviewHandoff();
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
        var photoAlignment = _gameTablePhotoAlignment;
        var previousRegistration = _gameTableRegistration;
        var cropRevision = _gameTableCropRevision;
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
            if (!_gameTablePreviewRequested || cropRevision != _gameTableCropRevision ||
                !ReferenceEquals(reference, _gameTableReference) ||
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
            // Anchor to the same board artwork used to measure the route geometry. A saved
            // photo can itself have a biased crop, so it is only a fallback for weak matches.
            // A brief focus change can produce worse corners for an unmoved board. Compare
            // both crops against this fresh image before replacing the earlier registration.
            selected = await Task.Run(() => AlignGameTableCrop(source, selected, photoAlignment, token,
                previousRegistration), token);
            if (!_gameTablePreviewRequested || cropRevision != _gameTableCropRevision ||
                !ReferenceEquals(reference, _gameTableReference) || !Capture.IsRunning ||
                source.Age > TimeSpan.FromSeconds(2) || Capture.LatestFrame is not { } alignedCurrent ||
                alignedCurrent.Age > TimeSpan.FromSeconds(2) || !selected.Matches(alignedCurrent)) return;
            current = alignedCurrent;
            if (!reference.IsAligned(selected.Rectify(current, BoardOrientationReference.Width,
                    BoardOrientationReference.Height)))
            {
                if (!IsGameTablePreviewUpright)
                    GameTablePreviewStatus = "Hold the board steady while its orientation is checked.";
                return;
            }
            if (_gameTableRegistration is { } previous && IsGameTablePreviewUpright &&
                previous.Matches(current) && SameCorners(previous.Corners, selected.Corners) &&
                _gameTableAlignmentPendingRevision is null) return;
            ClearGameTableAnalysis();
            _gameTableAlignmentPendingRevision = null;
            _gameTableRegistration = selected;
            _gameTableCropRevision++;
            BoardInteractionLog.Write("camera.board.aligned", new
            {
                source.Sequence, source.Epoch,
                cropRevision = _gameTableCropRevision,
                orientation = orientation.Value,
                previousPreviewRetained = GameTablePreview is not null,
                corners = selected.Corners.Select(point => new
                {
                    x = Math.Round(point.X, 5), y = Math.Round(point.Y, 5)
                }).ToArray()
            });
            _lastGameTableCropAt = DateTimeOffset.MinValue;
            IsGameTablePreviewUpright = true;
            BeginGameTablePreviewHandoff();
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

    private void BeginGameTablePreviewHandoff()
    {
        if (GameTablePreview is null)
        {
            CancelGameTablePreviewHandoff();
            GameTablePreviewStatus = "Updating the upright live board view…";
            return;
        }

        if (_gameTableHandoffTimer is null)
        {
            _gameTableHandoffTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            _gameTableHandoffTimer.Tick += OnGameTableHandoffExpired;
        }
        if (!_gameTableHandoffTimer.IsEnabled)
        {
            _gameTableHandoffStartedAt = DateTimeOffset.UtcNow;
            _gameTableHandoffBlackedOut = false;
            _gameTableHandoffTimer.Start();
        }
        _gameTableHandoffRevision = _gameTableCropRevision;
        GameTablePreviewStatus = "";
    }

    private void OnGameTableHandoffExpired(object? sender, EventArgs args)
    {
        _gameTableHandoffTimer?.Stop();
        if (_gameTableHandoffRevision != _gameTableCropRevision ||
            !_gameTablePreviewRequested || !IsGameTablePreviewUpright || GameTablePreview is null)
        {
            CancelGameTablePreviewHandoff();
            return;
        }
        _gameTableHandoffBlackedOut = true;
        GameTablePreview = null;
        if (string.IsNullOrEmpty(GameTablePreviewStatus))
            GameTablePreviewStatus = "Updating the upright live board view…";
        BoardInteractionLog.Write("camera.board.handoff-timeout", new
        {
            cropRevision = _gameTableCropRevision,
            elapsedMs = Math.Round((DateTimeOffset.UtcNow - _gameTableHandoffStartedAt).TotalMilliseconds)
        });
    }

    private void CancelGameTablePreviewHandoff()
    {
        _gameTableHandoffTimer?.Stop();
        _gameTableHandoffRevision = -1;
        _gameTableHandoffBlackedOut = false;
    }
}
