using System.Collections.Immutable;
using GoldenTicket.AI;
using GoldenTicket.Application;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.Domain.Tests;

/// <summary>
/// Save, pack away and rebuild (DESIGN 19.8, acceptance table 22.8). This build produces state-only
/// checkpoints, so the rows about a verified photograph and a pending placement mask are not
/// exercised here; everything else in the protocol is.
/// </summary>
public class PackAwayTests
{
    private const string Operator = "tester";

    private static SessionSetup Setup(int seatCount = 3, bool allComputer = true)
    {
        var colors = Enum.GetValues<PlayerColor>();
        var seats = Enumerable.Range(0, seatCount)
            .Select(index => new Seat(
                new SeatId(index + 1), $"Seat {index + 1}", colors[index],
                allComputer || index > 0 ? SeatKind.Computer : SeatKind.Human,
                AiDifficulty.Standard))
            .ToImmutableArray();

        return new SessionSetup(SessionId.New(), seats, seats[0].SeatId, VerificationMode.Manual);
    }

    private static GameRules Rules() => new(TestManifest.Manifest, TestManifest.Catalog);

    /// <summary>Plays an all-computer match forward until <paramref name="until"/> is satisfied.</summary>
    private static async Task<GameCoordinator> PlayUntilAsync(
        InMemorySessionStore store, Func<GameCoordinator, bool> until, ulong seed = 4242, int guard = 400)
    {
        var coordinator = await GameCoordinator.CreateAsync(
            Rules(), store, Setup(), DeterministicRandom.SeedFrom(seed), TestContext.Current.CancellationToken);

        var driver = new ComputerSeatDriver(coordinator, new HeuristicAiPolicy(), aiSeed: seed);

        for (var step = 0; step < guard && !until(coordinator); step++)
        {
            await driver.AdvanceAsync(TestContext.Current.CancellationToken);
            if (until(coordinator)) break;

            if (coordinator.Public.PendingClaim is not { } pending) break;

            await coordinator.SubmitAsync(new SubmitClaimEvidence(
                coordinator.NewEnvelope(pending.SeatId), pending.OperationId,
                EvidenceKind.ManualAttestation, Operator, "whole board checked"),
                TestContext.Current.CancellationToken);
        }

        Assert.True(until(coordinator), "The match never reached the state this test needs.");
        return coordinator;
    }

    /// <summary>Packs away, rebuilds and resumes, attesting as the operator would.</summary>
    private static async Task RebuildAndResumeAsync(GameCoordinator coordinator)
    {
        var checkpoint = coordinator.Public.Checkpoint!;
        var token = TestContext.Current.CancellationToken;

        Assert.True((await coordinator.SubmitAsync(
            new BeginBoardRebuild(coordinator.NewEnvelope(), checkpoint.CheckpointId), token)).IsAccepted);

        Assert.True((await coordinator.SubmitAsync(new AttestBoardRebuild(
            coordinator.NewEnvelope(), checkpoint.CheckpointId, checkpoint.PhysicalTargetHash, Operator),
            token)).IsAccepted);

        Assert.True((await coordinator.SubmitAsync(
            new ResumePackedGame(coordinator.NewEnvelope(), checkpoint.CheckpointId), token)).IsAccepted);
    }

    // ---- The core guarantee ----------------------------------------------------------------

