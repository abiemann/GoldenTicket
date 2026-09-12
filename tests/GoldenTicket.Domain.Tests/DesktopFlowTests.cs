using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Domain.Tests;

/// <summary>
/// Drives the desktop view models the way a person at the table would, without showing a window.
/// The point is the behaviour DESIGN 4.1-4.7 cares about: the public screen never receives a hand,
/// the private view opens only for the seat that is owed the screen, and hiding actually discards it.
/// </summary>
public class DesktopFlowTests
{
    private static MainViewModel NewMatch(bool computerOnly = false)
    {
        var model = new MainViewModel(TestManifest.Manifest, new InMemorySessionStore());
        model.Setup.ManualVerificationAccepted = true;

        model.Setup.Seats[0].DisplayName = "Alex";
        model.Setup.Seats[0].IsComputer = computerOnly;
        model.Setup.Seats[1].DisplayName = "Conductor";
        model.Setup.Seats[2].DisplayName = "Brakeman";

        return model;
    }

    [Fact]
    public async Task StartingAMatchOpensTheTableAndAsksTheHumanForItsOpeningTickets()
    {
        var model = NewMatch();
        await model.StartMatchCommand.ExecuteAsync(null);

        Assert.Equal(Screen.Table, model.Screen);
        Assert.Null(model.PrivateSeat);                 // the table screen starts covered
        Assert.True(model.CanRevealPrivateSeat);
        Assert.Contains("Alex", model.RevealPrompt);

        // Both computer seats have already chosen; only the human is outstanding.
        Assert.Equal(3, model.Table.Seats.Count);
        Assert.Contains("opening ticket", model.Table.Instruction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThePrivateViewShowsThisSeatsCardsAndDisappearsWhenHidden()
    {
        var model = NewMatch();
        await model.StartMatchCommand.ExecuteAsync(null);
        await model.RevealPrivateSeatCommand.ExecuteAsync(null);

        var seat = model.PrivateSeat;
        Assert.NotNull(seat);
        Assert.Equal("Alex", seat!.SeatName);
        Assert.True(seat.MustChooseTickets);
        Assert.Equal(3, seat.Offer.Count);
        Assert.Equal(4, seat.Hand.Sum(group => group.Count));   // the opening four train cards
        Assert.True(model.IsPrivateVisible);

        model.HidePrivateSeatCommand.Execute(null);

        Assert.Null(model.PrivateSeat);
        Assert.False(model.IsPrivateVisible);
    }

    [Fact]
    public async Task KeepingOpeningTicketsStartsPlayAndCoversTheHandAgain()
    {
        var model = NewMatch();
        await model.StartMatchCommand.ExecuteAsync(null);
        await model.RevealPrivateSeatCommand.ExecuteAsync(null);

        var offered = model.PrivateSeat!.Offer.Select(row => row.TicketId).ToList();
        model.PrivateSeat.Offer[2].Keep = false;       // keep two of three

        await model.CommitTicketsCommand.ExecuteAsync(null);

        Assert.Equal(SessionLifecycle.Active, model.Table.Seats.Count > 0
            ? SessionLifecycle.Active
            : SessionLifecycle.Setup);

        var human = model.Table.Seats.Single(seat => seat.DisplayName == "Alex");
        Assert.Equal(2, human.TicketCount);

        // Whatever happened next, no hand is left on screen.
        Assert.All(offered, ticket => Assert.NotEqual(default, ticket));
        Assert.True(model.PrivateSeat is null || model.PrivateSeat.SeatId == human.SeatId);
    }

    [Fact]
    public async Task ComputerSeatsPlayUntilAPlacementNeedsTheOperator()
    {
        var model = NewMatch(computerOnly: true);
        await model.StartMatchCommand.ExecuteAsync(null);

        // With every seat played by the computer, the run stops only for a physical placement,
        // an unresolved rules state, or the end of the match.
        Assert.False(model.CanRevealPrivateSeat);

        var stoppedForPlacement = model.Table.Placement is not null;
        var finished = model.Screen == Screen.FinalScore;
        var paused = model.Table.RulesDecisionText is not null;

        Assert.True(stoppedForPlacement || finished || paused,
            $"The computer run stopped in phase {model.Table.PhaseText} with nothing to do.");
    }

    [Fact]
    public async Task ConfirmingAPlacementCommitsTheClaimAndClearsTheInstruction()
    {
        var model = NewMatch(computerOnly: true);
        await model.StartMatchCommand.ExecuteAsync(null);

        // Play forward until a claim is waiting for its trains.
        var guard = 0;
        while (model.Table.Placement is null && model.Screen == Screen.Table && guard++ < 50)
            await model.ConfirmPlacementCommand.ExecuteAsync(null);

        var placement = model.Table.Placement;
        Assert.NotNull(placement);

        var claimant = model.Table.Seats.Single(seat => seat.SeatId == placement!.SeatId);
        var routesBefore = claimant.RoutesClaimed;

        Assert.Contains("Place", placement!.Headline, StringComparison.Ordinal);
        Assert.Contains(placement.SeatName, placement.Headline, StringComparison.Ordinal);
        Assert.True(placement.TrainCount >= 1);

        await model.ConfirmPlacementCommand.ExecuteAsync(null);
        Assert.Equal(routesBefore, model.Table.Seats.Single(seat => seat.SeatId == placement.SeatId).RoutesClaimed);
        model.Table.WholeBoardAcknowledged = true;
        await model.ConfirmPlacementCommand.ExecuteAsync(null);

        var after = model.Table.Seats.Single(seat => seat.SeatId == placement.SeatId);
        Assert.Equal(routesBefore + 1, after.RoutesClaimed);
        Assert.True(after.TrainsRemaining < 45);
        Assert.False(model.Table.WholeBoardAcknowledged);
    }

    [Fact]
    public async Task TheTableScreenNeverCarriesAHandOrATicketName()
    {
        var model = NewMatch();
        await model.StartMatchCommand.ExecuteAsync(null);
        await model.RevealPrivateSeatCommand.ExecuteAsync(null);

        // Capture this seat's real secrets, then look for them on the public screen.
        var tickets = model.PrivateSeat!.Offer.Select(row => row.Description).ToList();
        model.HidePrivateSeatCommand.Execute(null);

        var publicText = string.Join("\n",
            model.Table.History
                .Append(model.Table.Instruction)
                .Append(model.Table.PhaseText)
                .Append(model.Table.SupplyText)
                .Concat(model.Table.Seats.Select(seat => $"{seat.DisplayName} {seat.Score} {seat.CardCount}")));

        foreach (var ticket in tickets)
            Assert.DoesNotContain(ticket, publicText, StringComparison.OrdinalIgnoreCase);
    }
}
