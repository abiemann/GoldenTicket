using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Persistence;
using GoldenTicket.Testing;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class SavedMatchDeletionTests
{
    [Fact]
    public async Task RequestIdentifiesTheSelectedMatchAndCancelLeavesBothMatchesIntact()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        var selected = model.Setup.SavedSessions[0];
        model.Setup.SelectedSavedSession = selected;

        Assert.True(model.RequestDeleteSavedMatchCommand.CanExecute(null));
        model.RequestDeleteSavedMatchCommand.Execute(null);

        Assert.True(model.IsSavedMatchDeleteConfirmationOpen);
        Assert.Contains(selected.Description, model.SavedMatchDeleteDescription);
        Assert.True(model.ConfirmDeleteSavedMatchCommand.CanExecute(null));
        Assert.True(model.CancelDeleteSavedMatchCommand.CanExecute(null));
        Assert.Empty(fixture.Store.DeleteAttempts);
        Assert.Equal(2, (await fixture.Store.ListSessionsAsync(Token)).Count);

        model.CancelDeleteSavedMatchCommand.Execute(null);

        Assert.False(model.IsSavedMatchDeleteConfirmationOpen);
        Assert.False(model.ConfirmDeleteSavedMatchCommand.CanExecute(null));
        Assert.Same(selected, model.Setup.SelectedSavedSession);
        Assert.Equal(2, model.Setup.SavedSessions.Count);
        Assert.Empty(fixture.Store.DeleteAttempts);
        Assert.Equal(2, (await fixture.Store.ListSessionsAsync(Token)).Count);
    }

    [Fact]
    public async Task DeletionRequiresASelectionAndAnExplicitConfirmationRequest()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        Assert.Null(model.Setup.SelectedSavedSession);
        Assert.False(model.RequestDeleteSavedMatchCommand.CanExecute(null));
        Assert.False(model.ConfirmDeleteSavedMatchCommand.CanExecute(null));

        // Commands must remain safe even if invoked directly without consulting CanExecute.
        model.RequestDeleteSavedMatchCommand.Execute(null);
        await model.ConfirmDeleteSavedMatchCommand.ExecuteAsync(null);
        Assert.False(model.IsSavedMatchDeleteConfirmationOpen);

        model.Setup.SelectedSavedSession = model.Setup.SavedSessions[0];
        await model.ConfirmDeleteSavedMatchCommand.ExecuteAsync(null);

        Assert.Empty(fixture.Store.DeleteAttempts);
        Assert.Equal(2, model.Setup.SavedSessions.Count);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("refresh")]
    [InlineData("navigation")]
    [InlineData("start")]
    [InlineData("resume")]
    public async Task ChangingContextInvalidatesThePendingConfirmation(string action)
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        model.Setup.SelectedSavedSession = model.Setup.SavedSessions[0];
        model.RequestDeleteSavedMatchCommand.Execute(null);
        Assert.True(model.IsSavedMatchDeleteConfirmationOpen);

        switch (action)
        {
            case "selection":
                model.Setup.SelectedSavedSession = model.Setup.SavedSessions[1];
                break;
            case "refresh":
                await model.LoadSavedSessionsAsync();
                break;
            case "navigation":
                await model.ShowCheckpointPhotoCommand.ExecuteAsync(null);
                Assert.Equal(Screen.CheckpointPhoto, model.Screen);
                model.ShowGameCommand.Execute(null);
                Assert.Equal(Screen.Setup, model.Screen);
                break;
            case "start":
                await model.StartMatchAsync();
                Assert.Equal(Screen.Table, model.Screen);
                break;
            case "resume":
                await model.ResumeMatchAsync();
                Assert.Equal(Screen.Table, model.Screen);
                break;
        }

        Assert.False(model.IsSavedMatchDeleteConfirmationOpen);
        Assert.False(model.ConfirmDeleteSavedMatchCommand.CanExecute(null));
        await model.ConfirmDeleteSavedMatchCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Store.DeleteAttempts);
        Assert.Equal(action == "start" ? 3 : 2, (await fixture.Store.ListSessionsAsync(Token)).Count);
    }

    [Fact]
    public async Task ConfirmDeletesOnlyTheNamedMatchAndRefreshesTheSavedList()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        var selected = model.Setup.SavedSessions[0];
        var other = model.Setup.SavedSessions[1];
        model.Setup.SelectedSavedSession = selected;
        model.RequestDeleteSavedMatchCommand.Execute(null);
        var readsBeforeDelete = fixture.Store.ListReads;

        await model.ConfirmDeleteSavedMatchCommand.ExecuteAsync(null);

        Assert.Equal(selected.SessionId, Assert.Single(fixture.Store.DeleteAttempts));
        Assert.True(fixture.Store.ListReads > readsBeforeDelete);
        Assert.Equal(other.SessionId, Assert.Single(model.Setup.SavedSessions).SessionId);
        Assert.Equal(other.SessionId, Assert.Single(await fixture.Store.ListSessionsAsync(Token)).SessionId);
        Assert.False(model.IsSavedMatchDeleteConfirmationOpen);
        Assert.False(model.ConfirmDeleteSavedMatchCommand.CanExecute(null));
        Assert.Null(model.Busy);
    }

    [Fact]
    public async Task DeletingTheLastMatchLeavesAnEmptyListWithNoDeletionAvailable()
    {
        await using var fixture = await Fixture.CreateAsync(count: 1);
        var model = fixture.Model;
        Assert.NotNull(model.Setup.SelectedSavedSession);
        model.RequestDeleteSavedMatchCommand.Execute(null);

        await model.ConfirmDeleteSavedMatchCommand.ExecuteAsync(null);

        Assert.Empty(model.Setup.SavedSessions);
        Assert.Null(model.Setup.SelectedSavedSession);
        Assert.False(model.RequestDeleteSavedMatchCommand.CanExecute(null));
        Assert.False(model.ConfirmDeleteSavedMatchCommand.CanExecute(null));
        Assert.False(model.ResumeMatchCommand.CanExecute(null));
        Assert.Empty(await fixture.Store.ListSessionsAsync(Token));
    }

    [Fact]
    public async Task ARefreshStartedBeforeDeletionCannotRestoreTheDeletedRow()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        var selected = model.Setup.SavedSessions[0];
        var other = model.Setup.SavedSessions[1];
        model.Setup.SelectedSavedSession = selected;
        fixture.Store.DelayNextList = true;
        var staleRefresh = model.LoadSavedSessionsAsync();
        try
        {
            await fixture.Store.ListStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            model.RequestDeleteSavedMatchCommand.Execute(null);
            await model.ConfirmDeleteSavedMatchCommand.ExecuteAsync(null);
            Assert.Equal(other.SessionId, Assert.Single(model.Setup.SavedSessions).SessionId);

            fixture.Store.ReleaseList.TrySetResult();
            await staleRefresh.WaitAsync(TimeSpan.FromSeconds(10), Token);

            Assert.Equal(selected.SessionId, Assert.Single(fixture.Store.DeleteAttempts));
            Assert.Equal(other.SessionId, Assert.Single(model.Setup.SavedSessions).SessionId);
            Assert.Equal(other.SessionId, Assert.Single(await fixture.Store.ListSessionsAsync(Token)).SessionId);
        }
        finally
        {
            fixture.Store.ReleaseList.TrySetResult();
            await staleRefresh.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    [Fact]
    public async Task StorageFailureKeepsTheSavedMatchAndAllowsAnExplicitRetry()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        var selected = model.Setup.SavedSessions[0];
        model.Setup.SelectedSavedSession = selected;
        fixture.Store.NextDeleteFailure = new IOException("Synthetic storage failure");
        model.RequestDeleteSavedMatchCommand.Execute(null);

        await model.ConfirmDeleteSavedMatchCommand.ExecuteAsync(null);

        Assert.Contains("delete", model.Setup.SavedMatchMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(model.Setup.SavedSessions, row => row.SessionId == selected.SessionId);
        Assert.Contains(await fixture.Store.ListSessionsAsync(Token), row => row.SessionId == selected.SessionId);
        Assert.Null(model.Busy);
        Assert.Equal(selected.SessionId, Assert.Single(fixture.Store.DeleteAttempts));

        model.RequestDeleteSavedMatchCommand.Execute(null);
        Assert.True(model.IsSavedMatchDeleteConfirmationOpen);
        Assert.True(model.ConfirmDeleteSavedMatchCommand.CanExecute(null));
        await model.ConfirmDeleteSavedMatchCommand.ExecuteAsync(null);

        Assert.Equal(new[] { selected.SessionId, selected.SessionId }, fixture.Store.DeleteAttempts);
        Assert.DoesNotContain(model.Setup.SavedSessions, row => row.SessionId == selected.SessionId);
        Assert.DoesNotContain(await fixture.Store.ListSessionsAsync(Token), row => row.SessionId == selected.SessionId);
        Assert.False(model.IsSavedMatchDeleteConfirmationOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NavigatingAwayDisablesDeletionEvenWithTheGameLayerVisible(bool gameLayerVisible)
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        model.SetGameLayerVisible(gameLayerVisible);
        model.Setup.SelectedSavedSession = model.Setup.SavedSessions[0];
        model.RequestDeleteSavedMatchCommand.Execute(null);

        await model.ShowCheckpointPhotoCommand.ExecuteAsync(null);

        Assert.False(model.IsSavedMatchDeleteConfirmationOpen);
        Assert.False(model.RequestDeleteSavedMatchCommand.CanExecute(null));
        Assert.False(model.ConfirmDeleteSavedMatchCommand.CanExecute(null));
        model.RequestDeleteSavedMatchCommand.Execute(null);
        await model.ConfirmDeleteSavedMatchCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Store.DeleteAttempts);

        model.ShowGameCommand.Execute(null);
        Assert.True(model.RequestDeleteSavedMatchCommand.CanExecute(null));
        Assert.False(model.IsSavedMatchDeleteConfirmationOpen);
    }

    [Fact]
    public async Task TheCurrentlyLoadedMatchCannotBeDeletedEvenFromTheSetupScreen()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        var selected = model.Setup.SavedSessions[0];
        var other = model.Setup.SavedSessions[1];
        model.Setup.SelectedSavedSession = selected;
        await model.ResumeMatchAsync();
        Assert.Equal(Screen.Table, model.Screen);

        model.Screen = Screen.Setup;
        Assert.False(model.RequestDeleteSavedMatchCommand.CanExecute(null));
        model.RequestDeleteSavedMatchCommand.Execute(null);
        await model.ConfirmDeleteSavedMatchCommand.ExecuteAsync(null);
        Assert.False(model.IsSavedMatchDeleteConfirmationOpen);
        Assert.Empty(fixture.Store.DeleteAttempts);

        model.Setup.SelectedSavedSession = other;
        Assert.True(model.RequestDeleteSavedMatchCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnExitRequestInvalidatesConfirmationAndBlocksDeletionUntilCancelled()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        model.Setup.SelectedSavedSession = model.Setup.SavedSessions[0];
        model.RequestDeleteSavedMatchCommand.Execute(null);

        model.BeginExitRequest();

        Assert.False(model.RequestDeleteSavedMatchCommand.CanExecute(null));
        Assert.False(model.ConfirmDeleteSavedMatchCommand.CanExecute(null));
        await model.ConfirmDeleteSavedMatchCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Store.DeleteAttempts);
        model.CancelExitRequest();
        Assert.True(model.RequestDeleteSavedMatchCommand.CanExecute(null));
        Assert.False(model.IsSavedMatchDeleteConfirmationOpen);
        Assert.False(model.ConfirmDeleteSavedMatchCommand.CanExecute(null));
    }

    [Fact]
    public async Task InFlightDeletionBlocksOtherActionsAndCannotFollowAChangedSelection()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = fixture.Model;
        var selected = model.Setup.SavedSessions[0];
        var other = model.Setup.SavedSessions[1];
        model.Setup.SelectedSavedSession = selected;
        model.RequestDeleteSavedMatchCommand.Execute(null);
        fixture.Store.DelayDeletion = true;
        var deleting = model.ConfirmDeleteSavedMatchCommand.ExecuteAsync(null);
        try
        {
            await fixture.Store.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.NotNull(model.Busy);
            Assert.False(model.RequestDeleteSavedMatchCommand.CanExecute(null));
            Assert.False(model.ConfirmDeleteSavedMatchCommand.CanExecute(null));
            Assert.False(model.CancelDeleteSavedMatchCommand.CanExecute(null));
            Assert.False(model.ResumeMatchCommand.CanExecute(null));
            Assert.False(model.LoadSavedSessionsCommand.CanExecute(null));
            await model.StartMatchAsync();
            Assert.Equal(2, (await fixture.Store.ListSessionsAsync(Token)).Count);

            // A stale UI event must not retarget or duplicate the pending store operation.
            model.Setup.SelectedSavedSession = other;
            model.RequestDeleteSavedMatchCommand.Execute(null);
            await model.ConfirmDeleteSavedMatchCommand.ExecuteAsync(null);
            model.CancelDeleteSavedMatchCommand.Execute(null);
            Assert.Equal(selected.SessionId, Assert.Single(fixture.Store.DeleteAttempts));

            fixture.Store.ReleaseDelete.TrySetResult();
            await deleting.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(other.SessionId, Assert.Single(model.Setup.SavedSessions).SessionId);
            Assert.Equal(other.SessionId, Assert.Single(await fixture.Store.ListSessionsAsync(Token)).SessionId);
            Assert.False(model.IsSavedMatchDeleteConfirmationOpen);
            Assert.True(model.RequestDeleteSavedMatchCommand.CanExecute(null));
            Assert.Null(model.Busy);
        }
        finally
        {
            fixture.Store.ReleaseDelete.TrySetResult();
            await deleting.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _photoRoot = Path.Combine(Path.GetTempPath(), "GoldenTicket.Tests",
            Guid.NewGuid().ToString("N"));

        private Fixture()
        {
            var camera = new CameraViewModel(capture: new FakeCameraCapture(),
                enumerateDevices: _ => Task.FromResult<IReadOnlyList<CameraDevice>>([]));
            Model = new MainViewModel(TestManifest.Manifest, Store,
                new CheckpointPhotoStore(_photoRoot), camera: camera);
            Model.Setup.ManualVerificationAccepted = true;
            foreach (var seat in Model.Setup.Seats) seat.IsComputer = false;
        }

        public TrackingStore Store { get; } = new();
        public MainViewModel Model { get; }

        public static async Task<Fixture> CreateAsync(int count = 2)
        {
            var fixture = new Fixture();
            try
            {
                for (var index = 0; index < count; index++)
                {
                    var setup = new SessionSetup(SessionId.New(),
                        [new(new SeatId(1), $"Player {index + 1}", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard),
                         new(new SeatId(2), "Opponent", PlayerColor.Red, SeatKind.Human, AiDifficulty.Standard)],
                        new SeatId(1), VerificationMode.Manual);
                    await GameCoordinator.CreateAsync(new GameRules(TestManifest.Manifest, TestManifest.Catalog),
                        fixture.Store, setup, DeterministicRandom.SeedFrom((ulong)index + 1), Token);
                }

                await fixture.Model.LoadSavedSessionsAsync();
                Assert.Equal(count, fixture.Model.Setup.SavedSessions.Count);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Model.DisposeToolsAsync();
            if (Directory.Exists(_photoRoot)) Directory.Delete(_photoRoot, recursive: true);
        }
    }

    private sealed class TrackingStore : ISessionStore
    {
        private readonly InMemorySessionStore _inner = new();

        public List<SessionId> DeleteAttempts { get; } = [];
        public int ListReads { get; private set; }
        public Exception? NextDeleteFailure { get; set; }
        public bool DelayDeletion { get; set; }
        public bool DelayNextList { get; set; }
        public TaskCompletionSource DeleteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDelete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ListStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseList { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DeleteSessionAsync(SessionId sessionId, CancellationToken token)
        {
            DeleteAttempts.Add(sessionId);
            if (NextDeleteFailure is { } failure)
            {
                NextDeleteFailure = null;
                throw failure;
            }

            if (DelayDeletion)
            {
                DeleteStarted.TrySetResult();
                await ReleaseDelete.Task.WaitAsync(token);
            }

            await _inner.DeleteSessionAsync(sessionId, token);
        }

        public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken token)
        {
            ListReads++;
            var rows = await _inner.ListSessionsAsync(token);
            if (DelayNextList)
            {
                DelayNextList = false;
                ListStarted.TrySetResult();
                await ReleaseList.Task.WaitAsync(token);
            }
            return rows;
        }

        public Task CreateAsync(GameState state, CommandId commandId, Transition transition,
            string stateHash, CancellationToken token) =>
            _inner.CreateAsync(state, commandId, transition, stateHash, token);
        public Task<StoredCommandOutcome?> FindCommandOutcomeAsync(SessionId sessionId,
            CommandId commandId, CancellationToken token) =>
            _inner.FindCommandOutcomeAsync(sessionId, commandId, token);
        public Task CommitAsync(GameState state, StoredCommandOutcome outcome,
            Transition transition, string stateHash, CancellationToken token) =>
            _inner.CommitAsync(state, outcome, transition, stateHash, token);
        public Task RecordRejectionAsync(SessionId sessionId, StoredCommandOutcome outcome,
            CancellationToken token) => _inner.RecordRejectionAsync(sessionId, outcome, token);
        public Task<RestoredSession> RestoreAsync(SessionId sessionId, BoardManifest manifest,
            CardCatalog catalog, CancellationToken token) =>
            _inner.RestoreAsync(sessionId, manifest, catalog, token);
        public Task<PackAwayCheckpoint?> ReadCheckpointAsync(SessionId sessionId,
            CheckpointId checkpointId, CancellationToken token) =>
            _inner.ReadCheckpointAsync(sessionId, checkpointId, token);
    }
}
