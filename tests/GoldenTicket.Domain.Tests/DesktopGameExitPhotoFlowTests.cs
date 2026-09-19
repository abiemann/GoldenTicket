using System.Reflection;
using System.Security.Cryptography;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Persistence;
using GoldenTicket.Vision;
using Microsoft.Data.Sqlite;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopGameExitPhotoFlowTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task Save_during_computer_placement_preserves_turn_and_physical_progress(int placedCount)
    {
        var root = Path.Combine(Path.GetTempPath(), "GoldenTicket.Tests", Guid.NewGuid().ToString("N"));
        var store = new SqliteSessionStore(root);
        var manifest = ManifestLoader.LoadClassicUs();
        var model = new MainViewModel(manifest, store);
        model.Setup.ManualVerificationAccepted = true;
        foreach (var seat in model.Setup.Seats) seat.IsComputer = true;
        model.SetGameLayerVisible(true);
        try
        {
            await model.StartMatchAsync();
            var coordinator = (GameCoordinator)typeof(MainViewModel).GetField("_coordinator",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(model)!;
            var pending = Assert.IsType<GoldenTicket.Domain.Projections.PublicPendingClaim>(coordinator.Public.PendingClaim);
            var active = coordinator.Public.ActiveSeatId;
            var turn = coordinator.Public.TurnNumber;
            Assert.Equal(SeatKind.Computer, coordinator.Public.SeatOf(active).Kind);
            Assert.Empty(coordinator.Public.RouteOwners);
            var count = placedCount < 0 ? pending.TrainCount : Math.Min(placedCount, pending.TrainCount);
            var mask = (1 << count) - 1;
            var color = Enum.Parse<MarkerColor>(coordinator.Public.SeatOf(active).Color.ToString());
            model.OpenGameExitMenu();
            Assert.True(model.IsGameExitMenuOpen);
            using var camera = new SyntheticCamera(model.Camera);
            camera.Publish(pending.RouteId.Value, color, mask);
            var save = model.SaveGameToMenuCommand.ExecuteAsync(null);
            for (var attempt = 0; !save.IsCompleted && attempt < 55; attempt++)
            {
                await Task.Delay(250, TestContext.Current.CancellationToken);
                camera.Publish(pending.RouteId.Value, color, mask);
            }
            await save.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.False(model.IsGameExitMenuOpen, model.GameExitStatus);
            Assert.Equal(GameScreenStage.Welcome, model.Game.Stage);
            var restored = await store.RestoreAsync(coordinator.SessionId, manifest,
                CardCatalog.FromManifest(manifest), TestContext.Current.CancellationToken);
            Assert.Equal(active, restored.State.ActiveSeatId);
            Assert.Equal(turn, restored.State.TurnNumber);
            Assert.Equal(pending.OperationId, restored.State.PendingClaim?.OperationId);
            Assert.Empty(restored.State.RouteOwners);
            var checkpoint = Assert.IsType<PackAwayCheckpoint>(restored.State.Checkpoint);
            var attachment = await new CheckpointPhotoStore(root).ReadReferenceAsync(checkpoint,
                TestContext.Current.CancellationToken);
            Assert.NotNull(attachment);
            try
            {
                Assert.Equal(mask, attachment.Reference.PendingPlacement?.OccupiedSlotMask);
                Assert.True(attachment.Reference.PendingPlacement?.Matches(pending,
                    coordinator.Public.SeatOf(active).Color));
                Assert.Equal(count, attachment.Reference.ObservedTrainInventory?.Total);
            }
            finally { CryptographicOperations.ZeroMemory(attachment.PngBytes); }
        }
        finally
        {
            await model.DisposeToolsAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveGameChecksLiveInventoryStoresBoardPhotoAndReturnsToMenu()
    {
        var root = Path.Combine(Path.GetTempPath(), "GoldenTicket.Tests", Guid.NewGuid().ToString("N"));
        var store = new SqliteSessionStore(root);
        var manifest = ManifestLoader.LoadClassicUs();
        var model = new MainViewModel(manifest, store);
        model.Setup.ManualVerificationAccepted = true;
        model.SetGameLayerVisible(true);
        try
        {
            await model.StartMatchAsync();
            await model.CommitTicketsAsync();
            Assert.True(model.Game.IsPlaying);

            using var camera = new SyntheticCamera(model.Camera);
            camera.Publish();
            model.OpenGameExitMenu();
            Assert.True(model.IsGameExitMenuOpen);
            Assert.Contains("0 on board", model.GameExitInventorySummary);
            Assert.True(model.Camera.CanCaptureGameTablePhoto);

            var save = model.SaveGameToMenuCommand.ExecuteAsync(null);
            for (var attempt = 0; !save.IsCompleted && attempt < 55; attempt++)
            {
                await Task.Delay(250);
                camera.Publish();
            }
            await save.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(model.IsGameExitMenuOpen, model.GameExitStatus);
            Assert.Equal(GameScreenStage.Welcome, model.Game.Stage);
            var listed = Assert.Single(await store.ListSessionsAsync(CancellationToken.None));
            var restored = await store.RestoreAsync(listed.SessionId, manifest,
                CardCatalog.FromManifest(manifest), CancellationToken.None);
            var checkpoint = Assert.IsType<PackAwayCheckpoint>(restored.State.Checkpoint);
            Assert.True(checkpoint.IsSafeToPackAway);
            var attachment = await new CheckpointPhotoStore(root).ReadReferenceAsync(checkpoint);
            Assert.NotNull(attachment);
            try
            {
                Assert.Equal(CheckpointTrainInventoryProvenance.CameraObserved,
                    attachment.Reference.ObservedTrainInventory?.Provenance);
                Assert.Equal(0, attachment.Reference.ObservedTrainInventory?.Total);
                Assert.True(attachment.PngBytes.Length > 100);
            }
            finally { CryptographicOperations.ZeroMemory(attachment.PngBytes); }
        }
        finally
        {
            await model.DisposeToolsAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class SyntheticCamera : IDisposable
    {
        private readonly CameraViewModel _camera;
        private BoardRegistration? _registration;
        private long _sequence;

        public SyntheticCamera(CameraViewModel camera)
        {
            _camera = camera;
            SetCapture("<ActiveDevice>k__BackingField",
                new CameraDevice("save-game-synthetic-camera", "Synthetic board camera"));
            SetCapture("_epoch", 1L);
            SetCapture("_running", true);
            _camera.IsRunning = true;
        }

        public void Publish(string? routeId = null, MarkerColor color = MarkerColor.Blue, int mask = 0)
        {
            const int width = 960, height = 600;
            var pixels = new byte[width * height * 4];
            for (var offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 255;
            var candidates = new List<PieceCandidate>();
            if (routeId is not null && ClassicUsRouteGeometry.TryGetSlots(routeId, out var slots))
            {
                var rgb = color switch
                {
                    MarkerColor.Blue => (20, 75, 195), MarkerColor.Red => (190, 35, 30),
                    MarkerColor.Green => (20, 125, 35), MarkerColor.Yellow => (225, 180, 20),
                    _ => (20, 20, 20)
                };
                for (var index = 0; index < slots.Count; index++)
                {
                    if ((mask & (1 << index)) == 0) continue;
                    var x = (int)Math.Round(slots[index].X * width);
                    var y = (int)Math.Round(slots[index].Y * height);
                    for (var py = y - 5; py <= y + 5; py++)
                    for (var px = x - 7; px <= x + 7; px++)
                    {
                        var offset = (py * width + px) * 4;
                        pixels[offset] = (byte)rgb.Item3;
                        pixels[offset + 1] = (byte)rgb.Item2;
                        pixels[offset + 2] = (byte)rgb.Item1;
                    }
                    candidates.Add(new(PieceCandidateKind.Train,
                        [new((x - 7d) / width, (y - 5d) / height),
                         new((x + 7d) / width, (y - 5d) / height),
                         new((x + 7d) / width, (y + 5d) / height),
                         new((x - 7d) / width, (y + 5d) / height)], .95));
                }
            }
            var frame = CameraFrame.CopyFromBgra32(width, height, pixels, ++_sequence, 1);
            SetCapture("_latest", frame);
            _registration ??= BoardRegistration.Create(frame,
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]);
            SetCamera("_gameTableRegistration", _registration);
            SetCamera("_gameTableCropRevision", 7L);
            _camera.IsGameTablePreviewUpright = true;
            var board = _registration.Rectify(frame,
                LearnedPieceDetector.BoardWidth, LearnedPieceDetector.BoardHeight);
            typeof(CameraViewModel).GetProperty(nameof(CameraViewModel.GameTableAnalysis))!
                .SetValue(_camera, new GameTableAnalysis(board, candidates, [], 7, 0, "synthetic-test-model"));
        }

        private void SetCamera(string name, object value) => typeof(CameraViewModel)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_camera, value);

        private void SetCapture(string name, object value) => typeof(CameraCaptureService)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_camera.Capture, value);

        public void Dispose() { }
    }
}
