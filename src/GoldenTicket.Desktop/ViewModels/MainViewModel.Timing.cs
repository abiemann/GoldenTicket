using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GoldenTicket.Application;
using GoldenTicket.Domain;

namespace GoldenTicket.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private readonly TimeProvider _turnTimeProvider;
    private DispatcherTimer? _turnClockTimer;
    [ObservableProperty] private string _turnClockSuffix = "";

    private void InitializeTurnClock()
    {
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(IsGameExitMenuOpen) or nameof(IsResumeTurnAnnouncementOpen) or
                nameof(IsCheckingResumedGame) or nameof(NeedsBoardReconciliation) or nameof(Screen) or
                nameof(GameplayScreen) or nameof(ShowMultiHumanPhoneSetup))
                UpdateTurnClock();
        };
        if (System.Windows.Application.Current is { } app)
        {
            _turnClockTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
                (_, _) => UpdateTurnClock(), app.Dispatcher);
        }
    }

    private void UpdateTurnClock()
    {
        var coordinator = _coordinator;
        if (coordinator is null) { TurnClockSuffix = ""; return; }
        coordinator.SetTurnTimingPaused(IsGameInputPaused || IsCheckingResumedGame ||
            NeedsBoardReconciliation || ShowMultiHumanPhoneSetup || _mustReload || _exitRequested ||
            _toolsDisposed || !_systemAvailable || !_windowActive || !IsGameplayScreenActive(Screen.Table));
        var current = coordinator.TurnTiming.Turns.LastOrDefault(turn => !turn.Completed);
        TurnClockSuffix = IsCheckingResumedGame || current is null ? "" :
            "  ·  " + FormatTurnTime(TimeSpan.FromTicks(current.ElapsedTicks));
    }

    private async Task PersistTurnClockAsync()
    {
        if (_coordinator is not { } coordinator) return;
        try { await coordinator.FlushTurnTimingAsync(); }
        catch (Exception error)
        {
            // Timing is supplementary; a statistics write must not discard or block game moves.
            BoardInteractionLog.Write("turn-timing.save-failed", new { errorType = error.GetType().Name });
        }
    }

    private static string FormatTurnTime(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? $"{(long)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
        : $"{(long)elapsed.TotalMinutes}:{elapsed.Seconds:00}";

    private FinalScoreRow WithTurnTiming(FinalScoreRow row, SeatId seatId)
    {
        var turns = _coordinator!.TurnTiming.Turns.Where(turn => turn.SeatId == seatId).ToArray();
        var complete = turns.Where(turn => turn.Completed && !turn.IsPartial).ToArray();
        var total = TimeSpan.FromTicks(turns.Sum(turn => turn.ElapsedTicks));
        var average = complete.Length == 0 ? (TimeSpan?)null :
            TimeSpan.FromTicks((long)complete.Average(turn => turn.ElapsedTicks));
        var recordedFrom = _coordinator.TurnTiming.Turns.FirstOrDefault()?.TurnNumber ?? 0;
        return row with
        {
            TotalTurnTimeText = turns.Length == 0 ? "—" : FormatTurnTime(total),
            AverageTurnTimeText = average is null ? "—" : FormatTurnTime(average.Value),
            CompletedTurns = complete.Length,
            TimingNote = turns.Length == 0 ? "Timing was not recorded for this game." :
                "Includes placement; excludes pauses." +
                (recordedFrom > 1 || turns.Any(turn => turn.IsPartial)
                    ? $" Timing available from turn {recordedFrom}; partial turns excluded from the average." : "")
        };
    }
}
