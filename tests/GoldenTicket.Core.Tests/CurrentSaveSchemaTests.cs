using GoldenTicket.Application;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Persistence;
using Microsoft.Data.Sqlite;

namespace GoldenTicket.Domain.Tests;

public sealed class CurrentSaveSchemaTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "GoldenTicket.Tests", Guid.NewGuid().ToString("n"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GameRules Rules() => new(TestManifest.Manifest, TestManifest.Catalog);

    private async Task<(SqliteSessionStore Store, GameCoordinator Coordinator)> CreateAsync()
    {
        var store = new SqliteSessionStore(_root);
        var setup = new SessionSetup(SessionId.New(),
            [new(new SeatId(1), "First", PlayerColor.Red, SeatKind.Human, AiDifficulty.Standard),
             new(new SeatId(2), "Second", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard)],
            new SeatId(1), VerificationMode.Manual);
        var coordinator = await GameCoordinator.CreateAsync(
            Rules(), store, setup, DeterministicRandom.SeedFrom(2026), Token);
        return (store, coordinator);
    }

    [Fact]
    public async Task NewSaveUsesTheCurrentPlaintextOnlySchema()
    {
        var (store, coordinator) = await CreateAsync();
        await using var connection = await OpenAsync(store.DatabasePath(coordinator.SessionId));
        Assert.Equal((long)SqliteSessionStore.StoreSchemaVersion,
            await ScalarAsync(connection, "SELECT StoreSchemaVersion FROM Session;"));

        foreach (var table in new[] { "Session", "Event", "PackAwayCheckpoint" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await command.ExecuteReaderAsync(Token);
            var columns = new List<string>();
            while (await reader.ReadAsync(Token)) columns.Add(reader.GetString(1));
            if (table is "Event" or "PackAwayCheckpoint") Assert.Contains("Payload", columns);
            Assert.DoesNotContain(columns, column =>
                column is "EncryptionVersion" or "ProtectedDataKey" or "Encrypted" or "Nonce");
        }

        var restored = await GameCoordinator.RestoreAsync(
            Rules(), new SqliteSessionStore(_root), coordinator.SessionId, Token);
        Assert.Equal(await coordinator.ComputeStateHashAsync(Token),
            await restored.ComputeStateHashAsync(Token));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task UnsupportedSaveSchemaIsRejectedWithoutMigrationOrBackup(int version)
    {
        var (store, coordinator) = await CreateAsync();
        var databasePath = store.DatabasePath(coordinator.SessionId);
        await using var connection = await OpenAsync(databasePath);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE Session SET StoreSchemaVersion = $version;";
            command.Parameters.AddWithValue("$version", version);
            await command.ExecuteNonQueryAsync(Token);
        }

        await Assert.ThrowsAsync<SessionIntegrityException>(() => GameCoordinator.RestoreAsync(
            Rules(), new SqliteSessionStore(_root), coordinator.SessionId, Token));
        Assert.Empty(await new SqliteSessionStore(_root).ListSessionsAsync(Token));
        Assert.Equal((long)version, await ScalarAsync(connection, "SELECT StoreSchemaVersion FROM Session;"));
        Assert.True(File.Exists(databasePath));
        Assert.False(Directory.Exists(Path.Combine(store.SessionDirectory(coordinator.SessionId), "backups")));
    }

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        await connection.OpenAsync(Token);
        return connection;
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(Token));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
