using System.Net;
using System.Net.Http.Json;
using GoldenTicket.Application;
using GoldenTicket.CompanionHost;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Domain.Tests;

public partial class CompanionHostTransportTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public async Task CameraRejectedDrawKeepsTheTurnAndRetryAwardsTheSelectedInstanceBeforeHandoff(
        bool secondPick, bool blind, bool locomotive)
    {
        var token = TestContext.Current.CancellationToken;
        var (game, store) = await CreateDrawAuditGame(locomotive, token);
        var seat = new SeatId(1);
        if (secondPick)
            Assert.True((await game.SubmitAsync(new SelectTrainCard(game.NewEnvelope(seat), null), token)).IsAccepted);
        var saved = await store.RestoreAsync(game.SessionId, TestManifest.Manifest, TestManifest.Catalog, token);
        var slot = blind ? (int?)null : Enumerable.Range(0, 5).First(index =>
            game.Public.FaceUp[index] is { } kind && (kind == TrainCardKind.Locomotive) == locomotive);
        var expectedCard = slot is { } selectedSlot ? saved.State.FaceUp[selectedSlot]!.Value : saved.State.TrainDeck[0];
        var before = await game.GetSeatViewAsync(seat, token);
        var beforeHash = await game.ComputeStateHashAsync(token);
        await using var host = await ContinuationHost.Create(game, token);
        var grant = await host.Reveal(token);
        var command = host.Command("drawTrain", slot);
        host.Bridge.BoardInteraction = new(true, true, "Keep the board clear, then try again.");
        host.Bridge.RejectNextDrawForBoardCheck = true;

        var rejected = await host.Send(grant, command, token);

        Assert.False(rejected.Accepted);
        Assert.Equal("BoardCheckRequired", rejected.Code);
        Assert.Equal(beforeHash, await game.ComputeStateHashAsync(token));
        var continued = Assert.IsType<CompanionPrivateContinuation>(rejected.Continuation);
        Assert.Equal(before.Hand.ToArray(), continued.Data.View.Hand.ToArray());
        Assert.Equal(before.Public.StateVersion, continued.Snapshot.Game!.StateVersion);
        Assert.Equal(before.Public.TurnNumber, continued.Snapshot.Game.TurnNumber);
        Assert.Equal(seat, continued.Snapshot.Game.ActiveSeatId);
        Assert.True(continued.Snapshot.BoardInteraction!.CardActionsBlocked);
        Assert.True(host.Server.Authority.ValidateGrant(host.Credentials, continued.Grant, 1,
            game.SessionId.Value, before.Public.StateVersion));

        // A transport retry acknowledges the same refusal; it cannot turn it into a second draw.
        var repeatedRefusal = await host.Send(grant, command, token);
        Assert.False(repeatedRefusal.Accepted);
        Assert.Null(repeatedRefusal.Continuation);
        Assert.Equal(beforeHash, await game.ComputeStateHashAsync(token));
        Assert.True(host.Server.Authority.ValidateGrant(host.Credentials, continued.Grant, 1,
            game.SessionId.Value, before.Public.StateVersion));

        host.Bridge.BoardInteraction = new(true, false, null);
        var retry = host.Command("drawTrain", slot);
        var accepted = await host.Send(continued.Grant, retry, token);
        Assert.True(accepted.Accepted);
        var after = await game.GetSeatViewAsync(seat, token);
        Assert.Equal(before.Hand.Length + 1, after.Hand.Length);
        var awarded = Assert.Single(after.Hand.Where(card => !before.Hand.Any(old => old.Id == card.Id)));
        Assert.Equal(expectedCard, awarded.Id);
        Assert.Equal(TestManifest.Catalog.KindOf(expectedCard), awarded.Kind);
        Assert.Equal(secondPick || locomotive ? new SeatId(2) : seat, game.Public.ActiveSeatId);
        Assert.Equal(before.Public.TurnNumber + (secondPick || locomotive ? 1 : 0), game.Public.TurnNumber);
        if (secondPick || locomotive) Assert.Null(accepted.Continuation);
        else Assert.Contains(Assert.IsType<CompanionPrivateContinuation>(accepted.Continuation).Data.View.Hand,
            card => card.Id == expectedCard);

        // The saved transaction contains the award before any turn completion, and replay agrees.
        saved = await store.RestoreAsync(game.SessionId, TestManifest.Manifest, TestManifest.Catalog, token);
        var events = saved.Journal.Where(row => row.StateVersion == accepted.StateVersion)
            .Select(row => row.Event).ToArray();
        var awardIndex = Array.FindIndex(events, domainEvent => domainEvent is
            FaceUpCardTaken { Card: var card } && card == expectedCard || domainEvent is
            BlindCardDrawn { Card: var drawn } && drawn == expectedCard);
        Assert.True(awardIndex >= 0);
        var completedIndex = Array.FindIndex(events, domainEvent => domainEvent is TurnCompleted);
        Assert.Equal(secondPick || locomotive, completedIndex >= 0);
        if (completedIndex >= 0) Assert.True(awardIndex < completedIndex);
        Assert.Contains(expectedCard, saved.State.HandOf(seat));
        var acceptedHash = await game.ComputeStateHashAsync(token);
        Assert.Equal(acceptedHash, StateHash.Compute(saved.State));

        var repeatedSuccess = await host.Send(continued.Grant, retry, token);
        Assert.True(repeatedSuccess.Accepted);
        Assert.Null(repeatedSuccess.Continuation);
        Assert.Equal(acceptedHash, await game.ComputeStateHashAsync(token));

        // A fresh command id carrying the previous version cannot accidentally spend another pick.
        using var stale = await host.Client.PostAsJsonAsync("/api/command", new
        {
            seat = 1, grant = accepted.Continuation?.Grant ?? continued.Grant,
            command = retry with { CommandId = Guid.NewGuid().ToString("N") }
        }, token);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Equal(acceptedHash, await game.ComputeStateHashAsync(token));
        Assert.Empty(await game.CheckInvariantsAsync(token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CameraRejectedDrawCannotRestoreCardsAfterHideOrRevocation(bool revoke)
    {
        var token = TestContext.Current.CancellationToken;
        var game = await CreateActiveContinuationGame(token);
        await using var host = await ContinuationHost.Create(game, token);
        var grant = await host.Reveal(token);
        var before = await game.ComputeStateHashAsync(token);
        host.Bridge.RejectNextDrawForBoardCheck = true;
        host.Bridge.AfterPrivateRead = _ =>
        {
            if (revoke) host.Server.RevokeController();
            else host.Server.InvalidatePrivateGrants();
            return Task.CompletedTask;
        };

        var receipt = await host.Send(grant, host.Command("drawTrain"), token);

        Assert.False(receipt.Accepted);
        Assert.Equal("BoardCheckRequired", receipt.Code);
        Assert.Null(receipt.Continuation);
        Assert.Equal(before, await game.ComputeStateHashAsync(token));
    }

    [Fact]
    public async Task SecondPickLocomotiveRefusalDoesNotAwardACardOrAdvanceTheTurn()
    {
        var token = TestContext.Current.CancellationToken;
        var (game, _) = await CreateDrawAuditGame(locomotive: true, token);
        Assert.True((await game.SubmitAsync(new SelectTrainCard(game.NewEnvelope(new(1)), null), token)).IsAccepted);
        var slot = game.Public.FaceUp.IndexOf(TrainCardKind.Locomotive);
        var before = await game.ComputeStateHashAsync(token);
        await using var host = await ContinuationHost.Create(game, token);
        var receipt = await host.Send(await host.Reveal(token), host.Command("drawTrain", slot), token);

        Assert.False(receipt.Accepted);
        Assert.Equal("LocomotiveCannotBeSecondPick", receipt.Code);
        Assert.Equal(before, await game.ComputeStateHashAsync(token));
        Assert.Equal(TurnPhase.AwaitingSecondTrainCard, game.Public.TurnPhase);
        Assert.Equal(new SeatId(1), game.Public.ActiveSeatId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentSelectionsForOneVersionAwardOnlyOneCardAndDuplicateCannotAdvanceAgain(bool secondPick)
    {
        var token = TestContext.Current.CancellationToken;
        var (game, store) = await CreateDrawAuditGame(locomotive: false, token);
        var seat = new SeatId(1);
        if (secondPick)
            Assert.True((await game.SubmitAsync(new SelectTrainCard(game.NewEnvelope(seat), null), token)).IsAccepted);
        var before = await game.GetSeatViewAsync(seat, token);
        var slot = Enumerable.Range(0, 5).First(index => game.Public.FaceUp[index] is not (null or TrainCardKind.Locomotive));
        var saved = await store.RestoreAsync(game.SessionId, TestManifest.Manifest, TestManifest.Catalog, token);
        var expected = saved.State.FaceUp[slot]!.Value;
        var commands = new[]
        {
            new SelectTrainCard(game.NewEnvelope(seat), slot),
            new SelectTrainCard(game.NewEnvelope(seat), slot)
        };

        var outcomes = await Task.WhenAll(commands.Select(command => game.SubmitAsync(command, token)));

        Assert.Single(outcomes, outcome => outcome.IsAccepted);
        Assert.Equal("StaleStateVersion", Assert.Single(outcomes, outcome => !outcome.IsAccepted).Result.Rejection!.Code);
        var after = await game.GetSeatViewAsync(seat, token);
        Assert.Equal(before.Hand.Length + 1, after.Hand.Length);
        Assert.Equal(expected, Assert.Single(after.Hand.Where(card => !before.Hand.Any(old => old.Id == card.Id))).Id);
        Assert.Equal(secondPick ? new SeatId(2) : seat, game.Public.ActiveSeatId);
        var afterHash = await game.ComputeStateHashAsync(token);
        var accepted = commands[Array.FindIndex(outcomes, outcome => outcome.IsAccepted)];
        var duplicate = await game.SubmitAsync(accepted, token);
        Assert.True(duplicate.IsAccepted);
        Assert.True(duplicate.WasDuplicate);
        Assert.Equal(afterHash, await game.ComputeStateHashAsync(token));
        Assert.Empty(await game.CheckInvariantsAsync(token));
    }

    private static async Task<(GameCoordinator Game, InMemorySessionStore Store)> CreateDrawAuditGame(
        bool locomotive, CancellationToken token)
    {
        for (ulong seed = 91; seed < 120; seed++)
        {
            var store = new InMemorySessionStore();
            var game = await CreateActiveContinuationGame(token, seed, store);
            if (!locomotive || game.Public.FaceUp.Contains(TrainCardKind.Locomotive)) return (game, store);
        }
        throw new InvalidOperationException("No fixture seed supplied a visible locomotive.");
    }
}
