using System.Collections.Immutable;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Vision;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GoldenTicket.Domain.Tests;

/// <summary>
/// Drives the desktop view models the way a person at the table would, without showing a window.
/// The point is the behaviour DESIGN 4.1-4.7 cares about: the public screen never receives a hand,
/// the private view opens only for the seat that is owed the screen, and hiding actually discards it.
/// </summary>
public class DesktopFlowTests
{
    [Fact]
    public void Placement_guidance_uses_singular_train_and_position_for_one_piece()
    {
        var harness = RulesHarness.Create();
        harness.CompleteSetup();
        var current = harness.PublicView();
        var active = current.SeatOf(current.ActiveSeatId);
        var table = new TableViewModel(ManifestLoader.LoadClassicUs());

        void ShowPlacement(string routeId, int count)
        {
            var pending = new PublicPendingClaim(OperationId.New(), active.SeatId,
                new RouteId(routeId), count, false);
            table.Update(current with
            {
                TurnPhase = TurnPhase.AwaitingPhysicalPlacement,
                PendingClaim = pending
            }, []);
        }

        ShowPlacement("dallas--houston--a", 1);
        Assert.Contains($"1 {active.Color} train on", table.Instruction);
        Assert.Contains("its position", table.Instruction);

        ShowPlacement("houston--new-orleans", 2);
        Assert.Contains($"2 {active.Color} trains on", table.Instruction);
        Assert.Contains("their positions", table.Instruction);
    }

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

    private static void ShowUprightBoardPreview(MainViewModel model)
    {
        var pixels = new byte[960 * 600 * 4];
        var preview = BitmapSource.Create(960, 600, 96, 96, PixelFormats.Bgra32,
            null, pixels, 960 * 4);
        preview.Freeze();
        model.Camera.GameTablePreview = preview;
        model.Camera.IsGameTablePreviewUpright = true;
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
        await model.RevealPrivateSeatCommand.ExecuteAsync(null);

        await model.DrawBlindCardAsync();
        Assert.Equal("Drew 1 blind train card.", model.Table.Seats.Single(seat => seat.DisplayName == "Alex").LastAction);
        Assert.Equal(Screen.Table, model.Screen);
        Assert.Null(model.PrivateSeat);

        await model.RevealPrivateSeatAsync();
        await model.DrawBlindCardAsync();
        Assert.Equal("Drew 2 blind train cards.", model.Table.Seats.Single(seat => seat.DisplayName == "Alex").LastAction);
        Assert.Equal(Screen.Table, model.Screen);
        Assert.Null(model.PrivateSeat);
    }

    [Fact]
    public async Task KeepingOpeningTicketsReturnsToTableAndCanOpenTheSoloHumansHand()
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

        // The board stays visible until the sole human opens their cards from the player tile.
        Assert.All(offered, ticket => Assert.NotEqual(default, ticket));
        Assert.Null(model.PrivateSeat);
        Assert.True(model.CanRevealPrivateSeat);
        await model.RevealPrivateSeatAsync();
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
    public async Task PendingComputerPlacementTargetsThePublicBoardOnlyWhileAClaimIsWaiting()
    {
        var model = NewMatch(computerOnly: true);
        await model.StartMatchCommand.ExecuteAsync(null);

        var placement = Assert.IsType<PlacementInstruction>(model.Table.Placement);
        Assert.Equal(placement.SeatName, model.Table.ActiveSeatName);
        Assert.False(model.Game.ShowPlacementTarget); // no registered live crop yet
        ShowUprightBoardPreview(model);
        Assert.True(PlacementBoardOverlay.TryGetTarget(TestManifest.Manifest, placement.RouteId,
            out var expectedX, out var expectedY));
        Assert.True(PlacementBoardOverlay.TryGetTargets(TestManifest.Manifest, placement.RouteId,
            placement.TrainCount, out var trainSlots));
        Assert.True(model.Game.ShowPlacementTarget);
        Assert.Equal(expectedX, model.Game.PlacementTargetX);
        Assert.Equal(expectedY, model.Game.PlacementTargetY);
        Assert.Equal(placement.TrainCount, model.Game.PlacementTargets.Count);
        for (var index = 0; index < trainSlots.Count; index++)
        {
            Assert.Equal(trainSlots[index].X, model.Game.PlacementTargets[index].X);
            Assert.Equal(trainSlots[index].Y, model.Game.PlacementTargets[index].Y);
        }
        Assert.InRange(expectedX, 0, DestinationBoardOverlay.Width);
        Assert.InRange(expectedY, 0, DestinationBoardOverlay.Height);

        model.Camera.IsGameTablePreviewUpright = false;
        Assert.False(model.Game.ShowPlacementTarget);
        Assert.Empty(model.Game.PlacementTargets);
        model.Camera.IsGameTablePreviewUpright = true;
        Assert.True(model.Game.ShowPlacementTarget);

        // Updating the public projection after confirmation must remove the cue. Restore the
        // original instruction here so the same fixture can also exercise a fresh pending claim.
        model.Table.Placement = null;
        Assert.False(model.Game.ShowPlacementTarget);
        Assert.Empty(model.Game.PlacementTargets);
        model.Table.Placement = placement;
        Assert.True(model.Game.ShowPlacementTarget);
    }