    /// <summary>
    /// DESIGN 7.2 invariant 15: packing away and rebuilding never changes card ownership, route
    /// ownership, scores or turn order.
    /// </summary>
    [Fact]
    public async Task PackingAwayAndRebuildingChangesNothingAboutTheGame()
    {
        var store = new InMemorySessionStore();
        var coordinator = await PlayUntilAsync(store, c => c.Public.TurnNumber >= 12);

        var before = await coordinator.ComputeLogicalStateHashAsync(TestContext.Current.CancellationToken);
        var scoresBefore = coordinator.Public.Seats
            .ToDictionary(seat => seat.SeatId, seat => (seat.RouteScore, seat.TrainsRemaining, seat.TrainCardCount));
        var turnBefore = coordinator.Public.TurnNumber;
        var activeBefore = coordinator.Public.ActiveSeatId;

        var saved = await coordinator.SaveAndPackAwayAsync("After twelve turns", TestContext.Current.CancellationToken);
        Assert.True(saved.SafeToPack, saved.Problem);
        Assert.Equal(SessionLifecycle.PackedAway, coordinator.Public.Lifecycle);

        await RebuildAndResumeAsync(coordinator);

        Assert.Equal(SessionLifecycle.Active, coordinator.Public.Lifecycle);
        Assert.Equal(before, await coordinator.ComputeLogicalStateHashAsync(TestContext.Current.CancellationToken));
        Assert.Equal(turnBefore, coordinator.Public.TurnNumber);
        Assert.Equal(activeBefore, coordinator.Public.ActiveSeatId);

        foreach (var seat in coordinator.Public.Seats)
            Assert.Equal(scoresBefore[seat.SeatId], (seat.RouteScore, seat.TrainsRemaining, seat.TrainCardCount));

        Assert.Empty(await coordinator.CheckInvariantsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASavedGameSurvivesRestartAndStillRebuildsToTheSamePosition()
    {
        var store = new InMemorySessionStore();
        var coordinator = await PlayUntilAsync(store, c => c.Public.TurnNumber >= 8);

        var saved = await coordinator.SaveAndPackAwayAsync("Packed", TestContext.Current.CancellationToken);
        Assert.True(saved.SafeToPack);

        var before = await coordinator.ComputeLogicalStateHashAsync(TestContext.Current.CancellationToken);

        // Reopen exactly as a restart would.
        var reopened = await GameCoordinator.RestoreAsync(
            Rules(), store, coordinator.SessionId, TestContext.Current.CancellationToken);

        Assert.Equal(SessionLifecycle.PackedAway, reopened.Public.Lifecycle);
        Assert.Equal(CheckpointStatus.Verified, reopened.Public.Checkpoint!.Status);
        Assert.Equal(before, await reopened.ComputeLogicalStateHashAsync(TestContext.Current.CancellationToken));

        await RebuildAndResumeAsync(reopened);

        Assert.Equal(SessionLifecycle.Active, reopened.Public.Lifecycle);
        Assert.Equal(before, await reopened.ComputeLogicalStateHashAsync(TestContext.Current.CancellationToken));
    }

    // ---- Gates ------------------------------------------------------------------------------

    [Fact]
    public async Task GameplayIsRefusedWhileTheGameIsPackedAway()
    {
        var store = new InMemorySessionStore();
        var coordinator = await PlayUntilAsync(store, c => c.Public.TurnNumber >= 6);

        await coordinator.SaveAndPackAwayAsync("Packed", TestContext.Current.CancellationToken);

        var active = coordinator.Public.ActiveSeatId;
        var draw = await coordinator.SubmitAsync(
            new SelectTrainCard(coordinator.NewEnvelope(active), null), TestContext.Current.CancellationToken);

        Assert.False(draw.IsAccepted);
        Assert.Equal("SessionSuspended", draw.Result.Rejection!.Code);

        // DESIGN 4.5 / 9.2: the computer seats stop too.
        var driver = new ComputerSeatDriver(coordinator, new HeuristicAiPolicy(), aiSeed: 1);
        Assert.Equal(0, await driver.AdvanceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SessionLifecycle.PackedAway, coordinator.Public.Lifecycle);
    }

    [Fact]
    public async Task GameplayIsRefusedWhileASaveIsBeingPrepared()
    {
        var store = new InMemorySessionStore();
        var coordinator = await PlayUntilAsync(store, c => c.Public.TurnNumber >= 6);

        // Stop after the first durable boundary, before the checkpoint exists.
        var requested = await coordinator.SubmitAsync(
            new SaveAndPackAway(coordinator.NewEnvelope(), "Interrupted"), TestContext.Current.CancellationToken);
        Assert.True(requested.IsAccepted);
        Assert.Equal(SessionLifecycle.PreparingPackAway, coordinator.Public.Lifecycle);

        var draw = await coordinator.SubmitAsync(
            new SelectTrainCard(coordinator.NewEnvelope(coordinator.Public.ActiveSeatId), null),
            TestContext.Current.CancellationToken);

        Assert.False(draw.IsAccepted);
        Assert.Equal("SessionSuspended", draw.Result.Rejection!.Code);
        Assert.Null(coordinator.Public.Checkpoint);
    }

    [Fact]
    public async Task ASecondSaveCannotBeStartedWhileOneIsInFlight()
    {
        var store = new InMemorySessionStore();
        var coordinator = await PlayUntilAsync(store, c => c.Public.TurnNumber >= 4);

        await coordinator.SubmitAsync(
            new SaveAndPackAway(coordinator.NewEnvelope(), "First"), TestContext.Current.CancellationToken);

        var second = await coordinator.SubmitAsync(
            new SaveAndPackAway(coordinator.NewEnvelope(), "Second"), TestContext.Current.CancellationToken);

        // The save controls pass the lifecycle gate, so the refusal is the specific one.
        Assert.False(second.IsAccepted);
        Assert.Equal("CannotPackAwayNow", second.Result.Rejection!.Code);
        Assert.Equal(SessionLifecycle.PreparingPackAway, coordinator.Public.Lifecycle);
    }

    // ---- The readback boundary ---------------------------------------------------------------

    /// <summary>
    /// DESIGN 19.8 step 6: a committed checkpoint is not a safe-to-pack result. Only the verified
    /// one is, and a restart between the two repeats the validation.
    /// </summary>
    [Fact]
    public async Task ACommittedCheckpointIsNotSafeToPackUntilItHasBeenReadBack()
    {
        var store = new InMemorySessionStore();
        var coordinator = await PlayUntilAsync(store, c => c.Public.TurnNumber >= 5);
        var token = TestContext.Current.CancellationToken;

        var requested = await coordinator.SubmitAsync(new SaveAndPackAway(coordinator.NewEnvelope(), "Half"), token);
        Assert.True(requested.IsAccepted);

        // Write the checkpoint without running the readback, which is where a crash could land.
        var checkpointId = (await coordinator.PendingCheckpointIdAsync(token))!.Value;
        Assert.True((await coordinator.SubmitAsync(
            new CommitPackAwayCheckpoint(coordinator.NewEnvelope(), checkpointId), token)).IsAccepted);

        Assert.Equal(SessionLifecycle.PackedAway, coordinator.Public.Lifecycle);
        Assert.Equal(CheckpointStatus.CommittedAwaitingReadback, coordinator.Public.Checkpoint!.Status);
        Assert.False(coordinator.Public.Checkpoint.IsSafeToPackAway);

        // A rebuild cannot start from an unvalidated checkpoint.
        var early = await coordinator.SubmitAsync(new BeginBoardRebuild(coordinator.NewEnvelope(), checkpointId), token);
        Assert.False(early.IsAccepted);
        Assert.Equal("CheckpointNotVerified", early.Result.Rejection!.Code);

        // Restarting repeats the validation rather than reporting success from the committed row.
        var reopened = await GameCoordinator.RestoreAsync(Rules(), store, coordinator.SessionId, token);
        Assert.Equal(CheckpointStatus.CommittedAwaitingReadback, reopened.Public.Checkpoint!.Status);

        var finished = await reopened.ContinuePackAwayAsync(token);
        Assert.True(finished.SafeToPack, finished.Problem);
        Assert.Equal(CheckpointStatus.Verified, reopened.Public.Checkpoint!.Status);
    }

    [Fact]
    public async Task AnInterruptedPreparationStaysPausedAndIsRetriedAgainstTheSameSource()
    {
        var store = new InMemorySessionStore();
        var coordinator = await PlayUntilAsync(store, c => c.Public.TurnNumber >= 7);
        var token = TestContext.Current.CancellationToken;

        await coordinator.SubmitAsync(new SaveAndPackAway(coordinator.NewEnvelope(), "Interrupted"), token);
        var frozen = await coordinator.ComputeLogicalStateHashAsync(token);

        // Restart in the middle of preparation: still paused, no checkpoint, nothing claimed safe.
        var reopened = await GameCoordinator.RestoreAsync(Rules(), store, coordinator.SessionId, token);
        Assert.Equal(SessionLifecycle.PreparingPackAway, reopened.Public.Lifecycle);
        Assert.Null(reopened.Public.Checkpoint);

        var finished = await reopened.ContinuePackAwayAsync(token);

        Assert.True(finished.SafeToPack, finished.Problem);
        Assert.Equal(frozen, finished.Checkpoint!.LogicalStateHash);
    }

    [Fact]
    public async Task CancellingPreparationReturnsToPlay()
    {
        var store = new InMemorySessionStore();
        var coordinator = await PlayUntilAsync(store, c => c.Public.TurnNumber >= 4);
        var token = TestContext.Current.CancellationToken;

        var before = await coordinator.ComputeLogicalStateHashAsync(token);
        await coordinator.SubmitAsync(new SaveAndPackAway(coordinator.NewEnvelope(), "Never mind"), token);

        var cancelled = await coordinator.SubmitAsync(
            new CancelPackAwayPreparation(coordinator.NewEnvelope(), "the board was disturbed"), token);

        Assert.True(cancelled.IsAccepted);
        Assert.Equal(SessionLifecycle.Active, coordinator.Public.Lifecycle);
        Assert.Null(coordinator.Public.Checkpoint);
        Assert.Equal(before, await coordinator.ComputeLogicalStateHashAsync(token));
    }

    // ---- Suspending an unfinished action -----------------------------------------------------

    /// <summary>
    /// DESIGN 19.8: a save may suspend an authorised placement. No payment or score happens during
    /// capture, cleanup or rebuild, and the claim completes at most once after resume.
    /// </summary>
    [Fact]
    public async Task APendingPlacementSurvivesTheSaveAndCommitsExactlyOnceAfterResume()
    {
        var store = new InMemorySessionStore();
        var coordinator = await PlayUntilAsync(store, c => c.Public.PendingClaim is not null);
        var token = TestContext.Current.CancellationToken;

        var pending = coordinator.Public.PendingClaim!;
        var seatBefore = coordinator.Public.SeatOf(pending.SeatId);
        var scoreBefore = seatBefore.RouteScore;
        var trainsBefore = seatBefore.TrainsRemaining;
        var cardsBefore = seatBefore.TrainCardCount;

        var saved = await coordinator.SaveAndPackAwayAsync("Mid placement", token);
        Assert.True(saved.SafeToPack, saved.Problem);

        // The suspended operation is recorded, and nothing was spent or scored by saving.
        Assert.Equal(TurnPhase.AwaitingPhysicalPlacement, saved.Checkpoint!.SuspendedTurnPhase);
        Assert.Equal(pending.OperationId, saved.Checkpoint.PendingOperationId);
        Assert.Equal(scoreBefore, coordinator.Public.SeatOf(pending.SeatId).RouteScore);
        Assert.Equal(trainsBefore, coordinator.Public.SeatOf(pending.SeatId).TrainsRemaining);
        Assert.Equal(cardsBefore, coordinator.Public.SeatOf(pending.SeatId).TrainCardCount);

        // The saved target contains only committed routes: the pending claim is not on the board.
        Assert.DoesNotContain(saved.Checkpoint.PhysicalTarget, route => route.RouteId == pending.RouteId);
        Assert.Equal(TargetProvenance.LogicalStateOnly, saved.Checkpoint.TargetProvenance);
        Assert.Null(saved.Checkpoint.PhotoHash);

        await RebuildAndResumeAsync(coordinator);

        // Resume restored the placement instruction; it did not commit the route.
        Assert.Equal(TurnPhase.AwaitingPhysicalPlacement, coordinator.Public.TurnPhase);
        Assert.Equal(pending.OperationId, coordinator.Public.PendingClaim!.OperationId);
        Assert.Equal(scoreBefore, coordinator.Public.SeatOf(pending.SeatId).RouteScore);

        // The ordinary protocol then completes it once, and only once.
        var first = await coordinator.SubmitAsync(new SubmitClaimEvidence(
            coordinator.NewEnvelope(pending.SeatId), pending.OperationId,
            EvidenceKind.ManualAttestation, Operator, "whole board checked"), token);
        Assert.True(first.IsAccepted);

        var after = coordinator.Public.SeatOf(pending.SeatId);
        Assert.True(after.RouteScore > scoreBefore);
        Assert.True(after.TrainsRemaining < trainsBefore);

        var again = await coordinator.SubmitAsync(new SubmitClaimEvidence(
            coordinator.NewEnvelope(pending.SeatId), pending.OperationId,
            EvidenceKind.ManualAttestation, Operator, "again"), token);
        Assert.False(again.IsAccepted);

        Assert.Empty(await coordinator.CheckInvariantsAsync(token));
    }

    /// <summary>
    /// DESIGN 19.8 / 22.8: saving after the first card of a draw restores the same revealed result
    /// and the same remaining choice, with no reroll and no extra draw.
    /// </summary>
    [Fact]
    public async Task SavingAfterTheFirstDrawRestoresTheSameRevealedCardAndRemainingChoice()
    {
        var store = new InMemorySessionStore();
        var rules = Rules();
        var token = TestContext.Current.CancellationToken;

        var coordinator = await GameCoordinator.CreateAsync(
            rules, store, Setup(3, allComputer: false), DeterministicRandom.SeedFrom(99), token);

        // Seat 1 is human: settle its opening tickets, then let the computers finish setup.
        var human = new SeatId(1);
        var view = await coordinator.GetSeatViewAsync(human, token);
        await coordinator.SubmitAsync(new CommitTicketSelection(
            coordinator.NewEnvelope(human), [.. view.SetupOffer.Take(2)], []), token);

        var driver = new ComputerSeatDriver(coordinator, new HeuristicAiPolicy(), aiSeed: 99);
        await driver.AdvanceAsync(token);

        while (coordinator.Public.ActiveSeatId != human)
        {
            if (coordinator.Public.PendingClaim is { } pending)
            {
                await coordinator.SubmitAsync(new SubmitClaimEvidence(
                    coordinator.NewEnvelope(pending.SeatId), pending.OperationId,
                    EvidenceKind.ManualAttestation, Operator, "checked"), token);
            }

            await driver.AdvanceAsync(token);
        }

        // Take one card, leaving the turn mid-draw.
        await coordinator.SubmitAsync(new SelectTrainCard(coordinator.NewEnvelope(human), null), token);
        Assert.Equal(TurnPhase.AwaitingSecondTrainCard, coordinator.Public.TurnPhase);

        var handBefore = (await coordinator.GetSeatViewAsync(human, token)).Hand
            .Select(card => card.Id).OrderBy(id => id.Value).ToArray();

        var saved = await coordinator.SaveAndPackAwayAsync("Mid draw", token);
        Assert.True(saved.SafeToPack, saved.Problem);
        Assert.Equal(TurnPhase.AwaitingSecondTrainCard, saved.Checkpoint!.SuspendedTurnPhase);

        await RebuildAndResumeAsync(coordinator);

        Assert.Equal(TurnPhase.AwaitingSecondTrainCard, coordinator.Public.TurnPhase);
        Assert.Equal(human, coordinator.Public.ActiveSeatId);

        var handAfter = (await coordinator.GetSeatViewAsync(human, token)).Hand
            .Select(card => card.Id).OrderBy(id => id.Value).ToArray();

        Assert.Equal(handBefore, handAfter);   // the revealed card is still the same instance
    }

    // ---- Rebuild checks ----------------------------------------------------------------------

    [Fact]
    public async Task AnAttestationForADifferentBoardIsRefused()
    {
        var store = new InMemorySessionStore();
        var coordinator = await PlayUntilAsync(store, c => c.Public.TurnNumber >= 6);
        var token = TestContext.Current.CancellationToken;

        await coordinator.SaveAndPackAwayAsync("Packed", token);
        var checkpoint = coordinator.Public.Checkpoint!;

        await coordinator.SubmitAsync(new BeginBoardRebuild(coordinator.NewEnvelope(), checkpoint.CheckpointId), token);

        var wrong = await coordinator.SubmitAsync(new AttestBoardRebuild(
            coordinator.NewEnvelope(), checkpoint.CheckpointId, "sha256:not-the-saved-board", Operator), token);

        Assert.False(wrong.IsAccepted);
        Assert.Equal("TargetHashMismatch", wrong.Result.Rejection!.Code);

        // Resume is still blocked, because nothing was attested.
        var resume = await coordinator.SubmitAsync(
            new ResumePackedGame(coordinator.NewEnvelope(), checkpoint.CheckpointId), token);

        Assert.False(resume.IsAccepted);
        Assert.Equal("BoardNotConfirmed", resume.Result.Rejection!.Code);
    }

    [Fact]
    public async Task ResumeCannotRunTwice()
    {
        var store = new InMemorySessionStore();
        var coordinator = await PlayUntilAsync(store, c => c.Public.TurnNumber >= 6);
        var token = TestContext.Current.CancellationToken;

        await coordinator.SaveAndPackAwayAsync("Packed", token);
        var checkpointId = coordinator.Public.Checkpoint!.CheckpointId;

        await RebuildAndResumeAsync(coordinator);
        Assert.Equal(SessionLifecycle.Active, coordinator.Public.Lifecycle);

        var again = await coordinator.SubmitAsync(
            new ResumePackedGame(coordinator.NewEnvelope(), checkpointId), token);

        Assert.False(again.IsAccepted);
        Assert.Equal("NotRebuilding", again.Result.Rejection!.Code);

        var resumes = store.JournalOf(coordinator.SessionId).Count(row => row.Event is PackedGameResumed);
        Assert.Equal(1, resumes);
    }

    [Fact]
    public async Task ARebuildCannotBeStartedForAnotherMatchesCheckpoint()
    {
        var store = new InMemorySessionStore();
        var coordinator = await PlayUntilAsync(store, c => c.Public.TurnNumber >= 4);
        var token = TestContext.Current.CancellationToken;

        await coordinator.SaveAndPackAwayAsync("Packed", token);

        var wrong = await coordinator.SubmitAsync(
            new BeginBoardRebuild(coordinator.NewEnvelope(), CheckpointId.New()), token);

        Assert.False(wrong.IsAccepted);
        Assert.Equal("CheckpointMismatch", wrong.Result.Rejection!.Code);
    }

    // ---- Privacy ------------------------------------------------------------------------------

    /// <summary>
    /// DESIGN 22.8: the public rebuild payload carries board and public placement information only,
    /// never hidden cards or destination tickets.
    /// </summary>
    [Fact]
    public async Task TheRebuildTargetAndItsHistoryCarryNoPrivateInformation()
    {
        var store = new InMemorySessionStore();
        var coordinator = await PlayUntilAsync(store, c => c.Public.TurnNumber >= 10);
        var token = TestContext.Current.CancellationToken;

        var seat = coordinator.Public.Seats[0].SeatId;
        var secretTickets = (await coordinator.GetSeatViewAsync(seat, token)).Tickets;
        var secretCards = (await coordinator.GetSeatViewAsync(seat, token)).Hand.Select(card => card.Id).ToArray();

        await coordinator.SaveAndPackAwayAsync("Packed", token);

        var payload = System.Text.Json.JsonSerializer.Serialize(coordinator.Public.Checkpoint);

        foreach (var ticket in secretTickets)
            Assert.DoesNotContain(ticket.Value, payload, StringComparison.Ordinal);

        foreach (var card in secretCards)
            Assert.DoesNotContain($"\"{card.Value}\"", payload, StringComparison.Ordinal);

        // The target is the board: every entry is a claimed route with its owner and length.
        Assert.All(coordinator.Public.Checkpoint!.PhysicalTarget, route =>
        {
            Assert.True(TestManifest.Manifest.TryGetRoute(route.RouteId, out var definition));
            Assert.Equal(definition.Length, route.Length);
            Assert.Contains(coordinator.Public.Seats, s => s.SeatId == route.SeatId);
        });

        var history = string.Join("\n", coordinator.PublicHistory.Select(entry => entry.Text));
        foreach (var ticket in secretTickets)
            Assert.DoesNotContain(ticket.Value, history, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSavedTargetMatchesTheCommittedBoardExactly()
    {
        var store = new InMemorySessionStore();
        var coordinator = await PlayUntilAsync(store, c => c.Public.TurnNumber >= 15);
        var token = TestContext.Current.CancellationToken;

        var owners = coordinator.Public.RouteOwners;
        await coordinator.SaveAndPackAwayAsync("Packed", token);

        var target = coordinator.Public.Checkpoint!.PhysicalTarget;

        Assert.Equal(owners.Count, target.Length);
        foreach (var route in target)
            Assert.Equal(owners[route.RouteId], route.SeatId);

        Assert.Equal(
            target.Sum(route => route.Length),
            coordinator.Public.Seats.Sum(seat => 45 - seat.TrainsRemaining));
    }

    // ---- Naming --------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ASaveNeedsAName(string name)
    {
        var store = new InMemorySessionStore();
        var coordinator = await PlayUntilAsync(store, c => c.Public.TurnNumber >= 3);

        var refused = await coordinator.SubmitAsync(
            new SaveAndPackAway(coordinator.NewEnvelope(), name), TestContext.Current.CancellationToken);

        Assert.False(refused.IsAccepted);
        Assert.Equal("SaveNameMissing", refused.Result.Rejection!.Code);
        Assert.Equal(SessionLifecycle.Active, coordinator.Public.Lifecycle);
    }

    [Fact]
    public async Task AnOverlongSaveNameIsRefused()
    {
        var store = new InMemorySessionStore();
        var coordinator = await PlayUntilAsync(store, c => c.Public.TurnNumber >= 3);

        var refused = await coordinator.SubmitAsync(
            new SaveAndPackAway(coordinator.NewEnvelope(), new string('x', GameRules.MaximumCheckpointNameLength + 1)),
            TestContext.Current.CancellationToken);

        Assert.False(refused.IsAccepted);
        Assert.Equal("SaveNameTooLong", refused.Result.Rejection!.Code);
    }
}

/// <summary>
/// The same protocol against the real SQLite store, so the checkpoint table, its encryption and the
/// readback path are exercised rather than an in-memory stand-in.
/// </summary>
public class PackAwayDurabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "GoldenTicket.PackAway", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A held handle on a temporary directory must not fail the run.
        }
    }

    /// <summary>
    /// Plays computer turns, standing in for the operator on each placement, until the match reaches
    /// the wanted turn. Bounded so a stalled match fails the test instead of hanging the run.
    /// </summary>
    private static async Task PlayToTurnAsync(
        GameCoordinator coordinator, int targetTurn, ulong aiSeed, CancellationToken token)
    {
        var driver = new ComputerSeatDriver(coordinator, new HeuristicAiPolicy(), aiSeed);

        for (var step = 0; step < 400 && coordinator.Public.TurnNumber < targetTurn; step++)
        {
            var advanced = await driver.AdvanceAsync(token);

            if (coordinator.Public.PendingClaim is { } pending)
            {
                var evidence = await coordinator.SubmitAsync(new SubmitClaimEvidence(
                    coordinator.NewEnvelope(pending.SeatId), pending.OperationId,
                    EvidenceKind.ManualAttestation, "tester", "whole board checked"), token);

                Assert.True(evidence.IsAccepted, evidence.Result.Rejection?.Code);
                continue;
            }

            Assert.True(advanced > 0, $"The match stalled in phase {coordinator.Public.TurnPhase}.");
        }

        Assert.True(coordinator.Public.TurnNumber >= targetTurn,
            $"The match did not reach turn {targetTurn}.");
    }

    private static SessionSetup Setup()
    {
        var colors = Enum.GetValues<PlayerColor>();
        var seats = Enumerable.Range(0, 3)
            .Select(index => new Seat(
                new SeatId(index + 1), $"Seat {index + 1}", colors[index],
                SeatKind.Computer, AiDifficulty.Standard))
            .ToImmutableArray();

        return new SessionSetup(SessionId.New(), seats, seats[0].SeatId, VerificationMode.Manual);
    }

    [Fact]
    public async Task ACheckpointIsWrittenReadBackAndRebuiltFromDisk()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new Persistence.SqliteSessionStore(_root);
        var rules = new GameRules(TestManifest.Manifest, TestManifest.Catalog);

        var coordinator = await GameCoordinator.CreateAsync(
            rules, store, Setup(), DeterministicRandom.SeedFrom(2026), token);

        await PlayToTurnAsync(coordinator, targetTurn: 10, aiSeed: 2026, token);

        var before = await coordinator.ComputeLogicalStateHashAsync(token);

        var saved = await coordinator.SaveAndPackAwayAsync("On disk", token);
        Assert.True(saved.SafeToPack, saved.Problem);

        // The row is really in the database and decrypts to the same checkpoint.
        var stored = await store.ReadCheckpointAsync(coordinator.SessionId, saved.Checkpoint!.CheckpointId, token);
        Assert.NotNull(stored);
        Assert.Equal(saved.Checkpoint.LogicalStateHash, stored!.LogicalStateHash);
        Assert.Equal(saved.Checkpoint.PhysicalTargetHash, stored.PhysicalTargetHash);
        Assert.Equal(CheckpointStatus.Verified, stored.Status);
        Assert.Equal(TargetProvenance.LogicalStateOnly, stored.TargetProvenance);
        Assert.Null(stored.PhotoHash);

        // Reopen from disk, rebuild and resume.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var reopened = await GameCoordinator.RestoreAsync(rules, store, coordinator.SessionId, token);

        Assert.Equal(SessionLifecycle.PackedAway, reopened.Public.Lifecycle);
        var checkpoint = reopened.Public.Checkpoint!;

        Assert.True((await reopened.SubmitAsync(
            new BeginBoardRebuild(reopened.NewEnvelope(), checkpoint.CheckpointId), token)).IsAccepted);
        Assert.True((await reopened.SubmitAsync(new AttestBoardRebuild(
            reopened.NewEnvelope(), checkpoint.CheckpointId, checkpoint.PhysicalTargetHash, "tester"),
            token)).IsAccepted);
        Assert.True((await reopened.SubmitAsync(
            new ResumePackedGame(reopened.NewEnvelope(), checkpoint.CheckpointId), token)).IsAccepted);

        Assert.Equal(SessionLifecycle.Active, reopened.Public.Lifecycle);
        Assert.Equal(before, await reopened.ComputeLogicalStateHashAsync(token));
        Assert.Empty(await reopened.CheckInvariantsAsync(token));
    }

