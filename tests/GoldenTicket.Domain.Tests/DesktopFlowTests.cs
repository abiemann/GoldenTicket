using System.Collections.Immutable;
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
        Assert.NotNull(model.PrivateSeat);              // one human uses the laptop directly
        Assert.True(model.PrivateSeat.MustChooseTickets);
        Assert.Equal("Alex", model.PrivateSeat.SeatName);
        Assert.False(model.CanConnectPhone);
        Assert.True(model.CanRevealPrivateSeat);
        Assert.Contains("Alex", model.RevealPrompt);

        // Both computer seats have already chosen; only the human is outstanding.
        Assert.Equal(3, model.Table.Seats.Count);
        Assert.Equal(3, model.Table.Seats.Single(seat => seat.DisplayName == "Alex").TicketCount);
        Assert.Equal("Waiting for first action.", model.Table.Seats.Single(seat => seat.DisplayName == "Alex").LastAction);
        Assert.All(model.Table.Seats.Where(seat => seat.Operator == "computer"),
            seat => Assert.StartsWith("Kept ", seat.LastAction));
        Assert.Equal("Alex", model.Table.ActiveSeatName);
        Assert.Equal("Choose whether to keep all destinations or drop one.", model.Table.Instruction);
        Assert.DoesNotContain("laptop", model.Table.Instruction, StringComparison.OrdinalIgnoreCase);
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
    public async Task ThePlayerTileSummarizesBothPubliclyRecordedDrawsInOneTurn()
    {
        var model = NewMatch();
        await model.StartMatchCommand.ExecuteAsync(null);
        await model.CommitTicketsCommand.ExecuteAsync(null);

        await model.DrawBlindCardAsync();
        Assert.Equal("Drew 1 blind train card.", model.Table.Seats.Single(seat => seat.DisplayName == "Alex").LastAction);

        await model.DrawBlindCardAsync();
        Assert.Equal("Drew 2 blind train cards.", model.Table.Seats.Single(seat => seat.DisplayName == "Alex").LastAction);
    }

    [Fact]
    public async Task KeepingOpeningTicketsStartsPlayAndShowsTheSoloHumansHand()
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
        Assert.Equal("Kept 2 destinations and returned 1.", human.LastAction);

        // The sole human can continue on the laptop without another reveal or phone handoff.
        Assert.All(offered, ticket => Assert.NotEqual(default, ticket));
        Assert.NotNull(model.PrivateSeat);
        Assert.Equal(human.SeatId, model.PrivateSeat.SeatId);
        Assert.False(model.PrivateSeat.MustChooseTickets);
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
        Assert.StartsWith("Claimed ", after.LastAction);
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

/// <summary>
/// The save-and-pack-away screens (DESIGN 4.10, 19.8) driven the way a person at the table would,
/// without showing a window.
/// </summary>
public class DesktopPackAwayFlowTests
{
    private static async Task<MainViewModel> StartedMatchAsync()
    {
        var model = new MainViewModel(TestManifest.Manifest, new Application.InMemorySessionStore());
        model.Setup.ManualVerificationAccepted = true;

        foreach (var seat in model.Setup.Seats) seat.IsComputer = true;

        await model.StartMatchCommand.ExecuteAsync(null);
        return model;
    }

