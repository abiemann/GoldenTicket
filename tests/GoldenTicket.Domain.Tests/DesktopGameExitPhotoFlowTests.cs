using GoldenTicket.Testing;
using System.Reflection;
using System.Security.Cryptography;
using GoldenTicket.Application;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Domain.Manifest;
using GoldenTicket.Domain.Model;
using GoldenTicket.Persistence;
using GoldenTicket.Vision;
using Microsoft.Data.Sqlite;

namespace GoldenTicket.Domain.Tests;

public sealed class DesktopGameExitPhotoFlowTests
{
    [Fact]
    public async Task Technical_photo_after_interrupted_pending_save_records_exact_slots_and_inventory()
    {
        var root = Path.Combine(Path.GetTempPath(), "GoldenTicket.Tests", Guid.NewGuid().ToString("N"));
        var store = new SqliteSessionStore(root);
        var manifest = ManifestLoader.LoadClassicUs();
        var model = new MainViewModel(manifest, store, camera: new CameraViewModel(capture: new FakeCameraCapture()));
        model.Setup.ManualVerificationAccepted = true;
        foreach (var seat in model.Setup.Seats) seat.IsComputer = true;
        model.SetGameLayerVisible(true);
        try
        {
            await model.StartMatchAsync();
            var coordinator = (GameCoordinator)typeof(MainViewModel).GetField("_coordinator",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(model)!;
            var pending = Assert.IsType<GoldenTicket.Domain.Projections.PublicPendingClaim>(coordinator.Public.PendingClaim);
            var checkpointResult = await coordinator.SaveAndPackAwayAsync("Interrupted photo save",
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(checkpointResult.SafeToPack);
            var checkpoint = Assert.IsType<PackAwayCheckpoint>(await coordinator.GetCheckpointAsync(
                TestContext.Current.CancellationToken));
            using var camera = new SyntheticCamera(model.Camera);
            var color = Enum.Parse<MarkerColor>(coordinator.Public.SeatOf(pending.SeatId).Color.ToString());
            const int mask = 1;
            camera.Publish(pending.RouteId.Value, color, mask);
            Assert.False(model.Camera.CanCapturePhoto);
            Assert.True(model.Camera.CanCaptureGameTablePhoto);
            await model.ShowCheckpointPhotoCommand.ExecuteAsync(null);
            Assert.True(model.CheckpointPhoto.CaptureAllowed);
            model.CheckpointPhoto.OperatorAcknowledged = true;
            Assert.True(model.CheckpointPhoto.CaptureReferenceCommand.CanExecute(null));
            var capture = model.CheckpointPhoto.CaptureReferenceCommand.ExecuteAsync(null);
            for (var attempt = 0; !capture.IsCompleted && attempt < 55; attempt++)
            {
                await Task.Delay(250, TestContext.Current.CancellationToken);
                camera.Publish(pending.RouteId.Value, color, mask);
            }
            await capture.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.True(model.CheckpointPhoto.HasPhoto, model.CheckpointPhoto.Status);
            var attachment = await new CheckpointPhotoStore(root).ReadReferenceAsync(checkpoint,
                TestContext.Current.CancellationToken);
            Assert.NotNull(attachment);
            try
            {
                Assert.Equal(2, attachment.Reference.FormatVersion);
                Assert.Equal(mask, attachment.Reference.PendingPlacement?.OccupiedSlotMask);
                Assert.Equal(1 + checkpoint.TotalTrainsOnBoard,
                    attachment.Reference.ObservedTrainInventory?.Total);
                Assert.Equal(CheckpointTrainInventoryProvenance.CameraObserved,
                    attachment.Reference.ObservedTrainInventory?.Provenance);
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task Save_during_computer_placement_preserves_turn_and_physical_progress(int placedCount)
    {
        var root = Path.Combine(Path.GetTempPath(), "GoldenTicket.Tests", Guid.NewGuid().ToString("N"));
        var store = new SqliteSessionStore(root);
        var manifest = ManifestLoader.LoadClassicUs();
        var model = new MainViewModel(manifest, store, camera: new CameraViewModel(capture: new FakeCameraCapture()));
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
        var model = new MainViewModel(manifest, store, camera: new CameraViewModel(capture: new FakeCameraCapture()));
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
                await Task.Delay(250, cancellationToken: TestContext.Current.CancellationToken);
                camera.Publish();
            }
            await save.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(model.IsGameExitMenuOpen, model.GameExitStatus);
            Assert.Equal(GameScreenStage.Welcome, model.Game.Stage);
            var listed = Assert.Single(await store.ListSessionsAsync(CancellationToken.None));
            var restored = await store.RestoreAsync(listed.SessionId, manifest,
                CardCatalog.FromManifest(manifest), CancellationToken.None);
            var checkpoint = Assert.IsType<PackAwayCheckpoint>(restored.State.Checkpoint);
            Assert.True(checkpoint.IsSafeToPackAway);
            var attachment = await new CheckpointPhotoStore(root).ReadReferenceAsync(checkpoint, cancellationToken: TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task Save_names_unclaimed_black_trains_and_waits_for_their_removal_without_claiming_them()
    {
        var root = Path.Combine(Path.GetTempPath(), "GoldenTicket.Tests", Guid.NewGuid().ToString("N"));
        var store = new SqliteSessionStore(root);
        var manifest = ManifestLoader.LoadClassicUs();
        var model = new MainViewModel(manifest, store, camera: new CameraViewModel(capture: new FakeCameraCapture()));
        model.Setup.ManualVerificationAccepted = true;
        model.SetGameLayerVisible(true);
        try
        {
            await model.StartMatchAsync();
            await model.CommitTicketsAsync();
            var coordinator = (GameCoordinator)typeof(MainViewModel).GetField("_coordinator",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(model)!;
            var beforeHash = await coordinator.ComputeStateHashAsync(cancellationToken: TestContext.Current.CancellationToken);
            var beforeVersion = coordinator.Public.StateVersion;
            var beforeTurn = coordinator.Public.TurnNumber;
            var beforeSeat = coordinator.Public.ActiveSeatId;
            Assert.Empty(coordinator.Public.RouteOwners);
            model.OpenGameExitMenu();
            Assert.True(model.IsGameExitMenuOpen);
            using var camera = new SyntheticCamera(model.Camera);
            camera.Publish("little-rock--saint-louis", MarkerColor.Black, 0b11);
            Assert.True(model.Camera.CanCaptureGameTablePhoto);

            var save = model.SaveGameToMenuCommand.ExecuteAsync(null);
            await Task.Delay(250, TestContext.Current.CancellationToken);
            camera.Publish("little-rock--saint-louis", MarkerColor.Black, 0b11);

            Assert.True(model.IsGameExitSaving);
            Assert.False(save.IsCompleted);
            Assert.Contains("2 black trains", model.GameExitStatus);
            Assert.Contains("Little Rock", model.GameExitStatus);
            Assert.Contains("Saint Louis", model.GameExitStatus);
            Assert.Contains("remove", model.GameExitStatus, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UnexpectedTrain", model.GameExitStatus);
            Assert.Equal(beforeVersion, coordinator.Public.StateVersion);
            Assert.Equal(beforeTurn, coordinator.Public.TurnNumber);
            Assert.Equal(beforeSeat, coordinator.Public.ActiveSeatId);
            Assert.Empty(coordinator.Public.RouteOwners);
            Assert.Equal(beforeHash, await coordinator.ComputeStateHashAsync(cancellationToken: TestContext.Current.CancellationToken));
            var blocked = await store.RestoreAsync(coordinator.SessionId, manifest,
                CardCatalog.FromManifest(manifest), TestContext.Current.CancellationToken);
            Assert.Equal(beforeHash, StateHash.Compute(blocked.State));
            Assert.Null(blocked.State.Checkpoint);
            Assert.Empty(blocked.State.RouteOwners);

            // One empty observation is not enough; the existing save must wait for stability.
            camera.Publish();
            Assert.False(save.IsCompleted);
            Assert.True(model.IsGameExitMenuOpen);
            for (var attempt = 0; !save.IsCompleted && attempt < 55; attempt++)
            {
                await Task.Delay(250, TestContext.Current.CancellationToken);
                camera.Publish();
            }
            await save.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            Assert.False(model.IsGameExitMenuOpen, model.GameExitStatus);
            Assert.Equal(GameScreenStage.Welcome, model.Game.Stage);
            var restored = await store.RestoreAsync(coordinator.SessionId, manifest,
                CardCatalog.FromManifest(manifest), TestContext.Current.CancellationToken);
            Assert.Equal(beforeTurn, restored.State.TurnNumber);
            Assert.Equal(beforeSeat, restored.State.ActiveSeatId);
            Assert.Empty(restored.State.RouteOwners);
            var checkpoint = Assert.IsType<PackAwayCheckpoint>(restored.State.Checkpoint);
            Assert.True(checkpoint.IsSafeToPackAway);
            var attachment = await new CheckpointPhotoStore(root).ReadReferenceAsync(checkpoint,
                TestContext.Current.CancellationToken);
            Assert.NotNull(attachment);
            try { Assert.Equal(0, attachment.Reference.ObservedTrainInventory?.Total); }
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
    public async Task Save_shows_the_exact_camera_evidence_for_an_unlocated_train_and_clears_it_when_removed()
    {
        var root = Path.Combine(Path.GetTempPath(), "GoldenTicket.Tests", Guid.NewGuid().ToString("N"));
        var store = new SqliteSessionStore(root);
        var manifest = ManifestLoader.LoadClassicUs();
        var model = new MainViewModel(manifest, store, camera: new CameraViewModel(capture: new FakeCameraCapture()));
        model.Setup.ManualVerificationAccepted = true;
        model.SetGameLayerVisible(true);
        try
        {
            await model.StartMatchAsync();
            await model.CommitTicketsAsync();
            var coordinator = (GameCoordinator)typeof(MainViewModel).GetField("_coordinator",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(model)!;
            var beforeHash = await coordinator.ComputeStateHashAsync(cancellationToken: TestContext.Current.CancellationToken);
            var beforeTurn = coordinator.Public.TurnNumber;
            var beforeSeat = coordinator.Public.ActiveSeatId;
            model.OpenGameExitMenu();
            using var camera = new SyntheticCamera(model.Camera);
            camera.Publish(extraOffRoute: true);
            var observedFrame = Assert.IsType<GameTableAnalysis>(model.Camera.GameTableAnalysis).Board;

            var save = model.SaveGameToMenuCommand.ExecuteAsync(null);
            Assert.True(model.IsGameExitSaving);
            Assert.False(save.IsCompleted);
            var evidence = Assert.IsType<BoardCheckEvidence>(model.GameExitEvidence);
            var detection = Assert.Single(evidence.Detections);
            Assert.Equal(1, detection.Number);
            Assert.False(string.IsNullOrWhiteSpace(detection.Description));
            Assert.False(string.IsNullOrWhiteSpace(evidence.Caption));
            // The detected object is off the printed routes, but still has an exact location.
            Assert.InRange(950d / 1996 * 960, detection.Left, detection.Left + detection.Width);
            Assert.InRange(1140d / 1248 * 600, detection.Top, detection.Top + detection.Height);
            Assert.Equal(observedFrame.Width, evidence.Image.PixelWidth);
            Assert.Equal(observedFrame.Height, evidence.Image.PixelHeight);
            var shownPixels = new byte[observedFrame.Stride * observedFrame.Height];
            evidence.Image.CopyPixels(shownPixels, observedFrame.Stride, 0);
            Assert.Equal(observedFrame.Bgra32.ToArray(), shownPixels);
            Assert.Empty(coordinator.Public.RouteOwners);
            Assert.Equal(beforeHash, await coordinator.ComputeStateHashAsync(cancellationToken: TestContext.Current.CancellationToken));

            camera.Publish();
            Assert.Null(model.GameExitEvidence);
            Assert.False(save.IsCompleted); // A clean frame clears the warning, not the stability check.
            Assert.DoesNotContain("extra", model.GameExitStatus ?? "", StringComparison.OrdinalIgnoreCase);
            // Updating the live preview does not mutate the image which explained the failed check.
            evidence.Image.CopyPixels(shownPixels, observedFrame.Stride, 0);
            Assert.Equal(observedFrame.Bgra32.ToArray(), shownPixels);
            for (var attempt = 0; !save.IsCompleted && attempt < 55; attempt++)
            {
                await Task.Delay(250, TestContext.Current.CancellationToken);
                camera.Publish();
            }
            await save.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.False(model.IsGameExitMenuOpen, model.GameExitStatus);
            var restored = await store.RestoreAsync(coordinator.SessionId, manifest,
                CardCatalog.FromManifest(manifest), TestContext.Current.CancellationToken);
            Assert.Equal(beforeTurn, restored.State.TurnNumber);
            Assert.Equal(beforeSeat, restored.State.ActiveSeatId);
            Assert.Empty(restored.State.RouteOwners);
            Assert.True(Assert.IsType<PackAwayCheckpoint>(restored.State.Checkpoint).IsSafeToPackAway);
        }
        finally
        {
            await model.DisposeToolsAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Save_timeout_retains_its_evidence_then_clears_the_error_on_a_fresh_clean_frame_without_saving()
    {
        var root = Path.Combine(Path.GetTempPath(), "GoldenTicket.Tests", Guid.NewGuid().ToString("N"));
        var store = new SqliteSessionStore(root);
        var manifest = ManifestLoader.LoadClassicUs();
        var model = new MainViewModel(manifest, store, camera: new CameraViewModel(capture: new FakeCameraCapture()));
        model.Setup.ManualVerificationAccepted = true;
        model.SetGameLayerVisible(true);
        try
        {
            await model.StartMatchAsync();
            await model.CommitTicketsAsync();
            var coordinator = (GameCoordinator)typeof(MainViewModel).GetField("_coordinator",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(model)!;
            var beforeHash = await coordinator.ComputeStateHashAsync(cancellationToken: TestContext.Current.CancellationToken);
            model.OpenGameExitMenu();
            using var camera = new SyntheticCamera(model.Camera);
            camera.Publish(extraOffRoute: true);
            var save = model.SaveGameToMenuCommand.ExecuteAsync(null);
            var evidence = Assert.IsType<BoardCheckEvidence>(model.GameExitEvidence);

            // Exercise the timeout path without sleeping through its fifteen-second deadline.
            var completion = (TaskCompletionSource<BoardInventoryObservation>)typeof(MainViewModel)
                .GetField("_gameExitInventoryCompletion", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(model)!;
            completion.SetException(new TimeoutException());
            await save.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.False(model.IsGameExitSaving);
            Assert.True(model.IsGameExitMenuOpen);
            Assert.Same(evidence, model.GameExitEvidence);

            // Re-observing the same failed frame cannot dismiss the evidence.
            typeof(MainViewModel).GetMethod("ObserveGameExitInventory",
                BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, null);
            Assert.Same(evidence, model.GameExitEvidence);
            camera.Publish();
            Assert.Null(model.GameExitEvidence);
            Assert.Contains("Save Game", model.GameExitStatus);
            Assert.DoesNotContain("extra", model.GameExitStatus, StringComparison.OrdinalIgnoreCase);
            Assert.True(model.IsGameExitMenuOpen);
            Assert.False(model.IsGameExitSaving);
            Assert.True(model.Game.IsPlaying);
            Assert.Equal(beforeHash, await coordinator.ComputeStateHashAsync(cancellationToken: TestContext.Current.CancellationToken));
            var restored = await store.RestoreAsync(coordinator.SessionId, manifest,
                CardCatalog.FromManifest(manifest), TestContext.Current.CancellationToken);
            Assert.Null(restored.State.Checkpoint);
            Assert.Equal(beforeHash, StateHash.Compute(restored.State));
        }
        finally
        {
            await model.DisposeToolsAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Save_explains_the_unpaid_detected_route_instead_of_reporting_an_unknown_train()
    {
        var root = Path.Combine(Path.GetTempPath(), "GoldenTicket.Tests", Guid.NewGuid().ToString("N"));
        var store = new SqliteSessionStore(root);
        var manifest = ManifestLoader.LoadClassicUs();
        var model = new MainViewModel(manifest, store, camera: new CameraViewModel(capture: new FakeCameraCapture()));
        model.Setup.ManualVerificationAccepted = true;
        model.SetGameLayerVisible(true);
        try
        {
            await model.StartMatchAsync();
            await model.CommitTicketsAsync();
            var coordinator = (GameCoordinator)typeof(MainViewModel).GetField("_coordinator",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(model)!;
            var beforeHash = await coordinator.ComputeStateHashAsync(cancellationToken: TestContext.Current.CancellationToken);
            var route = new RouteId("kansas-city--oklahoma-city--a");
            var proposal = new BoardFirstClaimProposal(coordinator.SessionId, coordinator.Public.ActiveSeatId,
                coordinator.Public.SeatOf(coordinator.Public.ActiveSeatId).DisplayName, route, manifest.Describe(route),
                coordinator.Public.StateVersion, 7, 0, 1, []);
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.BoardFirstProposal))!.SetValue(model, proposal);
            model.OpenGameExitMenu();

            await model.SaveGameToMenuCommand.ExecuteAsync(null);

            Assert.True(model.IsGameExitMenuOpen);
            Assert.False(model.IsGameExitSaving);
            Assert.Contains("Kansas City", model.GameExitStatus);
            Assert.Contains("Oklahoma City", model.GameExitStatus);
            Assert.Contains("payment", model.GameExitStatus, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cannot identify", model.GameExitStatus, StringComparison.OrdinalIgnoreCase);
            Assert.Null(model.GameExitEvidence);
            Assert.Same(proposal, model.BoardFirstProposal);
            Assert.Equal(beforeHash, await coordinator.ComputeStateHashAsync(cancellationToken: TestContext.Current.CancellationToken));
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
        private FakeCameraCapture Capture => (FakeCameraCapture)_camera.Capture;
        private BoardRegistration? _registration;
        private long _sequence;

        public SyntheticCamera(CameraViewModel camera)
        {
            _camera = camera;
            Capture.ActiveDevice = new CameraDevice("save-game-synthetic-camera", "Synthetic board camera");
            Capture.Epoch = 1L;
            Capture.IsRunning = true;
            _camera.IsRunning = true;
        }

        public void Publish(string? routeId = null, MarkerColor color = MarkerColor.Blue, int mask = 0,
            bool extraOffRoute = false)
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
            if (extraOffRoute)
            {
                // A known off-route location from BoardInventoryVerifierTests, near the bottom edge.
                var x = (int)Math.Round(950d / 1996 * width);
                var y = (int)Math.Round(1140d / 1248 * height);
                for (var py = y - 5; py <= y + 5; py++)
                for (var px = x - 7; px <= x + 7; px++)
                {
                    var offset = (py * width + px) * 4;
                    pixels[offset] = 195;
                    pixels[offset + 1] = 75;
                    pixels[offset + 2] = 20;
                }
                candidates.Add(new(PieceCandidateKind.Train,
                    [new((x - 7d) / width, (y - 5d) / height),
                     new((x + 7d) / width, (y - 5d) / height),
                     new((x + 7d) / width, (y + 5d) / height),
                     new((x - 7d) / width, (y + 5d) / height)], .95));
            }
            var frame = CameraFrame.CopyFromBgra32(width, height, pixels, ++_sequence, 1);
            Capture.LatestFrame = frame;
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

        public void PrepareTechnicalCapture()
        {
            if (_registration is null) throw new InvalidOperationException("Publish a board first.");
            SetCamera("_registration", _registration);
            SetCamera("_cropRevision", 7L);
            _camera.HasBoardCrop = true;
            _camera.SafetyHeld = false;
        }

        public void Dispose() { }
    }
}
