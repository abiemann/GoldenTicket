using GoldenTicket.Application;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Projections;
using GoldenTicket.Domain.Randomness;

namespace GoldenTicket.Domain.Tests;

public sealed class TotalGameTimingTests
{
    [Fact]
    public void Game_time_continues_through_turn_pauses_and_rules_decisions_using_monotonic_time()
    {
        var clock = new GameClock();
        var timing = new TurnTimingTracker(clock);
        var view = ActiveView();
        timing.Synchronize(view with { Lifecycle = SessionLifecycle.Setup });
        timing.SetGameRunning(true);
        clock.Advance(TimeSpan.FromHours(1));
        timing.Synchronize(view);
        timing.SetPaused(false);
        clock.Advance(TimeSpan.FromSeconds(10));
        timing.SetPaused(true);
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(TimeSpan.FromSeconds(1210).Ticks, timing.Snapshot().GameElapsedTicks);
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, Assert.Single(timing.Snapshot().Turns).ElapsedTicks);

        timing.SetPaused(false);
        clock.Advance(TimeSpan.FromSeconds(3));
        timing.Synchronize(view with { TurnPhase = TurnPhase.RulesDecisionRequired });
        clock.Advance(TimeSpan.FromSeconds(60));
        clock.UtcNow = clock.UtcNow.AddDays(-1);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(1275).Ticks, timing.Snapshot().GameElapsedTicks);
        Assert.Equal(TimeSpan.FromSeconds(13).Ticks, Assert.Single(timing.Snapshot().Turns).ElapsedTicks);
    }

    [Fact]
    public void Open_game_save_and_rebuild_checks_count_until_the_runtime_clock_is_stopped()
    {
        var clock = new ManualFrameTimeProvider();
        var timing = new TurnTimingTracker(clock);
        var view = ActiveView();
        timing.Synchronize(view);
        timing.SetGameRunning(true);
        clock.Advance(TimeSpan.FromSeconds(5));
        var expectedSeconds = 5;
        foreach (var lifecycle in new[] { SessionLifecycle.PreparingPackAway,
                     SessionLifecycle.PackedAway, SessionLifecycle.Rebuilding })
        {
            timing.Synchronize(view with { Lifecycle = lifecycle });
            clock.Advance(TimeSpan.FromSeconds(10));
            expectedSeconds += 10;
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds).Ticks, timing.Snapshot().GameElapsedTicks);
        }
        timing.Synchronize(view);
        clock.Advance(TimeSpan.FromSeconds(3));
        timing.SetGameRunning(false);
        clock.Advance(TimeSpan.FromDays(4));
        Assert.Equal(TimeSpan.FromSeconds(38).Ticks, timing.Snapshot().GameElapsedTicks);
        timing.SetGameRunning(true);
        clock.Advance(TimeSpan.FromSeconds(2));
        timing.Synchronize(view with { Lifecycle = SessionLifecycle.Finished });
        clock.Advance(TimeSpan.FromDays(5));
        Assert.Equal(TimeSpan.FromSeconds(40).Ticks, timing.Snapshot().GameElapsedTicks);
    }

    [Fact]
    public void Total_game_time_includes_the_final_marker_hold_even_while_the_turn_clock_is_paused()
    {
        var clock = new ManualFrameTimeProvider();
        var timing = new TurnTimingTracker(clock);
        var view = ActiveView();
        timing.Synchronize(view);
        timing.SetGameRunning(true);
        timing.SetPaused(false);
        clock.Advance(TimeSpan.FromSeconds(5));
        timing.HoldForScoreMarker();
        timing.Synchronize(view with { Lifecycle = SessionLifecycle.Finished });
        timing.SetPaused(true);
        clock.Advance(TimeSpan.FromSeconds(12));
        timing.ReleaseScoreMarker();
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(TimeSpan.FromSeconds(17).Ticks, timing.Snapshot().GameElapsedTicks);
        Assert.Equal(new TurnTimingEntry(1, view.ActiveSeatId, TimeSpan.FromSeconds(5).Ticks, true, false),
            Assert.Single(timing.Snapshot().Turns));
    }

    [Theory]
    [InlineData(null, 25)]
    [InlineData(100, 100)]
    public void Restored_totals_use_saved_time_or_recorded_legacy_turns_without_counting_closed_time(
        int? savedSeconds, int expectedSeconds)
    {
        var clock = new ManualFrameTimeProvider();
        var saved = new TurnTimingSnapshot(
            [new(1, new(1), TimeSpan.FromSeconds(10).Ticks, true, false),
             new(2, new(2), TimeSpan.FromSeconds(15).Ticks, false, false)],
            GameElapsedTicks: savedSeconds is { } seconds ? TimeSpan.FromSeconds(seconds).Ticks : null);
        var timing = new TurnTimingTracker(clock, saved, restored: true);
        timing.Synchronize(ActiveView() with { TurnNumber = 2, ActiveSeatId = new(2) });
        clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds).Ticks, timing.Snapshot().GameElapsedTicks);
        timing.SetGameRunning(true);
        timing.SetPaused(false);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds + 3).Ticks, timing.Snapshot().GameElapsedTicks);
        Assert.Equal(TimeSpan.FromSeconds(18).Ticks, timing.Snapshot().Turns[1].ElapsedTicks);
    }

    [Fact]
    public async Task Coordinator_commits_and_flushes_total_time_without_changing_gameplay_or_duplicate_commands()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new ManualFrameTimeProvider();
        var store = new InMemorySessionStore();
        var rules = new GameRules(TestManifest.Manifest, TestManifest.Catalog);
        var game = await GameCoordinator.CreateAsync(rules, store,
            new(SessionId.New(), [new(new(1), "One", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard),
                new(new(2), "Two", PlayerColor.Yellow, SeatKind.Human, AiDifficulty.Standard)],
                new(1), VerificationMode.Manual), DeterministicRandom.SeedFrom(91), token, clock);
        game.SetGameTimingRunning(true);
        game.SetTurnTimingPaused(false);
        foreach (var seat in game.Seats)
        {
            var hand = await game.GetSeatViewAsync(seat.SeatId, token);
            Assert.True((await game.SubmitAsync(new CommitTicketSelection(game.NewEnvelope(seat.SeatId),
                [.. hand.SetupOffer.Take(2)], []), token)).IsAccepted);
        }
        clock.Advance(TimeSpan.FromSeconds(10));
        game.SetTurnTimingPaused(true);
        clock.Advance(TimeSpan.FromSeconds(20));
        var draw = new SelectTrainCard(game.NewEnvelope(new(1)), null);
        Assert.True((await game.SubmitAsync(draw, token)).IsAccepted);
        Assert.True((await game.SubmitAsync(draw, token)).WasDuplicate);
        var restored = await GameCoordinator.RestoreAsync(rules, store, game.SessionId, token, clock);
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, restored.TurnTiming.GameElapsedTicks);
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, Assert.Single(restored.TurnTiming.Turns).ElapsedTicks);
        clock.Advance(TimeSpan.FromDays(7));
        restored.SetGameTimingRunning(true);
        clock.Advance(TimeSpan.FromSeconds(3));
        var hash = await restored.ComputeStateHashAsync(token);
        await restored.FlushTurnTimingAsync(token);
        Assert.Equal(hash, await restored.ComputeStateHashAsync(token));
        var reopened = await GameCoordinator.RestoreAsync(rules, store, game.SessionId, token, clock);
        Assert.Equal(TimeSpan.FromSeconds(33).Ticks, reopened.TurnTiming.GameElapsedTicks);
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, Assert.Single(reopened.TurnTiming.Turns).ElapsedTicks);
    }

    private static PublicView ActiveView() => Projector.ProjectPublic(RulesHarness.Create(2).State) with
    {
        Lifecycle = SessionLifecycle.Active, TurnPhase = TurnPhase.TurnStart, TurnNumber = 1
    };

    private sealed class GameClock : TimeProvider
    {
        private long _ticks;
        public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public void Advance(TimeSpan elapsed) { _ticks += elapsed.Ticks; UtcNow += elapsed; }
    }
}
