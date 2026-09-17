using System.Reflection;
using System.Security.Cryptography;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Persistence;
using GoldenTicket.Vision;
using Microsoft.Data.Sqlite;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopGameExitPhotoFlowTests
{
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

        public void Publish()
        {
            const int width = 96, height = 60;
            var pixels = new byte[width * height * 4];
            for (var offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 255;
            var frame = CameraFrame.CopyFromBgra32(width, height, pixels, ++_sequence, 1);
            SetCapture("_latest", frame);
            _registration ??= BoardRegistration.Create(frame,
                [new(.05, .05), new(.95, .05), new(.95, .95), new(.05, .95)]);
            SetCamera("_gameTableRegistration", _registration);
            SetCamera("_gameTableCropRevision", 7L);
            _camera.IsGameTablePreviewUpright = true;
            var board = _registration.Rectify(frame,
                LearnedPieceDetector.BoardWidth, LearnedPieceDetector.BoardHeight);
            typeof(CameraViewModel).GetProperty(nameof(CameraViewModel.GameTableAnalysis))!
                .SetValue(_camera, new GameTableAnalysis(board, [], [], 7, 0, "synthetic-test-model"));
        }

        private void SetCamera(string name, object value) => typeof(CameraViewModel)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_camera, value);

        private void SetCapture(string name, object value) => typeof(CameraCaptureService)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_camera.Capture, value);

        public void Dispose() { }
    }
}
