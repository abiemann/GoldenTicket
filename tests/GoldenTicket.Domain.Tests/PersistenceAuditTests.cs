using GoldenTicket.Application;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Persistence;
using Microsoft.Data.Sqlite;

namespace GoldenTicket.Domain.Tests;

public sealed class PersistenceAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GoldenTicket.Tests", Guid.NewGuid().ToString("n"));

    private static SessionSetup Setup() => new(
        SessionId.New(),
        [new(new SeatId(1), "First", PlayerColor.Red, SeatKind.Human, AiDifficulty.Standard),
         new(new SeatId(2), "Second", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard)],
        new SeatId(1), VerificationMode.Manual);

    private async Task<(GameRules Rules, SqliteSessionStore Store, GameCoordinator Coordinator)> CreateAsync()
    {
        var rules = new GameRules(TestManifest.Manifest, TestManifest.Catalog);
        var store = new SqliteSessionStore(_root);
        var coordinator = await GameCoordinator.CreateAsync(rules, store, Setup(), DeterministicRandom.SeedFrom(42));
        return (rules, store, coordinator);
    }

    private static async Task<SubmitOutcome> SelectOpeningTicketsAsync(GameCoordinator coordinator)
    {
        var seat = new SeatId(1);
        var view = await coordinator.GetSeatViewAsync(seat);
        return await coordinator.SubmitAsync(new CommitTicketSelection(
            coordinator.NewEnvelope(seat), [.. view.SetupOffer.Take(2)], []));
    }

    private static async Task MutateAsync(SqliteSessionStore store, SessionId sessionId, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = store.DatabasePath(sessionId), Mode = SqliteOpenMode.ReadWrite,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../../victim")]
    [InlineData("..\\..\\victim")]
    [InlineData("C:\\Windows")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    public async Task UntrustedSessionIdsCannotReachTheFilesystem(string value)
    {
        var store = new SqliteSessionStore(_root);
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteSessionAsync(new SessionId(value), CancellationToken.None));
        Assert.Throws<ArgumentException>(() => store.DatabasePath(new SessionId(value)));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task LookingUpAMissingMatchDoesNotCreateAnEmptyDatabase()
    {
        var store = new SqliteSessionStore(_root);
        var sessionId = SessionId.New();
        Directory.CreateDirectory(store.SessionDirectory(sessionId));

        await Assert.ThrowsAsync<SqliteException>(() => store.FindCommandOutcomeAsync(sessionId, CommandId.New(), CancellationToken.None));

        Assert.False(File.Exists(store.DatabasePath(sessionId)));
    }

    [Theory]
    [InlineData("DELETE FROM Session;")]
    [InlineData("UPDATE Session SET Lifecycle = 'unknown';")]
    [InlineData("UPDATE Session SET Lifecycle = '999';")]
    [InlineData("UPDATE Session SET UpdatedAt = 'invalid';")]
    [InlineData("DROP TABLE Session;")]
    public async Task CorruptMetadataStaysVisibleAsAnUnavailableSave(string mutation)
    {
        var (_, store, coordinator) = await CreateAsync();
        await MutateAsync(store, coordinator.SessionId, mutation);

        var summary = Assert.Single(await store.ListSessionsAsync(CancellationToken.None));

        Assert.Equal(coordinator.SessionId, summary.SessionId);
        Assert.NotNull(summary.UnavailableReason);
        Assert.Empty(summary.SeatNames);
        Assert.Equal(DateTimeOffset.UnixEpoch, summary.UpdatedAt);
        Assert.DoesNotContain(mutation, summary.UnavailableReason);
        Assert.True(File.Exists(store.DatabasePath(coordinator.SessionId)));
    }

    [Fact]
    public async Task DamagedDatabaseStaysVisibleButUnrelatedDirectoriesAreIgnored()
    {
        var store = new SqliteSessionStore(_root);
        var sessionId = SessionId.New();
        Directory.CreateDirectory(store.SessionDirectory(sessionId));
        var damagedBytes = "This is not a SQLite database."u8.ToArray();
        await File.WriteAllBytesAsync(store.DatabasePath(sessionId), damagedBytes);
        Directory.CreateDirectory(Path.Combine(_root, "sessions", "unrelated"));

        var summary = Assert.Single(await store.ListSessionsAsync(CancellationToken.None));

        Assert.Equal(sessionId, summary.SessionId);
        Assert.NotNull(summary.UnavailableReason);
        Assert.Equal(damagedBytes, await File.ReadAllBytesAsync(store.DatabasePath(sessionId)));
    }

    [Fact]
    public async Task DuplicateCreateCannotReplaceAnExistingSessionsEncryptionKey()
    {
        var rules = new GameRules(TestManifest.Manifest, TestManifest.Catalog);
        var store = new SqliteSessionStore(_root);
        var setup = Setup();
        var original = await GameCoordinator.CreateAsync(rules, store, setup, DeterministicRandom.SeedFrom(42));
        var expectedHash = await original.ComputeStateHashAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GameCoordinator.CreateAsync(rules, store, setup, DeterministicRandom.SeedFrom(123)));

        var restored = await GameCoordinator.RestoreAsync(rules, store, setup.SessionId);
        Assert.Equal(expectedHash, await restored.ComputeStateHashAsync());
        Assert.True((await SelectOpeningTicketsAsync(original)).IsAccepted);
        restored = await GameCoordinator.RestoreAsync(rules, new SqliteSessionStore(_root), setup.SessionId);
        Assert.Equal(await original.ComputeStateHashAsync(), await restored.ComputeStateHashAsync());
    }

    [Theory]
    [InlineData("DELETE FROM Snapshot;")]
    [InlineData("UPDATE Snapshot SET JournalSequence = JournalSequence + 7;")]
    [InlineData("UPDATE Snapshot SET BoardRevision = BoardRevision + 1;")]
    [InlineData("UPDATE Snapshot SET StateVersion = StateVersion + 7;")]
    [InlineData("UPDATE Event SET Sequence = Sequence + 100;")]
    [InlineData("UPDATE Event SET StateVersion = StateVersion + 100;")]
    [InlineData("UPDATE Event SET SchemaVersion = 999;")]
    [InlineData("UPDATE Event SET Visibility = 'Public';")]
    [InlineData("UPDATE Event SET Nonce = zeroblob(12) WHERE Encrypted = 1;")]
    [InlineData("UPDATE Event SET Encrypted = 2;")]
    [InlineData("UPDATE Session SET StoreSchemaVersion = 0;")]
    [InlineData("UPDATE Session SET StoreSchemaVersion = 999;")]
    [InlineData("UPDATE Session SET EncryptionVersion = 999;")]
    [InlineData("UPDATE Session SET RulesPolicyVersion = 999;")]
    [InlineData("UPDATE Session SET ProtectedDataKey = X'00';")]
    public async Task MissingOrCorruptSaveMetadataStopsRestore(string mutation)
    {
        var (rules, store, coordinator) = await CreateAsync();
        await MutateAsync(store, coordinator.SessionId, mutation);

        await Assert.ThrowsAsync<SessionIntegrityException>(() =>
            GameCoordinator.RestoreAsync(rules, store, coordinator.SessionId));
    }

    [Fact]
    public async Task ASecondCoordinatorCannotOverwriteAnAlreadyAdvancedSave()
    {
        var (rules, store, first) = await CreateAsync();
        var second = await GameCoordinator.RestoreAsync(rules, new SqliteSessionStore(_root), first.SessionId);

        Assert.True((await SelectOpeningTicketsAsync(first)).IsAccepted);
        var expectedHash = await first.ComputeStateHashAsync();

        await Assert.ThrowsAsync<SessionIntegrityException>(() => SelectOpeningTicketsAsync(second));

        var restored = await GameCoordinator.RestoreAsync(rules, new SqliteSessionStore(_root), first.SessionId);
        Assert.Equal(expectedHash, await restored.ComputeStateHashAsync());
        Assert.Empty(await restored.CheckInvariantsAsync());
    }

    [Fact]
    public async Task FailedRestoreDoesNotExposePrivateTicketsInItsError()
    {
        var (rules, store, coordinator) = await CreateAsync();
        var restored = await store.RestoreAsync(coordinator.SessionId, rules.Manifest, rules.Catalog, CancellationToken.None);
        var secretTicket = restored.State.SetupOffers[new SeatId(2)][0];
        // Simulate a past engine bug whose persisted state and hash agree, but violate conservation.
        var invalid = new Transition([new TicketSelectionCommitted(new SeatId(1), [secretTicket], [], true)]);
        GameReducer.ApplyTransition(restored.State, invalid.Events);
        await store.CommitAsync(restored.State,
            new StoredCommandOutcome(CommandId.New(), true, restored.State.StateVersion, null, null),
            invalid, StateHash.Compute(restored.State), CancellationToken.None);

        var error = await Assert.ThrowsAsync<SessionIntegrityException>(() =>
            GameCoordinator.RestoreAsync(rules, store, coordinator.SessionId));

        Assert.Contains("integrity checks", error.Message);
        Assert.DoesNotContain(secretTicket.Value, error.Message);
        Assert.DoesNotContain("seat 2", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NewSnapshotsStoreTheSameEventCountAsTheAuthoritativeState()
    {
        var (rules, store, coordinator) = await CreateAsync();
        Assert.True((await SelectOpeningTicketsAsync(coordinator)).IsAccepted);
        var restored = await store.RestoreAsync(coordinator.SessionId, rules.Manifest, rules.Catalog, CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath(coordinator.SessionId)}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT JournalSequence FROM Snapshot ORDER BY StateVersion DESC LIMIT 1;";
        var sequence = (long)(await command.ExecuteScalarAsync())!;

        Assert.Equal(restored.State.JournalSequence, sequence);
        Assert.Equal(restored.Journal.Count, sequence);
    }

    [Fact]
    public async Task LegacySnapshotEncodingAndHashRestoreThenUpgradeOnTheNextCommit()
    {
        var (rules, store, coordinator) = await CreateAsync();
        var restored = await store.RestoreAsync(coordinator.SessionId, rules.Manifest, rules.Catalog, CancellationToken.None);
        var legacyHash = StateHash.ComputeLegacy(restored.State);
        await MutateAsync(store, coordinator.SessionId,
            $"UPDATE Session SET StoreSchemaVersion = 1; UPDATE Snapshot SET JournalSequence = JournalSequence - 1, StateHash = '{legacyHash}';");

        var reopened = await GameCoordinator.RestoreAsync(rules, new SqliteSessionStore(_root), coordinator.SessionId);
        Assert.Equal(await coordinator.ComputeStateHashAsync(), await reopened.ComputeStateHashAsync());
        Assert.True((await SelectOpeningTicketsAsync(reopened)).IsAccepted);

        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath(coordinator.SessionId)}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT StoreSchemaVersion FROM Session;";
        Assert.Equal(SqliteSessionStore.StoreSchemaVersion, Convert.ToInt32(await command.ExecuteScalarAsync()));
        var upgraded = await store.RestoreAsync(coordinator.SessionId, rules.Manifest, rules.Catalog, CancellationToken.None);
        Assert.Equal(await reopened.ComputeStateHashAsync(), StateHash.Compute(upgraded.State));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
