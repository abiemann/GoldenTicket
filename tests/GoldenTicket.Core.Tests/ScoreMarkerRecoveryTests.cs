using GoldenTicket.Application;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Persistence;
using Microsoft.Data.Sqlite;

namespace GoldenTicket.Domain.Tests;

public sealed class ScoreMarkerRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GoldenTicket.Tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Claim_and_marker_confirmation_have_separate_durable_boundaries(bool sqlite)
    {
        var token = TestContext.Current.CancellationToken;
        ISessionStore store = sqlite ? new SqliteSessionStore(_root) : new InMemorySessionStore();
        var rules = new GameRules(TestManifest.Manifest, TestManifest.Catalog);
        var setup = new SessionSetup(SessionId.New(),
            [new(new(1), "Blue", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard),
             new(new(2), "Yellow", PlayerColor.Yellow, SeatKind.Human, AiDifficulty.Standard)],
            new(1), VerificationMode.Manual);
        var game = await GameCoordinator.CreateAsync(rules, store, setup,
            DeterministicRandom.SeedFrom(91), token);
        foreach (var seat in setup.Seats)
        {
            var view = await game.GetSeatViewAsync(seat.SeatId, token);
            Assert.True((await game.SubmitAsync(new CommitTicketSelection(game.NewEnvelope(seat.SeatId),
                [.. view.SetupOffer.Take(2)], []), token)).IsAccepted);
        }

        var claimant = game.Public.ActiveSeatId;
        var legal = (await game.GetLegalActionsAsync(claimant, token)).Claims.First();
        var hand = await game.GetSeatViewAsync(claimant, token);
        Assert.True((await game.SubmitAsync(new PlanClaim(game.NewEnvelope(claimant), legal.RouteId,
            LegalActionCalculator.ResolveCards(hand, legal.Payments[0])), token)).IsAccepted);
        var operation = game.Public.PendingClaim!.OperationId;
        var points = TestManifest.Manifest.RulesConstants.ScoreForLength(legal.Length);
        Assert.True((await game.SubmitAsync(new SubmitClaimEvidence(game.NewEnvelope(claimant),
            operation, EvidenceKind.CameraAutomatic, "camera", "Whole board verified",
            RequireScoreMarkerConfirmation: true), token)).IsAccepted);
        var required = Assert.IsType<PendingScoreMarkerMove>(game.Public.PendingScoreMarkerMove);
        Assert.Equal(operation, required.OperationId);
        Assert.Equal(1, required.FromPrintedScore);
        Assert.Equal(points + 1, required.ToPrintedScore);

        store = sqlite ? new SqliteSessionStore(_root) : store;
        var reopened = await GameCoordinator.RestoreAsync(rules, store, setup.SessionId, token);
        Assert.Equal(required, reopened.Public.PendingScoreMarkerMove);
        var next = reopened.Public.ActiveSeatId;
        Assert.Equal("ScoreMarkerMoveRequired", (await reopened.SubmitAsync(
            new SelectTrainCard(reopened.NewEnvelope(next), null), token)).Result.Rejection?.Code);
        Assert.Equal("OperationMismatch", (await reopened.SubmitAsync(
            new ConfirmScoreMarkerMove(reopened.NewEnvelope(claimant), OperationId.New(),
                "camera", "wrong operation"), token)).Result.Rejection?.Code);
        Assert.True((await reopened.SubmitAsync(new ConfirmScoreMarkerMove(
            reopened.NewEnvelope(claimant), operation, "camera",
            $"Marker observed at {required.ToPrintedScore}"), token)).IsAccepted);

        var complete = await GameCoordinator.RestoreAsync(rules, store, setup.SessionId, token);
        Assert.Null(complete.Public.PendingScoreMarkerMove);
        Assert.True((await complete.SubmitAsync(new SelectTrainCard(
            complete.NewEnvelope(complete.Public.ActiveSeatId), null), token)).IsAccepted);
    }

    [Fact]
    public void Existing_claims_without_a_marker_requirement_replay_unchanged()
    {
        var harness = RulesHarness.Create();
        harness.CompleteSetup();
        var seat = harness.State.ActiveSeatId;
        var legal = harness.Legal(seat).Claims.First();
        var payment = LegalActionCalculator.ResolveCards(harness.SeatView(seat), legal.Payments[0]);
        harness.SubmitAccepted(new PlanClaim(harness.Envelope(seat), legal.RouteId, payment));
        var operation = harness.State.PendingClaim!.OperationId;
        harness.SubmitAccepted(new SubmitClaimEvidence(harness.Envelope(seat), operation,
            EvidenceKind.ManualAttestation, "operator", "Whole board checked"));

        Assert.Null(harness.State.PendingScoreMarkerMove);
        Assert.Null(harness.Replay().PendingScoreMarkerMove);
        harness.AssertReplayMatches();
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var database in Directory.EnumerateFiles(_root, "session.db", SearchOption.AllDirectories))
        {
            foreach (var mode in new[] { SqliteOpenMode.ReadWrite, SqliteOpenMode.ReadWriteCreate })
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = database, Mode = mode, Pooling = true }.ToString());
                SqliteConnection.ClearPool(connection);
            }
        }
        Directory.Delete(_root, recursive: true);
    }
}