    [Fact]
    public async Task Human_route_claim_does_not_show_computer_placement_cue()
    {
        var model = NewMatch();
        await model.StartMatchCommand.ExecuteAsync(null);
        ShowUprightBoardPreview(model);

        var human = model.Table.Seats.Single(seat => seat.Operator == "human");
        var routeId = new RouteId("dallas--houston--b");
        var placement = new PlacementInstruction(OperationId.New(), 1,
            human.SeatId, human.DisplayName, human.Color, human.Symbol,
            routeId, "Dallas - Houston (lane B)", "lane B", 1, false);
        model.Table.Placement = placement;

        Assert.False(model.Game.ShowPlacementTarget);
        Assert.Empty(model.Game.PlacementTargets);

        var computer = model.Table.Seats.First(seat => seat.Operator == "computer");
        model.Table.Placement = placement with
        {
            SeatId = computer.SeatId,
            SeatName = computer.DisplayName,
            Color = computer.Color,
            Symbol = computer.Symbol
        };
        Assert.True(model.Game.ShowPlacementTarget);
        Assert.Single(model.Game.PlacementTargets);
    }

    [Fact]
    public async Task ConfirmingAPlacementCommitsTheClaimAndClearsTheInstruction()
    {
        var model = NewMatch(computerOnly: true);
        await model.StartMatchCommand.ExecuteAsync(null);
        ShowUprightBoardPreview(model);

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
        Assert.True(model.Game.ShowPlacementTarget);

        await model.ConfirmPlacementCommand.ExecuteAsync(null);
        Assert.Equal(routesBefore, model.Table.Seats.Single(seat => seat.SeatId == placement.SeatId).RoutesClaimed);
        model.Table.WholeBoardAcknowledged = true;
        await model.ConfirmPlacementCommand.ExecuteAsync(null);

        var after = model.Table.Seats.Single(seat => seat.SeatId == placement.SeatId);
        Assert.Equal(routesBefore + 1, after.RoutesClaimed);
        Assert.True(after.TrainsRemaining < 45);
        Assert.StartsWith("Claimed ", after.LastAction);
        Assert.False(model.Table.WholeBoardAcknowledged);
        Assert.NotEqual(placement.OperationId, model.Table.Placement?.OperationId);
        Assert.Equal(model.Table.Placement is not null, model.Game.ShowPlacementTarget);
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
    private static async Task<MainViewModel> StartedMatchAsync(InMemorySessionStore? store = null,
        TestCheckpointPhotos? photos = null)
    {
        var model = new MainViewModel(TestManifest.Manifest, store ?? new InMemorySessionStore(), photos?.Store);
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
    public async Task DigitalCheckpointStillRequiresBoardImageBeforeClearing()
    {
        var model = await StartedMatchAsync();
        model.Table.SaveName = "Sunday game";

        await model.SaveAndPackAwayCommand.ExecuteAsync(null);

        Assert.True(model.Table.IsPackedAway);
        Assert.Contains("required board image", model.Table.SaveStatus!, StringComparison.OrdinalIgnoreCase);
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
            if (model.Table.Placement is not { } placement) continue;

            model.Table.WholeBoardAcknowledged = true;
            await model.ConfirmPlacementCommand.ExecuteAsync(null);

            // A route claim now waits for its physical score marker before the next AI turn.
            // Complete that public-table step so this test can reach several claimed routes.
            var target = model.Table.Seats.Single(seat => seat.SeatId == placement.SeatId).Score % 100 + 1;
            var color = Enum.Parse<MarkerColor>(placement.Color.ToString());
            model.Camera.IsGameTablePreviewUpright = true;
            var firstAt = DateTimeOffset.UtcNow;
            PublishScore(model.Camera, 1, firstAt, color, target);
            PublishScore(model.Camera, 2, firstAt.AddSeconds(1.1), color, target);
            await WaitUntilAsync(() => model.Game.GuidanceTurn != "Scoring" &&
                model.Table.Placement is not null);
        }

        Assert.True(model.Table.ClaimedRoutes.Count >= 3,
            $"No routes were claimed to rebuild. Placement={model.Table.Placement}, " +
            $"Guidance={model.Game.GuidanceTurn}/{model.Game.GuidanceInstruction}, Status={model.Status}");

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

    private static void PublishScore(CameraViewModel camera, long sequence,
        DateTimeOffset capturedAt, MarkerColor color, int score)
    {
        var frame = CameraFrame.CopyFromBgra32(320, 200, new byte[320 * 200 * 4],
            sequence: sequence, epoch: 1, capturedAt: capturedAt);
        var analysis = new GameTableAnalysis(frame, [],
            [new ScoreMarkerReading(0, color, score, ScoreMarkerReadingStatus.Read,
                "Printed score track position read.")],
            1, 1, "synthetic-test-model");
        typeof(CameraViewModel).GetProperty(nameof(CameraViewModel.GameTableAnalysis))!
            .GetSetMethod(nonPublic: true)!.Invoke(camera, [analysis]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(condition(), "The score-marker step did not finish.");
    }

    [Fact]
    public async Task ResumeNeedsTheOperatorToTickTheConfirmationFirst()
    {
        var store = new InMemorySessionStore();
        using var photos = new TestCheckpointPhotos();
        var model = await StartedMatchAsync(store, photos);
        model.Table.SaveName = "Packed";
        await model.SaveAndPackAwayCommand.ExecuteAsync(null);
        var session = Assert.Single(await store.ListSessionsAsync(CancellationToken.None));
        var saved = await store.RestoreAsync(session.SessionId, TestManifest.Manifest,
            TestManifest.Catalog, CancellationToken.None);
        await photos.AttachAsync(saved.State.Checkpoint!);
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
    /// An interrupted digital save cannot become a completed game save without its required image.
    /// </summary>
    [Fact]
    public async Task ReopeningAnInterruptedSaveKeepsItIntactAndReportsIncompleteImage()
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
            rules, store, setup, Domain.Randomness.DeterministicRandom.SeedFrom(5), cancellationToken: TestContext.Current.CancellationToken);

        var driver = new Application.ComputerSeatDriver(coordinator, new AI.HeuristicAiPolicy(), aiSeed: 5);
        await driver.AdvanceAsync(cancellationToken: TestContext.Current.CancellationToken);

        await coordinator.SubmitAsync(
            new Domain.Engine.SaveAndPackAway(coordinator.NewEnvelope(), "Interrupted"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(SessionLifecycle.PreparingPackAway, coordinator.Public.Lifecycle);

        // Reopen it through the interface.
        using var photos = new TestCheckpointPhotos();
        var model = new MainViewModel(TestManifest.Manifest, store, photos.Store);
        await model.LoadSavedSessionsCommand.ExecuteAsync(null);
        model.Setup.SelectedSavedSession = model.Setup.SavedSessions.Single();

        await model.ResumeMatchCommand.ExecuteAsync(null);

        Assert.False(model.NeedsBoardReconciliation);
        Assert.False(model.Table.IsSaving);
        Assert.False(model.Table.IsPackedAway);
        Assert.Equal(Screen.Setup, model.Screen);
        Assert.Contains("required board image", model.Setup.SavedMatchMessage);
        var unchanged = await store.RestoreAsync(setup.SessionId, TestManifest.Manifest,
            TestManifest.Catalog, CancellationToken.None);
        Assert.Equal(SessionLifecycle.PreparingPackAway, unchanged.State.Lifecycle);
        Assert.Equal(coordinator.Public.StateVersion, unchanged.State.StateVersion);
        await model.DisposeToolsAsync();
    }
}
