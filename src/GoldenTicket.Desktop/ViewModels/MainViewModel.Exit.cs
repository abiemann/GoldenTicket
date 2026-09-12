using GoldenTicket.Domain;

namespace GoldenTicket.Desktop.ViewModels;

/// <summary>A close warning; a pending write cannot be bypassed by confirming.</summary>
public sealed record ExitPrompt(string Message, bool CanExit);

public sealed partial class MainViewModel
{
    private bool _exitRequested;

    /// <summary>
    /// Freeze new game input while the modal close prompt pumps the dispatcher. Keep digital
    /// autosave separate from permission to clear the physical board and its optional photo.
    /// </summary>
    public ExitPrompt? BeginExitRequest()
    {
        _exitRequested = true;
        HidePrivateSeat();
        OnPropertyChanged(nameof(CanRevealPrivateSeat));

        if (_operationInProgress || CheckpointPhoto.IsBusy)
            return new("Please wait for the current action or save to finish, then try closing again.", false);

        if (_mustReload || _coordinator?.StorageFaulted == true)
            return new("The latest action may not have been saved. Are you sure you want to exit?\n\n" +
                "Reopen the app and verify the saved match before continuing or clearing the board.", true);

        var game = _coordinator?.Public;
        if (game is null || game.Lifecycle == SessionLifecycle.Finished ||
            (game.Lifecycle is SessionLifecycle.PackedAway or SessionLifecycle.Rebuilding &&
             game.Checkpoint is { IsSafeToPackAway: true }))
            return null;

        if (game.Lifecycle is SessionLifecycle.PreparingPackAway or SessionLifecycle.PackedAway or SessionLifecycle.Rebuilding)
            return new("The pack-away save has not been verified. Are you sure you want to exit?\n\n" +
                "Keep the board in place. Reopen the saved match and verify the save before clearing the board.", true);

        return new("This game has not been saved for packing away. Are you sure you want to exit?\n\n" +
            "Completed game actions are saved automatically. Keep the board in place, or choose No " +
            "and use Save and pack away before clearing it.", true);
    }

    public void CancelExitRequest()
    {
        _exitRequested = false;
        // A canceled prompt never reveals private cards automatically.
        OnPropertyChanged(nameof(CanRevealPrivateSeat));
    }
}
