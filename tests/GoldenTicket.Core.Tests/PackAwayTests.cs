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
/// exercised here. These tests cover the implemented manual, state-only continuation protocol.
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

    [Fact]
    public async Task SavingDuringClaimCancellationPreservesTheRemovalWorkflow()
    {
        var store = new InMemorySessionStore();
        var token = TestContext.Current.CancellationToken;
        var coordinator = await PlayUntilAsync(store, c => c.Public.PendingClaim is not null);
        var pending = coordinator.Public.PendingClaim!;
        await coordinator.SubmitAsync(new CancelPendingClaim(
            coordinator.NewEnvelope(pending.SeatId), pending.OperationId, TrainsWerePlaced: true), token);
        var scoreBefore = coordinator.Public.SeatOf(pending.SeatId).RouteScore;
        var saved = await coordinator.SaveAndPackAwayAsync("Cancelling placement", token);
        Assert.True(saved.SafeToPack, saved.Problem);
        Assert.Equal(TurnPhase.RestoreBeforeState, saved.Checkpoint!.SuspendedTurnPhase);
        Assert.DoesNotContain(saved.Checkpoint.PhysicalTarget, route => route.RouteId == pending.RouteId);
        await RebuildAndResumeAsync(coordinator);
        Assert.Equal(TurnPhase.RestoreBeforeState, coordinator.Public.TurnPhase);
        var restored = await coordinator.SubmitAsync(new ConfirmBeforeStateRestored(
            coordinator.NewEnvelope(pending.SeatId), pending.OperationId), token);
        Assert.True(restored.IsAccepted);
        Assert.Null(coordinator.Public.PendingClaim);
        Assert.Equal(scoreBefore, coordinator.Public.SeatOf(pending.SeatId).RouteScore);
        Assert.Empty(await coordinator.CheckInvariantsAsync(token));
    }

    [Fact]
    public async Task SavingAnOpenTicketOfferPreservesTheExactOfferAndRemainingDeck()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new InMemorySessionStore();
        var coordinator = await GameCoordinator.CreateAsync(Rules(), store, Setup(), DeterministicRandom.SeedFrom(21), token);
        foreach (var seat in coordinator.Seats)
        {
            var offered = (await coordinator.GetSeatViewAsync(seat.SeatId, token)).SetupOffer;
            await coordinator.SubmitAsync(new CommitTicketSelection(
                coordinator.NewEnvelope(seat.SeatId), [.. offered.Take(2)], []), token);
        }
        var active = coordinator.Public.ActiveSeatId;
        var request = await coordinator.SubmitAsync(new RequestTicketOffer(coordinator.NewEnvelope(active)), token);
        Assert.True(request.IsAccepted);
        var before = await coordinator.ComputeLogicalStateHashAsync(token);
        var offeredBefore = (await coordinator.GetSeatViewAsync(active, token)).Offer!;
        Assert.True((await coordinator.SaveAndPackAwayAsync("Open tickets", token)).SafeToPack);
        var restored = await GameCoordinator.RestoreAsync(Rules(), store, coordinator.SessionId, token);
        await RebuildAndResumeAsync(restored);
        Assert.Equal(before, await restored.ComputeLogicalStateHashAsync(token));
        var offeredAfter = (await restored.GetSeatViewAsync(active, token)).Offer!;
        Assert.Equal(offeredBefore.Offered.ToArray(), offeredAfter.Offered.ToArray());
        Assert.Equal(offeredBefore.MinimumKeep, offeredAfter.MinimumKeep);
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

    [Fact]
    public async Task RestoringAnAttestedRebuildRequiresAFreshPhysicalCheck()
    {
        var store = new InMemorySessionStore();
        var token = TestContext.Current.CancellationToken;
        var coordinator = await PlayUntilAsync(store, c => c.Public.TurnNumber >= 3);
        var saved = await coordinator.SaveAndPackAwayAsync("Before restart", token);
        var checkpoint = saved.Checkpoint!;
        await coordinator.SubmitAsync(new BeginBoardRebuild(coordinator.NewEnvelope(), checkpoint.CheckpointId), token);
        await coordinator.SubmitAsync(new AttestBoardRebuild(
            coordinator.NewEnvelope(), checkpoint.CheckpointId, checkpoint.PhysicalTargetHash, Operator), token);
        Assert.True(coordinator.Public.RebuildAttested);

        var restored = await GameCoordinator.RestoreAsync(Rules(), store, coordinator.SessionId, token);
        Assert.False(restored.Public.RebuildAttested);
        var resume = await restored.SubmitAsync(new ResumePackedGame(restored.NewEnvelope(), checkpoint.CheckpointId), token);
        Assert.Equal("BoardNotConfirmed", resume.Result.Rejection?.Code);
        await RebuildAndResumeAsync(restored);
    }

    [Fact]
    public async Task AHistoricalVerifiedCheckpointDoesNotAuthorizePackingDuringRebuild()
    {
        var store = new InMemorySessionStore();
        var token = TestContext.Current.CancellationToken;
        var coordinator = await PlayUntilAsync(store, c => c.Public.TurnNumber >= 3);
        var saved = await coordinator.SaveAndPackAwayAsync("Packed", token);
        await coordinator.SubmitAsync(new BeginBoardRebuild(coordinator.NewEnvelope(), saved.Checkpoint!.CheckpointId), token);
        var oldResult = await coordinator.ContinuePackAwayAsync(token);
        Assert.False(oldResult.SafeToPack);
        Assert.Equal("NotPackedAway", oldResult.Rejection?.Code);
    }

    [Fact]
    public async Task ConcurrentReadbackRetriesCompleteTheSameCheckpointOnce()
    {
        var store = new InMemorySessionStore();
        var token = TestContext.Current.CancellationToken;
        var coordinator = await PlayUntilAsync(store, c => c.Public.TurnNumber >= 3);
        await coordinator.SubmitAsync(new SaveAndPackAway(coordinator.NewEnvelope(), "Retry"), token);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => coordinator.ContinuePackAwayAsync(token)));
        Assert.All(outcomes, outcome => Assert.True(outcome.SafeToPack, outcome.Problem));
        Assert.Single(store.JournalOf(coordinator.SessionId), row => row.Event is PackAwayCheckpointCommitted);
        Assert.Single(store.JournalOf(coordinator.SessionId), row => row.Event is PackAwayCheckpointVerified);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("source")]
    [InlineData("target")]
    public async Task ReadbackChecksTheWholeCheckpointAndCanRetryAfterFailure(string corruption)
    {
        var store = new InMemorySessionStore();
        var token = TestContext.Current.CancellationToken;
        var original = await PlayUntilAsync(store, c => c.Public.TurnNumber >= 8);
        var readbackStore = new CheckpointReadStore(store)
        {
            Corrupt = checkpoint => corruption switch
            {
                "identity" => checkpoint with { CheckpointId = CheckpointId.New() },
                "source" => checkpoint with { SourceStateVersion = checkpoint.SourceStateVersion + 1 },
                _ => checkpoint with { PhysicalTarget = [new TargetRoute(new RouteId("wrong"), new SeatId(1), 1)] },
            },
        };
        var coordinator = await GameCoordinator.RestoreAsync(Rules(), readbackStore, original.SessionId, token);
        var failed = await coordinator.SaveAndPackAwayAsync("Validate every field", token);
        Assert.False(failed.SafeToPack);
        Assert.Equal(CheckpointStatus.Faulted, coordinator.Public.Checkpoint!.Status);

        readbackStore.Corrupt = null;
        var retry = await coordinator.ContinuePackAwayAsync(token);
        Assert.True(retry.SafeToPack, retry.Problem);
        Assert.Equal(failed.Checkpoint!.CheckpointId, retry.Checkpoint!.CheckpointId);
        Assert.Single(store.JournalOf(original.SessionId), row => row.Event is PackAwayCheckpointCommitted);
    }

    [Fact]
    public void LogicalHashCoversAcceptedPoliciesAndConsecutivePasses()
    {
        var state = RulesHarness.Create().State;
        var original = StateHash.ComputeLogical(state);
        state.AcceptedRulesPolicies = state.AcceptedRulesPolicies.Add("policy", "version");
        Assert.False(StateHash.MatchesLogical(state, original));
        var withPolicy = StateHash.ComputeLogical(state);
        state.ConsecutivePasses = 1;
        Assert.False(StateHash.MatchesLogical(state, withPolicy));
        Assert.True(StateHash.MatchesLogical(state, StateHash.ComputeLogical(state)));
        Assert.False(StateHash.MatchesLogical(state, "logical-v99:unsupported"));
    }

    [Fact]
    public void LogicalHashStillReadsTheOriginalCheckpointFormat()
    {
        var state = RulesHarness.Create().State;
        var normalised = state.Fork();
        normalised.StateVersion = 0;
        normalised.JournalSequence = 0;
        normalised.Lifecycle = SessionLifecycle.Active;
        var legacy = "logical-v1:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(StateHash.Canonicalize(normalised))));
        Assert.True(StateHash.MatchesLogical(state, legacy));
    }

    private sealed class CheckpointReadStore(ISessionStore inner) : ISessionStore
    {
        public Func<PackAwayCheckpoint, PackAwayCheckpoint>? Corrupt { get; set; }
        public Task CreateAsync(GameState state, CommandId commandId, Transition transition, string hash, CancellationToken ct) =>
            inner.CreateAsync(state, commandId, transition, hash, ct);
        public Task<StoredCommandOutcome?> FindCommandOutcomeAsync(SessionId id, CommandId command, CancellationToken ct) =>
            inner.FindCommandOutcomeAsync(id, command, ct);
        public Task CommitAsync(GameState state, StoredCommandOutcome outcome, Transition transition, string hash, CancellationToken ct) =>
            inner.CommitAsync(state, outcome, transition, hash, ct);
        public Task RecordRejectionAsync(SessionId id, StoredCommandOutcome outcome, CancellationToken ct) =>
            inner.RecordRejectionAsync(id, outcome, ct);
        public Task<RestoredSession> RestoreAsync(SessionId id, Manifest.BoardManifest manifest, CardCatalog catalog, CancellationToken ct) =>
            inner.RestoreAsync(id, manifest, catalog, ct);
        public async Task<PackAwayCheckpoint?> ReadCheckpointAsync(SessionId id, CheckpointId checkpointId, CancellationToken ct)
        {
            var checkpoint = await inner.ReadCheckpointAsync(id, checkpointId, ct);
            return checkpoint is not null && Corrupt is { } corrupt ? corrupt(checkpoint) : checkpoint;
        }
        public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken ct) => inner.ListSessionsAsync(ct);
        public Task DeleteSessionAsync(SessionId id, CancellationToken ct) => inner.DeleteSessionAsync(id, ct);
    }

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
/// The same protocol against the real SQLite store, so the checkpoint table, its readable payload and the
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

        // The row is really in the database and reads back as the same checkpoint.
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

    [Fact]
    public async Task ComputerPlacementReloadsFromDiskWithTheSameTurnAndPaymentReservedExactlyOnce()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new Persistence.SqliteSessionStore(_root);
        var rules = new GameRules(TestManifest.Manifest, TestManifest.Catalog);
        var setup = Setup();
        var coordinator = await GameCoordinator.CreateAsync(
            rules, store, setup, DeterministicRandom.SeedFrom(2026), token);
        var driver = new ComputerSeatDriver(coordinator, new HeuristicAiPolicy(), aiSeed: 2026);

        // Save a seat other than the starting seat, so restoring to the default player fails.
        for (var step = 0; step < 400; step++)
        {
            await driver.AdvanceAsync(token);
            if (coordinator.Public.PendingClaim is not { } placement) continue;
            if (placement.SeatId != setup.Seats[0].SeatId) break;
            Assert.True((await coordinator.SubmitAsync(new SubmitClaimEvidence(
                coordinator.NewEnvelope(placement.SeatId), placement.OperationId,
                EvidenceKind.ManualAttestation, "tester", "whole board checked"), token)).IsAccepted);
        }

        var pending = Assert.IsType<GoldenTicket.Domain.Projections.PublicPendingClaim>(coordinator.Public.PendingClaim);
        Assert.NotEqual(setup.Seats[0].SeatId, pending.SeatId);
        Assert.Equal(SeatKind.Computer, coordinator.Public.SeatOf(pending.SeatId).Kind);
        var turn = coordinator.Public.TurnNumber;
        var before = await coordinator.GetSeatViewAsync(pending.SeatId, token);
        var logicalHash = await coordinator.ComputeLogicalStateHashAsync(token);
        Assert.NotEmpty(before.ReservedCards);

        var saved = await coordinator.SaveAndPackAwayAsync("Computer placing trains", token);
        Assert.True(saved.SafeToPack, saved.Problem);
        Assert.Equal(pending.OperationId, saved.Checkpoint!.PendingOperationId);
        Assert.DoesNotContain(saved.Checkpoint.PhysicalTarget, route => route.RouteId == pending.RouteId);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var reopened = await GameCoordinator.RestoreAsync(
            rules, new Persistence.SqliteSessionStore(_root), coordinator.SessionId, token);
        var checkpoint = reopened.Public.Checkpoint!;
        Assert.Equal(pending.SeatId, reopened.Public.ActiveSeatId);
        Assert.Equal(turn, reopened.Public.TurnNumber);
        Assert.Equal(pending, reopened.Public.PendingClaim);
        Assert.Equal(logicalHash, await reopened.ComputeLogicalStateHashAsync(token));
        Assert.True((await reopened.SubmitAsync(
            new BeginBoardRebuild(reopened.NewEnvelope(), checkpoint.CheckpointId), token)).IsAccepted);
        Assert.True((await reopened.SubmitAsync(new AttestBoardRebuild(
            reopened.NewEnvelope(), checkpoint.CheckpointId, checkpoint.PhysicalTargetHash, "tester"), token)).IsAccepted);
        Assert.True((await reopened.SubmitAsync(
            new ResumePackedGame(reopened.NewEnvelope(), checkpoint.CheckpointId), token)).IsAccepted);

        Assert.Equal(pending.SeatId, reopened.Public.ActiveSeatId);
        Assert.Equal(turn, reopened.Public.TurnNumber);
        Assert.Equal(TurnPhase.AwaitingPhysicalPlacement, reopened.Public.TurnPhase);
        Assert.Equal(pending, reopened.Public.PendingClaim);
        var resumed = await reopened.GetSeatViewAsync(pending.SeatId, token);
        Assert.Equal(before.Hand.ToArray(), resumed.Hand.ToArray());
        Assert.Equal(before.ReservedCards.ToArray(), resumed.ReservedCards.ToArray());
        var seatBefore = before.Public.SeatOf(pending.SeatId);
        Assert.Equal(seatBefore, resumed.Public.SeatOf(pending.SeatId));

        var evidence = new SubmitClaimEvidence(reopened.NewEnvelope(pending.SeatId), pending.OperationId,
            EvidenceKind.ManualAttestation, "tester", "whole board checked after reload");
        Assert.True((await reopened.SubmitAsync(evidence, token)).IsAccepted);
        Assert.True((await reopened.SubmitAsync(evidence, token)).IsAccepted); // Same command is idempotent.
        Assert.False((await reopened.SubmitAsync(evidence with
        {
            Envelope = reopened.NewEnvelope(pending.SeatId),
        }, token)).IsAccepted);
        Assert.Equal(turn + 1, reopened.Public.TurnNumber);
        Assert.Null(reopened.Public.PendingClaim);
        var after = reopened.Public.SeatOf(pending.SeatId);
        Assert.Equal(seatBefore.TrainsRemaining - pending.TrainCount, after.TrainsRemaining);
        Assert.Equal(seatBefore.TrainCardCount - before.ReservedCards.Length, after.TrainCardCount);
        Assert.Equal(pending.SeatId, reopened.Public.RouteOwners[pending.RouteId]);
        var onDisk = await store.RestoreAsync(coordinator.SessionId, TestManifest.Manifest, TestManifest.Catalog, token);
        Assert.Single(onDisk.Journal.Select(row => row.Event).OfType<ClaimCommitted>(),
            claim => claim.OperationId == pending.OperationId);
        Assert.Empty(await reopened.CheckInvariantsAsync(token));
    }

    /// <summary>
    /// The checkpoint carries the logical-state fingerprint in a readable local save. The
    /// redundant metadata columns still have to agree with that payload on readback.
    /// </summary>
    [Fact]
    public async Task TheCheckpointRowIsReadableInTheDatabaseFile()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new Persistence.SqliteSessionStore(_root);
        var rules = new GameRules(TestManifest.Manifest, TestManifest.Catalog);

        var coordinator = await GameCoordinator.CreateAsync(
            rules, store, Setup(), DeterministicRandom.SeedFrom(7), token);

        await PlayToTurnAsync(coordinator, targetTurn: 4, aiSeed: 7, token);

        var saved = await coordinator.SaveAndPackAwayAsync("Secretive", token);
        Assert.True(saved.SafeToPack, saved.Problem);

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={store.DatabasePath(coordinator.SessionId)}");
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload, Name FROM PackAwayCheckpoint WHERE CheckpointId = $id;";
        command.Parameters.AddWithValue("$id", saved.Checkpoint!.CheckpointId.Value);
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        var text = System.Text.Encoding.UTF8.GetString((byte[])reader["Payload"]);
        Assert.Equal("Secretive", reader.GetString(1));
        Assert.Contains(saved.Checkpoint.LogicalStateHash, text, StringComparison.Ordinal);
        Assert.Contains(saved.Checkpoint.PhysicalTargetHash, text, StringComparison.Ordinal);
        var restored = await store.ReadCheckpointAsync(
            coordinator.SessionId, saved.Checkpoint.CheckpointId, token);
        Assert.NotNull(restored);
        Assert.Equal(saved.Checkpoint.CheckpointId, restored.CheckpointId);
        Assert.Equal(saved.Checkpoint.LogicalStateHash, restored.LogicalStateHash);
        Assert.Equal(saved.Checkpoint.PhysicalTargetHash, restored.PhysicalTargetHash);
        Assert.True(saved.Checkpoint.PhysicalTarget.SequenceEqual(restored.PhysicalTarget));
    }

    [Theory]
    [InlineData("Name", "Other save")]
    [InlineData("Status", "Faulted")]
    [InlineData("SourceStateVersion", "999999")]
    public async Task CheckpointMetadataTamperingIsDetected(string column, string changed)
    {
        var token = TestContext.Current.CancellationToken;
        var store = new Persistence.SqliteSessionStore(_root);
        var rules = new GameRules(TestManifest.Manifest, TestManifest.Catalog);
        var coordinator = await GameCoordinator.CreateAsync(
            rules, store, Setup(), DeterministicRandom.SeedFrom(7), token);
        await PlayToTurnAsync(coordinator, 3, 7, token);
        var saved = await coordinator.SaveAndPackAwayAsync("Authenticated metadata", token);
        Assert.True(saved.SafeToPack, saved.Problem);

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath(coordinator.SessionId) }.ToString());
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE PackAwayCheckpoint SET {column} = $changed;";
        command.Parameters.AddWithValue("$changed", changed);
        await command.ExecuteNonQueryAsync(token);
        await Assert.ThrowsAsync<SessionIntegrityException>(() =>
            store.ReadCheckpointAsync(coordinator.SessionId, saved.Checkpoint!.CheckpointId, token));
    }
}
