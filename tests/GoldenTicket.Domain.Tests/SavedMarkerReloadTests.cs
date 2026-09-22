using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Model;
using GoldenTicket.Domain.Randomness;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class SavedMarkerReloadTests
{
    [Fact]
    public async Task Reload_with_three_clear_markers_ignores_an_unrelated_unknown_object()
    {
        await using var scene = await ReloadScene.CreateAsync();
        Assert.Equal(3, scene.Coordinator.Public.Seats.Length);

        await scene.CompleteAsync();

        Assert.Equal(SessionLifecycle.Active, scene.Coordinator.Public.Lifecycle);
        Assert.True(scene.Model.IsResumeTurnAnnouncementOpen, scene.Model.Game.GuidanceInstruction);
        Assert.Empty(scene.Model.Game.PlacementTargets);
        Assert.False(scene.Model.Game.ShowPlacementTarget);
        Assert.Empty(scene.Coordinator.Public.RouteOwners);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reload_explains_and_marks_only_conflicting_detections_until_a_fresh_correction(
        bool duplicateBlue)
    {
        await using var scene = await ReloadScene.CreateAsync();
        var originalVersion = scene.Coordinator.Public.StateVersion;
        var conflictCenter = duplicateBlue ? new NormalizedPoint(.02, .88) : new(.025, .93);
        var conflict = MarkerAt(conflictCenter.X, conflictCenter.Y);
        var conflictReading = new ScoreMarkerReading(4, duplicateBlue ? MarkerColor.Blue : null,
            duplicateBlue ? 2 : null,
            duplicateBlue ? ScoreMarkerReadingStatus.Read : ScoreMarkerReadingStatus.UnknownColor,
            "Conflicting detection");

        scene.Publish(conflict, conflictReading);
        scene.Publish(conflict, conflictReading);

        Assert.Equal(SessionLifecycle.PackedAway, scene.Coordinator.Public.Lifecycle);
        Assert.Equal(originalVersion, scene.Coordinator.Public.StateVersion);
        Assert.False(scene.Model.IsResumeTurnAnnouncementOpen);
        Assert.Contains(duplicateBlue ? "2 possible BLUE scoring markers" :
            "cannot distinguish the BLUE scoring marker from a nearby detection",
            scene.Model.Game.GuidanceInstruction);
        Assert.Contains("should be on 1", scene.Model.Game.GuidanceInstruction);
        Assert.Contains("yellow spheres", scene.Model.Game.GuidanceInstruction);
        var targets = scene.Model.Game.PlacementTargets.ToArray();
        Assert.Equal(2, targets.Length);
        Assert.True(scene.Model.Game.ShowPlacementTarget);
        Assert.Equal(.02 * 960, targets[0].X, precision: 5);
        Assert.Equal(.93 * 600, targets[0].Y, precision: 5);
        Assert.Equal(conflictCenter.X * 960, targets[1].X, precision: 5);
        Assert.Equal(conflictCenter.Y * 600, targets[1].Y, precision: 5);
        Assert.All(targets, target => Assert.Contains("Blue", target.ProblemDescription));

        // The distant unknown object is retained in every capture. Neither it nor
        // stale evidence should erase the cues for the actual marker conflict.
        scene.Publish(repeatSequence: true);
        Assert.Equal(targets, scene.Model.Game.PlacementTargets.ToArray());
        scene.Publish(stale: true);
        Assert.Equal(targets, scene.Model.Game.PlacementTargets.ToArray());
        Assert.Equal(originalVersion, scene.Coordinator.Public.StateVersion);

        scene.Publish();
        Assert.Empty(scene.Model.Game.PlacementTargets);
        Assert.False(scene.Model.Game.ShowPlacementTarget);
        Assert.DoesNotContain("yellow spheres", scene.Model.Game.GuidanceInstruction);
        Assert.Contains("stable camera reading", scene.Model.Game.GuidanceInstruction);
        Assert.Equal(SessionLifecycle.PackedAway, scene.Coordinator.Public.Lifecycle);
        Assert.False(scene.Model.IsResumeTurnAnnouncementOpen);

        await scene.CompleteAsync();
        Assert.Equal(SessionLifecycle.Active, scene.Coordinator.Public.Lifecycle);
        Assert.True(scene.Model.IsResumeTurnAnnouncementOpen, scene.Model.Game.GuidanceInstruction);
        Assert.Empty(scene.Model.Game.PlacementTargets);
        Assert.Empty(scene.Coordinator.Public.RouteOwners);
    }

    private static PieceCandidate MarkerAt(double x, double y) => new(PieceCandidateKind.PlayerMarker,
        [new(x - .008, y - .013), new(x + .008, y - .013),
         new(x + .008, y + .013), new(x - .008, y + .013)], .95);

    private sealed class ReloadScene : IAsyncDisposable
    {
        private readonly TestCheckpointPhotos _photos;
        private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
        private long _sequence;
        private readonly PieceCandidate[] _candidates =
            [MarkerAt(.02, .93), MarkerAt(.05, .93), MarkerAt(.08, .93), MarkerAt(.4, .5)];
        private readonly ScoreMarkerReading[] _readings =
            [new(0, MarkerColor.Blue, 1, ScoreMarkerReadingStatus.Read, "Blue saved score"),
             new(1, MarkerColor.Red, 1, ScoreMarkerReadingStatus.Read, "Red saved score"),
             new(2, MarkerColor.Green, 1, ScoreMarkerReadingStatus.Read, "Green saved score"),
             new(3, null, null, ScoreMarkerReadingStatus.OffTrack, "Unrelated unknown object")];

        private ReloadScene(MainViewModel model, TestCheckpointPhotos photos)
        {
            Model = model;
            _photos = photos;
        }

        public MainViewModel Model { get; }
        public GameCoordinator Coordinator => (GameCoordinator)typeof(MainViewModel)
            .GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Model)!;

        public static async Task<ReloadScene> CreateAsync()
        {
            var token = TestContext.Current.CancellationToken;
            var store = new InMemorySessionStore();
            var photos = new TestCheckpointPhotos();
            var model = new MainViewModel(TestManifest.Manifest, store, photos.Store);
            try
            {
                var game = await GameCoordinator.CreateAsync(
                    new GameRules(TestManifest.Manifest, TestManifest.Catalog), store,
                    new SessionSetup(SessionId.New(),
                        [new Seat(new(1), "Blue player", PlayerColor.Blue, SeatKind.Human, AiDifficulty.Standard),
                         new Seat(new(2), "Red player", PlayerColor.Red, SeatKind.Human, AiDifficulty.Standard),
                         new Seat(new(3), "Green player", PlayerColor.Green, SeatKind.Human, AiDifficulty.Standard)],
                        new(1), VerificationMode.Manual), DeterministicRandom.SeedFrom(91), token);
                foreach (var seat in game.Seats)
                {
                    var hand = await game.GetSeatViewAsync(seat.SeatId, token);
                    Assert.True((await game.SubmitAsync(new CommitTicketSelection(
                        game.NewEnvelope(seat.SeatId), [.. hand.SetupOffer.Take(2)], []), token)).IsAccepted);
                }
                Assert.True((await game.SaveAndPackAwayAsync("Marker clutter regression",
                    cancellationToken: token)).SafeToPack);
                await photos.AttachAsync((await game.GetCheckpointAsync(cancellationToken: token))!);
                model.SetGameLayerVisible(true);
                await model.LoadSavedSessionsAsync();
                model.Setup.SelectedSavedSession = Assert.Single(model.Setup.SavedSessions);
                await model.ResumeMatchAsync();
                Assert.True(model.Game.IsPlaying, model.Setup.SavedMatchMessage);
                var preview = BitmapSource.Create(960, 600, 96, 96, PixelFormats.Bgra32,
                    null, new byte[960 * 600 * 4], 960 * 4);
                preview.Freeze();
                model.Camera.GameTablePreview = preview;
                model.Camera.IsGameTablePreviewUpright = true;
                return new(model, photos);
            }
            catch
            {
                await model.DisposeToolsAsync();
                photos.Dispose();
                throw;
            }
        }

        public void Publish(PieceCandidate? conflict = null, ScoreMarkerReading? conflictReading = null,
            bool repeatSequence = false, bool stale = false)
        {
            if (!repeatSequence) _sequence++;
            var clock = stale ? new ManualFrameTimeProvider() : null;
            var board = CameraFrame.CopyFromBgra32(320, 200, new byte[320 * 200 * 4],
                _sequence, 1, _started.AddSeconds(_sequence * 1.1), clock);
            clock?.Advance(TimeSpan.FromSeconds(3));
            IReadOnlyList<PieceCandidate> candidates = conflict is null ? _candidates : [.. _candidates, conflict];
            IReadOnlyList<ScoreMarkerReading> readings = conflictReading is null ? _readings :
                [.. _readings, conflictReading];
            typeof(CameraViewModel).GetProperty(nameof(CameraViewModel.GameTableAnalysis))!
                .SetValue(Model.Camera, new GameTableAnalysis(board, candidates, readings, 1, 1, "test model"));
        }

        public async Task CompleteAsync()
        {
            for (var index = 0; index < 12 && Coordinator.Public.Lifecycle != SessionLifecycle.Active; index++)
                Publish();
            for (var attempt = 0; attempt < 100 && !Model.IsResumeTurnAnnouncementOpen; attempt++)
                await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await Model.DisposeToolsAsync();
            _photos.Dispose();
        }
    }
}
