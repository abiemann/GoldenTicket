using System.Collections.Immutable;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Model;
using GoldenTicket.Persistence;

namespace GoldenTicket.Domain.Tests;

/// <summary>Recovery from a durable immutable photo that the current screen has not acknowledged.</summary>
public sealed class PhotoRecoveryTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
        "GoldenTicket.PhotoRecoveryTests", Guid.NewGuid().ToString("N")));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReloadRecoversPhotoFinalizedAfterTheScreenLastCheckedStorage()
    {
        var store = new CheckpointPhotoStore(_root);
        var checkpoint = Checkpoint();
        var captureCalls = 0;
        var newCapture = Png(110);
        var vm = new CheckpointPhotoViewModel(store, _ =>
        {
            captureCalls++;
            return Task.FromResult(new CheckpointPhotoCaptureInput(newCapture, Capture()));
        });
        await vm.LoadCheckpointAsync(checkpoint, Token);
        Assert.False(vm.HasPhoto);

        // Same observable condition as a successful finalization whose reply/readback was lost:
        // the immutable attachment exists, while this screen still believes there is none.
        var originalPng = Png(30);
        await store.SaveReferenceAsync(checkpoint, originalPng, Capture(), Token);
        var path = store.AttachmentPath(checkpoint.SessionId, checkpoint.CheckpointId);
        var originalEnvelope = await File.ReadAllBytesAsync(path, Token);
        vm.OperatorAcknowledged = true;
        await vm.CaptureReferenceCommand.ExecuteAsync(null);
        Assert.False(vm.HasPhoto);
        Assert.True(vm.NeedsReferenceReload);
        Assert.False(vm.CaptureReferenceCommand.CanExecute(null));
        Assert.True(vm.ReloadReferenceCommand.CanExecute(null));
        Assert.Contains("Reload reference", vm.Status);
        Assert.All(newCapture, value => Assert.Equal(0, value));

        await vm.ReloadReferenceCommand.ExecuteAsync(null);
        Assert.True(vm.HasPhoto, vm.Status);
        Assert.NotNull(vm.PhotoImage);
        Assert.False(vm.NeedsReferenceReload);
        Assert.False(vm.CaptureReferenceCommand.CanExecute(null));
        Assert.Equal(1, captureCalls);
        Assert.Equal(originalEnvelope, await File.ReadAllBytesAsync(path, Token));
        Assert.Equal(originalPng, (await store.ReadReferenceAsync(checkpoint, Token))!.PngBytes);
    }

    [Fact]
    public async Task CancelledAttemptCanReloadAnAlreadyFinalizedReference()
    {
        var store = new CheckpointPhotoStore(_root);
        var checkpoint = Checkpoint();
        var pending = new TaskCompletionSource<CheckpointPhotoCaptureInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new CheckpointPhotoViewModel(store, _ => pending.Task);
        await vm.LoadCheckpointAsync(checkpoint, Token);
        vm.OperatorAcknowledged = true;
        var attempt = vm.CaptureReferenceCommand.ExecuteAsync(null);
        Assert.False(vm.ReloadReferenceCommand.CanExecute(null));
        await store.SaveReferenceAsync(checkpoint, Png(25), Capture(), Token);
        vm.CaptureReferenceCommand.Cancel();
        pending.SetResult(new CheckpointPhotoCaptureInput(Png(115), Capture()));
        await attempt;
        Assert.True(vm.NeedsReferenceReload);
        Assert.Contains("Reload reference", vm.Status);
        await vm.ReloadReferenceCommand.ExecuteAsync(null);
        Assert.True(vm.HasPhoto, vm.Status);
        Assert.False(vm.NeedsReferenceReload);
    }

    [Fact]
    public async Task ReloadRequiresACheckpointAndCannotInterruptAnActiveCapture()
    {
        var pending = new TaskCompletionSource<CheckpointPhotoCaptureInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new CheckpointPhotoViewModel(new CheckpointPhotoStore(_root), _ => pending.Task);
        Assert.False(vm.ReloadReferenceCommand.CanExecute(null));
        await vm.LoadCheckpointAsync(Checkpoint(), Token);
        Assert.True(vm.ReloadReferenceCommand.CanExecute(null));
        vm.OperatorAcknowledged = true;
        var attempt = vm.CaptureReferenceCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.ReloadReferenceCommand.CanExecute(null));
        // Guard the method as well as the button: an explicit command call cannot reset generation.
        await vm.ReloadReferenceCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        pending.SetResult(new CheckpointPhotoCaptureInput(Png(40), Capture()));
        await attempt;
        Assert.True(vm.HasPhoto, vm.Status);
        Assert.False(vm.IsBusy);
    }

    private static PackAwayCheckpoint Checkpoint()
    {
        ImmutableArray<TargetRoute> target = [new(new RouteId("test-route"), new SeatId(1), 3)];
        return new PackAwayCheckpoint(CheckpointId.New(), SessionId.New(), "Photo recovery",
            DateTimeOffset.UtcNow.AddMinutes(-1), PackAwayCheckpoint.CurrentFormatVersion, 12, 9, 4,
            "ttr-us-classic-en-v1", "sha256:manifest", "logical-v2:test", TurnPhase.TurnStart,
            null, target, PackAwayCheckpoint.HashTarget(target), TargetProvenance.LogicalStateOnly,
            null, CheckpointStatus.Verified);
    }

    private static CheckpointPhotoCapture Capture() => new(DateTimeOffset.UtcNow, "Synthetic board camera", 7, 2, true);

    private static byte[] Png(byte value)
    {
        var pixels = Enumerable.Range(0, 16 * 8 * 3).Select(index => (byte)(value + index % 64)).ToArray();
        var bitmap = BitmapSource.Create(16, 8, 96, 96, PixelFormats.Rgb24, null, pixels, 16 * 3);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public void Dispose()
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GoldenTicket.PhotoRecoveryTests")) + Path.DirectorySeparatorChar;
        if (!_root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The generated test directory escaped its fixture root.");
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
