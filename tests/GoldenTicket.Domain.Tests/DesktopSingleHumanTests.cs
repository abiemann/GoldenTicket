using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopSingleHumanTests
{
    [Fact]
    public async Task OneHumanGetsOpeningTicketsAndBothDrawsOnTheLaptop()
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
            Assert.NotNull(model.PrivateSeat);
            Assert.False(model.PrivateSeat.MustChooseTickets);
            Assert.False(model.ShowSoloOpeningTicketsOnBoard);
            Assert.True(model.PrivateSeat.CanDrawBlind);

            await model.DrawBlindCardAsync();
            Assert.NotNull(model.PrivateSeat);
            Assert.True(model.PrivateSeat.IsSecondDraw);
            Assert.Equal(5, model.PrivateSeat.Hand.Sum(group => group.Count));

            await model.DrawBlindCardAsync();
            await ConfirmComputerPlacementsAsync(model);
            Assert.NotNull(model.PrivateSeat);
            Assert.Equal(model.Table.Seats[0].SeatId, model.PrivateSeat.SeatId);
            Assert.False(model.PrivateSeat.IsSecondDraw);
            Assert.Equal(6, model.PrivateSeat.Hand.Sum(group => group.Count));
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
            Assert.NotNull(model.PrivateSeat);
            Assert.Equal(model.Table.Seats[0].SeatId, model.PrivateSeat.SeatId);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task PackedSoloCardsReturnOnlyAfterTheBoardHasBeenRebuiltAndResumed()
    {
        var model = await StartedSingleHumanMatchAsync(new InMemorySessionStore());
        try
        {
            model.Table.SaveName = "Solo rebuild";
            await model.SaveAndPackAwayAsync();
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
            Assert.NotNull(model.PrivateSeat);
            Assert.Equal(model.Table.Seats[0].SeatId, model.PrivateSeat.SeatId);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static MainViewModel NewSingleHumanMatch(ISessionStore? store = null, int humanIndex = 0)
    {
        var model = new MainViewModel(TestManifest.Manifest, store ?? new InMemorySessionStore());
        model.Setup.ManualVerificationAccepted = true;
        for (var index = 0; index < model.Setup.Seats.Count; index++)
            model.Setup.Seats[index].IsComputer = index != humanIndex;
        return model;
    }

    private static async Task<MainViewModel> StartedSingleHumanMatchAsync(ISessionStore store)
    {
        var model = NewSingleHumanMatch(store);
        await model.StartMatchAsync();
        Assert.NotNull(model.PrivateSeat);
        await model.CommitTicketsAsync();
        Assert.NotNull(model.PrivateSeat);
        return model;
    }

    private static async Task ConfirmComputerPlacementsAsync(MainViewModel model)
    {
        // At most two computer turns intervene before this match's only human acts.
        for (var step = 0; model.Table.Placement is not null && step < 5; step++)
        {
            Assert.Null(model.PrivateSeat);
            Assert.False(model.CanRevealPrivateSeat);
            model.Table.WholeBoardAcknowledged = true;
            await model.ConfirmPlacementAsync();
        }
        Assert.Null(model.Table.Placement);
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
