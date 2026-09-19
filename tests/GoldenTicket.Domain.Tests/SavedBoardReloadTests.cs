using System.Reflection;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class SavedBoardReloadTests
{
    [Fact]
    public async Task Reload_stays_on_table_until_saved_markers_and_board_are_camera_verified()
    {
        var store = new InMemorySessionStore();
        var original = new MainViewModel(TestManifest.Manifest, store);
        var reloaded = new MainViewModel(TestManifest.Manifest, store);
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

            await reloaded.LoadSavedSessionsAsync();
            reloaded.Setup.SelectedSavedSession = reloaded.Setup.SavedSessions.Single();
            await reloaded.ResumeMatchAsync();

            Assert.Equal(Screen.Table, reloaded.Screen);
            Assert.Contains("Checking the saved board", reloaded.Game.GuidanceInstruction);
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
            Assert.Contains("BLUE scoring marker on 1", reloaded.Game.GuidanceInstruction);

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
