using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Randomness;
using System.Reflection;

namespace GoldenTicket.Domain.Tests;

public sealed class TurnTimingTests
{
    [Fact]
    public void Full_turn_includes_placement_and_marker_but_excludes_pauses()
    {
        var clock = new ManualFrameTimeProvider();
        var timing = new TurnTimingTracker(clock);
        var view = ActiveView();
        timing.Synchronize(view);
        timing.SetPaused(false);
        clock.Advance(TimeSpan.FromSeconds(10));
        timing.Synchronize(view with { TurnPhase = TurnPhase.AwaitingPhysicalPlacement });
        clock.Advance(TimeSpan.FromSeconds(20));
        timing.SetPaused(true);
        clock.Advance(TimeSpan.FromHours(3));
        timing.SetPaused(false);
        timing.HoldForScoreMarker();
        timing.Synchronize(view with { TurnNumber = 2, ActiveSeatId = new(2) });
        clock.Advance(TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(37).Ticks, Assert.Single(timing.Snapshot().Turns).ElapsedTicks);
        timing.ReleaseScoreMarker();
        clock.Advance(TimeSpan.FromSeconds(4));
        var turns = timing.Snapshot().Turns;
        Assert.Equal(new TurnTimingEntry(1, new(1), TimeSpan.FromSeconds(37).Ticks, true, false), turns[0]);
        Assert.Equal(new TurnTimingEntry(2, new(2), TimeSpan.FromSeconds(4).Ticks, false, false), turns[1]);
    }

    [Fact]
    public void Final_marker_time_is_recorded_before_clock_stops()
    {
        var clock = new ManualFrameTimeProvider();
        var timing = new TurnTimingTracker(clock);
        var view = ActiveView();
        timing.Synchronize(view);
        timing.SetPaused(false);
        clock.Advance(TimeSpan.FromSeconds(5));
        timing.HoldForScoreMarker();
        timing.Synchronize(view with { Lifecycle = SessionLifecycle.Finished });
        clock.Advance(TimeSpan.FromSeconds(12));
        timing.ReleaseScoreMarker();
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(new TurnTimingEntry(1, new(1), TimeSpan.FromSeconds(17).Ticks, true, false),
            Assert.Single(timing.Snapshot().Turns));
    }

    [Fact]
    public void Restoring_legacy_or_interrupted_marker_timing_never_invents_full_turn_times()
    {
        var clock = new ManualFrameTimeProvider();
        var view = ActiveView() with { TurnNumber = 40 };
        var legacy = new TurnTimingTracker(clock, restored: true);
        legacy.Synchronize(view);
        clock.Advance(TimeSpan.FromDays(4));
        Assert.Empty(legacy.Snapshot().Turns);
        legacy.SetPaused(false);
        clock.Advance(TimeSpan.FromSeconds(8));
        legacy.Synchronize(view with { TurnNumber = 41, ActiveSeatId = new(2) });
        Assert.True(legacy.Snapshot().Turns[0].IsPartial);
        Assert.False(legacy.Snapshot().Turns[1].IsPartial);

        var interrupted = new TurnTimingTracker(clock,
            new([new(39, new(2), TimeSpan.FromSeconds(22).Ticks, false, false)], true), restored: true);
        interrupted.Synchronize(view);
        var old = Assert.Single(interrupted.Snapshot().Turns);
        Assert.True(old.Completed);
        Assert.True(old.IsPartial);
        Assert.Equal(TimeSpan.FromSeconds(22).Ticks, old.ElapsedTicks);
    }

    [Fact]
    public void Restored_active_autosaves_are_partial_but_clean_paused_checkpoints_are_exact()
    {
        var clock = new ManualFrameTimeProvider();
        var view = ActiveView();
        var timing = new TurnTimingTracker(clock);
        timing.Synchronize(view);
        timing.SetPaused(false);
        clock.Advance(TimeSpan.FromSeconds(10));
        var autosave = timing.Snapshot();
        Assert.True(autosave.WasRunning);
        clock.Advance(TimeSpan.FromMinutes(5)); // Lost if the process exits before another write.
        var interrupted = new TurnTimingTracker(clock, autosave, restored: true);
        interrupted.Synchronize(view);
        Assert.True(Assert.Single(interrupted.Snapshot().Turns).IsPartial);

        timing.SetPaused(true);
        var checkpoint = timing.Snapshot();
        Assert.False(checkpoint.WasRunning);
        clock.Advance(TimeSpan.FromDays(1));
        var clean = new TurnTimingTracker(clock, checkpoint, restored: true);
        clean.Synchronize(view);
        clean.SetPaused(false);
        clock.Advance(TimeSpan.FromSeconds(2));
        clean.Synchronize(view with { TurnNumber = 2, ActiveSeatId = new(2) });
        Assert.False(clean.Snapshot().Turns[0].IsPartial);
        Assert.Equal(TimeSpan.FromSeconds(312).Ticks, clean.Snapshot().Turns[0].ElapsedTicks);
    }

