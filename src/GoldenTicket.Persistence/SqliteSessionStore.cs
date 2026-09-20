using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GoldenTicket.Application;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using Microsoft.Data.Sqlite;

namespace GoldenTicket.Persistence;

/// <summary>
/// The local authoritative save (DESIGN 19.1-19.4). One SQLite database per match under
/// <c>%LOCALAPPDATA%\GoldenTicket\sessions\</c>, in WAL mode with full synchronous durability, where
/// each transaction commits its domain events, its command deduplication result and the resulting
/// version and state hash together.
/// </summary>
public sealed class SqliteSessionStore(string rootDirectory) : ISessionStore
{
    public const int StoreSchemaVersion = 4;

    /// <summary>Unit separator; seat names are free text and must not collide with it.</summary>
    private const char SeatNameSeparator = '\u001f';

    /// <summary>DESIGN 19.1: settings and matches live outside the installation directory.</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoldenTicket");

    public static SqliteSessionStore CreateDefault() => new(DefaultRoot);

    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);

    public string SessionDirectory(SessionId sessionId)
    {
        // Session ids are opaque identifiers, never caller-supplied filesystem paths.
        if (!Guid.TryParseExact(sessionId.Value, "N", out var id) ||
            !string.Equals(id.ToString("N"), sessionId.Value, StringComparison.Ordinal))
            throw new ArgumentException("A session id must be a lowercase, 32-digit GUID.", nameof(sessionId));

        var sessionsRoot = Path.Combine(RootDirectory, "sessions");
        var directory = Path.GetFullPath(Path.Combine(sessionsRoot, sessionId.Value));
        if (!directory.StartsWith(sessionsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The session directory must remain inside the saves directory.", nameof(sessionId));

        // Do not follow a save directory or its parent through a junction/symbolic link.
        foreach (var path in new[] { sessionsRoot, directory })
        {
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A saved-match directory cannot be a filesystem link.");
        }

        return directory;
    }

    public string DatabasePath(SessionId sessionId)
    {
        var path = Path.Combine(SessionDirectory(sessionId), "session.db");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("A saved-match database cannot be a filesystem link.");
        return path;
    }

    // ---- Creation ---------------------------------------------------------------------------

    public async Task CreateAsync(
        GameState state,
        CommandId commandId,
        Transition transition,
        string stateHash,
        CancellationToken cancellationToken)
    {
        if (File.Exists(DatabasePath(state.SessionId)))
            throw new InvalidOperationException($"Session {state.SessionId} already exists.");
        Directory.CreateDirectory(SessionDirectory(state.SessionId));

        await using var connection = await OpenAsync(state.SessionId, cancellationToken, create: true);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await CreateSchemaAsync(connection, cancellationToken);

        var now = DateTimeOffset.UtcNow.ToString("O");

        await ExecuteAsync(connection, """
            INSERT INTO Session (
                SessionId, ProfileId, ManifestHash, StoreSchemaVersion, RulesPolicyVersion,
                Lifecycle, TurnNumber, SeatNames, CreatedAt, UpdatedAt)
            VALUES (
                $sessionId, $profileId, $manifestHash, $storeSchema, $rulesPolicy,
                $lifecycle, $turnNumber, $seatNames, $createdAt, $updatedAt);
            """, cancellationToken,
            ("$sessionId", state.SessionId.Value),
            ("$profileId", state.Manifest.ProfileId),
            ("$manifestHash", state.Manifest.DataHash),
            ("$storeSchema", StoreSchemaVersion),
            ("$rulesPolicy", state.Manifest.RulesPolicyVersion),
            ("$lifecycle", state.Lifecycle.ToString()),
            ("$turnNumber", state.TurnNumber),
            ("$seatNames", string.Join(SeatNameSeparator, state.Seats.Select(seat => seat.DisplayName))),
            ("$createdAt", now),
            ("$updatedAt", now));

        await ExecuteAsync(connection,
            "INSERT INTO MigrationHistory (Version, AppliedAt) VALUES ($version, $appliedAt);",
            cancellationToken, ("$version", StoreSchemaVersion), ("$appliedAt", now));

        await AppendEventsAsync(
            connection, state.SessionId, state.StateVersion, transition.Events, cancellationToken);

        await WriteSnapshotAsync(connection, state.SessionId, state.StateVersion,
            state.JournalSequence, state.BoardRevision, stateHash, cancellationToken);

        await WriteCommandOutcomeAsync(connection, state.SessionId,
            new StoredCommandOutcome(commandId, true, state.StateVersion, null, null), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    // ---- Commands ---------------------------------------------------------------------------

    public async Task<StoredCommandOutcome?> FindCommandOutcomeAsync(
        SessionId sessionId, CommandId commandId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(sessionId, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Accepted, StateVersionAfter, RejectionCode, RejectionMessage
            FROM CommandResult WHERE SessionId = $sessionId AND CommandId = $commandId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.Value);
        command.Parameters.AddWithValue("$commandId", commandId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new StoredCommandOutcome(
            commandId,
            reader.GetBoolean(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    public async Task CommitAsync(
        GameState state,
        StoredCommandOutcome outcome,
        Transition transition,
        string stateHash,
        CancellationToken cancellationToken)
    {
        var sessionId = state.SessionId;
        await using var connection = await OpenAsync(sessionId, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var stored = await ReadLatestSnapshotAsync(connection, sessionId, cancellationToken);
        var (headSequence, _) = await ReadJournalHeadAsync(connection, sessionId, cancellationToken);
        if (!outcome.Accepted || outcome.StateVersionAfter != state.StateVersion ||
            transition.Events.Length == 0 || stored.StateVersion != state.StateVersion - 1 ||
            headSequence + 1 + transition.Events.Length != state.JournalSequence)
            throw new SessionIntegrityException("The save has changed or the proposed transaction is inconsistent. Reopen the match before continuing.");

        await AppendEventsAsync(
            connection, sessionId, state.StateVersion, transition.Events, cancellationToken);

        await WriteSnapshotAsync(
            connection, sessionId, state.StateVersion, state.JournalSequence, state.BoardRevision, stateHash, cancellationToken);

        await WriteCommandOutcomeAsync(connection, sessionId, outcome, cancellationToken);
        if (outcome.Timing is not null)
            await WriteTurnTimingAsync(connection, sessionId, state.StateVersion, outcome.Timing,
                cancellationToken);

        // DESIGN 19.2/19.3: the checkpoint row is written in the same transaction as the event that
        // created or re-statused it, so a listing can never disagree with the journal.
        if (state.Checkpoint is { } checkpoint &&
            transition.Events.Any(e => e is PackAwayCheckpointCommitted or PackAwayCheckpointVerified
                                            or PackAwayCheckpointFaulted))
        {
            await WriteCheckpointAsync(connection, checkpoint, cancellationToken);
        }

        await ExecuteAsync(connection,
            """
            UPDATE Session SET UpdatedAt = $updatedAt, TurnNumber = $turnNumber, Lifecycle = $lifecycle
            WHERE SessionId = $sessionId;
            """,
            cancellationToken,
            ("$updatedAt", DateTimeOffset.UtcNow.ToString("O")),
            ("$turnNumber", state.TurnNumber),
            ("$lifecycle", state.Lifecycle.ToString()),
            ("$sessionId", sessionId.Value));

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveTurnTimingAsync(
        SessionId sessionId, long stateVersion, TurnTimingSnapshot timing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timing);
        await using var connection = await OpenAsync(sessionId, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var stored = await ReadLatestSnapshotAsync(connection, sessionId, cancellationToken);
        if (stored.StateVersion != stateVersion)
            throw new SessionIntegrityException("The saved match changed. Reload it before saving turn timing.");
        await WriteTurnTimingAsync(connection, sessionId, stateVersion, timing, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordRejectionAsync(
        SessionId sessionId, StoredCommandOutcome outcome, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(sessionId, cancellationToken);
        if (outcome.Accepted)
            throw new ArgumentException("Only rejected outcomes can be recorded without game events.", nameof(outcome));
        await WriteCommandOutcomeAsync(connection, sessionId, outcome, cancellationToken, ignoreDuplicate: true);
    }

    // ---- Restore ----------------------------------------------------------------------------

    public async Task<RestoredSession> RestoreAsync(
        SessionId sessionId,
        BoardManifest manifest,
        CardCatalog catalog,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(DatabasePath(sessionId)))
            throw new FileNotFoundException($"No saved match named {sessionId}.", DatabasePath(sessionId));

        await using var connection = await OpenAsync(sessionId, cancellationToken);
        // Keep metadata, journal and snapshot on a single consistent SQLite read transaction.
        await using var transaction = connection.BeginTransaction(deferred: true);
        await VerifyCompatibilityAsync(connection, sessionId, manifest, cancellationToken);
        var journal = await ReadJournalAsync(connection, sessionId, cancellationToken);

        if (journal.Count == 0 || journal[0].Event is not SessionCreated created ||
            created.SessionId != sessionId || created.ProfileId != manifest.ProfileId ||
            created.ManifestHash != manifest.DataHash || created.RulesPolicyVersion != manifest.RulesPolicyVersion)
            throw new SessionIntegrityException("The journal does not contain the expected match and rules profile.");

        GameState state;
        try
        {
            state = GameReducer.Rebuild(manifest, catalog, journal);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            throw new SessionIntegrityException("The saved journal could not be replayed. Restoration stops without changing the save.");
        }

        // DESIGN 19.4 step 3 / invariant 12: the replayed state must reproduce the stored hash.
        var snapshot = await ReadLatestSnapshotAsync(connection, sessionId, cancellationToken);
        if (!StateHash.Matches(state, snapshot.StateHash) ||
            snapshot.StateVersion != state.StateVersion || snapshot.JournalSequence != state.JournalSequence ||
            snapshot.BoardRevision != state.BoardRevision)
        {
            throw new SessionIntegrityException(
                $"The replayed journal of {sessionId} does not match its required snapshot. " +
                "Restoration stops rather than inventing missing state.");
        }

        var problems = InvariantChecker.Check(state);
        if (problems.Count > 0)
        {
            throw new SessionIntegrityException(
                $"The restored match failed {problems.Count} integrity checks. " +
                "Restoration stops without exposing private cards or changing the save.");
        }

        var timing = await ReadTurnTimingAsync(connection, state, cancellationToken);
        return new RestoredSession(state, journal, timing);
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        var sessionsRoot = Path.Combine(RootDirectory, "sessions");
        if (!Directory.Exists(sessionsRoot)) return [];
        if ((File.GetAttributes(sessionsRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The saves directory cannot be a filesystem link.");

        var summaries = new List<SessionSummary>();

        foreach (var directory in Directory.EnumerateDirectories(sessionsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (!Guid.TryParseExact(name, "N", out var id) || id.ToString("N") != name) continue;
            var sessionId = new SessionId(name);

            try
            {
                if (!File.Exists(DatabasePath(sessionId)))
                    throw new FileNotFoundException("The saved-match database is missing.");
                await using var connection = await OpenAsync(sessionId, cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT StoreSchemaVersion FROM Session WHERE SessionId = $sessionId;";
                command.Parameters.AddWithValue("$sessionId", sessionId.Value);
                var listedSchema = await command.ExecuteScalarAsync(cancellationToken);
                if (listedSchema is null or DBNull)
                    throw new SessionIntegrityException("The saved match has no session metadata.");
                if (Convert.ToInt32(listedSchema) != StoreSchemaVersion)
                    continue;
                var checkpointName = """
                    (SELECT p.Name FROM PackAwayCheckpoint AS p WHERE p.SessionId = s.SessionId
                     ORDER BY p.SourceStateVersion DESC, p.CreatedAt DESC, p.CheckpointId DESC LIMIT 1)
                    """;
                command.CommandText = $"""
                    SELECT s.ProfileId, s.Lifecycle, s.TurnNumber, s.SeatNames, s.CreatedAt, s.UpdatedAt,
                           {checkpointName}
                    FROM Session AS s WHERE s.SessionId = $sessionId;
                    """;

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new SessionIntegrityException("The saved match has no session metadata.");

                if (!Enum.TryParse<SessionLifecycle>(reader.GetString(1), out var lifecycle) ||
                    !Enum.IsDefined(lifecycle))
                    throw new SessionIntegrityException("The saved match has an invalid lifecycle.");

                summaries.Add(new SessionSummary(
                    sessionId,
                    reader.GetString(0),
                    DateTimeOffset.Parse(reader.GetString(4)),
                    DateTimeOffset.Parse(reader.GetString(5)),
                    lifecycle,
                    reader.GetInt32(2),
                    reader.GetString(3).Split(SeatNameSeparator),
                    LatestCheckpointName: reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
            catch (Exception error) when (error is SqliteException or FormatException or ArgumentException or
                InvalidCastException or IOException or UnauthorizedAccessException or SessionIntegrityException)
            {
                // Keep the entry visible for deliberate retry/recovery. No private payload or raw
                // database error is included, and the damaged file is never silently replaced.
                summaries.Add(new SessionSummary(sessionId, "Unavailable", DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch, SessionLifecycle.Setup, 0, [],
                    "This saved match could not be read. Its files have been retained; retry opening it or restore a backup."));
            }
        }

        return [.. summaries.OrderByDescending(summary => summary.UpdatedAt)];
    }

    public Task DeleteSessionAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = SessionDirectory(sessionId);
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Restore the previously completed save and discard later auto-journaled play in one SQLite
    /// transaction. The chosen checkpoint remains packed, along with its reference photo sidecar.
    /// </summary>
    public async Task<RestoredSession> RewindToVerifiedCheckpointAsync(
        SessionId sessionId, CheckpointId checkpointId, BoardManifest manifest, CardCatalog catalog,
        CancellationToken cancellationToken)
    {
        await using (var connection = await OpenAsync(sessionId, cancellationToken))
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await VerifyCompatibilityAsync(connection, sessionId, manifest, cancellationToken);
            var journal = await ReadJournalAsync(connection, sessionId, cancellationToken);
            var current = GameReducer.Rebuild(manifest, catalog, journal);
            var head = await ReadLatestSnapshotAsync(connection, sessionId, cancellationToken);
            if (!StateHash.Matches(current, head.StateHash) ||
                head.StateVersion != current.StateVersion ||
                head.JournalSequence != current.JournalSequence ||
                head.BoardRevision != current.BoardRevision ||
                InvariantChecker.Check(current).Count > 0)
                throw new SessionIntegrityException("The current save is damaged; it was not rewound.");

            var target = CheckpointJournalRewind.AtVerifiedCheckpoint(
                journal, checkpointId, manifest, catalog);
            var state = target.State;
            await using (var snapshotCommand = connection.CreateCommand())
            {
                snapshotCommand.CommandText = """
                    SELECT JournalSequence, BoardRevision, StateHash FROM Snapshot
                    WHERE SessionId = $sessionId AND StateVersion = $version;
                    """;
                snapshotCommand.Parameters.AddWithValue("$sessionId", sessionId.Value);
                snapshotCommand.Parameters.AddWithValue("$version", state.StateVersion);
                await using var reader = await snapshotCommand.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken) ||
                    reader.GetInt64(0) != state.JournalSequence ||
                    reader.GetInt64(1) != state.BoardRevision ||
                    !StateHash.Matches(state, reader.GetString(2)))
                    throw new SessionIntegrityException("The selected checkpoint snapshot is damaged; it was not rewound.");
            }

            await ExecuteAsync(connection,
                "DELETE FROM Event WHERE SessionId = $sessionId AND Sequence >= $sequence;",
                cancellationToken, ("$sessionId", sessionId.Value),
                ("$sequence", state.JournalSequence));
            await ExecuteAsync(connection,
                "DELETE FROM Snapshot WHERE SessionId = $sessionId AND StateVersion > $version;",
                cancellationToken, ("$sessionId", sessionId.Value),
                ("$version", state.StateVersion));
            if (await HasTurnTimingTableAsync(connection, cancellationToken))
                await ExecuteAsync(connection,
                    "DELETE FROM TurnTiming WHERE SessionId = $sessionId AND StateVersion > $version;",
                    cancellationToken, ("$sessionId", sessionId.Value),
                    ("$version", state.StateVersion));
            await ExecuteAsync(connection,
                "DELETE FROM CommandResult WHERE SessionId = $sessionId AND (Accepted = 0 OR StateVersionAfter > $version);",
                cancellationToken, ("$sessionId", sessionId.Value),
                ("$version", state.StateVersion));
            await ExecuteAsync(connection,
                "DELETE FROM PackAwayCheckpoint WHERE SessionId = $sessionId AND SourceStateVersion > $version;",
                cancellationToken, ("$sessionId", sessionId.Value),
                ("$version", state.StateVersion));
            await WriteCheckpointAsync(connection, state.Checkpoint!, cancellationToken);
            await ExecuteAsync(connection, """
                UPDATE Session SET Lifecycle = $lifecycle, TurnNumber = $turn, UpdatedAt = $updatedAt
                WHERE SessionId = $sessionId;
                """, cancellationToken,
                ("$lifecycle", state.Lifecycle.ToString()),
                ("$turn", state.TurnNumber),
                ("$updatedAt", DateTimeOffset.UtcNow.ToString("O")),
                ("$sessionId", sessionId.Value));
            await transaction.CommitAsync(cancellationToken);
        }

        var readback = await RestoreAsync(sessionId, manifest, catalog, cancellationToken);
        if (readback.State.Checkpoint?.CheckpointId != checkpointId ||
            readback.State.Lifecycle != SessionLifecycle.PackedAway)
            throw new SessionIntegrityException("The restored save did not match the selected checkpoint.");
        return readback;
    }

    // ---- Internals --------------------------------------------------------------------------

    private static async Task WriteTurnTimingAsync(
        SqliteConnection connection, SessionId sessionId, long stateVersion,
        TurnTimingSnapshot timing, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(timing);
        // Optional metadata is additive: older v4 saves remain readable without this table.
        // Both creation and the write participate in the caller's game/timing transaction.
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS TurnTiming (
                SessionId    TEXT NOT NULL,
                StateVersion INTEGER NOT NULL,
                Payload      BLOB NOT NULL,
                PRIMARY KEY (SessionId, StateVersion)
            );
            """, cancellationToken);
        await ExecuteAsync(connection, """
            INSERT INTO TurnTiming (SessionId, StateVersion, Payload)
            VALUES ($sessionId, $version, $payload)
            ON CONFLICT (SessionId, StateVersion) DO UPDATE SET Payload = excluded.Payload;
            """, cancellationToken, ("$sessionId", sessionId.Value),
            ("$version", stateVersion), ("$payload", payload));
    }

    private static async Task<bool> HasTurnTimingTableAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'TurnTiming';";
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<TurnTimingSnapshot?> ReadTurnTimingAsync(
        SqliteConnection connection, GameState state, CancellationToken cancellationToken)
    {
        if (!await HasTurnTimingTableAsync(connection, cancellationToken)) return null;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Payload FROM TurnTiming WHERE SessionId = $sessionId AND StateVersion <= $version
            ORDER BY StateVersion DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", state.SessionId.Value);
        command.Parameters.AddWithValue("$version", state.StateVersion);
        var payload = await command.ExecuteScalarAsync(cancellationToken);
        if (payload is null) return null;
        try
        {
            var timing = JsonSerializer.Deserialize<TurnTimingSnapshot>((byte[])payload);
            if (timing?.Turns is null) return null;
            var maximumTicks = TimeSpan.FromDays(365).Ticks;
            if (timing.GameElapsedTicks is { } total && (total < 0 || total > maximumTicks)) return null;
            long recordedTicks = 0;
            var previousTurn = 0;
            var unfinished = false;
            // Statistics are supplementary. Damaged metadata must neither fabricate time nor
            // prevent a verified game journal from loading. Bound a save's total and recorded
            // turn sum to one year, also keeping legacy fallback addition safe from overflow.
            foreach (var turn in timing.Turns)
            {
                if (turn is null || turn.TurnNumber <= previousTurn || turn.TurnNumber > state.TurnNumber ||
                    unfinished || !state.Seats.Any(seat => seat.SeatId == turn.SeatId) ||
                    turn.ElapsedTicks < 0 || turn.ElapsedTicks > maximumTicks - recordedTicks)
                    return null;
                recordedTicks += turn.ElapsedTicks;
                previousTurn = turn.TurnNumber;
                unfinished = !turn.Completed;
            }
            return timing.AwaitingScoreMarker && !unfinished ? null : timing;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidCastException)
        {
            return null;
        }
    }

    private async Task<SqliteConnection> OpenAsync(
        SessionId sessionId, CancellationToken cancellationToken, bool create = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath(sessionId),
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = true,
        }.ToString());

        try
        {
            await connection.OpenAsync(cancellationToken);

            // DESIGN 19.3: WAL with a durability setting appropriate for power-loss recovery.
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);

            return connection;
        }
        catch
        {
            // An open/PRAGMA failure must not return a corrupt database handle to its pool and
            // keep the file locked. Retire only this connection-string pool, not unrelated games.
            SqliteConnection.ClearPool(connection);
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Session (
                SessionId          TEXT PRIMARY KEY,
                ProfileId          TEXT NOT NULL,
                ManifestHash       TEXT NOT NULL,
                StoreSchemaVersion INTEGER NOT NULL,
                RulesPolicyVersion INTEGER NOT NULL,
                Lifecycle          TEXT NOT NULL,
                TurnNumber         INTEGER NOT NULL,
                SeatNames          TEXT NOT NULL,
                CreatedAt          TEXT NOT NULL,
                UpdatedAt          TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Event (
                SessionId     TEXT NOT NULL,
                Sequence      INTEGER NOT NULL,
                StateVersion  INTEGER NOT NULL,
                Type          TEXT NOT NULL,
                SchemaVersion INTEGER NOT NULL,
                Visibility    TEXT NOT NULL,
                Payload       BLOB NOT NULL,
                PriorHash     TEXT NOT NULL,
                PRIMARY KEY (SessionId, Sequence)
            );

            CREATE TABLE IF NOT EXISTS Snapshot (
                SessionId       TEXT NOT NULL,
                StateVersion    INTEGER NOT NULL,
                JournalSequence INTEGER NOT NULL,
                BoardRevision   INTEGER NOT NULL,
                StateHash       TEXT NOT NULL,
                PRIMARY KEY (SessionId, StateVersion)
            );

            CREATE TABLE IF NOT EXISTS CommandResult (
                SessionId         TEXT NOT NULL,
                CommandId         TEXT NOT NULL,
                Accepted          INTEGER NOT NULL,
                StateVersionAfter INTEGER NOT NULL,
                RejectionCode     TEXT NULL,
                RejectionMessage  TEXT NULL,
                RecordedAt        TEXT NOT NULL,
                PRIMARY KEY (SessionId, CommandId)
            );

            CREATE TABLE IF NOT EXISTS MigrationHistory (
                Version   INTEGER PRIMARY KEY,
                AppliedAt TEXT NOT NULL
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        await CreateCheckpointSchemaAsync(connection, cancellationToken);
    }

    private static Task CreateCheckpointSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS PackAwayCheckpoint (
                SessionId           TEXT NOT NULL,
                CheckpointId        TEXT NOT NULL,
                Name                TEXT NOT NULL,
                CreatedAt           TEXT NOT NULL,
                FormatVersion       INTEGER NOT NULL,
                SourceStateVersion  INTEGER NOT NULL,
                SourceJournalSeq    INTEGER NOT NULL,
                TargetProvenance    TEXT NOT NULL,
                PhotoHash           TEXT NULL,
                Status              TEXT NOT NULL,
                Payload             BLOB NOT NULL,
                PRIMARY KEY (SessionId, CheckpointId)
            );
            """, cancellationToken);

    /// <summary>Appends the events of one transaction and returns the last sequence number used.</summary>
    private static async Task<long> AppendEventsAsync(
        SqliteConnection connection,
        SessionId sessionId,
        long stateVersion,
        IReadOnlyList<GameEvent> events,
        CancellationToken cancellationToken)
    {
        var (sequence, priorHash) = await ReadJournalHeadAsync(connection, sessionId, cancellationToken);

        foreach (var domainEvent in events)
        {
            var payload = EventSerializer.Serialize(domainEvent);

            priorHash = ChainHash(priorHash, domainEvent.GetType().Name, payload);

            await ExecuteAsync(connection, """
                INSERT INTO Event (
                    SessionId, Sequence, StateVersion, Type, SchemaVersion,
                    Visibility, Payload, PriorHash)
                VALUES (
                    $sessionId, $sequence, $stateVersion, $type, $schemaVersion,
                    $visibility, $payload, $priorHash);
                """, cancellationToken,
                ("$sessionId", sessionId.Value),
                ("$sequence", ++sequence),
                ("$stateVersion", stateVersion),
                ("$type", domainEvent.GetType().Name),
                ("$schemaVersion", domainEvent.SchemaVersion),
                ("$visibility", domainEvent.Visibility.ToString()),
                ("$payload", payload),
                ("$priorHash", priorHash));
        }

        return sequence;
    }

    private static async Task<(long Sequence, string PriorHash)> ReadJournalHeadAsync(
        SqliteConnection connection, SessionId sessionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Sequence, PriorHash FROM Event
            WHERE SessionId = $sessionId ORDER BY Sequence DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0), reader.GetString(1))
            : (-1, "sha256:" + new string('0', 64));
    }

    private static async Task<List<JournaledEvent>> ReadJournalAsync(
        SqliteConnection connection,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var journal = new List<JournaledEvent>();
        var priorHash = "sha256:" + new string('0', 64);
        long previousVersion = 1;

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Sequence, StateVersion, Type, Payload, PriorHash, SchemaVersion, Visibility
            FROM Event WHERE SessionId = $sessionId ORDER BY Sequence ASC;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var sequence = reader.GetInt64(0);
            var stateVersion = reader.GetInt64(1);
            var type = reader.GetString(2);
            var payload = (byte[])reader["Payload"];
            var recordedHash = reader.GetString(4);
            var schemaVersion = reader.GetInt32(5);
            var visibility = reader.GetString(6);

            if (sequence != journal.Count || stateVersion < previousVersion || stateVersion > previousVersion + 1 ||
                (sequence == 0 && stateVersion != 1) || schemaVersion != 1)
                throw new SessionIntegrityException($"Journal row {sequence} has invalid ordering or format metadata.");
            previousVersion = stateVersion;

            priorHash = ChainHash(priorHash, type, payload);
            if (!string.Equals(priorHash, recordedHash, StringComparison.Ordinal))
            {
                throw new SessionIntegrityException(
                    $"Journal row {sequence} of {sessionId} does not match its recorded hash chain.");
            }

            try
            {
                var domainEvent = EventSerializer.Deserialize(payload);
                if (domainEvent.GetType().Name != type || domainEvent.SchemaVersion != schemaVersion ||
                    domainEvent.Visibility.ToString() != visibility)
                    throw new SessionIntegrityException($"Journal row {sequence} does not match its event metadata.");
                journal.Add(new JournaledEvent(sequence, stateVersion, domainEvent));
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException)
            {
                // Do not include payloads or deserializer messages in diagnostics; events can be private.
                throw new SessionIntegrityException($"Journal row {sequence} could not be decoded.");
            }
        }

        return journal;
    }

    private sealed record SnapshotHead(long StateVersion, long JournalSequence, long BoardRevision, string StateHash);

    private static async Task<SnapshotHead> ReadLatestSnapshotAsync(
        SqliteConnection connection, SessionId sessionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT StateVersion, JournalSequence, BoardRevision, StateHash FROM Snapshot
            WHERE SessionId = $sessionId ORDER BY StateVersion DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new SessionIntegrityException($"The save for {sessionId} has no authoritative snapshot.");
        return new SnapshotHead(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3));
    }

    private static async Task VerifyCompatibilityAsync(
        SqliteConnection connection, SessionId sessionId, BoardManifest manifest, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT ProfileId, ManifestHash, StoreSchemaVersion, RulesPolicyVersion FROM Session WHERE SessionId = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new SessionIntegrityException($"The save for {sessionId} has no session row.");

        var profileId = reader.GetString(0);
        var manifestHash = reader.GetString(1);
        var schemaVersion = reader.GetInt32(2);

        if (schemaVersion != StoreSchemaVersion)
        {
            throw new SessionIntegrityException(
                $"That save uses an unsupported GoldenTicket store schema ({schemaVersion}).");
        }

        if (reader.GetInt32(3) != manifest.RulesPolicyVersion)
            throw new SessionIntegrityException("That save uses an unsupported rules version.");

        if (!string.Equals(profileId, manifest.ProfileId, StringComparison.Ordinal))
        {
            throw new SessionIntegrityException(
                $"That save belongs to profile '{profileId}', not '{manifest.ProfileId}'.");
        }

        if (!string.Equals(manifestHash, manifest.DataHash, StringComparison.Ordinal))
        {
            throw new SessionIntegrityException(
                "The board data package has changed since that match was saved. " +
                "Restoring it against different route or ticket data would silently alter the game.");
        }

    }

    /// <summary>Writes or re-statuses one checkpoint as JSON.</summary>
    private static async Task WriteCheckpointAsync(
        SqliteConnection connection,
        PackAwayCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            checkpoint, CheckpointSerializerOptions);

        await ExecuteAsync(connection, """
            INSERT OR REPLACE INTO PackAwayCheckpoint (
                SessionId, CheckpointId, Name, CreatedAt, FormatVersion, SourceStateVersion,
                SourceJournalSeq, TargetProvenance, PhotoHash, Status, Payload)
            VALUES (
                $sessionId, $checkpointId, $name, $createdAt, $formatVersion, $sourceStateVersion,
                $sourceJournalSeq, $targetProvenance, $photoHash, $status, $payload);
            """, cancellationToken,
            ("$sessionId", checkpoint.SessionId.Value),
            ("$checkpointId", checkpoint.CheckpointId.Value),
            ("$name", checkpoint.Name),
            ("$createdAt", checkpoint.CreatedAt.ToString("O")),
            ("$formatVersion", checkpoint.FormatVersion),
            ("$sourceStateVersion", checkpoint.SourceStateVersion),
            ("$sourceJournalSeq", checkpoint.SourceJournalSequence),
            ("$targetProvenance", checkpoint.TargetProvenance.ToString()),
            ("$photoHash", (object?)checkpoint.PhotoHash ?? DBNull.Value),
            ("$status", checkpoint.Status.ToString()),
            ("$payload", payload));
    }

    private static readonly System.Text.Json.JsonSerializerOptions CheckpointSerializerOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>
    /// Reads one checkpoint back from storage. DESIGN 19.8 step 6 validates a committed checkpoint by
    /// reading it again rather than trusting the write that produced it.
    /// </summary>
    public async Task<PackAwayCheckpoint?> ReadCheckpointAsync(
        SessionId sessionId, CheckpointId checkpointId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(sessionId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Payload, Name, CreatedAt, FormatVersion, SourceStateVersion,
                SourceJournalSeq, TargetProvenance, PhotoHash, Status FROM PackAwayCheckpoint
            WHERE SessionId = $sessionId AND CheckpointId = $checkpointId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.Value);
        command.Parameters.AddWithValue("$checkpointId", checkpointId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        byte[]? plaintext = null;
        try
        {
            plaintext = (byte[])reader["Payload"];
            var checkpoint = JsonSerializer.Deserialize<PackAwayCheckpoint>(plaintext, CheckpointSerializerOptions);
            if (checkpoint is null || checkpoint.SessionId != sessionId || checkpoint.CheckpointId != checkpointId ||
                checkpoint.FormatVersion != PackAwayCheckpoint.CurrentFormatVersion ||
                checkpoint.Name != reader.GetString(1) || checkpoint.CreatedAt.ToString("O") != reader.GetString(2) ||
                checkpoint.FormatVersion != reader.GetInt32(3) || checkpoint.SourceStateVersion != reader.GetInt64(4) ||
                checkpoint.SourceJournalSequence != reader.GetInt64(5) ||
                checkpoint.TargetProvenance.ToString() != reader.GetString(6) ||
                checkpoint.PhotoHash != (reader.IsDBNull(7) ? null : reader.GetString(7)) ||
                checkpoint.Status.ToString() != reader.GetString(8) ||
                checkpoint.PhysicalTarget.IsDefault ||
                checkpoint.PhysicalTargetHash != PackAwayCheckpoint.HashTarget(checkpoint.PhysicalTarget))
                throw new SessionIntegrityException("The stored checkpoint does not match its metadata or target.");
            return checkpoint;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidCastException)
        {
            throw new SessionIntegrityException("The stored checkpoint could not be decoded.");
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static Task WriteSnapshotAsync(
        SqliteConnection connection,
        SessionId sessionId,
        long stateVersion,
        long journalSequence,
        long boardRevision,
        string stateHash,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, """
            INSERT INTO Snapshot (SessionId, StateVersion, JournalSequence, BoardRevision, StateHash)
            VALUES ($sessionId, $stateVersion, $journalSequence, $boardRevision, $stateHash);
            """, cancellationToken,
            ("$sessionId", sessionId.Value),
            ("$stateVersion", stateVersion),
            ("$journalSequence", journalSequence),
            ("$boardRevision", boardRevision),
            ("$stateHash", stateHash));

    private static Task WriteCommandOutcomeAsync(
        SqliteConnection connection,
        SessionId sessionId,
        StoredCommandOutcome outcome,
        CancellationToken cancellationToken,
        bool ignoreDuplicate = false) =>
        ExecuteAsync(connection, $$"""
            INSERT {{(ignoreDuplicate ? "OR IGNORE " : "")}}INTO CommandResult (
                SessionId, CommandId, Accepted, StateVersionAfter, RejectionCode, RejectionMessage, RecordedAt)
            VALUES ($sessionId, $commandId, $accepted, $version, $code, $message, $recordedAt);
            """, cancellationToken,
            ("$sessionId", sessionId.Value),
            ("$commandId", outcome.CommandId.Value),
            ("$accepted", outcome.Accepted ? 1 : 0),
            ("$version", outcome.StateVersionAfter),
            ("$code", (object?)outcome.RejectionCode ?? DBNull.Value),
            ("$message", (object?)outcome.RejectionMessage ?? DBNull.Value),
            ("$recordedAt", DateTimeOffset.UtcNow.ToString("O")));

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Tamper-evident chaining so an edited journal row is detected on restore.</summary>
    private static string ChainHash(string priorHash, string type, byte[] payload)
    {
        var header = Encoding.UTF8.GetBytes(priorHash + "|" + type + "|");
        var buffer = new byte[header.Length + payload.Length];
        header.CopyTo(buffer, 0);
        payload.CopyTo(buffer, header.Length);

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(buffer));
    }
}
