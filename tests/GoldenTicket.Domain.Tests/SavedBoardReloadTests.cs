using System.Reflection;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class SavedBoardReloadTests
{
    [Fact]
    public async Task Reload_stays_on_table_until_saved_markers_and_board_are_camera_verified()
    {
        var store = new InMemorySessionStore();
        using var photos = new TestCheckpointPhotos();
        var original = new MainViewModel(TestManifest.Manifest, store, photos.Store);
        var reloaded = new MainViewModel(TestManifest.Manifest, store, photos.Store);
        try
        {
            original.Setup.ManualVerificationAccepted = true;
            foreach (var seat in original.Setup.Seats) seat.IsComputer = false;
            await original.StartMatchAsync();
            for (var index = 0; index < original.Setup.Seats.Count; index++)
            {
                await original.RevealPrivateSeatAsync();
                await original.CommitTicketsAsync();
            }
            original.Table.SaveName = "Camera restore";
            await original.SaveAndPackAwayAsync();
            var saved = Assert.Single(await store.ListSessionsAsync(CancellationToken.None));
            var packed = await store.RestoreAsync(saved.SessionId, TestManifest.Manifest,
                TestManifest.Catalog, CancellationToken.None);
            await photos.AttachAsync(packed.State.Checkpoint!);

            await reloaded.LoadSavedSessionsAsync();
            reloaded.Setup.SelectedSavedSession = reloaded.Setup.SavedSessions.Single();
            await reloaded.ResumeMatchAsync();

            Assert.Equal(Screen.Table, reloaded.Screen);
            Assert.Equal("Saved board.", reloaded.Game.GuidanceInstruction);
            Assert.DoesNotContain("packed away", reloaded.Game.GuidanceInstruction,
                StringComparison.OrdinalIgnoreCase);
            var coordinator = (GameCoordinator)typeof(MainViewModel)
                .GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(reloaded)!;
            Assert.Equal(SessionLifecycle.PackedAway, coordinator.Public.Lifecycle);

            typeof(CameraViewModel).GetProperty(nameof(CameraViewModel.IsGameTablePreviewUpright))!
                .SetValue(reloaded.Camera, true);
            var scores = coordinator.Public.Seats.Select((seat, index) =>
                new ScoreMarkerReading(index, Color(seat.Color), 1,
                    ScoreMarkerReadingStatus.Read, "saved score")).ToArray();
            var incorrect = scores.Select(score => score with { Score = 2 }).ToArray();
            var at = DateTimeOffset.UtcNow;
            Publish(1, at, incorrect);
            Publish(2, at.AddSeconds(1.1), incorrect);
            Assert.Equal(SessionLifecycle.PackedAway, coordinator.Public.Lifecycle);
            Assert.Contains("BLUE scoring marker on 2", reloaded.Game.GuidanceInstruction);
            Assert.Contains("saved game expects 1", reloaded.Game.GuidanceInstruction);

            Publish(3, at.AddSeconds(3.3), scores);
            Publish(4, at.AddSeconds(4.4), scores);
            var secondColor = new[] { MarkerColor.Blue, MarkerColor.Red, MarkerColor.Green,
                    MarkerColor.Yellow, MarkerColor.Black }
                .First(color => color != MarkerColor.Blue && scores.Any(score => score.Color == color));
            Assert.Contains($"{secondColor.ToString().ToUpperInvariant()} scoring marker on 1",
                reloaded.Game.GuidanceInstruction);
            for (var sequence = 5; sequence <= 2 * scores.Length + 7; sequence++)
                Publish(sequence, at.AddSeconds(sequence * 1.1), scores);
            for (var attempt = 0; attempt < 100 &&
                 coordinator.Public.Lifecycle != SessionLifecycle.Active; attempt++)
                await Task.Delay(20, TestContext.Current.CancellationToken);

            Assert.True(coordinator.Public.Lifecycle == SessionLifecycle.Active,
                $"Reload stayed {coordinator.Public.Lifecycle}: {reloaded.Game.GuidanceInstruction}; " +
                $"status={reloaded.Status}; upright={reloaded.Camera.IsGameTablePreviewUpright}");
            Assert.Equal(Screen.Table, reloaded.Screen);
            Assert.DoesNotContain("packed away", reloaded.Game.GuidanceInstruction,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(reloaded.IsResumeTurnAnnouncementOpen);
            Assert.Contains(coordinator.Public.SeatOf(coordinator.Public.ActiveSeatId).DisplayName,
                reloaded.ResumeTurnAnnouncementText);

            void Publish(long sequence, DateTimeOffset capturedAt,
                IReadOnlyList<ScoreMarkerReading> readings)
            {
                var board = CameraFrame.CopyFromBgra32(320, 200,
                    new byte[320 * 200 * 4], sequence, 1, capturedAt);
                typeof(CameraViewModel).GetProperty(nameof(CameraViewModel.GameTableAnalysis))!
                    .SetValue(reloaded.Camera,
                        new GameTableAnalysis(board, [], readings, 1, 1, "test model"));
            }
        }
        finally
        {
            await original.DisposeToolsAsync();
            await reloaded.DisposeToolsAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reload_rejects_missing_or_corrupt_required_photo_without_entering_game(bool corrupt)
    {
        var store = new InMemorySessionStore();
        using var photos = new TestCheckpointPhotos();
        var original = new MainViewModel(TestManifest.Manifest, store, photos.Store);
        var reloaded = new MainViewModel(TestManifest.Manifest, store, photos.Store);
        try
        {
            original.Setup.ManualVerificationAccepted = true;
            await original.StartMatchAsync();
            await original.CommitTicketsAsync();
            original.Table.SaveName = "Incomplete image save";
            await original.SaveAndPackAwayAsync();
            var saved = Assert.Single(await store.ListSessionsAsync(CancellationToken.None));
            var before = await store.RestoreAsync(saved.SessionId, TestManifest.Manifest,
                TestManifest.Catalog, CancellationToken.None);
            var checkpoint = before.State.Checkpoint!;
            if (corrupt)
            {
                await photos.AttachAsync(checkpoint);
                await File.WriteAllBytesAsync(photos.Store.AttachmentPath(saved.SessionId, checkpoint.CheckpointId),
                    "damaged board image"u8.ToArray(), TestContext.Current.CancellationToken);
            }

            await reloaded.LoadSavedSessionsAsync();
            reloaded.Setup.SelectedSavedSession = reloaded.Setup.SavedSessions.Single();
            await reloaded.ResumeMatchAsync();

            Assert.Equal(Screen.Setup, reloaded.Screen);
            Assert.False(reloaded.Game.IsPlaying);
            Assert.Empty(reloaded.Table.Seats);
            Assert.Contains("required board image", reloaded.Setup.SavedMatchMessage);
            Assert.Contains(corrupt ? "damaged or unreadable" : "incomplete", reloaded.Setup.SavedMatchMessage);
            Assert.Null(typeof(MainViewModel).GetField("_coordinator",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reloaded));
            Assert.Null(typeof(MainViewModel).GetField("_savedBoardRestoreSession",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reloaded));
            var after = await store.RestoreAsync(saved.SessionId, TestManifest.Manifest,
                TestManifest.Catalog, CancellationToken.None);
            Assert.Equal(before.State.StateVersion, after.State.StateVersion);
            Assert.Equal(before.Journal.Count, after.Journal.Count);
            Assert.Equal(checkpoint, after.State.Checkpoint);

            if (!corrupt)
            {
                // A restored matching attachment makes the same saved game usable on retry.
                await photos.AttachAsync(checkpoint);
                await reloaded.ResumeMatchAsync();
                Assert.Equal(Screen.Table, reloaded.Screen);
                Assert.True(reloaded.Game.IsPlaying);
                Assert.True(reloaded.CheckpointPhoto.HasPhoto);
                Assert.Equal("Saved board.", reloaded.Game.GuidanceInstruction);
            }
        }
        finally
        {
            await original.DisposeToolsAsync();
            await reloaded.DisposeToolsAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Earlier_completed_save_recovery_is_explicit_and_revalidates_the_image(bool removePhoto)
    {
        var store = new InMemorySessionStore();
        using var photos = new TestCheckpointPhotos();
        var original = new MainViewModel(TestManifest.Manifest, store, photos.Store);
        var reloaded = new MainViewModel(TestManifest.Manifest, store, photos.Store);
        try
        {
            original.Setup.ManualVerificationAccepted = true;
            await original.StartMatchAsync();
            await original.CommitTicketsAsync();
            original.Table.SaveName = "Earlier completed save";
            await original.SaveAndPackAwayAsync();
            var session = Assert.Single(await store.ListSessionsAsync(CancellationToken.None));
            var earlier = await store.RestoreAsync(session.SessionId, TestManifest.Manifest,
                TestManifest.Catalog, CancellationToken.None);
            await photos.AttachAsync(earlier.State.Checkpoint!);
            await original.BeginRebuildAsync();
            original.Table.RebuildAcknowledged = true;
            await original.AttestRebuildAsync();
            await original.ResumePackedGameAsync();
            await original.AcknowledgeResumeTurnCommand.ExecuteAsync(null);
            original.Table.SaveName = "Failed later save";
            await original.SaveAndPackAwayAsync();
            var incomplete = await store.RestoreAsync(session.SessionId, TestManifest.Manifest,
                TestManifest.Catalog, CancellationToken.None);

            await reloaded.LoadSavedSessionsAsync();
            reloaded.Setup.SelectedSavedSession = reloaded.Setup.SavedSessions.Single();
            await reloaded.ResumeMatchAsync();
            Assert.False(reloaded.Game.IsPlaying);
            Assert.True(reloaded.HasEarlierCompletedSave);
            Assert.Contains("discard all actions", reloaded.Setup.SavedMatchMessage);
            Assert.Equal(incomplete.State.StateVersion, (await store.RestoreAsync(session.SessionId,
                TestManifest.Manifest, TestManifest.Catalog, CancellationToken.None)).State.StateVersion);

            if (removePhoto)
                File.Delete(photos.Store.AttachmentPath(session.SessionId, earlier.State.Checkpoint!.CheckpointId));
            await reloaded.RestoreEarlierCompletedSaveCommand.ExecuteAsync(null);
            var recovered = await store.RestoreAsync(session.SessionId, TestManifest.Manifest,
                TestManifest.Catalog, CancellationToken.None);
            if (removePhoto)
            {
                Assert.Equal(incomplete.State.StateVersion, recovered.State.StateVersion);
                Assert.False(reloaded.Game.IsPlaying);
                Assert.Contains("could not be restored", reloaded.Setup.SavedMatchMessage);
            }
            else
            {
                Assert.Equal(earlier.State.Checkpoint!.CheckpointId, recovered.State.Checkpoint!.CheckpointId);
                Assert.Equal(earlier.State.StateVersion, recovered.State.StateVersion);
                Assert.True(reloaded.Game.IsPlaying);
                Assert.False(reloaded.HasEarlierCompletedSave);
                Assert.Equal("Saved board.", reloaded.Game.GuidanceInstruction);
            }
        }
        finally
        {
            await original.DisposeToolsAsync();
            await reloaded.DisposeToolsAsync();
        }
    }

    private static MarkerColor Color(PlayerColor color) => color switch
    {
        PlayerColor.Blue => MarkerColor.Blue,
        PlayerColor.Red => MarkerColor.Red,
        PlayerColor.Green => MarkerColor.Green,
        PlayerColor.Yellow => MarkerColor.Yellow,
        PlayerColor.Black => MarkerColor.Black,
        _ => throw new ArgumentOutOfRangeException(nameof(color))
    };
}
