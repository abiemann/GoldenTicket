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
    private string? _lastGameTableAnalysisBlock;

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
            if (value is null && _gameTableAnalysis is { } previous)
                BoardInteractionLog.Write("camera.analysis.cleared", new
                {
                    previous.Board.Sequence, previous.Board.Epoch,
                    ageMs = previous.Board.Age.TotalMilliseconds
                });
            else if (value is { } current)
                BoardInteractionLog.Write("camera.analysis.published", new
                {
                    current.Board.Sequence, current.Board.Epoch,
                    ageMs = current.Board.Age.TotalMilliseconds,
                    current.CropRevision, current.ModelRevision, current.ModelId,
                    trains = current.Candidates.Count(candidate => candidate.Kind == PieceCandidateKind.Train),
                    markers = current.Candidates.Count(candidate => candidate.Kind == PieceCandidateKind.PlayerMarker)
                });
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
        if (_gameTableAnalysisBusy) return;
        var block = _disposed ? "disposed" : !Capture.IsRunning ? "camera-stopped" :
            !IsGameTablePreviewUpright ? "board-orientation-unverified" :
            _gameTableRegistration is null ? "board-crop-unavailable" :
            _gameTableAlignmentPendingRevision == _gameTableCropRevision ? "board-alignment-pending" :
            IsModelBusy ? "model-loading" : _pieceModel is null ? "piece-model-unavailable" : null;
        if (block is not null)
        {
            NoteGameTableAnalysisBlock(block);
            if (!IsGameTablePreviewUpright || _pieceModel is null || IsModelBusy)
                ClearGameTableAnalysis();
            return;
        }
        var registration = _gameTableRegistration!;
        if (!registration.Matches(frame) || frame.Age > TimeSpan.FromSeconds(2))
        {
            NoteGameTableAnalysisBlock(registration.Matches(frame) ? "source-frame-stale" : "camera-format-changed");
            ClearGameTableAnalysis();
            return;
        }
        if (_lastGameTableAnalysisBlock is { } recovered)
            BoardInteractionLog.Write("camera.analysis.resumed", new { previousBlock = recovered });
        _lastGameTableAnalysisBlock = null;
        var now = DateTimeOffset.UtcNow;
        if (now - _lastGameTableAnalysisAt < TimeSpan.FromMilliseconds(700)) return;
        _lastGameTableAnalysisAt = now;
        _gameTableAnalysisBusy = true;
        _gameTableAnalysisWork = AnalyzeGameTableAsync(frame, registration, _gameTableCropRevision, _modelRevision);
    }

    private void NoteGameTableAnalysisBlock(string reason)
    {
        if (_lastGameTableAnalysisBlock == reason) return;
        _lastGameTableAnalysisBlock = reason;
        BoardInteractionLog.Write("camera.analysis.blocked", new { reason });
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
                BoardInteractionLog.Write("camera.model.result", new
                {
                    board.Sequence, board.Epoch,
                    detection.ModelId, detection.Backend,
                    inferenceMs = detection.Elapsed.TotalMilliseconds,
                    boardAgeMs = board.Age.TotalMilliseconds,
                    trains = detection.Candidates.Count(candidate => candidate.Kind == PieceCandidateKind.Train),
                    markers = detection.Candidates.Count(candidate => candidate.Kind == PieceCandidateKind.PlayerMarker)
                });
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
                BoardInteractionLog.Write("camera.analysis.discarded", new
                {
                    frame.Sequence, frame.Epoch, cropRevision, modelRevision,
                    resultAgeMs = result?.Board.Age.TotalMilliseconds,
                    reason = _disposed ? "disposed" : result is null ? "no-result" :
                        !Capture.IsRunning ? "camera-stopped" : !IsGameTablePreviewUpright ? "orientation-unverified" :
                        !ReferenceEquals(registration, _gameTableRegistration) ? "registration-changed" :
                        cropRevision != _gameTableCropRevision ? "crop-changed" :
                        modelRevision != _modelRevision ? "model-changed" :
                        IsModelBusy ? "model-loading" :
                        result.Board.Age > TimeSpan.FromSeconds(2) ? "inference-result-stale" :
                        "source-frame-unavailable-or-changed"
                });
                ClearGameTableAnalysis();
                return;
            }
            GameTableAnalysis = result;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            ClearGameTableAnalysis();
        }
        catch (Exception error)
        {
            BoardInteractionLog.Write("camera.analysis.error", new
            {
                frame.Sequence, frame.Epoch,
                errorType = error.GetType().Name, errorCode = error.HResult
            });
            // A failed camera/model observation must never become game evidence. The next frame retries.
            ClearGameTableAnalysis();
        }
        finally { _gameTableAnalysisBusy = false; }
    }
}
