using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Model;
using GoldenTicket.Persistence;

namespace GoldenTicket.Desktop.ViewModels;

public sealed record CheckpointPhotoCaptureInput(byte[] PngBytes, CheckpointPhotoCapture Capture);

/// <summary>Explicit optional camera-photo retention, separate from authoritative checkpoint truth.</summary>
public sealed partial class CheckpointPhotoViewModel : ObservableObject
{
    private readonly CheckpointPhotoStore _store;
    private readonly Func<CancellationToken, Task<CheckpointPhotoCaptureInput>> _capture;
    private PackAwayCheckpoint? _checkpoint;
    private long _generation;

    public CheckpointPhotoViewModel(CheckpointPhotoStore store,
        Func<CancellationToken, Task<CheckpointPhotoCaptureInput>> capture)
    {
        _store = store;
        _capture = capture;
    }

    [ObservableProperty] private string _checkpointName = "No packed checkpoint selected";
    [ObservableProperty] private string _status = "Save and pack away first to create the digital checkpoint.";
    [ObservableProperty] private string _captureDetails = "";
    [ObservableProperty] private BitmapSource? _photoImage;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CaptureReferenceCommand))]
    private bool _operatorAcknowledged;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CaptureReferenceCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReloadReferenceCommand))]
    private bool _hasCheckpoint;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CaptureReferenceCommand))]
    private bool _hasPhoto;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CaptureReferenceCommand))]
    private bool _referenceUnavailable;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CaptureReferenceCommand))]
    private bool _needsReferenceReload;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CaptureReferenceCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReloadReferenceCommand))]
    private bool _isBusy;

    public string ReferenceExplanation => CheckpointPhotoReference.DisplayLabel;

    public async Task LoadCheckpointAsync(PackAwayCheckpoint? checkpoint, CancellationToken cancellationToken = default)
    {
        var generation = ++_generation;
        _checkpoint = checkpoint;
        HasCheckpoint = checkpoint is { IsSafeToPackAway: true, TargetProvenance: TargetProvenance.LogicalStateOnly };
        HasPhoto = false;
        ReferenceUnavailable = false;
        NeedsReferenceReload = false;
        PhotoImage = null;
        OperatorAcknowledged = false;
        CaptureDetails = "";
        CheckpointName = checkpoint?.Name ?? "No packed checkpoint selected";
        Status = HasCheckpoint
            ? "Optional: capture the board before clearing it. The digital save remains a state-only checkpoint."
            : "Save and pack away first to create the digital checkpoint.";
        IsBusy = false;
        if (!HasCheckpoint) return;
        IsBusy = true;
        CheckpointPhotoAttachment? attachment = null;
        try
        {
            attachment = await _store.ReadReferenceAsync(checkpoint!, cancellationToken);
            if (generation != _generation || attachment is null) return;
            Display(attachment);
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation)
            {
                NeedsReferenceReload = true;
                Status = "Reference lookup was canceled. Use Reload reference before taking another photo.";
            }
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            if (generation == _generation)
            {
                ReferenceUnavailable = true;
                Status = "The reference photo could not be read. Rebuild from the saved route list. This checkpoint's attachment cannot be replaced; a new photo needs a new checkpoint. " + ex.Message;
            }
        }
        finally
        {
            if (attachment is not null) CryptographicOperations.ZeroMemory(attachment.PngBytes);
            if (generation == _generation) IsBusy = false;
        }
    }

    private bool CanCaptureReference() => HasCheckpoint && !HasPhoto && !ReferenceUnavailable && !NeedsReferenceReload && !IsBusy && OperatorAcknowledged;
    private bool CanReloadReference() => HasCheckpoint && !IsBusy && _checkpoint is not null;

    [RelayCommand(CanExecute = nameof(CanReloadReference))]
    private async Task ReloadReferenceAsync(CancellationToken cancellationToken)
    {
        if (!CanReloadReference()) return;
        await LoadCheckpointAsync(_checkpoint, cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanCaptureReference))]
    private async Task CaptureReferenceAsync(CancellationToken cancellationToken)
    {
        var checkpoint = _checkpoint;
        var generation = _generation;
        if (!CanCaptureReference() || checkpoint is null) return;
        IsBusy = true;
        Status = "Capturing a fresh board frame and verifying its encrypted storage…";
        CheckpointPhotoCaptureInput? input = null;
        CheckpointPhotoAttachment? attachment = null;
        var attemptedStorage = false;
        try
        {
            input = await _capture(cancellationToken);
            if (generation != _generation || !OperatorAcknowledged)
                throw new InvalidOperationException("The selected checkpoint or board confirmation changed. Check the board and try again.");
            attemptedStorage = true;
            await _store.SaveReferenceAsync(checkpoint, input.PngBytes,
                input.Capture with { OperatorConfirmedBoardOnlyAndTarget = true }, cancellationToken);
            attachment = await _store.ReadReferenceAsync(checkpoint, cancellationToken)
                ?? throw new IOException("The reference photo is missing after storage verification.");
            if (generation == _generation) Display(attachment);
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation)
            {
                NeedsReferenceReload = attemptedStorage;
                Status = attemptedStorage
                    ? "Photo save confirmation was canceled. Use Reload reference to check whether the photo was saved before taking another. The digital save is unchanged."
                    : "Photo capture was canceled. The digital save is unchanged.";
            }
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            if (generation == _generation)
            {
                NeedsReferenceReload = attemptedStorage;
                Status = attemptedStorage
                    ? "The reference photo is not confirmed saved. Keep the board in place and use Reload reference before another capture. " + ex.Message
                    : "The reference photo was not captured. Keep the board in place and try again. " + ex.Message;
            }
        }
        finally
        {
            if (input is not null) CryptographicOperations.ZeroMemory(input.PngBytes);
            if (attachment is not null) CryptographicOperations.ZeroMemory(attachment.PngBytes);
            if (generation == _generation) IsBusy = false;
        }
    }

    private void Display(CheckpointPhotoAttachment attachment)
    {
        // Decode eagerly before the caller clears the temporary plaintext attachment buffer.
        using var stream = new MemoryStream(attachment.PngBytes, writable: false);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var image = decoder.Frames[0];
        image.Freeze();
        PhotoImage = image;
        HasPhoto = true;
        NeedsReferenceReload = false;
        OperatorAcknowledged = false;
        var metadata = attachment.Reference;
        CaptureDetails = $"{metadata.Width} × {metadata.Height} · Captured {metadata.Capture.CapturedAt.ToLocalTime():g}";
        Status = "Reference photo saved and read back successfully. It is bound to this checkpoint. Board contents were confirmed by the operator.";
    }

    private static bool IsExpectedFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or CryptographicException or InvalidOperationException or ArgumentException or NotSupportedException;
}
