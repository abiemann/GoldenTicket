using System.Collections.Immutable;
using GoldenTicket.AI;
using GoldenTicket.Application;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Persistence;

namespace GoldenTicket.Domain.Tests;

/// <summary>
/// Durability and idempotency (DESIGN 8.1, 19.3, 19.4). These run against the real SQLite store, so
/// they exercise readable storage, the hash chain and restore verification rather than a stub.
/// </summary>
public class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "GoldenTicket.Tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A held file handle on a temporary directory must not fail the test run.
        }
    }

    private (GameRules Rules, SqliteSessionStore Store) Build() =>
        (new GameRules(TestManifest.Manifest, TestManifest.Catalog), new SqliteSessionStore(_root));

    private static SessionSetup Setup(int seatCount = 3)
    {
        var colors = Enum.GetValues<PlayerColor>();
        var seats = Enumerable.Range(0, seatCount)
            .Select(index => new Seat(
                new SeatId(index + 1), $"Seat {index + 1}", colors[index],
                index == 0 ? SeatKind.Human : SeatKind.Computer, AiDifficulty.Standard))
            .ToImmutableArray();

        return new SessionSetup(SessionId.New(), seats, seats[0].SeatId, VerificationMode.Manual);
    }

    [Fact]
    public async Task AMatchSurvivesBeingClosedAndReopened()
    {
        var (rules, store) = Build();

        var coordinator = await GameCoordinator.CreateAsync(
            rules, store, Setup(), DeterministicRandom.SeedFrom(123));

        var driver = new ComputerSeatDriver(coordinator, new HeuristicAiPolicy(), aiSeed: 5);
        await driver.AdvanceAsync();

        var beforeHash = await coordinator.ComputeStateHashAsync();
        var beforeVersion = coordinator.Public.StateVersion;

        // Reopen from disk exactly as a restart would.
        var reopened = await GameCoordinator.RestoreAsync(rules, store, coordinator.SessionId);

        Assert.Equal(beforeHash, await reopened.ComputeStateHashAsync());
        Assert.Equal(beforeVersion, reopened.Public.StateVersion);
        Assert.Empty(await reopened.CheckInvariantsAsync());
    }

    [Fact]
    public async Task RepeatingACommandIdReturnsTheFirstOutcomeWithoutApplyingItTwice()
    {
        var (rules, store) = Build();

        var coordinator = await GameCoordinator.CreateAsync(
            rules, store, Setup(2), DeterministicRandom.SeedFrom(321));

        // Seat 1 is human; commit its opening tickets twice with the same command id.
        var seat = new SeatId(1);
        var view = await coordinator.GetSeatViewAsync(seat);
        var commandId = CommandId.New();

        var envelope = new CommandEnvelope(
            coordinator.SessionId, commandId, coordinator.Public.StateVersion, seat);

        var command = new CommitTicketSelection(envelope, [.. view.SetupOffer.Take(2)], []);

        var first = await coordinator.SubmitAsync(command);
        Assert.True(first.IsAccepted);
        Assert.False(first.WasDuplicate);

        var versionAfterFirst = coordinator.Public.StateVersion;
        var hashAfterFirst = await coordinator.ComputeStateHashAsync();

        var second = await coordinator.SubmitAsync(command);

        Assert.True(second.WasDuplicate);
        Assert.Equal(versionAfterFirst, coordinator.Public.StateVersion);
        Assert.Equal(hashAfterFirst, await coordinator.ComputeStateHashAsync());
        Assert.Equal(2, coordinator.Public.SeatOf(seat).TicketCount);
    }

    [Fact]
    public async Task ARejectedCommandIsRememberedSoARetryGetsTheSameAnswer()
    {
        var (rules, store) = Build();

        var coordinator = await GameCoordinator.CreateAsync(
            rules, store, Setup(2), DeterministicRandom.SeedFrom(77));

        var seat = new SeatId(1);
        var commandId = CommandId.New();
        var envelope = new CommandEnvelope(coordinator.SessionId, commandId, coordinator.Public.StateVersion, seat);

        // Keeping one ticket is below the opening minimum of two.
        var view = await coordinator.GetSeatViewAsync(seat);
        var command = new CommitTicketSelection(envelope, [view.SetupOffer[0]], []);

        var first = await coordinator.SubmitAsync(command);
        Assert.False(first.IsAccepted);
        Assert.Equal("TooFewTicketsKept", first.Result.Rejection!.Code);

        var second = await coordinator.SubmitAsync(command);
        Assert.True(second.WasDuplicate);
        Assert.False(second.IsAccepted);
        Assert.Equal("TooFewTicketsKept", second.Result.Rejection!.Code);
    }

    [Fact]
    public async Task ASavedMatchIsListedWithoutRevealingAnything()
    {
        var (rules, store) = Build();
        var setup = Setup(3);

        await GameCoordinator.CreateAsync(rules, store, setup, DeterministicRandom.SeedFrom(9));

        var sessions = await store.ListSessionsAsync(CancellationToken.None);
        var summary = Assert.Single(sessions);

        Assert.Equal(setup.SessionId, summary.SessionId);
        Assert.Equal(TestManifest.Manifest.ProfileId, summary.ProfileId);
        Assert.Equal(SessionLifecycle.Setup, summary.Lifecycle);
        Assert.Equal(3, summary.SeatNames.Count);
    }

    [Fact]
    public async Task PrivatePayloadsAreReadableInTheDatabaseAndStillRestore()
    {
        var (rules, store) = Build();

        var coordinator = await GameCoordinator.CreateAsync(
            rules, store, Setup(2), DeterministicRandom.SeedFrom(4242));

        var seat = new SeatId(1);
        var view = await coordinator.GetSeatViewAsync(seat);
        var ticket = view.SetupOffer[0];

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={store.DatabasePath(coordinator.SessionId)}");
        await connection.OpenAsync();
        await using (var sessionCommand = connection.CreateCommand())
        {
            sessionCommand.CommandText = "SELECT StoreSchemaVersion FROM Session;";
            Assert.Equal(SqliteSessionStore.StoreSchemaVersion,
                Convert.ToInt32(await sessionCommand.ExecuteScalarAsync()));
        }
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM Event WHERE Visibility = 'Referee';";
        await using var reader = await command.ExecuteReaderAsync();
        var payloads = new List<string>();
        while (await reader.ReadAsync())
        {
            payloads.Add(System.Text.Encoding.UTF8.GetString((byte[])reader["Payload"]));
        }

        // The local save is intentionally readable, including the referee's ticket deck.
        Assert.Contains(payloads, payload => payload.Contains(ticket.Value, StringComparison.Ordinal));
        var reopened = await GameCoordinator.RestoreAsync(rules, new SqliteSessionStore(_root), coordinator.SessionId);
        Assert.Equal(await coordinator.ComputeStateHashAsync(), await reopened.ComputeStateHashAsync());
    }

    [Fact]
    public async Task ATamperedJournalRowIsDetectedOnRestore()
    {
        var (rules, store) = Build();

        var coordinator = await GameCoordinator.CreateAsync(
            rules, store, Setup(2), DeterministicRandom.SeedFrom(31));

        var sessionId = coordinator.SessionId;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={store.DatabasePath(sessionId)}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();

            // Corrupt the newest payload; the chain hash no longer matches it.
            command.CommandText =
                """
                UPDATE Event SET Payload = X'7B7D'
                WHERE SessionId = $id
                  AND Sequence = (SELECT MAX(Sequence) FROM Event WHERE SessionId = $id);
                """;
            command.Parameters.AddWithValue("$id", sessionId.Value);
            await command.ExecuteNonQueryAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        await Assert.ThrowsAsync<SessionIntegrityException>(
            () => GameCoordinator.RestoreAsync(rules, store, sessionId));
    }
}
