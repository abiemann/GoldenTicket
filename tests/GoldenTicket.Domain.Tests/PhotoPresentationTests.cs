using System.Collections.Immutable;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Model;
using GoldenTicket.Persistence;

namespace GoldenTicket.Domain.Tests;

/// <summary>Photo guidance and camera readiness, using synthetic images and no camera hardware.</summary>
public sealed class PhotoPresentationTests : IDisposable
{
    private const string MissingSummary = "No board photo saved for this checkpoint.";
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
        "GoldenTicket.PhotoPresentationTests", Guid.NewGuid().ToString("N")));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MissingPhotoIsExplicitAndSwitchingCheckpointsClearsThePreviousImage()
    {
        var store = new CheckpointPhotoStore(_root);
        var saved = Checkpoint("With photo");
        var empty = Checkpoint("Without photo");
        await store.SaveReferenceAsync(saved, Png(), Capture(), Token);
        var vm = new CheckpointPhotoViewModel(store, _ => throw new InvalidOperationException("Must not capture."));

        await vm.LoadCheckpointAsync(empty, Token);
        Assert.Equal(MissingSummary, vm.PhotoStateSummary);
        Assert.False(vm.HasPhoto);
        Assert.Null(vm.PhotoImage);

        await vm.LoadCheckpointAsync(saved, Token);
        Assert.True(vm.HasPhoto, vm.Status);
        Assert.NotNull(vm.PhotoImage);
        Assert.True(vm.PhotoImage.IsFrozen);
        Assert.NotEqual(MissingSummary, vm.PhotoStateSummary);

        await vm.LoadCheckpointAsync(empty, Token);
        Assert.Equal("Without photo", vm.CheckpointName);
        Assert.Equal(MissingSummary, vm.PhotoStateSummary);
        Assert.False(vm.HasPhoto);
        Assert.Null(vm.PhotoImage);
        Assert.Empty(vm.CaptureDetails);

        await vm.LoadCheckpointAsync(null, Token);
        Assert.False(vm.HasCheckpoint);
        Assert.NotEqual(MissingSummary, vm.PhotoStateSummary);
        Assert.False(vm.CaptureReferenceCommand.CanExecute(null));
    }

    [Fact]
    public async Task CameraPrerequisitesExplainWhyConfirmationAloneCannotEnableCapture()
    {
        await using var camera = new CameraViewModel();
        var captureCalls = 0;
        var vm = new CheckpointPhotoViewModel(new CheckpointPhotoStore(_root), _ =>
        {
            captureCalls++;
            return Task.FromResult(new CheckpointPhotoCaptureInput(Png(), Capture()));
        }, camera);
        await vm.LoadCheckpointAsync(Checkpoint("Camera prerequisites"), Token);
        vm.OperatorAcknowledged = true;
        Assert.False(vm.CaptureReferenceCommand.CanExecute(null));
        Assert.Contains("camera", vm.CaptureGuidance, StringComparison.OrdinalIgnoreCase);
        await vm.CaptureReferenceCommand.ExecuteAsync(null);
        Assert.Equal(0, captureCalls);

        camera.IsRunning = true;
        Assert.Contains("corner", vm.CaptureGuidance, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.CaptureReferenceCommand.CanExecute(null));
        camera.HasBoardCrop = true;
        Assert.Contains("reference", vm.CaptureGuidance, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.CaptureReferenceCommand.CanExecute(null));

        camera.SafetyHeld = false;
        vm.OperatorAcknowledged = true;
        Assert.True(vm.CaptureReferenceCommand.CanExecute(null));
        await vm.CaptureReferenceCommand.ExecuteAsync(null);
        Assert.Equal(1, captureCalls);
        Assert.True(vm.HasPhoto, vm.Status);
        Assert.False(vm.CaptureReferenceCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("crop")]
    [InlineData("reference")]
    [InlineData("camera")]
    [InlineData("busy")]
    public async Task LosingCameraReadinessRevokesBoardConfirmation(string invalidation)
    {
        await using var camera = new CameraViewModel { IsRunning = true, HasBoardCrop = true, SafetyHeld = false };
        var vm = new CheckpointPhotoViewModel(new CheckpointPhotoStore(_root),
            _ => throw new InvalidOperationException("Must not capture."), camera);
        await vm.LoadCheckpointAsync(Checkpoint("Readiness reset"), Token);
        vm.OperatorAcknowledged = true;
        Assert.True(vm.CaptureReferenceCommand.CanExecute(null));
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        switch (invalidation)
        {
            case "crop": camera.HasBoardCrop = false; break;
            case "reference": camera.SafetyHeld = true; break;
            case "camera": camera.IsRunning = false; break;
            case "busy": camera.IsBusy = true; break;
        }

        Assert.False(vm.OperatorAcknowledged);
        Assert.False(vm.CaptureReferenceCommand.CanExecute(null));
        Assert.Contains(nameof(vm.CaptureGuidance), notifications);
        camera.HasBoardCrop = true;
        camera.SafetyHeld = false;
        camera.IsRunning = true;
        camera.IsBusy = false;
        Assert.False(vm.OperatorAcknowledged);
        Assert.False(vm.CaptureReferenceCommand.CanExecute(null));
        vm.OperatorAcknowledged = true;
        Assert.True(vm.CaptureReferenceCommand.CanExecute(null));
    }

    [Fact]
    public async Task LiveCropPreviewIsNotPresentedAsASavedPhoto()
    {
        await using var camera = new CameraViewModel { IsRunning = true, HasBoardCrop = true, SafetyHeld = false };
        var store = new CheckpointPhotoStore(_root);
        var checkpoint = Checkpoint("Live preview");
        var vm = new CheckpointPhotoViewModel(store, _ => throw new InvalidOperationException("Must not capture."), camera);
        await vm.LoadCheckpointAsync(checkpoint, Token);
        Assert.False(vm.HasLivePreview);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        camera.BoardPreview = Bitmap();
        Assert.True(vm.HasLivePreview);
        Assert.Contains(nameof(vm.HasLivePreview), notifications);
        Assert.False(vm.HasPhoto);
        Assert.Null(vm.PhotoImage);
        Assert.Equal(MissingSummary, vm.PhotoStateSummary);

        camera.IsRunning = false;
        Assert.False(vm.HasLivePreview);
        camera.IsRunning = true;
        await store.SaveReferenceAsync(checkpoint, Png(), Capture(), Token);
        await vm.ReloadReferenceCommand.ExecuteAsync(null);
        Assert.True(vm.HasPhoto, vm.Status);
        Assert.NotNull(vm.PhotoImage);
        Assert.False(vm.HasLivePreview);
    }

    [Fact]
    public async Task HistoricalCheckpointCanShowItsPhotoButCannotCaptureAnother()
    {
        var store = new CheckpointPhotoStore(_root);
        var checkpoint = Checkpoint("Historical checkpoint");
        var captureCalls = 0;
        var vm = new CheckpointPhotoViewModel(store, _ =>
        {
            captureCalls++;
            return Task.FromResult(new CheckpointPhotoCaptureInput(Png(), Capture()));
        });
        await vm.LoadCheckpointAsync(checkpoint, Token);
        vm.OperatorAcknowledged = true;
        Assert.True(vm.CaptureReferenceCommand.CanExecute(null));
        vm.CaptureAllowed = false;
        Assert.False(vm.CaptureReferenceCommand.CanExecute(null));
        await vm.CaptureReferenceCommand.ExecuteAsync(null);
        Assert.Equal(0, captureCalls);

        await store.SaveReferenceAsync(checkpoint, Png(), Capture(), Token);
        await vm.ReloadReferenceCommand.ExecuteAsync(null);
        Assert.True(vm.HasPhoto, vm.Status);
        Assert.NotNull(vm.PhotoImage);
        Assert.False(vm.CaptureAllowed);
        Assert.False(vm.CaptureReferenceCommand.CanExecute(null));
    }

    [Fact]
    public async Task CorruptAttachmentReportsUnavailableInsteadOfMissingAndPreventsReplacement()
    {
        var store = new CheckpointPhotoStore(_root);
        var checkpoint = Checkpoint("Unreadable photo");
        await store.SaveReferenceAsync(checkpoint, Png(), Capture(), Token);
        await File.WriteAllBytesAsync(store.AttachmentPath(checkpoint.SessionId, checkpoint.CheckpointId),
            "damaged photo"u8.ToArray(), Token);
        var vm = new CheckpointPhotoViewModel(store, _ => throw new InvalidOperationException("Must not capture."));
        await vm.LoadCheckpointAsync(checkpoint, Token);
        vm.OperatorAcknowledged = true;

        Assert.True(vm.ReferenceUnavailable);
        Assert.False(vm.HasPhoto);
        Assert.Null(vm.PhotoImage);
        Assert.NotEqual(MissingSummary, vm.PhotoStateSummary);
        Assert.Contains("read", vm.PhotoStateSummary, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.CaptureReferenceCommand.CanExecute(null));
        Assert.True(vm.ReloadReferenceCommand.CanExecute(null));
    }

    [Fact]
    public async Task UncertainSaveUsesReloadGuidanceAndRecoveredPhotoReplacesTheWarning()
    {
        var store = new CheckpointPhotoStore(_root);
        var checkpoint = Checkpoint("Reload required");
        var vm = new CheckpointPhotoViewModel(store,
            _ => Task.FromResult(new CheckpointPhotoCaptureInput(Png(160), Capture())));
        await vm.LoadCheckpointAsync(checkpoint, Token);
        // A previous save completed outside the current view's last storage read.
        await store.SaveReferenceAsync(checkpoint, Png(20), Capture(), Token);
        vm.OperatorAcknowledged = true;
        await vm.CaptureReferenceCommand.ExecuteAsync(null);

        Assert.True(vm.NeedsReferenceReload);
        Assert.NotEqual(MissingSummary, vm.PhotoStateSummary);
        Assert.Contains("reload", vm.CaptureGuidance, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.CaptureReferenceCommand.CanExecute(null));
        var uncertainSummary = vm.PhotoStateSummary;
        await vm.ReloadReferenceCommand.ExecuteAsync(null);
        Assert.True(vm.HasPhoto, vm.Status);
        Assert.NotNull(vm.PhotoImage);
        Assert.False(vm.NeedsReferenceReload);
        Assert.NotEqual(uncertainSummary, vm.PhotoStateSummary);
    }

    [Fact]
    public void CameraSetupCommandUsesTheSuppliedNavigationWithoutCapturing()
    {
        var navigationCalls = 0;
        var command = new RelayCommand(() => navigationCalls++);
        var vm = new CheckpointPhotoViewModel(new CheckpointPhotoStore(_root),
            _ => throw new InvalidOperationException("Must not capture."), cameraSetupCommand: command);
        Assert.Same(command, vm.CameraSetupCommand);
        vm.CameraSetupCommand!.Execute(null);
        Assert.Equal(1, navigationCalls);
        Assert.False(vm.HasPhoto);
    }

    private static PackAwayCheckpoint Checkpoint(string name)
    {
        ImmutableArray<TargetRoute> target = [];
        return new PackAwayCheckpoint(CheckpointId.New(), SessionId.New(), name,
            DateTimeOffset.UtcNow.AddMinutes(-1), PackAwayCheckpoint.CurrentFormatVersion, 12, 9, 4,
            "ttr-us-classic-en-v1", "sha256:manifest", "logical-v2:test", TurnPhase.TurnStart,
            null, target, PackAwayCheckpoint.HashTarget(target), TargetProvenance.LogicalStateOnly,
            null, CheckpointStatus.Verified);
    }

    private static CheckpointPhotoCapture Capture() => new(DateTimeOffset.UtcNow, "Synthetic board camera", 7, 2, true);

    private static BitmapSource Bitmap(byte value = 30)
    {
        var pixels = Enumerable.Range(0, 16 * 8 * 3).Select(index => (byte)(value + index % 64)).ToArray();
        var bitmap = BitmapSource.Create(16, 8, 96, 96, PixelFormats.Rgb24, null, pixels, 16 * 3);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Png(byte value = 30)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Bitmap(value)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public void Dispose()
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GoldenTicket.PhotoPresentationTests")) + Path.DirectorySeparatorChar;
        if (!_root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The generated test directory escaped its fixture root.");
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
