using GoldenTicket.Application;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Persistence;
using Microsoft.Data.Sqlite;

namespace GoldenTicket.Domain.Tests;

public sealed class SessionDeletionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "GoldenTicket.Tests", Guid.NewGuid().ToString("n"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact]
    public async Task DeletingOneMatchRemovesItsDatabasePhotosAndSidecarsAndPreservesOtherMatches()
    {
        var store = new SqliteSessionStore(_root);
        var deleted = await CreateAsync(store);
        var retained = await CreateAsync(store);
        var expectedHash = await retained.ComputeStateHashAsync(Token);
        SqliteConnection.ClearAllPools();

        var deletedDirectory = store.SessionDirectory(deleted.SessionId);
        var retainedDirectory = store.SessionDirectory(retained.SessionId);
        WriteAttachments(deletedDirectory);
        WriteAttachments(retainedDirectory);
        // Exercise deletion of database sidecars even when no connection is open.
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            await File.WriteAllTextAsync(store.DatabasePath(deleted.SessionId) + suffix, "sidecar", Token);
        var retainedFiles = Directory.GetFiles(retainedDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);

        await store.DeleteSessionAsync(deleted.SessionId, Token);

        Assert.False(Directory.Exists(deletedDirectory));
        Assert.True(Directory.Exists(retainedDirectory));
        foreach (var file in retainedFiles)
            Assert.Equal(file.Value, await File.ReadAllBytesAsync(file.Key, Token));
        var listed = Assert.Single(await store.ListSessionsAsync(Token));
        Assert.Equal(retained.SessionId, listed.SessionId);
        var restored = await store.RestoreAsync(retained.SessionId,
            TestManifest.Manifest, TestManifest.Catalog, Token);
        Assert.Equal(expectedHash, StateHash.Compute(restored.State));
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.RestoreAsync(deleted.SessionId,
            TestManifest.Manifest, TestManifest.Catalog, Token));
    }

    [Fact]
    public async Task AnUnavailableMatchCanBeDeletedWithoutOpeningItsDamagedDatabase()
    {
        var store = new SqliteSessionStore(_root);
        var sessionId = SessionId.New();
        var directory = store.SessionDirectory(sessionId);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(store.DatabasePath(sessionId), "Not a SQLite database", Token);
        WriteAttachments(directory);
        var summary = Assert.Single(await store.ListSessionsAsync(Token));
        Assert.Equal(sessionId, summary.SessionId);
        Assert.NotNull(summary.UnavailableReason);

        await store.DeleteSessionAsync(sessionId, Token);

        Assert.False(Directory.Exists(directory));
        Assert.Empty(await store.ListSessionsAsync(Token));
    }

    [Fact]
    public async Task RepeatingDeletionIsHarmlessAndDoesNotRecreateTheMatch()
    {
        var store = new SqliteSessionStore(_root);
        var coordinator = await CreateAsync(store);
        var directory = store.SessionDirectory(coordinator.SessionId);

        await store.DeleteSessionAsync(coordinator.SessionId, Token);
        await store.DeleteSessionAsync(coordinator.SessionId, Token);

        Assert.False(Directory.Exists(directory));
        Assert.Empty(await store.ListSessionsAsync(Token));
    }

    [Fact]
    public async Task DeletingAnUnknownMatchDoesNotCreateAnyDirectories()
    {
        var store = new SqliteSessionStore(_root);

        await store.DeleteSessionAsync(SessionId.New(), Token);

        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task CancellationBeforeDeletionPreservesTheMatchAndItsAttachments()
    {
        var store = new SqliteSessionStore(_root);
        var coordinator = await CreateAsync(store);
        var expectedHash = await coordinator.ComputeStateHashAsync(Token);
        var directory = store.SessionDirectory(coordinator.SessionId);
        WriteAttachments(directory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.DeleteSessionAsync(coordinator.SessionId, cancellation.Token));

        Assert.True(File.Exists(Path.Combine(directory, "checkpoint-photos", "reference.gtphoto")));
        Assert.Equal(coordinator.SessionId, Assert.Single(await store.ListSessionsAsync(Token)).SessionId);
        var restored = await store.RestoreAsync(coordinator.SessionId,
            TestManifest.Manifest, TestManifest.Catalog, Token);
        Assert.Equal(expectedHash, StateHash.Compute(restored.State));
    }

    [Fact(Skip = "This check requires Windows file-sharing semantics.", SkipUnless = nameof(IsWindows))]
    public async Task AFileDeletionFailureIsReportedAndCanBeRetriedAfterTheFileIsReleased()
    {
        var store = new SqliteSessionStore(_root);
        var sessionId = SessionId.New();
        var directory = store.SessionDirectory(sessionId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "locked-photo.gtphoto");
        await File.WriteAllTextAsync(path, "photo", Token);
        var retained = await CreateAsync(store);

        using (var heldFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => store.DeleteSessionAsync(sessionId, Token));
            Assert.True(Directory.Exists(directory));
            Assert.True(File.Exists(path));
        }

        await store.DeleteSessionAsync(sessionId, Token);

        Assert.False(Directory.Exists(directory));
        Assert.Equal(retained.SessionId, Assert.Single(await store.ListSessionsAsync(Token)).SessionId);
        await store.RestoreAsync(retained.SessionId, TestManifest.Manifest, TestManifest.Catalog, Token);
    }

    private static Task<GameCoordinator> CreateAsync(SqliteSessionStore store)
    {
        var setup = new SessionSetup(SessionId.New(),
            [new(new SeatId(1), "First", PlayerColor.Red, SeatKind.Human, AiDifficulty.Standard),
             new(new SeatId(2), "Second", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard)],
            new SeatId(1), VerificationMode.Manual);
        return GameCoordinator.CreateAsync(new GameRules(TestManifest.Manifest, TestManifest.Catalog),
            store, setup, DeterministicRandom.SeedFrom(912), Token);
    }

    private static void WriteAttachments(string directory)
    {
        var photos = Directory.CreateDirectory(Path.Combine(directory, "checkpoint-photos"));
        File.WriteAllText(Path.Combine(photos.FullName, "reference.gtphoto"), "saved board photo");
        var nested = Directory.CreateDirectory(Path.Combine(directory, "exports", "board"));
        File.WriteAllText(Path.Combine(nested.FullName, "alignment.json"), "{}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
