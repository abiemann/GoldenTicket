using GoldenTicket.Domain.Model;

namespace GoldenTicket.Application;

/// <summary>
/// Restore policy for supplementary timing data, shared by every session store. Invalid statistics
/// are discarded without preventing a verified game journal from loading.
/// </summary>
public static class TurnTimingSnapshotValidator
{
    public static TurnTimingSnapshot? ValidateForRestore(TurnTimingSnapshot? timing, GameState state)
    {
        if (timing?.Turns is null) return null;
        var maximumTicks = TimeSpan.FromDays(365).Ticks;
        if (timing.GameElapsedTicks is { } total && (total < 0 || total > maximumTicks)) return null;
        long recordedTicks = 0;
        var previousTurn = 0;
        var unfinished = false;
        // Bound both total time and the recorded turn sum, keeping the legacy fallback sum safe.
        foreach (var turn in timing.Turns)
        {
            if (turn is null || turn.TurnNumber <= previousTurn || turn.TurnNumber > state.TurnNumber ||
                unfinished || !state.Seats.Any(seat => seat.SeatId == turn.SeatId) ||
                turn.ElapsedTicks < 0 || turn.ElapsedTicks > maximumTicks - recordedTicks)
                return null;
            recordedTicks += turn.ElapsedTicks;
            previousTurn = turn.TurnNumber;
            unfinished = !turn.Completed;
        }
        return timing.AwaitingScoreMarker && !unfinished ? null : timing;
    }
}
