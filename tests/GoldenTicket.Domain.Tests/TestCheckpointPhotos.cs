using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenTicket.Domain.Model;
using GoldenTicket.Persistence;

namespace GoldenTicket.Domain.Tests;

/// <summary>Isolated durable board photos; tests never use the user's saved-game directory.</summary>
internal sealed class TestCheckpointPhotos : IDisposable
{
    private static readonly string Parent = Path.Combine(Path.GetTempPath(), "GoldenTicket.PhotoFixtures");
    private readonly string _root = Path.GetFullPath(Path.Combine(Parent, Guid.NewGuid().ToString("N")));

    public TestCheckpointPhotos() => Store = new CheckpointPhotoStore(_root);
    public CheckpointPhotoStore Store { get; }

    public Task<CheckpointPhotoReference> AttachAsync(PackAwayCheckpoint checkpoint,
        CheckpointPendingPlacement? pendingPlacement = null)
    {
        if (checkpoint.SuspendedTurnPhase == TurnPhase.AwaitingPhysicalPlacement && pendingPlacement is null)
            throw new InvalidOperationException("A new pending photo fixture needs its exact placement slots.");
        var pixels = Enumerable.Range(0, 80 * 50 * 3).Select(index => (byte)(index % 251)).ToArray();
        var image = BitmapSource.Create(80, 50, 96, 96, PixelFormats.Rgb24, null, pixels, 80 * 3);
        image.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var total = checkpoint.TotalTrainsOnBoard + (pendingPlacement?.TrainCount ?? 0);
        var inventory = pendingPlacement is null ? null : new CheckpointTrainInventory(
            pendingPlacement.Color == PlayerColor.Blue ? total : 0,
            pendingPlacement.Color == PlayerColor.Red ? total : 0,
            pendingPlacement.Color == PlayerColor.Green ? total : 0,
            pendingPlacement.Color == PlayerColor.Yellow ? total : 0,
            pendingPlacement.Color == PlayerColor.Black ? total : 0,
            CheckpointTrainInventoryProvenance.CameraObserved);
        return Store.SaveReferenceAsync(checkpoint, stream.ToArray(),
            new CheckpointPhotoCapture(DateTimeOffset.UtcNow, "Test board camera", 1, 1, true),
            TestContext.Current.CancellationToken, inventory, pendingPlacement);
    }

    public void Dispose()
    {
        var allowed = Path.GetFullPath(Parent) + Path.DirectorySeparatorChar;
        if (!_root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The photo fixture escaped its temporary parent.");
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
