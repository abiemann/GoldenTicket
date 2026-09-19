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
        var pixels = Enumerable.Range(0, 80 * 50 * 3).Select(index => (byte)(index % 251)).ToArray();
        var image = BitmapSource.Create(80, 50, 96, 96, PixelFormats.Rgb24, null, pixels, 80 * 3);
        image.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Store.SaveReferenceAsync(checkpoint, stream.ToArray(),
            new CheckpointPhotoCapture(DateTimeOffset.UtcNow, "Test board camera", 1, 1, true),
            TestContext.Current.CancellationToken, pendingPlacement: pendingPlacement);
    }

    public void Dispose()
    {
        var allowed = Path.GetFullPath(Parent) + Path.DirectorySeparatorChar;
        if (!_root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The photo fixture escaped its temporary parent.");
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
