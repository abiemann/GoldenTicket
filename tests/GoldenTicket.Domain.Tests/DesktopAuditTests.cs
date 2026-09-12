using GoldenTicket.Application;
using GoldenTicket.Desktop;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopAuditTests
{
    [Fact]
    public async Task ManualVerificationMustBeChosenExplicitly()
    {
        var store = new InMemorySessionStore();
        var model = new MainViewModel(TestManifest.Manifest, store);

        await model.StartMatchAsync();

        Assert.Equal(Screen.Setup, model.Screen);
        Assert.Contains("manual verification", model.Setup.ValidationMessage);
        Assert.Empty(await store.ListSessionsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HideOrDeactivationDuringSaveCannotReopenPrivateCards(bool deactivate)
    {
        var store = new DelayedStore();
        var model = await HumanMatchAsync(store);
        await model.RevealPrivateSeatAsync();
        var before = model.PrivateSeat!.Hand.Sum(group => group.Count);
        store.DelayNextCommit();

        var drawing = model.DrawBlindCardAsync();
        await store.CommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(model.PrivateSeat);
        if (deactivate) model.SetWindowActive(false);
        else model.HidePrivateSeat();
        store.ReleaseCommit.TrySetResult();
        await drawing;

        Assert.Null(model.PrivateSeat);
        Assert.Equal(before + 1, model.Table.Seats[0].CardCount);
        if (deactivate)
        {
            await model.RevealPrivateSeatAsync();
            Assert.Null(model.PrivateSeat);
            model.SetWindowActive(true);
            Assert.Null(model.PrivateSeat);
        }

        await model.RevealPrivateSeatAsync();
        Assert.NotNull(model.PrivateSeat);
        Assert.True(model.PrivateSeat.IsSecondDraw);
    }

    [Fact]
    public async Task RejectedPrivateSelectionPreservesChoicesWithoutPublicErrorDetails()
    {
        var model = new MainViewModel(TestManifest.Manifest, new InMemorySessionStore());
        model.Setup.ManualVerificationAccepted = true;
        foreach (var seat in model.Setup.Seats) seat.IsComputer = false;
        await model.StartMatchAsync();
        await model.RevealPrivateSeatAsync();
        foreach (var choice in model.PrivateSeat!.Offer) choice.Keep = false;

        await model.CommitTicketsAsync();

        Assert.NotNull(model.PrivateSeat);
        Assert.All(model.PrivateSeat.Offer, choice => Assert.False(choice.Keep));
        Assert.Contains("At least", model.PrivateSeat.Message);
        Assert.DoesNotContain("At least", model.Status);
    }

    [Fact]
    public async Task FailedSaveKeepsPrivateStateCoveredAndRequiresReload()
    {
        var store = new DelayedStore();
        var model = await HumanMatchAsync(store);
        await model.RevealPrivateSeatAsync();
        store.FailNextCommit = true;

        await model.DrawBlindCardAsync();
        await model.RevealPrivateSeatAsync();

        Assert.Null(model.PrivateSeat);
        Assert.False(model.CanRevealPrivateSeat);
        Assert.DoesNotContain("SECRET", model.Status);
        Assert.Contains("Reopen", model.Status);
    }

    [Fact]
    public async Task RestoringDoesNotRunComputerTurnBeforeWholeBoardCheck()
    {
        var store = new InMemorySessionStore();
        var setupModel = new MainViewModel(TestManifest.Manifest, store);
        setupModel.Setup.ManualVerificationAccepted = true;
        foreach (var seat in setupModel.Setup.Seats) seat.IsComputer = true;
        var setup = setupModel.Setup.TryBuildSetup()!;
        var rules = new GameRules(TestManifest.Manifest, TestManifest.Catalog);
        var coordinator = await GameCoordinator.CreateAsync(rules, store, setup, DeterministicRandom.SeedFrom(42));
        foreach (var seat in coordinator.Seats)
        {
            var view = await coordinator.GetSeatViewAsync(seat.SeatId);
            Assert.True((await coordinator.SubmitAsync(new CommitTicketSelection(
                coordinator.NewEnvelope(seat.SeatId), [.. view.SetupOffer.Take(2)], []))).IsAccepted);
        }

        var before = await coordinator.ComputeStateHashAsync();
        var model = new MainViewModel(TestManifest.Manifest, store);
        await model.LoadSavedSessionsAsync();
        model.Setup.SelectedSavedSession = model.Setup.SavedSessions.Single();
        await model.ResumeMatchAsync();
        await model.RevealPrivateSeatAsync();
        await model.ConfirmBoardReconciledAsync();

        Assert.True(model.NeedsBoardReconciliation);
        Assert.Null(model.PrivateSeat);
        Assert.False(model.CanRevealPrivateSeat);
        Assert.Equal(before, StateHash.Compute((await store.RestoreAsync(
            coordinator.SessionId, TestManifest.Manifest, TestManifest.Catalog, CancellationToken.None)).State));

        model.BoardReconciliationAcknowledged = true;
        await model.ConfirmBoardReconciledAsync();

        Assert.False(model.NeedsBoardReconciliation);
        Assert.False(model.BoardReconciliationAcknowledged);
        Assert.True(model.Table.Placement is not null || model.Table.RulesDecisionText is not null ||
                    model.Screen == Screen.FinalScore);
        Assert.NotEqual(before, StateHash.Compute((await store.RestoreAsync(
            coordinator.SessionId, TestManifest.Manifest, TestManifest.Catalog, CancellationToken.None)).State));
    }

    [Fact]
    public void DiagnosticFilesAreBoundedAndContainNoExceptionPayload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GoldenTicket-diagnostic-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var exception = new InvalidOperationException("SECRET ticket or hand payload");
            for (var day = 0; day < 10; day++) DiagnosticLog.Write(exception, directory, timestamp.AddDays(day));
            for (var error = 0; error < 1000; error++) DiagnosticLog.Write(exception, directory, timestamp.AddDays(9));

            var files = Directory.GetFiles(directory);
            Assert.Equal(DiagnosticLog.MaximumFiles, files.Length);
            Assert.All(files, file =>
            {
                Assert.True(new FileInfo(file).Length <= DiagnosticLog.MaximumFileBytes);
                Assert.DoesNotContain("SECRET", File.ReadAllText(file));
                Assert.Contains(nameof(InvalidOperationException), File.ReadAllText(file));
            });
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<MainViewModel> HumanMatchAsync(ISessionStore store)
    {
        var model = new MainViewModel(TestManifest.Manifest, store);
        model.Setup.ManualVerificationAccepted = true;
        foreach (var seat in model.Setup.Seats) seat.IsComputer = false;
        await model.StartMatchAsync();
        for (var seat = 0; seat < model.Setup.Seats.Count; seat++)
        {
            await model.RevealPrivateSeatAsync();
            Assert.NotNull(model.PrivateSeat);
            await model.CommitTicketsAsync();
        }
        return model;
    }

    private sealed class DelayedStore : ISessionStore
    {
        private readonly InMemorySessionStore _inner = new();
        private bool _delay;
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
                throw new IOException("SECRET ticket or hand payload");
            }
            await _inner.CommitAsync(state, outcome, transition, stateHash, cancellationToken);
        }

        public Task CreateAsync(GameState state, CommandId commandId, Transition transition, string stateHash,
            CancellationToken token) => _inner.CreateAsync(state, commandId, transition, stateHash, token);
        public Task<PackAwayCheckpoint?> ReadCheckpointAsync(SessionId sessionId, CheckpointId checkpointId,
            CancellationToken cancellationToken) => _inner.ReadCheckpointAsync(sessionId, checkpointId, cancellationToken);

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
