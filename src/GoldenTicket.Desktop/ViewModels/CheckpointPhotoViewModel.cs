using System.IO;
using System.Security.Cryptography;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Model;
using GoldenTicket.Persistence;

namespace GoldenTicket.Desktop.ViewModels;

public sealed record CheckpointPhotoCaptureInput(byte[] PngBytes, CheckpointPhotoCapture Capture);

/// <summary>The required saved board image, separate from authoritative logical checkpoint truth.</summary>
public sealed partial class CheckpointPhotoViewModel : ObservableObject
{
    private readonly CheckpointPhotoStore _store;
    private readonly Func<CancellationToken, Task<CheckpointPhotoCaptureInput>> _capture;
    private PackAwayCheckpoint? _checkpoint;
    private long _generation;

    public CheckpointPhotoViewModel(CheckpointPhotoStore store,
        Func<CancellationToken, Task<CheckpointPhotoCaptureInput>> capture,
        CameraViewModel? camera = null, ICommand? cameraSetupCommand = null)
    {
        _store = store;
        _capture = capture;
        Camera = camera;
        CameraSetupCommand = cameraSetupCommand;
        if (Camera is not null) Camera.PropertyChanged += CameraChanged;
    }

    public CameraViewModel? Camera { get; }
    public ICommand? CameraSetupCommand { get; }
    public bool HasLivePreview => !HasPhoto && Camera is { IsRunning: true, BoardPreview: not null };

    public bool HasPhotoFor(SessionId sessionId, CheckpointId checkpointId) =>
        _checkpoint is { } checkpoint && checkpoint.SessionId == sessionId && checkpoint.CheckpointId == checkpointId &&
        HasPhoto && PhotoImage is not null && !IsBusy && !ReferenceUnavailable && !NeedsReferenceReload;

    public string PhotoStateSummary => !HasCheckpoint ? "No saved checkpoint selected."
        : HasPhoto ? "Board photo saved for this checkpoint."
        : IsBusy ? "Checking or saving the board photo…"
        : ReferenceUnavailable ? "The saved board photo could not be read."
        : NeedsReferenceReload ? "Board photo status needs a reload."
        : "No board photo saved for this checkpoint.";

    public string CaptureGuidance => !HasCheckpoint ? "Save and pack away to create a checkpoint first."
        : HasPhoto ? "This is the saved photo. You can use it with the route list when rebuilding."
        : ReferenceUnavailable ? "Restore this checkpoint's matching board image from a backup. The game cannot resume without a readable image."
        : NeedsReferenceReload ? "Use Reload reference to check the existing attachment before another capture."
        : !CaptureAllowed ? "This game has resumed. Save and pack away again before attaching a new photo."
        : Camera is { IsRunning: false } ? "Open Camera setup and start the overhead camera preview."
        : Camera is { HasBoardCrop: false } ? "Open Camera setup and select all four board corners."
        : Camera is { SafetyHeld: true } ? "Open Camera setup, check the whole board and set a scene reference. Wait for stable framing."
        : Camera is { CanCapturePhoto: false } ? "Wait for a fresh, stable camera preview before capturing."
        : !OperatorAcknowledged ? "Check the board and live crop, tick the confirmation below, then select Capture reference photo."
        : "Ready to capture. Keep the board in place until the photo is saved and displayed here.";

    private void CameraChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CameraViewModel.CanCapturePhoto))
        {
            if (Camera?.CanCapturePhoto != true) OperatorAcknowledged = false;
            CaptureReferenceCommand.NotifyCanExecuteChanged();
        }
        if (args.PropertyName is nameof(CameraViewModel.IsRunning) or nameof(CameraViewModel.HasBoardCrop)
            or nameof(CameraViewModel.SafetyHeld) or nameof(CameraViewModel.CanCapturePhoto))
            OnPropertyChanged(nameof(CaptureGuidance));
        if (args.PropertyName is nameof(CameraViewModel.BoardPreview) or nameof(CameraViewModel.IsRunning)) OnPropertyChanged(nameof(HasLivePreview));
    }

    [ObservableProperty] private string _checkpointName = "No packed checkpoint selected";
    [ObservableProperty] private string _status = "Save and pack away first to create the digital checkpoint.";
    [ObservableProperty] private string _captureDetails = "";
    [ObservableProperty] private BitmapSource? _photoImage;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CaptureReferenceCommand))]
    [NotifyPropertyChangedFor(nameof(CaptureGuidance))]
    private bool _operatorAcknowledged;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CaptureReferenceCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReloadReferenceCommand))]
    [NotifyPropertyChangedFor(nameof(PhotoStateSummary), nameof(CaptureGuidance))]
    private bool _hasCheckpoint;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CaptureReferenceCommand))]
    [NotifyPropertyChangedFor(nameof(PhotoStateSummary), nameof(CaptureGuidance), nameof(HasLivePreview))]
    private bool _hasPhoto;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CaptureReferenceCommand))]
    [NotifyPropertyChangedFor(nameof(PhotoStateSummary), nameof(CaptureGuidance))]
    private bool _referenceUnavailable;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CaptureReferenceCommand))]
    [NotifyPropertyChangedFor(nameof(PhotoStateSummary), nameof(CaptureGuidance))]
    private bool _needsReferenceReload;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CaptureReferenceCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReloadReferenceCommand))]
    [NotifyPropertyChangedFor(nameof(PhotoStateSummary), nameof(CaptureGuidance))]
    private bool _isBusy;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CaptureReferenceCommand))]
    [NotifyPropertyChangedFor(nameof(CaptureGuidance))]
    private bool _captureAllowed = true;

    partial void OnCaptureAllowedChanged(bool value)
    {
        if (!value) OperatorAcknowledged = false;
    }

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
            ? "The digital checkpoint is saved. Save and verify its required board photo before clearing the board or resuming this game."
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
                Status = "The required board image could not be read. Restore the matching image from a backup before resuming. The digital save is unchanged. " + ex.Message;
            }
        }
        finally
        {
            if (attachment is not null) CryptographicOperations.ZeroMemory(attachment.PngBytes);
            if (generation == _generation) IsBusy = false;
        }
    }

    private bool CanCaptureReference() => HasCheckpoint && CaptureAllowed && !HasPhoto && !ReferenceUnavailable &&
        !NeedsReferenceReload && !IsBusy && OperatorAcknowledged && (Camera?.CanCapturePhoto ?? true);
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
        Status = "Capturing a fresh board frame and verifying its saved copy…";
        CheckpointPhotoCaptureInput? input = null;
        CheckpointPhotoAttachment? attachment = null;
        var attemptedStorage = false;
        try
        {
            input = await _capture(cancellationToken);
            if (generation != _generation || !OperatorAcknowledged || !CaptureAllowed)
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
