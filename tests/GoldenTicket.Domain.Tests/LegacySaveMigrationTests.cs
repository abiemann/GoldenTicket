using GoldenTicket.Application;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Persistence;
using Microsoft.Data.Sqlite;

namespace GoldenTicket.Domain.Tests;

public sealed class LegacySaveMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "GoldenTicket.Tests", Guid.NewGuid().ToString("n"));

    private static GameRules Rules() => new(TestManifest.Manifest, TestManifest.Catalog);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APreCheckpointSaveCanBePackedAwayAfterRestoring(bool preparationWasInterrupted)
    {
        var store = new SqliteSessionStore(_root);
        var original = await CreateActiveAsync(store);
        if (preparationWasInterrupted)
        {
            Assert.True((await original.SubmitAsync(
                new SaveAndPackAway(original.NewEnvelope(), "Interrupted save"), Token)).IsAccepted);
        }

        await using var connection = await OpenAsync(store.DatabasePath(original.SessionId));
        await MakeLegacyAsync(connection, version: 2, removeCheckpointTable: true);
        var beforeHash = await original.ComputeStateHashAsync(Token);

        var restored = await GameCoordinator.RestoreAsync(
            Rules(), new SqliteSessionStore(_root), original.SessionId, Token);

        Assert.Equal(beforeHash, await restored.ComputeStateHashAsync(Token));
        await AssertUpgradedAsync(connection);
        var result = preparationWasInterrupted
            ? await restored.ContinuePackAwayAsync(Token)
            : await restored.SaveAndPackAwayAsync("Migrated save", Token);
        Assert.True(result.SafeToPack, result.Problem);

        var reopened = await GameCoordinator.RestoreAsync(
            Rules(), new SqliteSessionStore(_root), original.SessionId, Token);
        Assert.Equal(SessionLifecycle.PackedAway, reopened.Public.Lifecycle);
        Assert.Equal(CheckpointStatus.Verified, reopened.Public.Checkpoint!.Status);
        Assert.Equal(await restored.ComputeStateHashAsync(Token), await reopened.ComputeStateHashAsync(Token));
        Assert.Empty(await reopened.CheckInvariantsAsync(Token));
    }

    [Fact]
    public async Task TheMigrationBackupIncludesCommittedWalChangesAndIsNotRepeated()
    {
        var store = new SqliteSessionStore(_root);
        var original = await CreateActiveAsync(store);
        var databasePath = store.DatabasePath(original.SessionId);

        // Keep a connection open so these committed changes still live in the WAL at migration.
        await using var connection = await OpenAsync(databasePath);
        await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA wal_autocheckpoint=0;");
        var draw = await original.SubmitAsync(new SelectTrainCard(
            original.NewEnvelope(original.Public.ActiveSeatId), null), Token);
        Assert.True(draw.IsAccepted, draw.Result.Rejection?.Message);
        await MakeLegacyAsync(connection, version: 2, removeCheckpointTable: true);
        Assert.True(new FileInfo(databasePath + "-wal").Length > 0);

        var beforeHash = await original.ComputeStateHashAsync(Token);
        var eventCount = await ScalarAsync(connection, "SELECT COUNT(*) FROM Event;");
        await GameCoordinator.RestoreAsync(Rules(), new SqliteSessionStore(_root), original.SessionId, Token);

        var backupPath = Assert.Single(BackupFiles(store, original.SessionId));
        Assert.False(File.Exists(backupPath + "-wal"));
        await using (var backup = await OpenAsync(backupPath, readOnly: true))
        {
            Assert.Equal(2L, await ScalarAsync(backup, "SELECT StoreSchemaVersion FROM Session;"));
            Assert.Equal(0L, await ScalarAsync(backup, CheckpointTableCount));
            Assert.Equal(original.Public.StateVersion, await ScalarAsync(backup,
                "SELECT MAX(StateVersion) FROM Snapshot;"));
            Assert.Equal(eventCount, await ScalarAsync(backup, "SELECT COUNT(*) FROM Event;"));
            Assert.Equal(beforeHash, await ScalarAsync(backup,
                "SELECT StateHash FROM Snapshot ORDER BY StateVersion DESC LIMIT 1;"));
            Assert.Equal("ok", await ScalarAsync(backup, "PRAGMA integrity_check;"));
        }

        await GameCoordinator.RestoreAsync(Rules(), new SqliteSessionStore(_root), original.SessionId, Token);
        Assert.Equal(backupPath, Assert.Single(BackupFiles(store, original.SessionId)));
    }

    [Fact]
    public async Task ExistingSchemaTwoCheckpointRowsSurviveMigration()
    {
        var store = new SqliteSessionStore(_root);
        var original = await CreateActiveAsync(store);
        var saved = await original.SaveAndPackAwayAsync("Existing checkpoint", Token);
        Assert.True(saved.SafeToPack, saved.Problem);
        await using var connection = await OpenAsync(store.DatabasePath(original.SessionId));
        var payload = await ScalarAsync(connection, "SELECT hex(Payload) FROM PackAwayCheckpoint;");
        await MakeLegacyAsync(connection, version: 2, removeCheckpointTable: false);

        var restored = await GameCoordinator.RestoreAsync(
            Rules(), new SqliteSessionStore(_root), original.SessionId, Token);

        await AssertUpgradedAsync(connection);
        Assert.Equal(payload, await ScalarAsync(connection, "SELECT hex(Payload) FROM PackAwayCheckpoint;"));
        var reread = await restored.ContinuePackAwayAsync(Token);
        Assert.True(reread.SafeToPack, reread.Problem);
        Assert.Equal(saved.Checkpoint!.CheckpointId, reread.Checkpoint!.CheckpointId);
    }

    [Fact]
    public async Task SchemaOneSnapshotIsNormalizedBeforeAnotherRestoreOrWrite()
    {
        var store = new SqliteSessionStore(_root);
        var original = await CreateActiveAsync(store);
        var state = (await store.RestoreAsync(original.SessionId,
            TestManifest.Manifest, TestManifest.Catalog, Token)).State;
        await using var connection = await OpenAsync(store.DatabasePath(original.SessionId));
        await MakeLegacyAsync(connection, version: 1, removeCheckpointTable: true);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE Snapshot SET JournalSequence = JournalSequence - 1, StateHash = $legacyHash
                WHERE StateVersion = (SELECT MAX(StateVersion) FROM Snapshot);
                """;
            command.Parameters.AddWithValue("$legacyHash", StateHash.ComputeLegacy(state));
            await command.ExecuteNonQueryAsync(Token);
        }

        await GameCoordinator.RestoreAsync(Rules(), new SqliteSessionStore(_root), original.SessionId, Token);
        await AssertUpgradedAsync(connection);
        Assert.Equal(state.JournalSequence, await ScalarAsync(connection,
            "SELECT JournalSequence FROM Snapshot ORDER BY StateVersion DESC LIMIT 1;"));
        Assert.Equal(StateHash.Compute(state), await ScalarAsync(connection,
            "SELECT StateHash FROM Snapshot ORDER BY StateVersion DESC LIMIT 1;"));

        var reopened = await GameCoordinator.RestoreAsync(
            Rules(), new SqliteSessionStore(_root), original.SessionId, Token);
        var result = await reopened.SaveAndPackAwayAsync("Legacy schema one", Token);
        Assert.True(result.SafeToPack, result.Problem);
        Assert.Single(BackupFiles(store, original.SessionId));
    }

    [Fact]
    public async Task AnIntegrityFailureDoesNotMigrateOrBackUpTheSave()
    {
        var store = new SqliteSessionStore(_root);
        var original = await CreateActiveAsync(store);
        await using var connection = await OpenAsync(store.DatabasePath(original.SessionId));
        await MakeLegacyAsync(connection, version: 2, removeCheckpointTable: true);
        await ExecuteAsync(connection,
            "UPDATE Snapshot SET StateHash = 'tampered' WHERE StateVersion = (SELECT MAX(StateVersion) FROM Snapshot);");

        await Assert.ThrowsAsync<SessionIntegrityException>(() => GameCoordinator.RestoreAsync(
            Rules(), new SqliteSessionStore(_root), original.SessionId, Token));

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT StoreSchemaVersion FROM Session;"));
        Assert.Equal(0L, await ScalarAsync(connection, CheckpointTableCount));
        Assert.Empty(BackupFiles(store, original.SessionId));
        Assert.Equal("tampered", await ScalarAsync(connection,
            "SELECT StateHash FROM Snapshot ORDER BY StateVersion DESC LIMIT 1;"));
    }

    [Fact]
    public async Task ABackupFailureLeavesTheLegacySchemaUnchanged()
    {
        var store = new SqliteSessionStore(_root);
        var original = await CreateActiveAsync(store);
        await using var connection = await OpenAsync(store.DatabasePath(original.SessionId));
        await MakeLegacyAsync(connection, version: 2, removeCheckpointTable: true);
        var beforeHash = await original.ComputeStateHashAsync(Token);
        var backupDirectory = Path.Combine(store.SessionDirectory(original.SessionId), "backups");
        await File.WriteAllTextAsync(backupDirectory, "Existing file must be preserved.", Token);

        await Assert.ThrowsAnyAsync<IOException>(() => GameCoordinator.RestoreAsync(
            Rules(), new SqliteSessionStore(_root), original.SessionId, Token));

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT StoreSchemaVersion FROM Session;"));
        Assert.Equal(0L, await ScalarAsync(connection, CheckpointTableCount));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM MigrationHistory WHERE Version = 3;"));
        Assert.Equal(beforeHash, await ScalarAsync(connection,
            "SELECT StateHash FROM Snapshot ORDER BY StateVersion DESC LIMIT 1;"));
        Assert.Equal("Existing file must be preserved.", await File.ReadAllTextAsync(backupDirectory, Token));
    }

    [Fact]
    public async Task AFailureAfterCreatingTheCheckpointTableRollsBackTheWholeMigration()
    {
        var store = new SqliteSessionStore(_root);
        var original = await CreateActiveAsync(store);
        var beforeHash = await original.ComputeStateHashAsync(Token);
        await using var connection = await OpenAsync(store.DatabasePath(original.SessionId));
        await MakeLegacyAsync(connection, version: 2, removeCheckpointTable: true);
        await ExecuteAsync(connection, """
            CREATE TRIGGER RejectSchemaUpgrade BEFORE UPDATE OF StoreSchemaVersion ON Session
            BEGIN SELECT RAISE(ABORT, 'Simulated migration write failure'); END;
            """);

        await Assert.ThrowsAsync<SqliteException>(() => GameCoordinator.RestoreAsync(
            Rules(), new SqliteSessionStore(_root), original.SessionId, Token));

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT StoreSchemaVersion FROM Session;"));
        Assert.Equal(0L, await ScalarAsync(connection, CheckpointTableCount));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM MigrationHistory WHERE Version = 3;"));
        Assert.Equal(beforeHash, await ScalarAsync(connection,
            "SELECT StateHash FROM Snapshot ORDER BY StateVersion DESC LIMIT 1;"));
        await using (var backup = await OpenAsync(Assert.Single(BackupFiles(store, original.SessionId)), readOnly: true))
        {
            Assert.Equal(2L, await ScalarAsync(backup, "SELECT StoreSchemaVersion FROM Session;"));
            Assert.Equal(0L, await ScalarAsync(backup, CheckpointTableCount));
            Assert.Equal(beforeHash, await ScalarAsync(backup,
                "SELECT StateHash FROM Snapshot ORDER BY StateVersion DESC LIMIT 1;"));
            Assert.Equal("ok", await ScalarAsync(backup, "PRAGMA integrity_check;"));
        }
        // The failure is recoverable: removing the injected storage fault permits a clean retry.
        await ExecuteAsync(connection, "DROP TRIGGER RejectSchemaUpgrade;");
        var restored = await GameCoordinator.RestoreAsync(
            Rules(), new SqliteSessionStore(_root), original.SessionId, Token);
        Assert.Equal(await original.ComputeStateHashAsync(Token), await restored.ComputeStateHashAsync(Token));
        await AssertUpgradedAsync(connection);
    }

    private const string CheckpointTableCount =
        "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'PackAwayCheckpoint';";

    private static async Task AssertUpgradedAsync(SqliteConnection connection)
    {
        Assert.Equal(3L, await ScalarAsync(connection, "SELECT StoreSchemaVersion FROM Session;"));
        Assert.Equal(1L, await ScalarAsync(connection, CheckpointTableCount));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM MigrationHistory WHERE Version = 3;"));
    }

    private static string[] BackupFiles(SqliteSessionStore store, SessionId sessionId)
    {
        var directory = Path.Combine(store.SessionDirectory(sessionId), "backups");
        return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.db") : [];
    }

    private static async Task MakeLegacyAsync(SqliteConnection connection, int version, bool removeCheckpointTable)
    {
        await ExecuteAsync(connection, $"""
            UPDATE Session SET StoreSchemaVersion = {version};
            DELETE FROM MigrationHistory;
            INSERT INTO MigrationHistory (Version, AppliedAt) VALUES ({version}, '2026-09-01T00:00:00Z');
            """ + (removeCheckpointTable ? "DROP TABLE PackAwayCheckpoint;" : ""));
    }

    private static async Task<GameCoordinator> CreateActiveAsync(SqliteSessionStore store)
    {
        var setup = new SessionSetup(SessionId.New(),
            [new(new SeatId(1), "First", PlayerColor.Red, SeatKind.Human, AiDifficulty.Standard),
             new(new SeatId(2), "Second", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard)],
            new SeatId(1), VerificationMode.Manual);
        var coordinator = await GameCoordinator.CreateAsync(
            Rules(), store, setup, DeterministicRandom.SeedFrom(2026), Token);
        foreach (var seat in setup.Seats)
        {
            var view = await coordinator.GetSeatViewAsync(seat.SeatId, Token);
            var selected = await coordinator.SubmitAsync(new CommitTicketSelection(
                coordinator.NewEnvelope(seat.SeatId), [.. view.SetupOffer.Take(2)], []), Token);
            Assert.True(selected.IsAccepted, selected.Result.Rejection?.Message);
        }
        Assert.Equal(SessionLifecycle.Active, coordinator.Public.Lifecycle);
        return coordinator;
    }

    private static async Task<SqliteConnection> OpenAsync(string path, bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync(Token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Token);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
