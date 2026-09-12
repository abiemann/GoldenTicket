using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Events;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopContinuationAuditTests
{
    [Fact]
    public async Task PackAndRebuildKeepPrivateViewsClosedUntilResume()
    {
        var model = await HumanMatchAsync();
        await model.RevealPrivateSeatAsync();
        Assert.NotNull(model.PrivateSeat);
        model.Table.SaveName = "Privacy audit";
        await model.SaveAndPackAwayAsync();

        Assert.True(model.Table.IsPackedAway);
        Assert.Contains("You can pack", model.Table.SaveStatus);
        Assert.False(model.CanRevealPrivateSeat);
        await model.RevealPrivateSeatAsync();
        Assert.Null(model.PrivateSeat);

        await model.BeginRebuildAsync();
        Assert.Equal(Screen.Rebuild, model.Screen);
        Assert.DoesNotContain("You can pack", model.Table.SaveStatus);
        Assert.False(model.CanRevealPrivateSeat);
        await model.RevealPrivateSeatAsync();
        Assert.Null(model.PrivateSeat);

        model.Table.RebuildAcknowledged = true;
        await model.AttestRebuildAsync();
        await model.ResumePackedGameAsync();
        Assert.Equal(Screen.Table, model.Screen);
        Assert.DoesNotContain("You can pack", model.Table.SaveStatus);
        Assert.True(model.CanRevealPrivateSeat);
        Assert.Null(model.PrivateSeat);
    }

    [Fact]
    public async Task PackedPendingClaimDoesNotAskForOrdinaryPlacement()
    {
        var model = await HumanMatchAsync();
        await model.RevealPrivateSeatAsync();
        for (var draw = 0; !model.PrivateSeat!.ClaimOptions.Any() && draw < 30; draw++)
        {
            await model.DrawBlindCardAsync();
            await model.RevealPrivateSeatAsync();
        }
        Assert.NotEmpty(model.PrivateSeat!.ClaimOptions);
        model.PrivateSeat.SelectedClaim = model.PrivateSeat.ClaimOptions[0];
        await model.PlanClaimAsync();
        Assert.NotNull(model.Table.Placement);
        model.Table.SaveName = "Pending claim";
        await model.SaveAndPackAwayAsync();

        Assert.True(model.Table.IsPackedAway);
        Assert.Null(model.Table.Placement);
        Assert.DoesNotContain("then confirm", model.Table.Instruction);
    }

    [Fact]
    public async Task RebuildResumeContinuesAnAiTurn()
    {
        var store = new InMemorySessionStore();
        var setup = new MainViewModel(TestManifest.Manifest, store);
        setup.Setup.ManualVerificationAccepted = true;
        foreach (var seat in setup.Setup.Seats) seat.IsComputer = true;
        var coordinator = await GameCoordinator.CreateAsync(
            new GameRules(TestManifest.Manifest, TestManifest.Catalog), store,
            setup.Setup.TryBuildSetup()!, DeterministicRandom.SeedFrom(42));
        foreach (var seat in coordinator.Seats)
        {
            var view = await coordinator.GetSeatViewAsync(seat.SeatId);
            Assert.True((await coordinator.SubmitAsync(new CommitTicketSelection(
                coordinator.NewEnvelope(seat.SeatId), [.. view.SetupOffer.Take(2)], []))).IsAccepted);
        }
        Assert.True((await coordinator.SaveAndPackAwayAsync("AI turn")).SafeToPack);

        var model = new MainViewModel(TestManifest.Manifest, store);
        await model.LoadSavedSessionsAsync();
        model.Setup.SelectedSavedSession = model.Setup.SavedSessions.Single();
        await model.ResumeMatchAsync();
        await model.BeginRebuildAsync();
        model.Table.RebuildAcknowledged = true;
        await model.AttestRebuildAsync();
        await model.ResumePackedGameAsync();

        Assert.True(model.Table.Placement is not null || model.Screen == Screen.FinalScore,
            "The resumed AI match must advance to a human/operator boundary, rather than stall at its saved turn.");
    }

    [Fact]
    public void DifferentRulesDecisionRequiresFreshAcknowledgement()
    {
        var harness = RulesHarness.Create();
        harness.CompleteSetup();
        var table = new TableViewModel(TestManifest.Manifest);
        GameReducer.ApplyTransition(harness.State,
            [new RulesDecisionRaised(RulesContinuations.PartialMarketSupply, "Partial market")]);
        table.Update(harness.PublicView(), []);
        table.RulesContinuationAccepted = true;
        GameReducer.ApplyTransition(harness.State,
            [new RulesDecisionRaised(RulesContinuations.NoSelectableSecondDraw, "No second draw")]);
        table.Update(harness.PublicView(), []);

        Assert.False(table.RulesContinuationAccepted);
    }

    [Fact]
    public async Task RebuildAcknowledgementCannotBeUncheckedThenUsedToResume()
    {
        var model = await HumanMatchAsync();
        model.Table.SaveName = "Changed mind";
        await model.SaveAndPackAwayAsync();
        await model.BeginRebuildAsync();
        model.Table.RebuildAcknowledged = true;
        await model.AttestRebuildAsync();
        model.Table.RebuildAcknowledged = false;
        await model.ResumePackedGameAsync();

        Assert.Equal(Screen.Rebuild, model.Screen);
        Assert.True(model.Table.IsRebuilding);
    }

    private static async Task<MainViewModel> HumanMatchAsync()
    {
        var model = new MainViewModel(TestManifest.Manifest, new InMemorySessionStore());
        model.Setup.ManualVerificationAccepted = true;
        foreach (var seat in model.Setup.Seats) seat.IsComputer = false;
        await model.StartMatchAsync();
        for (var i = 0; i < model.Setup.Seats.Count; i++)
        {
            await model.RevealPrivateSeatAsync();
            await model.CommitTicketsAsync();
        }
        return model;
    }
}
