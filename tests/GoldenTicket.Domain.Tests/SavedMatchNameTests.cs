using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Persistence;
using Microsoft.Data.Sqlite;

namespace GoldenTicket.Domain.Tests;

public sealed class SavedMatchNameTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "GoldenTicket.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveNameSurvivesReopeningResumingAndSavingAgain(bool useSqlite)
    {
        var token = TestContext.Current.CancellationToken;
        ISessionStore store = useSqlite ? new SqliteSessionStore(_root) : new InMemorySessionStore();
        var coordinator = await CreateActiveAsync(store);

        var saved = await coordinator.SaveAndPackAwayAsync("  test2  ", token);
        Assert.True(saved.SafeToPack, saved.Problem);

        // A fresh SQLite store has no coordinator cache to supply the display name.
        store = useSqlite ? new SqliteSessionStore(_root) : store;
        var summary = Assert.Single(await store.ListSessionsAsync(token));
        Assert.Equal("test2", summary.LatestCheckpointName);
        Assert.Equal(SessionLifecycle.PackedAway, summary.Lifecycle);

        coordinator = await GameCoordinator.RestoreAsync(Rules(), store, coordinator.SessionId, token);
        await RebuildAndResumeAsync(coordinator);
        Assert.Null(coordinator.Public.Checkpoint);

        summary = Assert.Single(await store.ListSessionsAsync(token));
        Assert.Equal("test2", summary.LatestCheckpointName);
        Assert.Equal(SessionLifecycle.Active, summary.Lifecycle);

        saved = await coordinator.SaveAndPackAwayAsync("Friday's game — next evening", token);
        Assert.True(saved.SafeToPack, saved.Problem);
        store = useSqlite ? new SqliteSessionStore(_root) : store;
        summary = Assert.Single(await store.ListSessionsAsync(token));
        Assert.Equal("Friday's game — next evening", summary.LatestCheckpointName);
        Assert.Equal(coordinator.SessionId, summary.SessionId);
        Assert.Null(summary.UnavailableReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAutomaticallySavedMatchHasNoInventedCheckpointName(bool useSqlite)
    {
        var token = TestContext.Current.CancellationToken;
        ISessionStore store = useSqlite ? new SqliteSessionStore(_root) : new InMemorySessionStore();
        var coordinator = await CreateActiveAsync(store);

        var summary = Assert.Single(await store.ListSessionsAsync(token));

        Assert.Equal(coordinator.SessionId, summary.SessionId);
        Assert.Null(summary.LatestCheckpointName);
        Assert.Null(summary.UnavailableReason);
        Assert.Equal(SessionLifecycle.Active, summary.Lifecycle);
    }

    [Fact]
    public async Task LatestNameUsesStateOrderEvenWhenCheckpointTimestampsGoBackwards()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new SqliteSessionStore(_root);
        var coordinator = await CreateActiveAsync(store);
        var first = await coordinator.SaveAndPackAwayAsync("First save", token);
        Assert.True(first.SafeToPack, first.Problem);
        await RebuildAndResumeAsync(coordinator);
        var second = await coordinator.SaveAndPackAwayAsync("Second save", token);
        Assert.True(second.SafeToPack, second.Problem);

        await using var connection = await OpenDatabaseAsync(store, coordinator.SessionId);
        await using var command = connection.CreateCommand();
        // Change only the synthetic listing metadata. It is intentionally not used to restore.
        command.CommandText = """
            UPDATE PackAwayCheckpoint
            SET CreatedAt = CASE WHEN CheckpointId = $first THEN $later ELSE $earlier END;
            """;
        command.Parameters.AddWithValue("$first", first.Checkpoint!.CheckpointId.Value);
        command.Parameters.AddWithValue("$later", "2040-01-01T00:00:00.0000000+00:00");
        command.Parameters.AddWithValue("$earlier", "2000-01-01T00:00:00.0000000+00:00");
        await command.ExecuteNonQueryAsync(token);

        var summary = Assert.Single(await new SqliteSessionStore(_root).ListSessionsAsync(token));
        Assert.Equal("Second save", summary.LatestCheckpointName);
    }

    [Fact]
    public async Task NameLookupCannotChooseACheckpointBelongingToAnotherSession()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new SqliteSessionStore(_root);
        var coordinator = await CreateActiveAsync(store);
        var saved = await coordinator.SaveAndPackAwayAsync("This match", token);
        Assert.True(saved.SafeToPack, saved.Problem);

        await using var connection = await OpenDatabaseAsync(store, coordinator.SessionId);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PackAwayCheckpoint (
                SessionId, CheckpointId, Name, CreatedAt, FormatVersion, SourceStateVersion,
                SourceJournalSeq, TargetProvenance, PhotoHash, Status, Payload)
            SELECT $otherSession, CheckpointId, $otherName, CreatedAt, FormatVersion,
                SourceStateVersion + 100, SourceJournalSeq, TargetProvenance, PhotoHash,
                Status, Payload
            FROM PackAwayCheckpoint WHERE SessionId = $sessionId;
            """;
        command.Parameters.AddWithValue("$otherSession", SessionId.New().Value);
        command.Parameters.AddWithValue("$otherName", "An unrelated match");
        command.Parameters.AddWithValue("$sessionId", coordinator.SessionId.Value);
        await command.ExecuteNonQueryAsync(token);

        var summary = Assert.Single(await new SqliteSessionStore(_root).ListSessionsAsync(token));
        Assert.Equal("This match", summary.LatestCheckpointName);
    }

    [Fact]
    public async Task UnsupportedSchemaIsOmittedFromSavedMatchListWithoutSchemaChanges()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new SqliteSessionStore(_root);
        var coordinator = await CreateActiveAsync(store);

        await using var connection = await OpenDatabaseAsync(store, coordinator.SessionId);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Session SET StoreSchemaVersion = 1;
            DROP TABLE PackAwayCheckpoint;
            """;
        await command.ExecuteNonQueryAsync(token);

        Assert.Empty(await new SqliteSessionStore(_root).ListSessionsAsync(token));

        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'PackAwayCheckpoint';";
        Assert.Equal(0L, await command.ExecuteScalarAsync(token));
        command.CommandText = "SELECT StoreSchemaVersion FROM Session;";
        Assert.Equal(1L, await command.ExecuteScalarAsync(token));
    }

    [Fact]
    public void SavedMatchDescriptionLeadsWithEnteredNameAndSeparatelyLabelsStatus()
    {
        var model = new SetupViewModel(TestManifest.Manifest);
        model.LoadSavedSessions([Summary(SessionLifecycle.PackedAway, "test2")]);

        var row = Assert.Single(model.SavedSessions);
        Assert.StartsWith("test2", row.Description);
        Assert.Contains("Packed away", row.Description);
        Assert.DoesNotContain("PackedAway", row.Description);
        Assert.Contains("Alex, Conductor", row.Description);
        Assert.Same(row, model.SelectedSavedSession);
    }

    [Fact]
    public void AnUnnamedSavedMatchStillShowsItsTimeTurnPlayersAndReadableStatus()
    {
        var model = new SetupViewModel(TestManifest.Manifest);
        var summary = Summary(SessionLifecycle.PreparingPackAway, null);
        model.LoadSavedSessions([summary]);

        var row = Assert.Single(model.SavedSessions);
        Assert.Contains(summary.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), row.Description);
        Assert.Contains("turn 7", row.Description);
        Assert.Contains("Alex, Conductor", row.Description);
        Assert.DoesNotContain("PreparingPackAway", row.Description);
        Assert.Same(row, model.SelectedSavedSession);
    }

    private static SessionSummary Summary(SessionLifecycle lifecycle, string? name) => new(
        SessionId.New(), "test", DateTimeOffset.Parse("2026-09-12T12:00:00Z"),
        DateTimeOffset.Parse("2026-09-12T15:48:00Z"), lifecycle, 7, ["Alex", "Conductor"],
        LatestCheckpointName: name);

    private static GameRules Rules() => new(TestManifest.Manifest, TestManifest.Catalog);

    private static async Task<GameCoordinator> CreateActiveAsync(ISessionStore store)
    {
        var token = TestContext.Current.CancellationToken;
        var setup = new SessionSetup(SessionId.New(),
            [new(new SeatId(1), "Alex", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard),
             new(new SeatId(2), "Conductor", PlayerColor.Red, SeatKind.Human, AiDifficulty.Standard)],
            new SeatId(1), VerificationMode.Manual);
        var coordinator = await GameCoordinator.CreateAsync(
            Rules(), store, setup, DeterministicRandom.SeedFrom(2026), token);
        foreach (var seat in setup.Seats)
        {
            var view = await coordinator.GetSeatViewAsync(seat.SeatId, token);
            var outcome = await coordinator.SubmitAsync(new CommitTicketSelection(
                coordinator.NewEnvelope(seat.SeatId), [.. view.SetupOffer.Take(2)], []), token);
            Assert.True(outcome.IsAccepted, outcome.Result.Rejection?.Message);
        }

        Assert.Equal(SessionLifecycle.Active, coordinator.Public.Lifecycle);
        return coordinator;
    }

    private static async Task RebuildAndResumeAsync(GameCoordinator coordinator)
    {
        var token = TestContext.Current.CancellationToken;
        var checkpoint = Assert.IsType<GoldenTicket.Domain.Projections.PublicCheckpoint>(coordinator.Public.Checkpoint);
        Assert.True((await coordinator.SubmitAsync(new BeginBoardRebuild(
            coordinator.NewEnvelope(), checkpoint.CheckpointId), token)).IsAccepted);
        Assert.True((await coordinator.SubmitAsync(new AttestBoardRebuild(
            coordinator.NewEnvelope(), checkpoint.CheckpointId, checkpoint.PhysicalTargetHash, "tester"), token)).IsAccepted);
        Assert.True((await coordinator.SubmitAsync(new ResumePackedGame(
            coordinator.NewEnvelope(), checkpoint.CheckpointId), token)).IsAccepted);
    }

    private static async Task<SqliteConnection> OpenDatabaseAsync(SqliteSessionStore store, SessionId sessionId)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = store.DatabasePath(sessionId), Mode = SqliteOpenMode.ReadWrite,
        }.ToString());
        try
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
