using GoldenTicket.Vision;

namespace GoldenTicket.Desktop.ViewModels;

/// <summary>Fresh evidence from the accepted, upright board crop. Coordinates are normalized to Board.</summary>
public sealed record GameTableAnalysis(CameraFrame Board, IReadOnlyList<PieceCandidate> Candidates,
    IReadOnlyList<ScoreMarkerReading> Scores, long CropRevision, long ModelRevision, string ModelId);

public sealed partial class CameraViewModel
{
    private GameTableAnalysis? _gameTableAnalysis;
    private Task _gameTableAnalysisWork = Task.CompletedTask;
    private bool _gameTableAnalysisBusy;
    private DateTimeOffset _lastGameTableAnalysisAt = DateTimeOffset.MinValue;

    /// <summary>
    /// The most recent camera/model result for the board accepted during setup. Consumers must still
    /// check Board.Age and the requested operation before treating its pieces as game evidence.
    /// </summary>
    public GameTableAnalysis? GameTableAnalysis
    {
        get => _gameTableAnalysis;
        private set
        {
            if (ReferenceEquals(_gameTableAnalysis, value)) return;
            _gameTableAnalysis = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanCaptureGameTablePhoto));
        }
    }

    private void ClearGameTableAnalysis() => GameTableAnalysis = null;

    partial void OnIsGameTablePreviewUprightChanged(bool value)
    {
        if (!value) ClearGameTableAnalysis();
        OnPropertyChanged(nameof(CanCaptureGameTablePhoto));
    }

    private void QueueGameTableAnalysis(CameraFrame frame)
    {
        if (_disposed || _gameTableAnalysisBusy || !Capture.IsRunning || !IsGameTablePreviewUpright ||
            _gameTableRegistration is not { } registration || _pieceModel is null || IsModelBusy)
        {
            if (!IsGameTablePreviewUpright || _pieceModel is null || IsModelBusy)
                ClearGameTableAnalysis();
            return;
        }
        if (!registration.Matches(frame) || frame.Age > TimeSpan.FromSeconds(2))
        {
            ClearGameTableAnalysis();
            return;
        }
        var now = DateTimeOffset.UtcNow;
        if (now - _lastGameTableAnalysisAt < TimeSpan.FromMilliseconds(700)) return;
        _lastGameTableAnalysisAt = now;
        _gameTableAnalysisBusy = true;
        _gameTableAnalysisWork = AnalyzeGameTableAsync(frame, registration, _gameTableCropRevision, _modelRevision);
    }

    private async Task AnalyzeGameTableAsync(CameraFrame frame, BoardRegistration registration,
        long cropRevision, long modelRevision)
    {
        try
        {
            var result = await Task.Run(() =>
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                var board = registration.Rectify(frame, LearnedPieceDetector.BoardWidth, LearnedPieceDetector.BoardHeight);
                if (board.Age > TimeSpan.FromSeconds(2)) return null;
                LearnedPieceDetection? detection;
                lock (_modelGate)
                {
                    if (modelRevision != _modelRevision || _pieceModel is null) return null;
                    detection = _pieceModel.Detect(board, _lifetime.Token);
                }
                var scores = ScoreMarkerReader.Read(board, detection.Candidates);
                return new GameTableAnalysis(board, detection.Candidates, scores, cropRevision,
                    modelRevision, detection.ModelId);
            }, _lifetime.Token);

            if (_disposed || result is null || !Capture.IsRunning || !IsGameTablePreviewUpright ||
                !ReferenceEquals(registration, _gameTableRegistration) || cropRevision != _gameTableCropRevision ||
                modelRevision != _modelRevision || IsModelBusy || frame.Epoch != Capture.Epoch ||
                result.Board.Age > TimeSpan.FromSeconds(2) ||
                Capture.LatestFrame is not { } latest || latest.Epoch != frame.Epoch ||
                latest.Age > TimeSpan.FromSeconds(2) || !registration.Matches(latest))
            {
                ClearGameTableAnalysis();
                return;
            }
            GameTableAnalysis = result;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            ClearGameTableAnalysis();
        }
        catch (Exception)
        {
            // A failed camera/model observation must never become game evidence. The next frame retries.
            ClearGameTableAnalysis();
        }
        finally { _gameTableAnalysisBusy = false; }
    }
}