    [Fact]
    public async Task SavingWithoutANameIsRefusedAndSaysSo()
    {
        var model = await StartedMatchAsync();
        model.Table.SaveName = "   ";

        await model.SaveAndPackAwayCommand.ExecuteAsync(null);

        Assert.Contains("name", model.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(model.Table.IsPackedAway);
    }

    [Fact]
    public async Task SavingShowsTheSafeToPackWordingOnlyAfterValidation()
    {
        var model = await StartedMatchAsync();
        model.Table.SaveName = "Sunday game";

        await model.SaveAndPackAwayCommand.ExecuteAsync(null);

        Assert.True(model.Table.IsPackedAway);
        Assert.Contains("pack the game away", model.Table.SaveStatus!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(model.Table.SaveProblem);

        // The name box is cleared so the next save has to be named deliberately.
        Assert.Equal("", model.Table.SaveName);
    }

    [Fact]
    public async Task TheRebuildScreenListsTheSavedRoutesAndTheStockEachSeatShouldHold()
    {
        var model = await StartedMatchAsync();

        // Play far enough that some routes are actually on the board.
        for (var step = 0; step < 40 && model.Table.ClaimedRoutes.Count < 3; step++)
        {
            if (model.Table.Placement is not null) model.Table.WholeBoardAcknowledged = true;
            await model.ConfirmPlacementCommand.ExecuteAsync(null);
        }

        Assert.True(model.Table.ClaimedRoutes.Count >= 3, "No routes were claimed to rebuild.");

        var claimed = model.Table.ClaimedRoutes.Count;
        model.Table.SaveName = "Mid game";
        await model.SaveAndPackAwayCommand.ExecuteAsync(null);
        Assert.True(model.Table.IsPackedAway, model.Status);

        await model.BeginRebuildCommand.ExecuteAsync(null);

        Assert.Equal(Screen.Rebuild, model.Screen);
        Assert.Equal(claimed, model.Table.RebuildTarget.Count);
        Assert.Equal(model.Table.Seats.Count, model.Table.RebuildStock.Count);

        // Each seat's placed-plus-in-hand totals the starting stock.
        Assert.All(model.Table.RebuildStock,
            row => Assert.Equal(45, row.OnBoard + row.RemainingOffBoard));

        // Every listed route names an owner, a readable route and a train count.
        Assert.All(model.Table.RebuildTarget, row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.OwnerName));
            Assert.Contains(" - ", row.RouteText, StringComparison.Ordinal);
            Assert.InRange(row.TrainCount, 1, 6);
        });
    }

    [Fact]
    public async Task ResumeNeedsTheOperatorToTickTheConfirmationFirst()
    {
        var model = await StartedMatchAsync();
        model.Table.SaveName = "Packed";
        await model.SaveAndPackAwayCommand.ExecuteAsync(null);
        await model.BeginRebuildCommand.ExecuteAsync(null);

        // Attesting without ticking the box is refused, and Resume stays blocked.
        await model.AttestRebuildCommand.ExecuteAsync(null);
        Assert.False(model.Table.RebuildAttested);
        Assert.Contains("Tick the confirmation", model.Status!, StringComparison.OrdinalIgnoreCase);

        await model.ResumePackedGameCommand.ExecuteAsync(null);
        Assert.Equal(Screen.Rebuild, model.Screen);

        // Ticking it, then attesting, then resuming works.
        model.Table.RebuildAcknowledged = true;
        await model.AttestRebuildCommand.ExecuteAsync(null);
        Assert.True(model.Table.RebuildAttested);

        await model.ResumePackedGameCommand.ExecuteAsync(null);

        Assert.Equal(Screen.Table, model.Screen);
        Assert.False(model.Table.IsPackedAway);
        Assert.False(model.Table.IsRebuilding);
    }

    /// <summary>
    /// DESIGN 19.5: reopening a match that was interrupted mid-save carries the save forward against
    /// the preserved source state, and does not ask the operator to reconcile a board that is
    /// already in the box.
    /// </summary>
    [Fact]
    public async Task ReopeningAnInterruptedSaveFinishesItInsteadOfAskingAboutTheBoard()
    {
        var store = new Application.InMemorySessionStore();
        var rules = new Domain.Engine.GameRules(TestManifest.Manifest, TestManifest.Catalog);

        // Build a match outside the interface and stop it at the first durable save boundary.
        var seats = Enumerable.Range(0, 3)
            .Select(index => new Seat(
                new SeatId(index + 1), $"Seat {index + 1}", Enum.GetValues<PlayerColor>()[index],
                SeatKind.Computer, AiDifficulty.Standard))
            .ToImmutableArray();

        var setup = new Domain.Engine.SessionSetup(
            SessionId.New(), seats, seats[0].SeatId, VerificationMode.Manual);

        var coordinator = await Application.GameCoordinator.CreateAsync(
            rules, store, setup, Domain.Randomness.DeterministicRandom.SeedFrom(5));

        var driver = new Application.ComputerSeatDriver(coordinator, new AI.HeuristicAiPolicy(), aiSeed: 5);
        await driver.AdvanceAsync();

        await coordinator.SubmitAsync(
            new Domain.Engine.SaveAndPackAway(coordinator.NewEnvelope(), "Interrupted"));

        Assert.Equal(SessionLifecycle.PreparingPackAway, coordinator.Public.Lifecycle);

        // Reopen it through the interface.
        var model = new MainViewModel(TestManifest.Manifest, store);
        await model.LoadSavedSessionsCommand.ExecuteAsync(null);
        model.Setup.SelectedSavedSession = model.Setup.SavedSessions.Single();

        await model.ResumeMatchCommand.ExecuteAsync(null);

        Assert.False(model.NeedsBoardReconciliation);
        Assert.False(model.Table.IsSaving);
        Assert.True(model.Table.IsPackedAway);
        Assert.Contains("pack the game away", model.Table.SaveStatus!, StringComparison.OrdinalIgnoreCase);
    }
}
