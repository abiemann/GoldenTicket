using System.Reflection;
using System.Text.Json;
using GoldenTicket.AI;
using GoldenTicket.Application;
using GoldenTicket.CompanionHost;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed partial class AutomaticPhysicalFlowTests
{
    [Fact]
    public async Task Phone_mirrors_computer_placement_and_score_marker_until_the_human_turn_begins()
    {
        var token = TestContext.Current.CancellationToken;
        var model = await CreateComputerPlacementCompanionAsync();
        try
        {
            using var updates = ObserveCompanionUpdates(model);
            var game = GetCoordinator(model);
            var bridge = GetCompanionBridge(model);
            var placement = Assert.IsType<PlacementInstruction>(model.Table.Placement);
            var placing = await bridge.ReadPublicAsync(token);
            Assert.Equal(new CompanionGuidance(model.Game.GuidanceSeat, model.Game.GuidanceInstruction),
                placing.Guidance);
            Assert.Contains($"Place {placement.SeatName}'s {placement.TrainCount} {placement.Color}",
                placing.Guidance!.Instruction);
            Assert.Contains(placement.RouteText, placing.Guidance.Instruction);
            Assert.False(placing.CanControl);
            Assert.Null(placing.RevealSeatId);
            Assert.Null(await bridge.ReadPrivateAsync(placement.SeatId, game.Public.StateVersion, token));
            var publicJson = JsonSerializer.Serialize(placing.Guidance);
            Assert.DoesNotContain("Hand", publicJson);
            Assert.DoesNotContain("Payment", publicJson);
            Assert.DoesNotContain("CardId", publicJson);

            var accept = typeof(MainViewModel).GetMethod("AcceptPhysicalPlacementAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var claim = Assert.IsAssignableFrom<Task>(accept.Invoke(model,
                [placement, EvidenceKind.CameraAutomatic, "synthetic-test-model", "Two stable route observations."]));
            await WaitUntilAsync(() => model.Game.GuidanceInstruction == "Thank you");
            Assert.True(updates.Changes.Reader.TryRead(out _));
            var thanks = await bridge.ReadPublicAsync(token).WaitAsync(TimeSpan.FromSeconds(1), token);
            Assert.Equal("Thank you", thanks.Guidance!.Instruction);
            Assert.Equal(placement.SeatName, thanks.Guidance.Title);
            Assert.False(claim.IsCompleted);
            Assert.False(thanks.CanControl);
            Assert.Null(thanks.RevealSeatId);

            await claim;
            Assert.True(updates.Changes.Reader.TryRead(out _));
            var scoring = await bridge.ReadPublicAsync(token);
            Assert.Equal(thanks.Game!.StateVersion, scoring.Game!.StateVersion);
            Assert.Equal(new CompanionGuidance(model.Game.GuidanceSeat, model.Game.GuidanceInstruction),
                scoring.Guidance);
            Assert.StartsWith($"Move {placement.SeatName}'s {placement.Color} scoring marker",
                scoring.Guidance!.Instruction);
            Assert.False(scoring.CanControl);
            Assert.Null(scoring.RevealSeatId);
            Assert.Null(model.PrivateSeat);

            var target = game.Public.SeatOf(placement.SeatId).RouteScore % 100 + 1;
            var color = Enum.Parse<MarkerColor>(placement.Color.ToString());
            model.Camera.IsGameTablePreviewUpright = true;
            var at = DateTimeOffset.UtcNow;
            PublishScore(model.Camera, 1, at, color, target);
            PublishScore(model.Camera, 2, at.AddSeconds(1.1), color, target);
            await WaitUntilAsync(() => model.Game.GuidanceTurn != "Scoring");
            Assert.True(updates.Changes.Reader.TryRead(out _));
            var next = await bridge.ReadPublicAsync(token);
            Assert.Equal(scoring.Game.StateVersion, next.Game!.StateVersion);
            Assert.Null(next.Guidance);
            Assert.True(next.CanControl);
            Assert.Equal(game.Public.ActiveSeatId.Value, next.RevealSeatId);
            Assert.Null(model.PrivateSeat);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("reconciliation")]
    [InlineData("fault")]
    [InlineData("menu")]
    [InlineData("packing")]
    [InlineData("cancelled-placement")]
    public async Task Phone_does_not_retain_physical_guidance_after_its_step_is_suspended_or_changed(string reason)
    {
        var token = TestContext.Current.CancellationToken;
        var model = await CreateComputerPlacementCompanionAsync();
        try
        {
            var game = GetCoordinator(model);
            var bridge = GetCompanionBridge(model);
            Assert.NotNull((await bridge.ReadPublicAsync(token)).Guidance);
            switch (reason)
            {
                case "pause": model.IsGameExitMenuOpen = true; break;
                case "resume": model.IsResumeTurnAnnouncementOpen = true; break;
                case "reconciliation": model.NeedsBoardReconciliation = true; break;
                case "fault": model.PauseAfterUnhandledFault(); break;
                case "menu": model.Screen = Screen.Setup; break;
                case "packing":
                    Assert.True((await game.SubmitAsync(new SaveAndPackAway(game.NewEnvelope(), "Guidance test"), token)).IsAccepted);
                    break;
                case "cancelled-placement":
                    // Deliberately leave the old Table projection in place to exercise the
                    // revision guard during a command, before the desktop refresh runs.
                    Assert.True((await game.SubmitAsync(new CancelPendingClaim(game.NewEnvelope(game.Public.ActiveSeatId),
                        game.Public.PendingClaim!.OperationId, TrainsWerePlaced: false), token)).IsAccepted);
                    break;
            }
            Assert.Null((await bridge.ReadPublicAsync(token)).Guidance);
            Assert.Null(model.PrivateSeat);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static async Task<MainViewModel> CreateComputerPlacementCompanionAsync()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new InMemorySessionStore();
        var game = await GameCoordinator.CreateAsync(new GameRules(TestManifest.Manifest, TestManifest.Catalog), store,
            new SessionSetup(SessionId.New(),
                [new(new(1), "Computer 1", PlayerColor.Green, SeatKind.Computer, AiDifficulty.Standard),
                 new(new(2), "Player 1", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard),
                 new(new(3), "Player 2", PlayerColor.Red, SeatKind.Human, AiDifficulty.Standard)],
                new(1), VerificationMode.Manual), DeterministicRandom.SeedFrom(42), token);
        foreach (var seat in game.Seats)
        {
            var view = await game.GetSeatViewAsync(seat.SeatId, token);
            Assert.True((await game.SubmitAsync(new CommitTicketSelection(game.NewEnvelope(seat.SeatId),
                [.. view.SetupOffer.Take(2)], []), token)).IsAccepted);
        }
        var active = game.Public.ActiveSeatId;
        var actions = await game.GetLegalActionsAsync(active, token);
        var route = actions.Claims.First(claim => RoutePlacementVerifier.Supports(claim.RouteId.Value, claim.Length));
        var cards = LegalActionCalculator.ResolveCards(await game.GetSeatViewAsync(active, token), route.Payments[0]);
        Assert.True((await game.SubmitAsync(new PlanClaim(game.NewEnvelope(active), route.RouteId, cards), token)).IsAccepted);

        // Supply a real deterministic coordinator at the physical step, without random AI
        // choices or camera/network setup affecting the public-guidance assertions.
        var model = new MainViewModel(TestManifest.Manifest, store) { Screen = Screen.Table };
        typeof(MainViewModel).GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, game);
        typeof(MainViewModel).GetField("_driver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(model, new ComputerSeatDriver(game, new HeuristicAiPolicy(), 42));
        model.Table.Update(game.Public, game.PublicHistory);
        return model;
    }
}
