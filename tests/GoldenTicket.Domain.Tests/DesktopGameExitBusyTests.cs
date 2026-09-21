using System.Reflection;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopGameExitBusyTests
{
    [Theory]
    [InlineData(0)] // The human's second card is still being saved.
    [InlineData(1)] // The next computer action is still being saved.
    public async Task EscapeDuringCardSaveDoesNotStallComputerTurnsOrLeaveTheTableStale(
        int commitsBeforeDelay)
    {
        var store = new DelayedCommitStore();
        var manifest = ManifestLoader.LoadClassicUs();
        var catalog = CardCatalog.FromManifest(manifest);
        var model = new MainViewModel(manifest, store);
        model.Setup.ManualVerificationAccepted = true;
        model.SetGameLayerVisible(true);
        Task? drawing = null;
        try
        {
            await model.StartMatchAsync();
            await model.CommitTicketsAsync();
            model.Camera.IsGameTablePreviewUpright = true;
            var firstDraw = model.DrawSoloBlindCommand.ExecuteAsync(null);
            ConfirmEmptyBoard(model.Camera, firstSequence: 1);
            await firstDraw.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("Taking a second train card", model.Table.PhaseText);
            var session = Assert.Single(await store.ListSessionsAsync(CancellationToken.None));
            var before = (await store.RestoreAsync(session.SessionId, manifest, catalog,
                CancellationToken.None)).State;

            store.DelayCommitAfter(commitsBeforeDelay);
            drawing = model.DrawSoloBlindCommand.ExecuteAsync(null);
            ConfirmEmptyBoard(model.Camera, firstSequence: 3);
            await store.CommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

            model.OpenGameExitMenu();
            Assert.False(model.IsGameExitMenuOpen);

            store.ReleaseCommit.TrySetResult();
            await drawing.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

            var after = (await store.RestoreAsync(session.SessionId, manifest, catalog,
                CancellationToken.None)).State;
            Assert.True(after.StateVersion > before.StateVersion + 1,
                "The computer must advance after the human's second draw is saved.");
            Assert.Equal(after.ActiveSeat.DisplayName, model.Table.ActiveSeatName);
            Assert.Equal($"Turn {after.TurnNumber}", model.Table.TurnText);
            Assert.Equal(after.PendingClaim is not null, model.Table.Placement is not null);
            Assert.False(after.ActiveSeat.Kind == SeatKind.Computer &&
                after.TurnPhase == TurnPhase.TurnStart);
            Assert.Null(model.Busy);

            // Escape is available again once the complete action has settled.
            model.OpenGameExitMenu();
            Assert.True(model.IsGameExitMenuOpen);
            model.CloseGameExitMenu();
            Assert.False(model.IsGameExitMenuOpen);
        }
        finally
        {
            store.ReleaseCommit.TrySetResult();
            try
            {
                // Drain the released save even if the test was cancelled, before disposing its tools.
                if (drawing is not null)
                    await drawing.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            finally
            {
                await model.DisposeToolsAsync();
            }
        }
    }

    private static void ConfirmEmptyBoard(CameraViewModel camera, long firstSequence)
    {
        var capturedAt = DateTimeOffset.UtcNow;
        for (var index = 0; index < 2; index++)
        {
            var frame = CameraFrame.CopyFromBgra32(320, 200, new byte[320 * 200 * 4],
                sequence: firstSequence + index, epoch: 1,
                capturedAt: capturedAt.AddSeconds(index * 1.1));
            var analysis = new GameTableAnalysis(frame, [], [], 1, 1, "synthetic-card-save-test");
            typeof(CameraViewModel).GetProperty(nameof(CameraViewModel.GameTableAnalysis))!
                .GetSetMethod(nonPublic: true)!.Invoke(camera, [analysis]);
        }
    }

    private sealed class DelayedCommitStore : ISessionStore
    {
        private readonly InMemorySessionStore _inner = new();
        private int _commitsBeforeDelay = -1;

        public TaskCompletionSource CommitStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCommit { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void DelayCommitAfter(int commitsBeforeDelay) =>
            _commitsBeforeDelay = commitsBeforeDelay;

        public async Task CommitAsync(GameState state, StoredCommandOutcome outcome,
            Transition transition, string stateHash, CancellationToken cancellationToken)
        {
            if (_commitsBeforeDelay >= 0 && _commitsBeforeDelay-- == 0)
            {
                CommitStarted.TrySetResult();
                await ReleaseCommit.Task.WaitAsync(cancellationToken);
            }
            await _inner.CommitAsync(state, outcome, transition, stateHash, cancellationToken);
        }

        public Task CreateAsync(GameState state, CommandId commandId, Transition transition,
            string stateHash, CancellationToken token) =>
            _inner.CreateAsync(state, commandId, transition, stateHash, token);
        public Task<StoredCommandOutcome?> FindCommandOutcomeAsync(SessionId sessionId,
            CommandId commandId, CancellationToken token) =>
            _inner.FindCommandOutcomeAsync(sessionId, commandId, token);
        public Task RecordRejectionAsync(SessionId sessionId, StoredCommandOutcome outcome,
            CancellationToken token) => _inner.RecordRejectionAsync(sessionId, outcome, token);
        public Task<RestoredSession> RestoreAsync(SessionId sessionId, BoardManifest manifest,
            CardCatalog catalog, CancellationToken token) =>
            _inner.RestoreAsync(sessionId, manifest, catalog, token);
        public Task<PackAwayCheckpoint?> ReadCheckpointAsync(SessionId sessionId,
            CheckpointId checkpointId, CancellationToken token) =>
            _inner.ReadCheckpointAsync(sessionId, checkpointId, token);
        public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken token) =>
            _inner.ListSessionsAsync(token);
        public Task DeleteSessionAsync(SessionId sessionId, CancellationToken token) =>
            _inner.DeleteSessionAsync(sessionId, token);
    }
}
