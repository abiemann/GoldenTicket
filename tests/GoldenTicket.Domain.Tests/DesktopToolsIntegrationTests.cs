using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopToolsIntegrationTests
{
    [Fact]
    public async Task GameNavigationPreservesTheCurrentMatchAndClosesPrivateViews()
    {
        var model = await CreateMatchAsync();
        try
        {
            model.ShowGameCommand.Execute(null);
            Assert.Equal(Screen.Table, model.Screen);
            await model.RevealPrivateSeatAsync();
            Assert.NotNull(model.PrivateSeat);
            await model.ShowCheckpointPhotoCommand.ExecuteAsync(null);
            Assert.Equal(Screen.CheckpointPhoto, model.Screen);
            Assert.Null(model.PrivateSeat);
            Assert.False(model.CanRevealPrivateSeat);
            model.ShowGameCommand.Execute(null);
            Assert.Equal(Screen.Table, model.Screen);
            Assert.True(model.CanRevealPrivateSeat);
            Assert.Null(model.PrivateSeat);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task ReferencePhotoNavigationPreservesTheRebuildLifecycle()
    {
        var model = await CreateMatchAsync();
        try
        {
            model.Table.SaveName = "Photo navigation";
            await model.SaveAndPackAwayAsync();
            Assert.True(model.CheckpointPhoto.HasCheckpoint);
            Assert.False(model.CheckpointPhoto.HasPhoto);
            await model.BeginRebuildAsync();
            await model.ShowCheckpointPhotoCommand.ExecuteAsync(null);
            model.ShowGameCommand.Execute(null);
            Assert.Equal(Screen.Rebuild, model.Screen);
            Assert.False(model.CanRevealPrivateSeat);
            Assert.False(model.Table.RebuildAcknowledged);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task MissingCameraCannotProduceASuccessfulCheckpointPhoto()
    {
        var model = await CreateMatchAsync();
        try
        {
            model.Table.SaveName = "No camera";
            await model.SaveAndPackAwayAsync();
            await model.ShowCheckpointPhotoCommand.ExecuteAsync(null);
            model.CheckpointPhoto.OperatorAcknowledged = true;
            await model.CheckpointPhoto.CaptureReferenceCommand.ExecuteAsync(null);
            Assert.False(model.CheckpointPhoto.HasPhoto);
            Assert.Null(model.CheckpointPhoto.PhotoImage);
            Assert.True(model.Table.IsPackedAway);
            Assert.Contains("photo", model.CheckpointPhoto.Status, StringComparison.OrdinalIgnoreCase);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task SystemLockHidesPrivateViewAndDoesNotRestartTheCameraOnUnlock()
    {
        var model = await CreateMatchAsync();
        try
        {
            await model.RevealPrivateSeatAsync();
            Assert.NotNull(model.PrivateSeat);
            await model.SetSystemAvailableAsync(false);
            Assert.Null(model.PrivateSeat);
            Assert.False(model.Camera.IsRunning);
            await model.SetSystemAvailableAsync(true);
            Assert.Null(model.PrivateSeat);
            Assert.False(model.Camera.IsRunning);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task UnhandledFaultCannotBeUndoneByReturningFromSystemLock()
    {
        var model = await CreateMatchAsync();
        try
        {
            model.PauseAfterUnhandledFault();
            await model.SetSystemAvailableAsync(true);
            await model.RevealPrivateSeatAsync();
            Assert.False(model.CanRevealPrivateSeat);
            Assert.Null(model.PrivateSeat);
            Assert.Contains("paused", model.Status);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static async Task<MainViewModel> CreateMatchAsync()
    {
        var model = new MainViewModel(TestManifest.Manifest, new InMemorySessionStore());
        model.Setup.ManualVerificationAccepted = true;
        foreach (var seat in model.Setup.Seats) seat.IsComputer = false;
        await model.StartMatchAsync();
        foreach (var _ in model.Setup.Seats)
        {
            await model.RevealPrivateSeatAsync();
            await model.CommitTicketsAsync();
        }
        return model;
    }
}
