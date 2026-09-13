using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoldenTicket.Vision;
using Microsoft.Win32;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class CameraViewModel
{
    private readonly string _pieceModelDirectory;
    private readonly Func<string, bool, IPieceModelDetector> _pieceModelFactory;
    private readonly object _modelGate = new();
    private IPieceModelDetector? _pieceModel;
    private Task? _modelInitialization;
    private long _modelRevision;
    private DetectionReviewFrame? _reviewDetection;

    [ObservableProperty] private string _modelStatus = "ML model has not been loaded yet.";
    [ObservableProperty] private bool _isModelBusy;
    [ObservableProperty] private string _detectionReviewNote = "";
    [ObservableProperty] private string? _lastDetectionExamplePath;
    public bool CanReloadPieceModel => !IsModelBusy && !_disposed;
    public bool CanSaveDetectionExample => !_disposed && _reviewDetection is { } review &&
        ShowPieceOutlines && Capture.IsRunning && review.Board.Epoch == Capture.Epoch &&
        review.Board.Age <= TimeSpan.FromSeconds(2);

    partial void OnIsModelBusyChanged(bool value) => OnPropertyChanged(nameof(CanReloadPieceModel));

    private Task EnsurePieceModelAsync() => _modelInitialization ??= LoadPieceModelCoreAsync();

    [RelayCommand]
    private async Task ReloadPieceModelAsync()
    {
        if (!CanReloadPieceModel) return;
        _modelRevision++;
        ClearDetectionPreview();
        _modelInitialization = LoadPieceModelCoreAsync();
        await _modelInitialization;
        _previewSequence = -1;
    }

    private async Task LoadPieceModelCoreAsync()
    {
        IsModelBusy = true;
        ModelStatus = "Loading the local ML model…";
        // The same explicit CPU preference also provides a way to diagnose ML GPU problems.
        // A reload is required to change inference provider; enhancement can change independently.
        var preferGpu = SelectedProcessor.Value != FrameComputeMode.Cpu;
        try
        {
            var status = await Task.Run(() =>
            {
                lock (_modelGate)
                {
                    _lifetime.Token.ThrowIfCancellationRequested();
                    _pieceModel?.Dispose();
                    _pieceModel = null;
                    var loaded = _pieceModelFactory(_pieceModelDirectory, preferGpu);
                    if (_lifetime.IsCancellationRequested)
                    {
                        loaded.Dispose();
                        _lifetime.Token.ThrowIfCancellationRequested();
                    }
                    _pieceModel = loaded;
                    return $"ML · {loaded.Backend} · {loaded.ModelId}" +
                        (loaded.FallbackReason is { Length: > 0 } reason ? " · " + reason : "");
                }
            }, _lifetime.Token);
            if (!_disposed) ModelStatus = status;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_disposed)
            {
                ClearDetectionPreview();
                ModelStatus = "ML unavailable: " + error.Message;
                DetectionText = "The local ML model could not be loaded. Manual play and the camera preview remain available.";
            }
        }
        finally { IsModelBusy = false; }
    }

    [RelayCommand]
    private async Task SaveDetectionExampleAsync()
    {
        if (!CanSaveDetectionExample) return;
        // Freeze the exact analyzed image and predictions before opening a file dialog.
        var review = _reviewDetection!;
        var note = DetectionReviewNote;
        var dialog = new SaveFileDialog
        {
            Title = "Save an ML detection example for review",
            Filter = "Detection review (*.zip)|*.zip",
            FileName = $"GoldenTicket-detection-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            AddExtension = true,
            DefaultExt = ".zip"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await WriteDetectionExampleAsync(review, note, dialog.FileName, _lifetime.Token);
            LastDetectionExamplePath = dialog.FileName;
            Problem = null;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Problem = "Detection example could not be saved: " + error.Message; }
    }

    /// <summary>Saves the current fresh inference frame, not a later camera frame or painted preview.</summary>
    public Task SaveDetectionExampleToAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!CanSaveDetectionExample) throw new InvalidOperationException("Wait for fresh ML outlines before saving a review example.");
        return WriteDetectionExampleAsync(_reviewDetection!, DetectionReviewNote, path, cancellationToken);
    }

    private static async Task WriteDetectionExampleAsync(DetectionReviewFrame review, string note,
        string path, CancellationToken cancellationToken)
    {
        var png = await review.Board.EncodePngAsync(cancellationToken);
        var record = new
        {
            version = 1, purpose = "model-prediction-review", reviewed = false,
            note = note[..Math.Min(note.Length, 2000)],
            image = "board.png", sha256 = Convert.ToHexStringLower(SHA256.HashData(png)),
            width = review.Board.Width, height = review.Board.Height,
            capturedAt = review.Board.CapturedAt, frameSequence = review.Board.Sequence,
            cameraEpoch = review.Board.Epoch, sensorWidth = review.SensorWidth, sensorHeight = review.SensorHeight,
            cropRevision = review.CropRevision, processingRevision = review.ProcessingRevision,
            modelRevision = review.ModelRevision, corners = review.Corners,
            modelId = review.Detection.ModelId, modelSha256 = review.Detection.ModelSha256, backend = review.Detection.Backend,
            inferenceMilliseconds = review.Detection.Elapsed.TotalMilliseconds,
            outlineCoordinateSpace = "normalized-board", boxCoordinateSpace = "board-pixels",
            predictions = review.Detection.Candidates.Select(candidate => new
            {
                kind = candidate.Kind == PieceCandidateKind.Train ? "train" : "player-marker",
                confidence = candidate.Confidence, outline = candidate.Outline.Select(point => new { x = point.X, y = point.Y }),
                x = candidate.Outline.Min(point => point.X) * review.Board.Width,
                y = candidate.Outline.Min(point => point.Y) * review.Board.Height,
                width = (candidate.Outline.Max(point => point.X) - candidate.Outline.Min(point => point.X)) * review.Board.Width,
                height = (candidate.Outline.Max(point => point.Y) - candidate.Outline.Min(point => point.Y)) * review.Board.Height
            }).ToArray()
        };
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                await using (var imageStream = archive.CreateEntry("board.png", CompressionLevel.NoCompression).Open())
                    await imageStream.WriteAsync(png, cancellationToken);
                await using var jsonStream = archive.CreateEntry("predictions.json").Open();
                await JsonSerializer.SerializeAsync(jsonStream, record, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // A review example never replaces an earlier example, even if a filename is reused.
            File.Move(temporary, fullPath, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record DetectionReviewFrame(CameraFrame Board, LearnedPieceDetection Detection,
        int SensorWidth, int SensorHeight, long CropRevision, long ProcessingRevision, long ModelRevision,
        NormalizedPoint[] Corners);
}
