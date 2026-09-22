using System.Reflection;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Ai;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Persistence;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopSingleHumanTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Solo_table_draw_pile_and_market_take_cards_without_opening_private_view(
        bool computerClaimsRoute)
    {
        var model = NewSingleHumanMatch();
        try
        {
            await model.StartMatchAsync();
            Assert.False(model.DrawSoloBlindCommand.CanExecute(null));
            await model.CommitTicketsAsync();
            Assert.True(model.DrawSoloBlindCommand.CanExecute(null));
            Assert.True(model.DrawSoloTicketsCommand.CanExecute(null));
            var coordinator = UseComputerPolicy(model, new DrawOrClaimPolicy(computerClaimsRoute));
            var firstTurn = coordinator.Public.TurnNumber;
            var firstCount = model.Table.Seats.Single(seat => seat.Operator == "human").CardCount;

            await model.DrawSoloBlindCommand.ExecuteAsync(null);
            Assert.Equal("Taking a second train card", model.Table.PhaseText);
            Assert.Equal(firstCount + 1,
                model.Table.Seats.Single(seat => seat.Operator == "human").CardCount);
            Assert.True(model.ShowSoloTrainCards);
            Assert.Null(model.PrivateSeat);
            Assert.Equal(Screen.Table, model.Screen);
            Assert.False(model.DrawSoloTicketsCommand.CanExecute(null));
            Assert.All(model.Table.Market.Where(slot => slot.Kind == TrainCardKind.Locomotive),
                slot => Assert.False(model.DrawSoloFaceUpCommand.CanExecute(slot)));

            var faceUp = model.Table.Market.First(slot =>
                model.DrawSoloFaceUpCommand.CanExecute(slot));
            var human = coordinator.Public.ActiveSeatId;
            var handBeforeFaceUp = await coordinator.GetSeatViewAsync(human,
                TestContext.Current.CancellationToken);
            await model.DrawSoloFaceUpCommand.ExecuteAsync(faceUp);
            var handAfterFaceUp = await coordinator.GetSeatViewAsync(human,
                TestContext.Current.CancellationToken);
            Assert.Equal(firstCount + 2,
                model.Table.Seats.Single(seat => seat.Operator == "human").CardCount);
            var awarded = Assert.Single(handAfterFaceUp.Hand.Except(handBeforeFaceUp.Hand));
            Assert.Equal(faceUp.Kind, awarded.Kind);
            if (computerClaimsRoute)
            {
                Assert.NotNull(model.Table.Placement);
                Assert.False(model.IsSoloHumanTurn);
                Assert.False(model.DrawSoloBlindCommand.CanExecute(null));
                Assert.False(model.DrawSoloTicketsCommand.CanExecute(null));
                Assert.Equal(firstTurn + 1, coordinator.Public.TurnNumber);
            }
            else
            {
                // PumpAsync completes card-only computer turns before returning. In that case
                // the human is already allowed to play again, rather than stuck on an AI turn.
                Assert.Null(model.Table.Placement);
                Assert.True(model.IsSoloHumanTurn);
                Assert.True(model.DrawSoloBlindCommand.CanExecute(null));
                Assert.True(model.DrawSoloTicketsCommand.CanExecute(null));
                Assert.Equal(firstTurn + coordinator.Seats.Length, coordinator.Public.TurnNumber);
            }
            Assert.Null(model.PrivateSeat);
            Assert.Equal(Screen.Table, model.Screen);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Solo_destination_pile_opens_a_table_offer_and_requires_a_kept_ticket()
    {
        var model = NewSingleHumanMatch();
        try
        {
            await model.StartMatchAsync();
            await model.CommitTicketsAsync();
            var firstCount = model.Table.Seats.Single(seat => seat.Operator == "human").TicketCount;

            await model.DrawSoloTicketsCommand.ExecuteAsync(null);
            Assert.True(model.ShowSoloTicketOffer);
            Assert.True(model.SoloTicketOffer.Count > 0);
            Assert.Equal("Choosing which destinations to keep", model.Table.PhaseText);
            Assert.False(model.ShowDrawPanels);
            Assert.Equal(120, model.GameTableBoardTop);
            Assert.Null(model.PrivateSeat);
            Assert.Equal(Screen.Table, model.Screen);

            foreach (var ticket in model.SoloTicketOffer) ticket.Keep = false;
            Assert.False(model.KeepSoloTicketsCommand.CanExecute(null));
            model.SoloTicketOffer[0].Keep = true;
            Assert.True(model.KeepSoloTicketsCommand.CanExecute(null));
            await model.KeepSoloTicketsCommand.ExecuteAsync(null);

            Assert.False(model.ShowSoloTicketOffer);
            Assert.True(model.ShowDrawPanels);
            Assert.Equal(190, model.GameTableBoardTop);
            Assert.Equal(firstCount + 1,
                model.Table.Seats.Single(seat => seat.Operator == "human").TicketCount);
            Assert.Null(model.PrivateSeat);
            Assert.Equal(Screen.Table, model.Screen);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task OneHumanGetsOpeningDestinationsButReturnsToTheTableAfterEachDraw()
    {
        var model = NewSingleHumanMatch();
        try
        {
            await model.StartMatchAsync();
            Assert.True(model.IsSingleHumanGame);
            Assert.False(model.CanConnectPhone);
            Assert.False(model.ShowConnectionCommand.CanExecute(null));
            Assert.NotNull(model.PrivateSeat);
            Assert.True(model.PrivateSeat.MustChooseTickets);
            Assert.True(model.ShowSoloOpeningTicketsOnBoard);
            Assert.Equal(4, model.PrivateSeat.Hand.Sum(group => group.Count));
            Assert.Equal(3, model.PrivateSeat.Offer.Count);

            // Even invoking the command directly must not navigate to phone setup.
            model.ShowConnectionCommand.Execute(null);
            Assert.Equal(Screen.Table, model.Screen);
            Assert.NotNull(model.PrivateSeat);

            await model.CommitTicketsAsync();
            Assert.Null(model.PrivateSeat);
            Assert.False(model.ShowSoloOpeningTicketsOnBoard);
            Assert.True(model.CanRevealPrivateSeat);
            await model.RevealPrivateSeatAsync();
            Assert.NotNull(model.PrivateSeat);
            Assert.False(model.PrivateSeat.MustChooseTickets);
            Assert.True(model.PrivateSeat.CanDrawBlind);

            await model.DrawBlindCardAsync();
            Assert.Equal(Screen.Table, model.Screen);
            Assert.Null(model.PrivateSeat);
            await model.RevealPrivateSeatAsync();
            Assert.NotNull(model.PrivateSeat);
            Assert.True(model.PrivateSeat.IsSecondDraw);
            Assert.Equal(5, model.PrivateSeat.Hand.Sum(group => group.Count));

            await model.DrawBlindCardAsync();
            await ConfirmComputerPlacementsAsync(model);
            Assert.Equal(Screen.Table, model.Screen);
            Assert.Null(model.PrivateSeat);
            await model.RevealPrivateSeatAsync();
            Assert.NotNull(model.PrivateSeat);
            Assert.Equal(model.Table.Seats[0].SeatId, model.PrivateSeat.SeatId);
            Assert.False(model.PrivateSeat.IsSecondDraw);
            Assert.Equal(6, model.PrivateSeat.Hand.Sum(group => group.Count));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task SoloTrainAndDestinationStacksToggleSmallCardsWithoutLeavingTheTable()
    {
        var model = NewSingleHumanMatch();
        try
        {
            await model.StartMatchAsync();
            var human = model.Game.TableSeats.Single(tile => tile.Seat.Operator == "human");
            var computer = model.Game.TableSeats.First(tile => tile.Seat.Operator == "computer");

            // The opening offer is the one automatic private presentation. Stack clicks during it
            // must leave that required choice in place.
            await model.ToggleSoloTrainCardsCommand.ExecuteAsync(human);
            Assert.True(model.ShowSoloOpeningTicketsOnBoard);
            Assert.False(model.ShowSoloCardPanel);

            await model.CommitTicketsAsync();
            Assert.Null(model.PrivateSeat);
            Assert.Equal(Screen.Table, model.Screen);

            await model.ToggleSoloTrainCardsCommand.ExecuteAsync(computer);
            Assert.False(model.ShowSoloCardPanel);
            await model.ToggleSoloTrainCardsCommand.ExecuteAsync(human);
            Assert.True(model.ShowSoloTrainCards);
            Assert.Equal(4, model.SoloTrainCards.Sum(card => card.Count));
            Assert.True(model.ShowDestinationsWhenViewingTrainCards);
            Assert.True(model.ShowDestinationMarkersOnBoard);
            Assert.NotEmpty(model.BoardDestinationMarkers);
            Assert.Equal(3, model.BoardDestinationLines.Count);
            Assert.All(model.BoardDestinationLines, line => Assert.True(line.IsVisible));
            model.ShowDestinationsWhenViewingTrainCards = false;
            Assert.False(model.ShowDestinationMarkersOnBoard);
            Assert.Empty(model.BoardDestinationMarkers);
            Assert.Empty(model.BoardDestinationLines);
            Assert.Null(model.PrivateSeat);
            Assert.Equal(Screen.Table, model.Screen);

            await model.ToggleSoloDestinationsCommand.ExecuteAsync(human);
            Assert.True(model.ShowSoloDestinations);
            Assert.False(model.ShowSoloTrainCards);
            Assert.Equal(3, model.SoloDestinationCards.Count);
            Assert.All(model.SoloDestinationCards, card => Assert.True(card.Points > 0));
            Assert.True(model.ShowDestinationMarkersOnBoard);
            Assert.NotEmpty(model.SoloDestinationMarkers);
            Assert.Equal(model.SoloDestinationMarkers, model.BoardDestinationMarkers);
            Assert.Equal(model.SoloDestinationLines, model.BoardDestinationLines);
            Assert.Equal(model.SoloDestinationCards.Count, model.BoardDestinationLines.Count);
            Assert.All(model.BoardDestinationMarkers, marker => Assert.True(marker.IsVisible));
            Assert.All(model.BoardDestinationLines, line => Assert.True(line.IsVisible));
            Assert.Null(model.PrivateSeat);

            await model.ToggleSoloDestinationsCommand.ExecuteAsync(human);
            Assert.False(model.ShowSoloCardPanel);
            Assert.Empty(model.SoloDestinationCards);
            Assert.Empty(model.SoloDestinationMarkers);
            Assert.Empty(model.BoardDestinationMarkers);
            Assert.Empty(model.BoardDestinationLines);
            Assert.False(model.ShowDestinationMarkersOnBoard);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Solo_train_preview_groups_duplicate_colors_and_refreshes_counts_after_a_draw()
    {
        var store = new InMemorySessionStore();
        var model = NewSingleHumanMatch(store);
        try
        {
            // This deterministic deal gives the human two pink, one white and one yellow card.
            var game = await GameCoordinator.CreateAsync(
                new GameRules(TestManifest.Manifest, TestManifest.Catalog), store,
                model.Setup.TryBuildSetup()!, DeterministicRandom.SeedFrom(42), cancellationToken: TestContext.Current.CancellationToken);
            foreach (var seat in game.Seats)
            {
                var view = await game.GetSeatViewAsync(seat.SeatId, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True((await game.SubmitAsync(new CommitTicketSelection(
                    game.NewEnvelope(seat.SeatId), [.. view.SetupOffer.Take(2)], []), cancellationToken: TestContext.Current.CancellationToken)).IsAccepted);
            }
            await model.LoadSavedSessionsAsync();
            model.Setup.SelectedSavedSession = Assert.Single(model.Setup.SavedSessions);
            await model.ResumeMatchAsync();
            model.BoardReconciliationAcknowledged = true;
            await model.ConfirmBoardReconciledAsync();
            await model.AcknowledgeResumeTurnCommand.ExecuteAsync(null);
            var coordinator = (GameCoordinator)typeof(MainViewModel)
                .GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
            var human = model.Game.TableSeats.Single(tile => tile.Seat.Operator == "human");
            var before = await coordinator.GetSeatViewAsync(human.Seat.SeatId, cancellationToken: TestContext.Current.CancellationToken);
            var hashBeforePreview = await coordinator.ComputeStateHashAsync(cancellationToken: TestContext.Current.CancellationToken);

            await model.ToggleSoloTrainCardsCommand.ExecuteAsync(human);

            Assert.True(model.ShowSoloTrainCards);
            Assert.Collection(model.SoloTrainCards,
                card => { Assert.Equal(TrainCardKind.Pink, card.Kind); Assert.Equal(2, card.Count); },
                card => { Assert.Equal(TrainCardKind.White, card.Kind); Assert.Equal(1, card.Count); },
                card => { Assert.Equal(TrainCardKind.Yellow, card.Kind); Assert.Equal(1, card.Count); });
            Assert.Equal(before.Hand.Length, model.SoloTrainCards.Sum(card => card.Count));
            Assert.Equal(hashBeforePreview, await coordinator.ComputeStateHashAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(before.Hand.ToArray(),
                (await coordinator.GetSeatViewAsync(human.Seat.SeatId, cancellationToken: TestContext.Current.CancellationToken)).Hand.ToArray());

            // A matching face-up draw must update the existing yellow preview, not add a duplicate.
            var yellow = model.Table.Market.First(slot => slot.Kind == TrainCardKind.Yellow);
            Assert.True(model.DrawSoloFaceUpCommand.CanExecute(yellow));
            await model.DrawSoloFaceUpCommand.ExecuteAsync(yellow);

            Assert.True(model.IsSoloHumanTurn);
            Assert.True(model.ShowSoloTrainCards);
            Assert.Equal(3, model.SoloTrainCards.Count);
            Assert.Equal(2, Assert.Single(model.SoloTrainCards, card => card.Kind == TrainCardKind.Yellow).Count);
            Assert.Equal(5, model.SoloTrainCards.Sum(card => card.Count));
            var after = await coordinator.GetSeatViewAsync(human.Seat.SeatId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(before.Hand.Length + 1, after.Hand.Length);
            Assert.All(before.Hand, card => Assert.Contains(card, after.Hand));
            Assert.Equal(5, after.Hand.Select(card => card.Id).Distinct().Count());
            Assert.Equal(TrainCardKind.Yellow,
                Assert.Single(after.Hand, card => !before.Hand.Any(old => old.Id == card.Id)).Kind);
            Assert.Null(model.PrivateSeat);
            Assert.Equal(Screen.Table, model.Screen);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Train_card_destination_overlay_setting_starts_on_and_survives_app_restart()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "GoldenTicket-presentation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new MainViewModel(ManifestLoader.LoadClassicUs(), new SqliteSessionStore(root));
            try
            {
                Assert.True(first.ShowDestinationsWhenViewingTrainCards);
                first.ShowDestinationsWhenViewingTrainCards = false;
            }
            finally { await first.DisposeToolsAsync(); }

            var second = new MainViewModel(ManifestLoader.LoadClassicUs(), new SqliteSessionStore(root));
            try { Assert.False(second.ShowDestinationsWhenViewingTrainCards); }
            finally { await second.DisposeToolsAsync(); }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SoloCardStacksStayClosedWhileComputerTakesItsTurn()
    {
        var model = NewSingleHumanMatch();
        try
        {
            await model.StartMatchAsync();
            await model.CommitTicketsAsync();
            UseComputerPolicy(model, new DrawOrClaimPolicy(claim: true));
            await model.RevealPrivateSeatAsync();
            await model.DrawBlindCardAsync();
            await model.RevealPrivateSeatAsync();
            await model.DrawBlindCardAsync();
            var human = model.Game.TableSeats.Single(tile => tile.Seat.Operator == "human");

            Assert.False(model.IsSoloHumanTurn,
                $"Active={model.Table.ActiveSeatName}; phase={model.Table.PhaseText}; placement={model.Table.Placement is not null}; status={model.Status}");
            Assert.Contains(model.Table.Seats, seat =>
                seat.Operator == "computer" && seat.DisplayName == model.Table.ActiveSeatName);
            await model.ToggleSoloTrainCardsCommand.ExecuteAsync(human);
            await model.ToggleSoloDestinationsCommand.ExecuteAsync(human);
            Assert.False(model.ShowSoloCardPanel);
            Assert.Empty(model.SoloTrainCards);
            Assert.Empty(model.SoloDestinationCards);
            Assert.False(model.ShowDestinationMarkersOnBoard);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task OpeningDestinationChoiceSurvivesFocusLossButLaterPrivateHandsDoNot()
    {
        var model = NewSingleHumanMatch();
        try
        {
            await model.StartMatchAsync();
            var opening = Assert.IsType<PrivateSeatViewModel>(model.PrivateSeat);
            var dropped = opening.Offer[0];
            dropped.Keep = false;
            opening.RefreshKeepValidity();

            model.SetWindowActive(false);
            Assert.Same(opening, model.PrivateSeat);
            Assert.True(model.ShowSoloOpeningTicketsOnBoard);
            Assert.False(model.CanRevealPrivateSeat);
            Assert.False(dropped.Keep);
            await model.CommitTicketsAsync();
            Assert.Same(opening, model.PrivateSeat);

            model.SetWindowActive(true);
            Assert.Same(opening, model.PrivateSeat);
            await model.CommitTicketsAsync();
            Assert.Equal(2, model.Table.Seats[0].TicketCount);
            Assert.False(model.ShowSoloOpeningTicketsOnBoard);
            Assert.Null(model.PrivateSeat);
            Assert.True(model.CanRevealPrivateSeat);
            await model.RevealPrivateSeatAsync();
            Assert.NotNull(model.PrivateSeat);

            model.SetWindowActive(false);
            Assert.Null(model.PrivateSeat);
            model.SetWindowActive(true);
            Assert.Null(model.PrivateSeat);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task AutomaticCardsBelongToTheOnlyHumanEvenWhenAnAiStarts()
    {
        var model = NewSingleHumanMatch(humanIndex: 2);
        try
        {
            await model.StartMatchAsync();
            Assert.NotNull(model.PrivateSeat);
            Assert.Equal(model.Table.Seats[2].SeatId, model.PrivateSeat.SeatId);
            await model.CommitTicketsAsync();
            await ConfirmComputerPlacementsAsync(model);
            Assert.Equal(Screen.Table, model.Screen);
            Assert.Null(model.PrivateSeat);
            await model.RevealPrivateSeatAsync();
            Assert.NotNull(model.PrivateSeat);
            Assert.Equal(model.Table.Seats[2].SeatId, model.PrivateSeat.SeatId);
            Assert.Equal(4, model.PrivateSeat.Hand.Sum(group => group.Count));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task PhoneAvailabilityTracksSetupHumansAndNotComputerSeats()
    {
        var model = NewSingleHumanMatch();
        try
        {
            var changes = new List<string?>();
            model.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
            Assert.Equal(1, model.Setup.HumanSeatCount);
            Assert.False(model.CanConnectPhone);

            model.Setup.AddSeat();
            Assert.Equal(1, model.Setup.HumanSeatCount);
            Assert.False(model.CanConnectPhone);
            model.Setup.Seats[^1].IsComputer = false;
            Assert.Equal(2, model.Setup.HumanSeatCount);
            Assert.True(model.CanConnectPhone);
            Assert.True(model.ShowConnectionCommand.CanExecute(null));
            Assert.Contains(nameof(MainViewModel.CanConnectPhone), changes);

            model.Setup.RemoveSeat();
            Assert.Equal(1, model.Setup.HumanSeatCount);
            Assert.False(model.CanConnectPhone);
            model.Setup.Seats[0].IsComputer = true;
            Assert.Equal(0, model.Setup.HumanSeatCount);
            Assert.False(model.IsSingleHumanGame);
            Assert.False(model.CanConnectPhone);
            model.ShowConnectionCommand.Execute(null);
            Assert.Equal(Screen.Setup, model.Screen);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveMatchUsesItsSavedRosterInsteadOfMutableSetupRows(bool multipleHumans)
    {
        var model = NewSingleHumanMatch();
        model.Setup.Seats[1].IsComputer = !multipleHumans;
        try
        {
            await model.StartMatchAsync();
            Assert.Equal(multipleHumans, model.CanConnectPhone);
            Assert.Equal(!multipleHumans, model.IsSingleHumanGame);
            if (multipleHumans) Assert.Null(model.PrivateSeat);
            else Assert.NotNull(model.PrivateSeat);

            foreach (var seat in model.Setup.Seats) seat.IsComputer = multipleHumans;
            Assert.Equal(multipleHumans, model.CanConnectPhone);
            Assert.Equal(!multipleHumans, model.IsSingleHumanGame);
            Assert.Equal(multipleHumans, model.ShowConnectionCommand.CanExecute(null));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Theory]
    [InlineData("hide")]
    [InlineData("deactivate")]
    [InlineData("lock")]
    public async Task DelayedSingleHumanDrawCannotUndoAnExplicitPrivacyAction(string action)
    {
        var store = new DelayedStore();
        var model = await StartedSingleHumanMatchAsync(store);
        try
        {
            store.DelayNextCommit();
            var drawing = model.DrawBlindCardAsync();
            await store.CommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Null(model.PrivateSeat);
            if (action == "deactivate") model.SetWindowActive(false);
            else if (action == "lock") await model.SetSystemAvailableAsync(false);
            else model.HidePrivateSeat();

            store.ReleaseCommit.TrySetResult();
            await drawing;
            Assert.Null(model.PrivateSeat);
            Assert.Equal(5, model.Table.Seats[0].CardCount);
            if (action != "hide")
            {
                await model.RevealPrivateSeatAsync();
                Assert.Null(model.PrivateSeat);
                if (action == "deactivate") model.SetWindowActive(true);
                else await model.SetSystemAvailableAsync(true);
                Assert.Null(model.PrivateSeat);
            }
            await model.RevealPrivateSeatAsync();
            Assert.NotNull(model.PrivateSeat);
            Assert.True(model.PrivateSeat.IsSecondDraw);
        }
        finally
        {
            store.ReleaseCommit.TrySetResult();
            await model.DisposeToolsAsync();
        }
    }

    [Fact]
    public async Task ReturningFromAToolDoesNotUndoTheSingleHumansPrivacyCurtain()
    {
        var model = await StartedSingleHumanMatchAsync(new InMemorySessionStore());
        try
        {
            await model.ShowCheckpointPhotoCommand.ExecuteAsync(null);
            Assert.Equal(Screen.CheckpointPhoto, model.Screen);
            Assert.Null(model.PrivateSeat);
            model.ShowGameCommand.Execute(null);
            Assert.Equal(Screen.Table, model.Screen);
            Assert.Null(model.PrivateSeat);
            model.SetWindowActive(true);
            Assert.Null(model.PrivateSeat);
            await model.RevealPrivateSeatAsync();
            Assert.NotNull(model.PrivateSeat);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task HideDuringInitialCreationCannotBeUndoneByOpeningTheTable()
    {
        var store = new DelayedStore { DelayCreation = true };
        var model = NewSingleHumanMatch(store);
        try
        {
            var starting = model.StartMatchAsync();
            await store.CommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            model.HidePrivateSeat();
            store.ReleaseCommit.TrySetResult();
            await starting;
            Assert.Equal(Screen.Table, model.Screen);
            Assert.Null(model.PrivateSeat);
            Assert.True(model.CanRevealPrivateSeat);
            await model.RevealPrivateSeatAsync();
            Assert.NotNull(model.PrivateSeat);
            Assert.True(model.PrivateSeat.MustChooseTickets);
        }
        finally
        {
            store.ReleaseCommit.TrySetResult();
            await model.DisposeToolsAsync();
        }
    }

    [Fact]
    public async Task StorageFailureBlocksAutomaticAndExplicitSingleHumanCards()
    {
        var store = new DelayedStore();
        var model = await StartedSingleHumanMatchAsync(store);
        try
        {
            store.FailNextCommit = true;
            await model.DrawBlindCardAsync();
            await model.RevealPrivateSeatAsync();
            Assert.Null(model.PrivateSeat);
            Assert.False(model.CanRevealPrivateSeat);
            Assert.Contains("Reopen", model.Status);
            Assert.DoesNotContain("SECRET", model.Status);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task RestoredSoloCardsWaitForWholeBoardReconciliation()
    {
        var store = new InMemorySessionStore();
        var original = await StartedSingleHumanMatchAsync(store);
        await original.DisposeToolsAsync();
        var model = new MainViewModel(TestManifest.Manifest, store);
        try
        {
            await model.LoadSavedSessionsAsync();
            model.Setup.SelectedSavedSession = model.Setup.SavedSessions.Single();
            await model.ResumeMatchAsync();
            Assert.True(model.IsSingleHumanGame);
            Assert.False(model.CanConnectPhone);
            Assert.True(model.NeedsBoardReconciliation);
            Assert.Null(model.PrivateSeat);
            await model.RevealPrivateSeatAsync();
            await model.ConfirmBoardReconciledAsync();
            Assert.Null(model.PrivateSeat);
            Assert.True(model.NeedsBoardReconciliation);

            model.BoardReconciliationAcknowledged = true;
            await model.ConfirmBoardReconciledAsync();
            Assert.False(model.NeedsBoardReconciliation);
            Assert.Equal(Screen.Table, model.Screen);
            Assert.Null(model.PrivateSeat);
            Assert.True(model.IsResumeTurnAnnouncementOpen);
            await model.RevealPrivateSeatAsync();
            Assert.Null(model.PrivateSeat);
            await model.AcknowledgeResumeTurnCommand.ExecuteAsync(null);
            await model.RevealPrivateSeatAsync();
            Assert.NotNull(model.PrivateSeat);
            Assert.Equal(model.Table.Seats[0].SeatId, model.PrivateSeat.SeatId);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task PackedSoloCardsReturnOnlyAfterTheBoardHasBeenRebuiltAndResumed()
    {
        using var photos = new TestCheckpointPhotos();
        var store = new InMemorySessionStore();
        var model = await StartedSingleHumanMatchAsync(store, photos);
        try
        {
            model.Table.SaveName = "Solo rebuild";
            await model.SaveAndPackAwayAsync();
            var session = Assert.Single(await store.ListSessionsAsync(CancellationToken.None));
            var saved = await store.RestoreAsync(session.SessionId, TestManifest.Manifest,
                TestManifest.Catalog, CancellationToken.None);
            await photos.AttachAsync(saved.State.Checkpoint!);
            Assert.True(model.Table.IsPackedAway);
            Assert.Null(model.PrivateSeat);
            Assert.False(model.CanRevealPrivateSeat);
            await model.BeginRebuildAsync();
            Assert.Equal(Screen.Rebuild, model.Screen);
            Assert.Null(model.PrivateSeat);
            await model.ResumePackedGameAsync();
            Assert.Equal(Screen.Rebuild, model.Screen);
            Assert.Null(model.PrivateSeat);

            model.Table.RebuildAcknowledged = true;
            await model.AttestRebuildAsync();
            Assert.Null(model.PrivateSeat);
            await model.ResumePackedGameAsync();
            Assert.Equal(Screen.Table, model.Screen);
            Assert.Null(model.PrivateSeat);
            Assert.True(model.IsResumeTurnAnnouncementOpen);
            await model.AcknowledgeResumeTurnCommand.ExecuteAsync(null);
            await model.RevealPrivateSeatAsync();
            Assert.NotNull(model.PrivateSeat);
            Assert.Equal(model.Table.Seats[0].SeatId, model.PrivateSeat.SeatId);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static MainViewModel NewSingleHumanMatch(ISessionStore? store = null, int humanIndex = 0,
        TestCheckpointPhotos? photos = null)
    {
        var model = new MainViewModel(TestManifest.Manifest, store ?? new InMemorySessionStore(), photos?.Store);
        model.Setup.ManualVerificationAccepted = true;
        for (var index = 0; index < model.Setup.Seats.Count; index++)
            model.Setup.Seats[index].IsComputer = index != humanIndex;
        return model;
    }

    private static GameCoordinator UseComputerPolicy(MainViewModel model, IAiPolicy policy)
    {
        var coordinator = (GameCoordinator)typeof(MainViewModel)
            .GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        typeof(MainViewModel).GetField("_driver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(model, new ComputerSeatDriver(coordinator, policy, aiSeed: 1));
        return coordinator;
    }

    private sealed class DrawOrClaimPolicy(bool claim) : IAiPolicy
    {
        public ValueTask<AiDecision> ChooseAsync(SeatView view, BoardManifest manifest,
            DecisionBudget budget, DeterministicRandom random, CancellationToken cancellationToken)
        {
            // A fresh classic board always offers a one-train grey route affordable from the
            // four-card starting hand. Use it to keep the turn awaiting physical placement.
            if (claim)
            {
                var route = LegalActionCalculator.For(view, manifest).Claims
                    .OrderBy(route => route.Length).ThenBy(route => route.RouteId.Value).First();
                return ValueTask.FromResult<AiDecision>(new AiClaimRoute(route.RouteId, route.Payments[0]));
            }
            return ValueTask.FromResult<AiDecision>(new AiDrawTrainCard(null));
        }
    }

    [Fact]
    public async Task Invalid_physical_route_blocks_draws_until_fresh_frames_verify_its_removal()
    {
        var model = NewSingleHumanMatch();
        try
        {
            await model.StartMatchAsync();
            await model.CommitTicketsAsync();
            var coordinator = (GameCoordinator)typeof(MainViewModel)
                .GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
            var seat = coordinator.Public.ActiveSeatId;
            await (Task)typeof(MainViewModel).GetMethod("LoadBoardFirstClaimsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(model, [coordinator, seat])!;
            var observe = typeof(MainViewModel).GetMethod("ObserveBoardFirstClaim", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ClassicUsRouteGeometry.TryGetSlots("calgary--winnipeg", out var slots);
            var color = Enum.Parse<MarkerColor>(coordinator.Public.SeatOf(seat).Color.ToString());
            var at = DateTimeOffset.UtcNow;
            GameTableAnalysis Analysis(long sequence, double seconds, bool occupied)
            {
                const int width = 960, height = 600;
                var pixels = Enumerable.Repeat((byte)180, width * height * 4).ToArray();
                var candidates = new List<PieceCandidate>();
                foreach (var slot in occupied ? slots : [])
                {
                    var x = (int)Math.Round(slot.X * width); var y = (int)Math.Round(slot.Y * height);
                    var rgb = color switch { MarkerColor.Blue => (20, 75, 195), MarkerColor.Yellow => (225, 180, 20), _ => throw new InvalidOperationException("Unexpected fixture color") };
                    for (var py = y - 7; py <= y + 7; py++)
                    for (var px = x - 11; px <= x + 11; px++)
                    {
                        var offset = (py * width + px) * 4;
                        pixels[offset] = (byte)rgb.Item3; pixels[offset + 1] = (byte)rgb.Item2; pixels[offset + 2] = (byte)rgb.Item1;
                    }
                    candidates.Add(new(PieceCandidateKind.Train,
                        [new((x - 11d) / width, (y - 7d) / height), new((x + 11d) / width, (y - 7d) / height),
                         new((x + 11d) / width, (y + 7d) / height), new((x - 11d) / width, (y + 7d) / height)], .9));
                }
                var clock = new ManualFrameTimeProvider();
                var frame = CameraFrame.CopyFromBgra32(width, height, pixels, sequence, 1, at.AddSeconds(seconds), clock);
                if (seconds < 0) clock.Advance(TimeSpan.FromSeconds(3));
                return new(frame, candidates, [], 1, 1, "test");
            }
            void Observe(long seq, double seconds, bool occupied) => observe.Invoke(model, [Analysis(seq, seconds, occupied)]);
            Assert.True(model.DrawSoloBlindCommand.CanExecute(null));
            Observe(1, 0, true);
            Observe(2, 1.1, true);
            Assert.Contains("Calgary", model.Game.GuidanceInstruction);
            Assert.Contains("Remove the trains before drawing cards", model.Game.GuidanceInstruction);
            Assert.False(model.DrawSoloBlindCommand.CanExecute(null));
            Assert.False(model.DrawSoloTicketsCommand.CanExecute(null));
            Assert.All(model.Table.Market, card => Assert.False(model.DrawSoloFaceUpCommand.CanExecute(card)));
            var before = await coordinator.ComputeStateHashAsync(TestContext.Current.CancellationToken);
            await model.DrawSoloBlindCommand.ExecuteAsync(null);
            await model.DrawSoloTicketsCommand.ExecuteAsync(null);
            await model.DrawSoloFaceUpCommand.ExecuteAsync(model.Table.Market[0]);
            Assert.Equal(before, await coordinator.ComputeStateHashAsync(TestContext.Current.CancellationToken));

            model.IsGameExitMenuOpen = true;
            Observe(3, 2.2, true);
            model.IsGameExitMenuOpen = false;
            Assert.False(model.DrawSoloBlindCommand.CanExecute(null));

            // Viewing a hand must not erase the physical warning or permit technical draws.
            await model.RevealPrivateSeatAsync();
            Assert.NotNull(model.PrivateSeat);
            Observe(3, 2.2, true);
            await model.DrawBlindCardAsync();
            Assert.Equal(before, await coordinator.ComputeStateHashAsync(TestContext.Current.CancellationToken));
            model.HidePrivateSeat();
            Assert.False(model.DrawSoloBlindCommand.CanExecute(null));

            // An old empty camera result is not evidence that the pieces were removed.
            Observe(4, -20, false);
            Observe(5, -18, false);
            Assert.False(model.DrawSoloBlindCommand.CanExecute(null));
            Observe(6, 4, false);
            Assert.False(model.DrawSoloBlindCommand.CanExecute(null));
            Observe(7, 5.1, false);
            Assert.True(model.DrawSoloBlindCommand.CanExecute(null));
            await model.DrawSoloBlindCommand.ExecuteAsync(null);
            Assert.Equal("Taking a second train card", model.Table.PhaseText);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Extra_train_guidance_names_the_route_count_and_color()
    {
        var model = NewSingleHumanMatch();
        try
        {
            var placement = new PlacementInstruction(OperationId.New(), 1, new SeatId(1),
                "Computer 1", PlayerColor.Blue, "", new RouteId("los-angeles--phoenix"),
                "Los Angeles - Phoenix", null, 3, false);
            var observation = new BoardInventoryObservation(BoardInventoryState.UnexpectedTrain,
                new Dictionary<MarkerColor, int>(), UnexpectedTrains:
                    new UnexpectedTrainLocation("calgary--winnipeg", MarkerColor.Yellow, 6));
            var update = typeof(MainViewModel).GetMethod("UpdatePlacementInventoryGuidance",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            update.Invoke(model, [placement, observation]);
            Assert.Equal("The camera sees 6 yellow trains on Calgary - Winnipeg, an unclaimed route. Remove them to continue.",
                model.Game.GuidanceInstruction);
            update.Invoke(model, [placement, observation with { UnexpectedTrains = null }]);
            Assert.StartsWith("Check for train pieces outside", model.Game.GuidanceInstruction);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static async Task<MainViewModel> StartedSingleHumanMatchAsync(ISessionStore store,
        TestCheckpointPhotos? photos = null)
    {
        var model = NewSingleHumanMatch(store, photos: photos);
        await model.StartMatchAsync();
        Assert.NotNull(model.PrivateSeat);
        await model.CommitTicketsAsync();
        Assert.Null(model.PrivateSeat);
        await model.RevealPrivateSeatAsync();
        Assert.NotNull(model.PrivateSeat);
        return model;
    }

    private static async Task ConfirmComputerPlacementsAsync(MainViewModel model)
    {
        // At most two computer turns intervene before this match's only human acts.
        for (var step = 0; model.Table.Placement is not null && step < 5; step++)
        {
            var placement = model.Table.Placement;
            Assert.Null(model.PrivateSeat);
            Assert.False(model.CanRevealPrivateSeat);
            model.Table.WholeBoardAcknowledged = true;
            await model.ConfirmPlacementAsync();
            Assert.Equal("Scoring", model.Game.GuidanceTurn);
            var target = model.Table.Seats.Single(seat => seat.SeatId == placement.SeatId).Score % 100 + 1;
            var color = Enum.Parse<MarkerColor>(placement.Color.ToString());
            var firstAt = DateTimeOffset.UtcNow;
            model.Camera.IsGameTablePreviewUpright = true;
            PublishScore(model.Camera, 1, firstAt, color, target);
            PublishScore(model.Camera, 2, firstAt.AddSeconds(1.1), color, target);
            for (var attempt = 0; attempt < 250 &&
                 (model.Game.GuidanceTurn == "Scoring" ||
                  (model.Table.Placement is null && !model.CanRevealPrivateSeat)); attempt++)
                await Task.Delay(20, TestContext.Current.CancellationToken);
            Assert.NotEqual("Scoring", model.Game.GuidanceTurn);
            Assert.True(model.Table.Placement is not null || model.CanRevealPrivateSeat,
                "A scored computer turn must advance to another placement or the human turn.");
        }
        Assert.Null(model.Table.Placement);
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

    private sealed class DelayedStore : ISessionStore
    {
        private readonly InMemorySessionStore _inner = new();
        private bool _delay;
        public bool DelayCreation { get; init; }
        public bool FailNextCommit { get; set; }
        public TaskCompletionSource CommitStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCommit { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void DelayNextCommit()
        {
            _delay = true;
            CommitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ReleaseCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task CommitAsync(GameState state, StoredCommandOutcome outcome, Transition transition,
            string stateHash, CancellationToken cancellationToken)
        {
            if (_delay)
            {
                _delay = false;
                CommitStarted.TrySetResult();
                await ReleaseCommit.Task.WaitAsync(cancellationToken);
            }
            if (FailNextCommit)
            {
                FailNextCommit = false;
                throw new IOException("SECRET test-only write failure");
            }
            await _inner.CommitAsync(state, outcome, transition, stateHash, cancellationToken);
        }

        public async Task CreateAsync(GameState state, CommandId commandId, Transition transition, string stateHash,
            CancellationToken token)
        {
            if (DelayCreation)
            {
                CommitStarted.TrySetResult();
                await ReleaseCommit.Task.WaitAsync(token);
            }
            await _inner.CreateAsync(state, commandId, transition, stateHash, token);
        }
        public Task<PackAwayCheckpoint?> ReadCheckpointAsync(SessionId sessionId, CheckpointId checkpointId,
            CancellationToken token) => _inner.ReadCheckpointAsync(sessionId, checkpointId, token);
        public Task<StoredCommandOutcome?> FindCommandOutcomeAsync(SessionId sessionId, CommandId commandId,
            CancellationToken token) => _inner.FindCommandOutcomeAsync(sessionId, commandId, token);
        public Task RecordRejectionAsync(SessionId sessionId, StoredCommandOutcome outcome, CancellationToken token) =>
            _inner.RecordRejectionAsync(sessionId, outcome, token);
        public Task<RestoredSession> RestoreAsync(SessionId sessionId, BoardManifest manifest, CardCatalog catalog,
            CancellationToken token) => _inner.RestoreAsync(sessionId, manifest, catalog, token);
        public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken token) => _inner.ListSessionsAsync(token);
        public Task DeleteSessionAsync(SessionId sessionId, CancellationToken token) => _inner.DeleteSessionAsync(sessionId, token);
    }
}