    [Fact]
    public async Task Timing_survives_reload_and_duplicate_commands_without_changing_game_hash()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new InMemorySessionStore();
        var clock = new ManualFrameTimeProvider();
        var rules = new GameRules(TestManifest.Manifest, TestManifest.Catalog);
        var coordinator = await GameCoordinator.CreateAsync(rules, store,
            new(SessionId.New(), [new(new(1), "One", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard),
                new(new(2), "Two", PlayerColor.Yellow, SeatKind.Human, AiDifficulty.Standard)],
                new(1), VerificationMode.Manual), DeterministicRandom.SeedFrom(91), token, clock);
        coordinator.SetTurnTimingPaused(false);
        foreach (var seat in coordinator.Seats)
        {
            var hand = await coordinator.GetSeatViewAsync(seat.SeatId, token);
            Assert.True((await coordinator.SubmitAsync(new CommitTicketSelection(coordinator.NewEnvelope(seat.SeatId),
                [.. hand.SetupOffer.Take(2)], []), token)).IsAccepted);
        }
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True((await coordinator.SubmitAsync(new SelectTrainCard(coordinator.NewEnvelope(new(1)), null), token)).IsAccepted);
        clock.Advance(TimeSpan.FromSeconds(10));
        var second = new SelectTrainCard(coordinator.NewEnvelope(new(1)), null);
        Assert.True((await coordinator.SubmitAsync(second, token)).IsAccepted);
        Assert.True((await coordinator.SubmitAsync(second, token)).WasDuplicate);
        var restored = await GameCoordinator.RestoreAsync(rules, store, coordinator.SessionId, token, clock);
        clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal(TimeSpan.FromSeconds(20).Ticks, restored.TurnTiming.Turns[0].ElapsedTicks);
        Assert.Equal(0, restored.TurnTiming.Turns[1].ElapsedTicks);
        restored.SetTurnTimingPaused(false);
        clock.Advance(TimeSpan.FromSeconds(3));
        var hash = await restored.ComputeStateHashAsync(token);
        await restored.FlushTurnTimingAsync(token);
        Assert.Equal(hash, await restored.ComputeStateHashAsync(token));
        var reopened = await GameCoordinator.RestoreAsync(rules, store, coordinator.SessionId, token, clock);
        Assert.Equal(TimeSpan.FromSeconds(3).Ticks, reopened.TurnTiming.Turns[1].ElapsedTicks);
    }

    [Fact]
    public async Task Desktop_menu_and_focus_pauses_freeze_current_turn_counter()
    {
        var clock = new ManualFrameTimeProvider();
        var model = new MainViewModel(TestManifest.Manifest, new InMemorySessionStore(), timeProvider: clock);
        model.Setup.ManualVerificationAccepted = true;
        model.SetGameLayerVisible(true);
        try
        {
            await model.StartMatchAsync();
            await model.CommitTicketsAsync();
            var coordinator = (GameCoordinator)typeof(MainViewModel).GetField("_coordinator",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
            clock.Advance(TimeSpan.FromSeconds(12));
            model.IsGameExitMenuOpen = true;
            clock.Advance(TimeSpan.FromMinutes(20));
            model.IsGameExitMenuOpen = false;
            clock.Advance(TimeSpan.FromSeconds(3));
            model.SetWindowActive(false);
            clock.Advance(TimeSpan.FromMinutes(10));
            model.SetWindowActive(true);
            Assert.Equal(TimeSpan.FromSeconds(15).Ticks, coordinator.TurnTiming.Turns.Last().ElapsedTicks);
            Assert.Equal("  ·  0:15", model.TurnClockSuffix);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static PublicView ActiveView() => Projector.ProjectPublic(RulesHarness.Create(2).State) with
    {
        Lifecycle = SessionLifecycle.Active, TurnPhase = TurnPhase.TurnStart, TurnNumber = 1
    };
}
