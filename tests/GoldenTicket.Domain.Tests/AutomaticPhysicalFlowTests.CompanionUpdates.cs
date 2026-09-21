using System.Reflection;
using GoldenTicket.CompanionHost;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Engine;

namespace GoldenTicket.Domain.Tests;

public sealed partial class AutomaticPhysicalFlowTests
{
    [Fact]
    public async Task Companion_updates_publish_same_version_camera_changes_without_repeating_unchanged_frames()
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
            Assert.True(updates.Changes.Reader.TryRead(out _));
            var unready = await bridge.ReadPublicAsync(token);
            Assert.Equal(version, unready.Game!.StateVersion);
            Assert.False(unready.BoardInteraction!.DetectedRoute!.Ready);

            model.Camera.IsGameTablePreviewUpright = true;
            // Losing orientation discards the analysis. Restoring orientation alone must
            // not advertise the old proof as ready; new camera evidence is required.
            Assert.False(updates.Changes.Reader.TryRead(out _));
            Assert.False((await bridge.ReadPublicAsync(token)).BoardInteraction!.DetectedRoute!.Ready);
            var resumed = DateTimeOffset.UtcNow.AddSeconds(6);
            PublishBlueTrains(model.Camera, proposal.RouteId.Value, 6, resumed);
            PublishBlueTrains(model.Camera, proposal.RouteId.Value, 7, resumed.AddSeconds(1.1));
            Assert.True(updates.Changes.Reader.TryRead(out _));
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
