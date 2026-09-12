using System.Reflection;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopExitTests
{
    [Fact]
    public async Task NoMatchNeedsNoWarningAndCannotStartWhileClosing()
    {
        var model = NewMatch();
        try
        {
            Assert.Null(model.BeginExitRequest());
            await model.StartMatchAsync();
            Assert.Equal(Screen.Setup, model.Screen);
            Assert.Empty(model.Table.Seats);

            model.CancelExitRequest();
            await model.StartMatchAsync();
            Assert.Equal(Screen.Table, model.Screen);
            Assert.NotEmpty(model.Table.Seats);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Theory]
    [InlineData(false, Screen.Table)]
    [InlineData(false, Screen.Camera)]
    [InlineData(false, Screen.CheckpointPhoto)]
    [InlineData(true, Screen.Table)]
    [InlineData(true, Screen.Camera)]
    [InlineData(true, Screen.CheckpointPhoto)]
    public async Task ActualMatchNeedsWarningAcrossSetupPlayAndToolScreens(bool active, Screen screen)
    {
        var model = NewMatch();
        try
        {
            await model.StartMatchAsync();
            if (active) await model.CommitTicketsAsync();
            model.Screen = screen;

            var prompt = Assert.IsType<ExitPrompt>(model.BeginExitRequest());
            Assert.True(prompt.CanExit);
            Assert.Contains("Are you sure you want to exit?", prompt.Message);
            Assert.Contains("saved automatically", prompt.Message);
            Assert.Null(model.PrivateSeat);
            Assert.False(model.CanRevealPrivateSeat);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task VerifiedPackAwayAndRebuildNeedNoPhotoOrWarningButResumedPlayDoes()
    {
        var model = await ActiveMatchAsync();
        try
        {
            model.Table.SaveName = "Exit checkpoint";
            await model.SaveAndPackAwayAsync();
            Assert.True(model.Table.IsPackedAway);
            Assert.False(model.CheckpointPhoto.HasPhoto);
            Assert.Null(model.BeginExitRequest());
            model.CancelExitRequest();

            await model.BeginRebuildAsync();
            Assert.True(model.Table.IsRebuilding);
            Assert.False(model.CheckpointPhoto.HasPhoto);
            Assert.Null(model.BeginExitRequest());
            model.CancelExitRequest();

            model.Table.RebuildAcknowledged = true;
            await model.AttestRebuildAsync();
            await model.ResumePackedGameAsync();
            Assert.Equal(Screen.Table, model.Screen);
            Assert.False(model.Table.IsPackedAway);
            var prompt = Assert.IsType<ExitPrompt>(model.BeginExitRequest());
            Assert.True(prompt.CanExit);
            Assert.Contains("not been saved for packing away", prompt.Message);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task FailedCheckpointReadbackMustNotClaimASafeSave()
    {
        var store = new ControlledStore { FailCheckpointRead = true };
        var model = await ActiveMatchAsync(store);
        try
        {
            model.Table.SaveName = "Unverified checkpoint";
            await model.SaveAndPackAwayAsync();
            Assert.True(model.Table.IsPackedAway);
            Assert.NotNull(model.Table.SaveProblem);

            var prompt = Assert.IsType<ExitPrompt>(model.BeginExitRequest());
            Assert.True(prompt.CanExit);
            Assert.Contains("save has not been verified", prompt.Message);
            Assert.Contains("Keep the board in place", prompt.Message);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task FailedGameWriteWarnsThatTheLatestActionMayNotBeSaved()
    {
        var store = new ControlledStore();
        var model = await ActiveMatchAsync(store);
        try
        {
            store.FailNextCommit = true;
            await model.DrawBlindCardAsync();

            var prompt = Assert.IsType<ExitPrompt>(model.BeginExitRequest());
            Assert.True(prompt.CanExit);
            Assert.Contains("latest action may not have been saved", prompt.Message);
            Assert.DoesNotContain("saved automatically", prompt.Message);
            Assert.DoesNotContain("SECRET", prompt.Message);
            model.CancelExitRequest();
            Assert.False(model.CanRevealPrivateSeat);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingCreationOrDrawCannotBeInterruptedOrRevealCardsAfterCloseRequest(bool creation)
    {
        var store = new ControlledStore { DelayCreation = creation };
        var model = creation ? NewMatch(store) : await ActiveMatchAsync(store);
        Task? operation = null;
        try
        {
            if (!creation) store.DelayNextCommit = true;
            operation = creation ? model.StartMatchAsync() : model.DrawBlindCardAsync();
            await store.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var busyPrompt = Assert.IsType<ExitPrompt>(model.BeginExitRequest());
            Assert.False(busyPrompt.CanExit);
            Assert.Contains("wait for the current action or save to finish", busyPrompt.Message);

            store.ReleaseWrite.TrySetResult();
            await operation;
            Assert.Null(model.PrivateSeat);
            Assert.False(model.CanRevealPrivateSeat);
            Assert.True(Assert.IsType<ExitPrompt>(model.BeginExitRequest()).CanExit);

            model.CancelExitRequest();
            Assert.Null(model.PrivateSeat);
            Assert.True(model.CanRevealPrivateSeat);
            await model.RevealPrivateSeatAsync();
            Assert.NotNull(model.PrivateSeat);
            Assert.Equal(creation ? 4 : 5, model.PrivateSeat.Hand.Sum(group => group.Count));
        }
        finally
        {
            store.ReleaseWrite.TrySetResult();
            if (operation is not null) await operation;
            await model.DisposeToolsAsync();
        }
    }

    [Fact]
    public async Task CancelingExitKeepsCardsHiddenButAllowsExplicitRevealAndContinuedPlay()
    {
        var model = await ActiveMatchAsync();
        try
        {
            Assert.NotNull(model.PrivateSeat);
            Assert.Equal(4, model.Table.Seats[0].CardCount);
            Assert.NotNull(model.BeginExitRequest());
            await model.RevealPrivateSeatAsync();
            await model.DrawBlindCardAsync();
            model.Table.SaveName = "Must not save during prompt";
            await model.SaveAndPackAwayAsync();
            Assert.Null(model.PrivateSeat);
            Assert.Equal(4, model.Table.Seats[0].CardCount);
            Assert.False(model.Table.IsPackedAway);

            model.CancelExitRequest();
            Assert.Null(model.PrivateSeat);
            await model.RevealPrivateSeatAsync();
            Assert.NotNull(model.PrivateSeat);
            await model.DrawBlindCardAsync();
            Assert.Equal(5, model.Table.Seats[0].CardCount);
            Assert.NotNull(model.PrivateSeat);
            Assert.True(model.PrivateSeat.IsSecondDraw);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task ExitPromptBlocksCompanionControlAndUsesTheMatchBehindPhoneSetup()
    {
        var model = NewMatch();
        model.Setup.Seats[1].IsComputer = false;
        var companionGate = typeof(MainViewModel).GetProperty("CanCompanionControl",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            await model.StartMatchAsync();
            Assert.True((bool)companionGate.GetValue(model)!);
            Assert.NotNull(model.BeginExitRequest());
            Assert.False((bool)companionGate.GetValue(model)!);
            model.CancelExitRequest();
            Assert.True((bool)companionGate.GetValue(model)!);

            model.ShowConnectionCommand.Execute(null);
            Assert.Equal(Screen.Connection, model.Screen);
            Assert.NotNull(model.BeginExitRequest());
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static MainViewModel NewMatch(ISessionStore? store = null)
    {
        var model = new MainViewModel(TestManifest.Manifest, store ?? new InMemorySessionStore());
        model.Setup.ManualVerificationAccepted = true;
        return model;
    }

    private static async Task<MainViewModel> ActiveMatchAsync(ISessionStore? store = null)
    {
        var model = NewMatch(store);
        await model.StartMatchAsync();
        Assert.NotNull(model.PrivateSeat);
        await model.CommitTicketsAsync();
        Assert.NotNull(model.PrivateSeat);
        Assert.True(model.PrivateSeat.CanDrawBlind);
        return model;
    }

    private sealed class ControlledStore : ISessionStore
    {
        private readonly InMemorySessionStore _inner = new();
        public bool DelayCreation { get; init; }
        public bool DelayNextCommit { get; set; }
        public bool FailNextCommit { get; set; }
        public bool FailCheckpointRead { get; init; }
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task CreateAsync(GameState state, CommandId commandId, Transition transition, string stateHash,
            CancellationToken cancellationToken)
        {
            if (DelayCreation) await WaitForReleaseAsync(cancellationToken);
            await _inner.CreateAsync(state, commandId, transition, stateHash, cancellationToken);
        }

        public async Task CommitAsync(GameState state, StoredCommandOutcome outcome, Transition transition,
            string stateHash, CancellationToken cancellationToken)
        {
            if (DelayNextCommit)
            {
                DelayNextCommit = false;
                await WaitForReleaseAsync(cancellationToken);
            }
            if (FailNextCommit)
            {
                FailNextCommit = false;
                throw new IOException("SECRET simulated storage failure");
            }
            await _inner.CommitAsync(state, outcome, transition, stateHash, cancellationToken);
        }

        private async Task WaitForReleaseAsync(CancellationToken cancellationToken)
        {
            WriteStarted.TrySetResult();
            await ReleaseWrite.Task.WaitAsync(cancellationToken);
        }

        public Task<PackAwayCheckpoint?> ReadCheckpointAsync(SessionId sessionId, CheckpointId checkpointId,
            CancellationToken cancellationToken) => FailCheckpointRead
            ? Task.FromResult<PackAwayCheckpoint?>(null)
            : _inner.ReadCheckpointAsync(sessionId, checkpointId, cancellationToken);
        public Task<StoredCommandOutcome?> FindCommandOutcomeAsync(SessionId sessionId, CommandId commandId,
            CancellationToken cancellationToken) => _inner.FindCommandOutcomeAsync(sessionId, commandId, cancellationToken);
        public Task RecordRejectionAsync(SessionId sessionId, StoredCommandOutcome outcome,
            CancellationToken cancellationToken) => _inner.RecordRejectionAsync(sessionId, outcome, cancellationToken);
        public Task<RestoredSession> RestoreAsync(SessionId sessionId, BoardManifest manifest, CardCatalog catalog,
            CancellationToken cancellationToken) => _inner.RestoreAsync(sessionId, manifest, catalog, cancellationToken);
        public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken) =>
            _inner.ListSessionsAsync(cancellationToken);
        public Task DeleteSessionAsync(SessionId sessionId, CancellationToken cancellationToken) =>
            _inner.DeleteSessionAsync(sessionId, cancellationToken);
    }
}
