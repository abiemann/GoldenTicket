using System.Reflection;
using GoldenTicket.CompanionHost;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed partial class AutomaticPhysicalFlowTests
{
    [Fact]
    public async Task Companion_pushes_transient_board_warning_recovery_without_changing_the_turn_or_hand()
    {
        var token = TestContext.Current.CancellationToken;
        var model = await StartCompanionMatchAsync();
        try
        {
            var bridge = GetCompanionBridge(model);
            var game = GetCoordinator(model);
            var version = game.Public.StateVersion;
            var active = game.Public.ActiveSeatId;
            var turn = game.Public.TurnNumber;
            var handCount = game.Public.SeatOf(active).TrainCardCount;
            var stateHash = await game.ComputeStateHashAsync(token);
            model.Camera.IsGameTablePreviewUpright = true;
            var at = DateTimeOffset.UtcNow;
            PublishTrains(model.Camera, "duluth--omaha--a", 1, at, MarkerColor.Blue, count: 0);
            await WaitUntilAsync(() => typeof(MainViewModel)
                .GetField("_boardFirstLegalActions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(model) is not null);
            var clean = model.Camera.GameTableAnalysis!;
            Assert.False((await bridge.ReadPublicAsync(token)).BoardInteraction!.CardActionsBlocked);
            using var updates = ObserveCompanionUpdates(model);

            // A false detection outside every route has no route-specific proposal to dismiss.
            Publish(2, extraTrain: true);
            Assert.True(updates.Changes.Reader.TryRead(out _));
            var blocked = (await bridge.ReadPublicAsync(token)).BoardInteraction!;
            Assert.True(blocked.CardActionsBlocked);
            Assert.NotNull(blocked.Message);
            Assert.Null(blocked.DetectedRoute);
            Assert.Equal(blocked.Message, model.Game.GuidanceInstruction);

            // Reusing the bad frame's sequence with different candidates is not recovery.
            Publish(2);
            Assert.Equal(blocked, (await bridge.ReadPublicAsync(token)).BoardInteraction);
            Assert.False(updates.Changes.Reader.TryRead(out _));
            Publish(3, stale: true);
            Assert.Equal(blocked, (await bridge.ReadPublicAsync(token)).BoardInteraction);
            Assert.False(updates.Changes.Reader.TryRead(out _));

            // The first fresh whole-board match clears the warning and pushes that change.
            // Nobody clicks a card, reloads, or acknowledges an error to make this happen.
            Publish(4);
            Assert.True(updates.Changes.Reader.TryRead(out _));
            var recovered = await bridge.ReadPublicAsync(token);
            Assert.False(recovered.BoardInteraction!.CardActionsBlocked);
            Assert.Null(recovered.BoardInteraction.Message);
            Assert.Null(recovered.BoardInteraction.DetectedRoute);
            Assert.NotEqual(blocked.Message, model.Game.GuidanceInstruction);
            Assert.Equal(version, recovered.Game!.StateVersion);
            Assert.Equal(version, game.Public.StateVersion);
            Assert.Equal(active, game.Public.ActiveSeatId);
            Assert.Equal(turn, game.Public.TurnNumber);
            Assert.Equal(handCount, game.Public.SeatOf(active).TrainCardCount);
            Assert.Equal(stateHash, await game.ComputeStateHashAsync(token));

            void Publish(long sequence, bool extraTrain = false, bool stale = false)
            {
                var clock = new ManualFrameTimeProvider();
                var frame = CameraFrame.CopyFromBgra32(clean.Board.Width, clean.Board.Height,
                    clean.Board.Bgra32.Span, sequence, clean.Board.Epoch,
                    at.AddMilliseconds(sequence * 100), clock);
                if (stale) clock.Advance(TimeSpan.FromSeconds(3));
                PieceCandidate[] candidates = extraTrain
                    ? [new(PieceCandidateKind.Train,
                        [new(.01, .01), new(.03, .01), new(.03, .03), new(.01, .03)], .95)]
                    : [];
                var analysis = clean with { Board = frame, Candidates = candidates };
                typeof(CameraViewModel).GetProperty(nameof(CameraViewModel.GameTableAnalysis))!
                    .GetSetMethod(nonPublic: true)!.Invoke(model.Camera, [analysis]);
            }
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Companion_updates_keep_detected_payment_ready_through_camera_changes_without_repeating_unchanged_frames()
    {
        var token = TestContext.Current.CancellationToken;
        var model = await StartCompanionMatchAsync();
        try
        {
            using var updates = ObserveCompanionUpdates(model);
            var bridge = GetCompanionBridge(model);
            var version = GetCoordinator(model).Public.StateVersion;
            var proposal = await DetectCompanionRouteAsync(model);
            Assert.True(updates.Changes.Reader.TryRead(out _));
            Assert.True((await bridge.ReadPublicAsync(token)).BoardInteraction!.DetectedRoute!.Ready);

            // New observations of the same confirmed route and the laptop clock are not
            // game updates. No public snapshot work is scheduled for these repetitions.
            PublishBlueTrains(model.Camera, proposal.RouteId.Value, 5, DateTimeOffset.UtcNow.AddSeconds(5));
            model.GameClockSuffix = " · 0:00:05";
            Assert.False(updates.Changes.Reader.TryRead(out _));

            model.Camera.IsGameTablePreviewUpright = false;
            Assert.False(updates.Changes.Reader.TryRead(out _));
            var waitingForPayment = await bridge.ReadPublicAsync(token);
            Assert.Equal(version, waitingForPayment.Game!.StateVersion);
            Assert.True(waitingForPayment.BoardInteraction!.DetectedRoute!.Ready);
            Assert.Equal(proposal.ProposalId, waitingForPayment.BoardInteraction.DetectedRoute.ProposalId);

            model.Camera.IsGameTablePreviewUpright = true;
            // Payment uses the route already verified before the offer. Camera recovery
            // and revisions no longer toggle readiness or disturb selected payment.
            Assert.False(updates.Changes.Reader.TryRead(out _));
            Assert.True((await bridge.ReadPublicAsync(token)).BoardInteraction!.DetectedRoute!.Ready);
            var resumed = DateTimeOffset.UtcNow.AddSeconds(6);
            PublishBlueTrains(model.Camera, proposal.RouteId.Value, 6, resumed, cropRevision: 2);
            PublishBlueTrains(model.Camera, proposal.RouteId.Value, 7, resumed.AddSeconds(1.1), cropRevision: 2);
            Assert.False(updates.Changes.Reader.TryRead(out _));
            Assert.True((await bridge.ReadPublicAsync(token)).BoardInteraction!.DetectedRoute!.Ready);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Companion_updates_publish_each_draw_and_the_next_player_without_a_poll()
    {
        var token = TestContext.Current.CancellationToken;
        var model = await StartCompanionMatchAsync();
        try
        {
            using var updates = ObserveCompanionUpdates(model);
            var bridge = GetCompanionBridge(model);
            var game = GetCoordinator(model);
            var active = game.Public.ActiveSeatId;
            var version = game.Public.StateVersion;
            Assert.True((await bridge.ExecuteAsync(active,
                new(Guid.NewGuid().ToString("N"), game.SessionId.Value, version, "drawTrain"), token)).Accepted);
            Assert.True(updates.Changes.Reader.TryRead(out _));
            Assert.Equal(TurnPhase.AwaitingSecondTrainCard, game.Public.TurnPhase);
            Assert.Equal(active, game.Public.ActiveSeatId);

            Assert.True((await bridge.ExecuteAsync(active,
                new(Guid.NewGuid().ToString("N"), game.SessionId.Value, game.Public.StateVersion, "drawTrain"), token)).Accepted);
            Assert.True(updates.Changes.Reader.TryRead(out _));
            Assert.NotEqual(active, game.Public.ActiveSeatId);
            Assert.Equal(game.Public.ActiveSeatId.Value, (await bridge.ReadPublicAsync(token)).RevealSeatId);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Companion_updates_publish_pause_and_resume_and_unsubscribe_during_disposal()
    {
        var token = TestContext.Current.CancellationToken;
        var model = await CreateComputerPlacementCompanionAsync();
        using var updates = ObserveCompanionUpdates(model);
        try
        {
            var bridge = GetCompanionBridge(model);
            model.IsGameExitMenuOpen = true;
            Assert.True(updates.Changes.Reader.TryRead(out _));
            Assert.Null((await bridge.ReadPublicAsync(token)).Guidance);
            model.IsGameExitMenuOpen = false;
            Assert.True(updates.Changes.Reader.TryRead(out _));
            Assert.NotNull((await bridge.ReadPublicAsync(token)).Guidance);

            await model.DisposeToolsAsync();
            while (updates.Changes.Reader.TryRead(out _)) { }
            var previous = typeof(MainViewModel).GetField("_lastCompanionPresentation",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model);
            model.Table.Instruction = "Changed after shutdown.";
            model.IsGameExitMenuOpen = true;
            Assert.Same(previous, typeof(MainViewModel).GetField("_lastCompanionPresentation",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model));
            Assert.False(updates.Changes.Reader.TryRead(out _));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static CompanionEventSubscriptions.Subscription ObserveCompanionUpdates(MainViewModel model)
    {
        var server = (CompanionServer)typeof(ConnectionViewModel).GetField("_server",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model.Connection)!;
        var events = (CompanionEventSubscriptions)typeof(CompanionServer).GetField("_events",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        return Assert.IsType<CompanionEventSubscriptions.Subscription>(events.Open(Guid.NewGuid().ToString("N")));
    }
}
