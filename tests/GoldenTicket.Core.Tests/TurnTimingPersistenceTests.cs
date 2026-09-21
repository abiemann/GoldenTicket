using System.Text;
using System.Text.Json;
using GoldenTicket.Application;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Persistence;
using Microsoft.Data.Sqlite;

namespace GoldenTicket.Domain.Tests;

public sealed class TurnTimingPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GoldenTicket.Tests",
        Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timing_commits_and_marker_completion_preserve_gameplay_hashes(bool sqlite)
    {
        var fixture = await CreateAsync(sqlite);
        var turns = new List<TurnTimingEntry> { new(1, new SeatId(1), 175_000_000, false, true) };
        await fixture.SubmitAsync(new SaveAndPackAway(fixture.Envelope(), "Timing save"),
            new TurnTimingSnapshot(turns, AwaitingScoreMarker: true, GameElapsedTicks: 300_000_000));
        turns[0] = turns[0] with { ElapsedTicks = 1 };
        var restored = await fixture.RestoreAsync();
        var hash = StateHash.Compute(restored.State);
        var version = restored.State.StateVersion;
        var journalCount = restored.Journal.Count;
        Assert.Equal(175_000_000, Assert.Single(restored.Timing!.Turns).ElapsedTicks);
        Assert.Equal(300_000_000, restored.Timing.GameElapsedTicks);
        Assert.True(restored.Timing.AwaitingScoreMarker);
        Assert.True(restored.Timing.Turns[0].IsPartial);

        var completedTurns = new List<TurnTimingEntry>
            { restored.Timing.Turns[0] with { ElapsedTicks = 215_000_000, Completed = true } };
        await fixture.Store.SaveTurnTimingAsync(fixture.State.SessionId, version,
            new TurnTimingSnapshot(completedTurns, GameElapsedTicks: 400_000_000), Token);
        completedTurns.Clear();
        restored = await fixture.RestoreAsync();
        Assert.Equal(215_000_000, Assert.Single(restored.Timing!.Turns).ElapsedTicks);
        Assert.Equal(400_000_000, restored.Timing.GameElapsedTicks);
        Assert.True(restored.Timing.Turns[0].Completed);
        Assert.False(restored.Timing.AwaitingScoreMarker);
        Assert.Equal(hash, StateHash.Compute(restored.State));
        Assert.Equal(version, restored.State.StateVersion);
        Assert.Equal(journalCount, restored.Journal.Count);

        // A later command without timing must retain the latest earlier snapshot.
        await fixture.SubmitAsync(new CancelPackAwayPreparation(fixture.Envelope(), "continue"));
        Assert.Equal(215_000_000, Assert.Single((await fixture.RestoreAsync()).Timing!.Turns).ElapsedTicks);
        Assert.Equal(400_000_000, (await fixture.RestoreAsync()).Timing!.GameElapsedTicks);
    }

    [Fact]
    public async Task In_memory_restore_returns_a_copy_of_timing_metadata()
    {
        var fixture = await CreateAsync(sqlite: false);
        await fixture.Store.SaveTurnTimingAsync(fixture.State.SessionId, fixture.State.StateVersion,
            Timing(100), Token);
        var restored = await fixture.RestoreAsync();
        Assert.IsType<TurnTimingEntry[]>(restored.Timing!.Turns)[0] =
            new TurnTimingEntry(1, new SeatId(1), 999, true, false);
        Assert.Equal(100, Assert.Single((await fixture.RestoreAsync()).Timing!.Turns).ElapsedTicks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Matches_without_recorded_timing_restore_with_no_invented_history(bool sqlite)
    {
        var fixture = await CreateAsync(sqlite);
        Assert.Null((await fixture.RestoreAsync()).Timing);
        if (fixture.Store is SqliteSessionStore disk)
        {
            Assert.Equal(0, await ScalarAsync(disk, fixture.State.SessionId,
                "SELECT COUNT(*) FROM sqlite_master WHERE name = 'TurnTiming';"));
            await fixture.Store.SaveTurnTimingAsync(fixture.State.SessionId, fixture.State.StateVersion,
                Timing(10), Token);
            Assert.Equal(1, await ScalarAsync(disk, fixture.State.SessionId,
                "SELECT COUNT(*) FROM sqlite_master WHERE name = 'TurnTiming';"));
            await MutateAsync(disk, fixture.State.SessionId, "DELETE FROM TurnTiming;");
            Assert.Null((await fixture.RestoreAsync()).Timing);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timing_only_writes_reject_both_stale_and_future_versions(bool sqlite)
    {
        var fixture = await CreateAsync(sqlite);
        var earlierVersion = fixture.State.StateVersion;
        await fixture.Store.SaveTurnTimingAsync(fixture.State.SessionId, earlierVersion, Timing(100), Token);
        await fixture.SubmitAsync(new SaveAndPackAway(fixture.Envelope(), "Later version"));
        var expectedHash = StateHash.Compute(fixture.State);

        foreach (var wrongVersion in new[] { earlierVersion, fixture.State.StateVersion + 1 })
            await Assert.ThrowsAsync<SessionIntegrityException>(() => fixture.Store.SaveTurnTimingAsync(
                fixture.State.SessionId, wrongVersion, Timing(999), Token));

        var restored = await fixture.RestoreAsync();
        Assert.Equal(100, Assert.Single(restored.Timing!.Turns).ElapsedTicks);
        Assert.Equal(expectedHash, StateHash.Compute(restored.State));
    }

    [Fact]
    public async Task A_failed_timing_write_rolls_back_the_whole_command_transaction()
    {
        var fixture = await CreateAsync(sqlite: true);
        var disk = (SqliteSessionStore)fixture.Store;
        await disk.SaveTurnTimingAsync(fixture.State.SessionId, fixture.State.StateVersion, Timing(100), Token);
        await MutateAsync(disk, fixture.State.SessionId, """
            CREATE TRIGGER RejectTiming BEFORE INSERT ON TurnTiming
            BEGIN SELECT RAISE(ABORT, 'synthetic timing write failure'); END;
            """);
        var hash = StateHash.Compute(fixture.State);
        var version = fixture.State.StateVersion;
        var command = new SaveAndPackAway(fixture.Envelope(), "Must roll back");

        await Assert.ThrowsAsync<SqliteException>(() => fixture.SubmitAsync(command, Timing(200)));

        var restored = await fixture.RestoreAsync();
        Assert.Equal(hash, StateHash.Compute(restored.State));
        Assert.Equal(version, restored.State.StateVersion);
        Assert.Equal(100, Assert.Single(restored.Timing!.Turns).ElapsedTicks);
        Assert.Null(await disk.FindCommandOutcomeAsync(fixture.State.SessionId,
            command.Envelope.CommandId, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rewind_removes_later_timing_even_when_the_new_branch_reuses_its_version(bool sqlite)
    {
        var fixture = await CreateAsync(sqlite);
        await fixture.SaveAsync("Earlier save", Timing(100) with { GameElapsedTicks = 200 });
        var checkpointId = fixture.State.Checkpoint!.CheckpointId;
        var savedVersion = fixture.State.StateVersion;
        await fixture.ResumeAsync();
        await fixture.SaveAsync("Discarded save", Timing(999) with { GameElapsedTicks = 1500 });
        var futureVersion = fixture.State.StateVersion;

        var rewound = await fixture.Store.RewindToVerifiedCheckpointAsync(fixture.State.SessionId,
            checkpointId, TestManifest.Manifest, TestManifest.Catalog, Token);
        Assert.Equal(savedVersion, rewound.State.StateVersion);
        Assert.Equal(100, Assert.Single(rewound.Timing!.Turns).ElapsedTicks);
        Assert.Equal(200, rewound.Timing.GameElapsedTicks);
        fixture.State = rewound.State;
        await fixture.ResumeAsync();
        await fixture.SaveAsync("Replacement save");
        Assert.Equal(futureVersion, fixture.State.StateVersion);
        Assert.Equal(100, Assert.Single((await fixture.RestoreAsync()).Timing!.Turns).ElapsedTicks);
        Assert.Equal(200, (await fixture.RestoreAsync()).Timing!.GameElapsedTicks);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData(",\"GameElapsedTicks\":null", null)]
    [InlineData(",\"GameElapsedTicks\":0", 0L)]
    [InlineData(",\"GameElapsedTicks\":1000", 1000L)]
    public async Task Legacy_and_current_total_fields_restore_without_inventing_historical_pause_time(
        string totalField, long? expectedTotal)
    {
        var fixture = await CreateAsync(sqlite: true);
        var disk = (SqliteSessionStore)fixture.Store;
        await disk.SaveTurnTimingAsync(fixture.State.SessionId, fixture.State.StateVersion, Timing(100), Token);
        var payload = "{\"Turns\":[{\"TurnNumber\":1,\"SeatId\":1," +
            "\"ElapsedTicks\":100,\"Completed\":false,\"IsPartial\":false}]" + totalField + "}";
        await MutateAsync(disk, fixture.State.SessionId, "UPDATE TurnTiming SET Payload = $payload;",
            Encoding.UTF8.GetBytes(payload));
        var restored = await fixture.RestoreAsync();
        Assert.NotNull(restored.Timing);
        Assert.Equal(expectedTotal, restored.Timing.GameElapsedTicks);
        Assert.Equal(expectedTotal ?? 100,
            new TurnTimingTracker(saved: restored.Timing, restored: true).Snapshot().GameElapsedTicks);
        Assert.Equal(StateHash.Compute(fixture.State), StateHash.Compute(restored.State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_total_ticks_are_supplementary_and_do_not_block_gameplay_restore(bool sqlite)
    {
        var fixture = await CreateAsync(sqlite);
        foreach (var ticks in new[] { -1L, TimeSpan.FromDays(365).Ticks + 1, long.MaxValue })
        {
            await fixture.Store.SaveTurnTimingAsync(fixture.State.SessionId, fixture.State.StateVersion,
                Timing(100) with { GameElapsedTicks = ticks }, Token);
            var restored = await fixture.RestoreAsync();
            Assert.Null(restored.Timing);
            Assert.Equal(fixture.State.StateVersion, restored.State.StateVersion);
            Assert.Equal(StateHash.Compute(fixture.State), StateHash.Compute(restored.State));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_turn_sum_is_bounded_before_it_can_seed_the_total_clock(bool sqlite)
    {
        var fixture = await CreateAsync(sqlite);
        await fixture.SubmitAsync(new SelectTrainCard(fixture.Envelope(new SeatId(1)), null));
        await fixture.SubmitAsync(new SelectTrainCard(fixture.Envelope(new SeatId(1)), null));
        await fixture.Store.SaveTurnTimingAsync(fixture.State.SessionId, fixture.State.StateVersion,
            new([new(1, new SeatId(1), TimeSpan.FromDays(200).Ticks, true, false),
                 new(2, new SeatId(2), TimeSpan.FromDays(200).Ticks, false, false)]), Token);
        var restored = await fixture.RestoreAsync();
        Assert.Null(restored.Timing);
        Assert.Equal(StateHash.Compute(fixture.State), StateHash.Compute(restored.State));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("missing-turns")]
    [InlineData("negative")]
    [InlineData("absurd")]
    [InlineData("invalid-turn")]
    [InlineData("future-turn")]
    [InlineData("invalid-seat")]
    [InlineData("duplicate-turn")]
    [InlineData("out-of-order")]
    [InlineData("multiple-unfinished")]
    [InlineData("unfinished-not-last")]
    [InlineData("marker-without-unfinished")]
    [InlineData("non-numeric-total")]
    [InlineData("out-of-range-total")]
    public async Task Corrupt_supplementary_timing_does_not_block_a_verified_game(string corruption)
    {
        var fixture = await CreateAsync(sqlite: true);
        var disk = (SqliteSessionStore)fixture.Store;
        if (corruption is "out-of-order" or "multiple-unfinished" or "unfinished-not-last")
        {
            await fixture.SubmitAsync(new SelectTrainCard(fixture.Envelope(new SeatId(1)), null));
            await fixture.SubmitAsync(new SelectTrainCard(fixture.Envelope(new SeatId(1)), null));
            Assert.Equal(2, fixture.State.TurnNumber);
        }
        var timing = Timing(100);
        await disk.SaveTurnTimingAsync(fixture.State.SessionId, fixture.State.StateVersion, timing, Token);
        var entry = timing.Turns[0];
        var payload = corruption switch
        {
            "malformed" => "{not-json",
            "missing-turns" => "{}",
            "negative" => JsonSerializer.Serialize(new TurnTimingSnapshot([entry with { ElapsedTicks = -1 }])),
            "absurd" => JsonSerializer.Serialize(new TurnTimingSnapshot([entry with { ElapsedTicks = long.MaxValue }])),
            "invalid-turn" => JsonSerializer.Serialize(new TurnTimingSnapshot([entry with { TurnNumber = 0 }])),
            "future-turn" => JsonSerializer.Serialize(new TurnTimingSnapshot([entry with { TurnNumber = fixture.State.TurnNumber + 1 }])),
            "invalid-seat" => JsonSerializer.Serialize(new TurnTimingSnapshot([entry with { SeatId = new SeatId(99) }])),
            "duplicate-turn" => JsonSerializer.Serialize(new TurnTimingSnapshot([entry, entry])),
            "out-of-order" => JsonSerializer.Serialize(new TurnTimingSnapshot(
                [entry with { TurnNumber = 2, SeatId = new SeatId(2), Completed = true }, entry with { Completed = true }])),
            "multiple-unfinished" => JsonSerializer.Serialize(new TurnTimingSnapshot(
                [entry, entry with { TurnNumber = 2, SeatId = new SeatId(2) }])),
            "unfinished-not-last" => JsonSerializer.Serialize(new TurnTimingSnapshot(
                [entry, entry with { TurnNumber = 2, SeatId = new SeatId(2), Completed = true }])),
            "marker-without-unfinished" => JsonSerializer.Serialize(new TurnTimingSnapshot(
                [entry with { Completed = true }], AwaitingScoreMarker: true)),
            "non-numeric-total" => JsonSerializer.Serialize(new { timing.Turns, GameElapsedTicks = "invalid" }),
            "out-of-range-total" => "{\"Turns\":[],\"GameElapsedTicks\":9223372036854775808}",
            _ => throw new InvalidOperationException()
        };
        await MutateAsync(disk, fixture.State.SessionId, "UPDATE TurnTiming SET Payload = $payload;",
            Encoding.UTF8.GetBytes(payload));

        var restored = await fixture.RestoreAsync();
        Assert.Null(restored.Timing);
        Assert.Equal(StateHash.Compute(fixture.State), StateHash.Compute(restored.State));
    }

    private static TurnTimingSnapshot Timing(long ticks) =>
        new([new(1, new SeatId(1), ticks, false, false)]);

    private async Task<Fixture> CreateAsync(bool sqlite)
    {
        ISessionStore store = sqlite ? new SqliteSessionStore(_root) : new InMemorySessionStore();
        var rules = new GameRules(TestManifest.Manifest, TestManifest.Catalog);
        var setup = new SessionSetup(SessionId.New(),
            [new(new SeatId(1), "First", PlayerColor.Red, SeatKind.Human, AiDifficulty.Standard),
             new(new SeatId(2), "Second", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard)],
            new SeatId(1), VerificationMode.Manual);
        var (state, transition) = rules.CreateSession(setup, DeterministicRandom.SeedFrom(972));
        await store.CreateAsync(state, CommandId.New(), transition, StateHash.Compute(state), Token);
        var fixture = new Fixture(rules, store, state);
        foreach (var seat in state.Seats)
            await fixture.SubmitAsync(new CommitTicketSelection(fixture.Envelope(seat.SeatId),
                [.. fixture.State.SetupOffers[seat.SeatId].Take(2)], []));
        Assert.Equal(SessionLifecycle.Active, fixture.State.Lifecycle);
        return fixture;
    }

    private sealed class Fixture(GameRules rules, ISessionStore store, GameState state)
    {
        public ISessionStore Store { get; } = store;
        public GameState State { get; set; } = state;
        public CommandEnvelope Envelope(SeatId? seat = null) =>
            new(State.SessionId, CommandId.New(), State.StateVersion, seat);

        public async Task SubmitAsync(GameCommand command, TurnTimingSnapshot? timing = null)
        {
            var result = rules.ValidateAndApply(State, command);
            Assert.True(result.IsAccepted, result.Rejection?.Message);
            var next = State.Fork();
            GameReducer.ApplyTransition(next, result.Transition!.Events);
            await Store.CommitAsync(next, new StoredCommandOutcome(command.Envelope.CommandId,
                    true, next.StateVersion, null, null, timing), result.Transition,
                StateHash.Compute(next), Token);
            State = next;
        }

        public Task<RestoredSession> RestoreAsync() =>
            Store.RestoreAsync(State.SessionId, TestManifest.Manifest, TestManifest.Catalog, Token);

        public async Task SaveAsync(string name, TurnTimingSnapshot? timing = null)
        {
            await SubmitAsync(new SaveAndPackAway(Envelope(), name), timing);
            await SubmitAsync(new CommitPackAwayCheckpoint(Envelope(), State.PackAwayRequest!.CheckpointId));
            await SubmitAsync(new RecordCheckpointReadback(Envelope(), State.Checkpoint!.CheckpointId, true, null));
        }

        public async Task ResumeAsync()
        {
            var checkpoint = State.Checkpoint!;
            await SubmitAsync(new BeginBoardRebuild(Envelope(), checkpoint.CheckpointId));
            await SubmitAsync(new AttestBoardRebuild(Envelope(), checkpoint.CheckpointId,
                checkpoint.PhysicalTargetHash, "timing test"));
            await SubmitAsync(new ResumePackedGame(Envelope(), checkpoint.CheckpointId));
        }
    }

    private static async Task MutateAsync(SqliteSessionStore store, SessionId sessionId,
        string sql, byte[]? payload = null)
    {
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath(sessionId)}");
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (payload is not null) command.Parameters.AddWithValue("$payload", payload);
        await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<long> ScalarAsync(SqliteSessionStore store, SessionId sessionId, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath(sessionId)}");
        await connection.OpenAsync(Token);
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