    /// <summary>
    /// DESIGN 19.2: the checkpoint carries the logical-state fingerprint, so its row is encrypted
    /// like every other non-public payload rather than sitting in the file in clear text.
    /// </summary>
    [Fact]
    public async Task TheCheckpointRowIsNotReadableInTheDatabaseFile()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new Persistence.SqliteSessionStore(_root);
        var rules = new GameRules(TestManifest.Manifest, TestManifest.Catalog);

        var coordinator = await GameCoordinator.CreateAsync(
            rules, store, Setup(), DeterministicRandom.SeedFrom(7), token);

        await PlayToTurnAsync(coordinator, targetTurn: 4, aiSeed: 7, token);

        var saved = await coordinator.SaveAndPackAwayAsync("Secretive", token);
        Assert.True(saved.SafeToPack, saved.Problem);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var text = System.Text.Encoding.UTF8.GetString(
            await File.ReadAllBytesAsync(store.DatabasePath(coordinator.SessionId), token));

        // The fingerprint is derived from hands, deck order and private offers, so it belongs inside
        // the encrypted payload and must not appear as a readable column.
        Assert.DoesNotContain(saved.Checkpoint!.LogicalStateHash, text, StringComparison.Ordinal);
        Assert.DoesNotContain(saved.Checkpoint.PhysicalTargetHash, text, StringComparison.Ordinal);

        // The name is the operator's own label and is deliberately listable without decrypting.
        Assert.Contains("Secretive", text, StringComparison.Ordinal);
    }
}
