using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenTicket.CompanionHost;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Domain.Engine;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed partial class AutomaticPhysicalFlowTests
{
    [Fact]
    public async Task Companion_map_uses_only_the_upright_camera_crop_and_exact_laptop_targets()
    {
        var token = TestContext.Current.CancellationToken;
        var model = await CreateComputerPlacementCompanionAsync();
        try
        {
            using var updates = ObserveCompanionUpdates(model);
            var bridge = GetCompanionBridge(model);
            var waiting = (await bridge.ReadPublicAsync(token)).BoardMap;
            Assert.NotNull(waiting);
            Assert.Null(waiting.ImageId);
            Assert.Empty(waiting.Targets);
            SetCompanionMapPreview(model);
            await WaitUntilAsync(() => CompanionMapImage(model) is not null);
            Assert.True(updates.Changes.Reader.TryRead(out _));
            var snapshot = await bridge.ReadPublicAsync(token);
            var map = Assert.IsType<CompanionBoardMap>(snapshot.BoardMap);
            Assert.Equal(model.Game.PlacementTargets.Select(point => new CompanionMapPoint(point.X, point.Y, point.Number)),
                map.Targets);
            Assert.NotEmpty(map.Targets);
            Assert.Equal(TestManifest.Manifest.Cities.Select(city => city.StableId.Value),
                map.Cities!.Select(city => city.Id));
            var image = Assert.IsType<CompanionBoardImage>(await bridge.ReadBoardImageAsync(map.ImageId!, token));
            using var input = new MemoryStream(image.Jpeg);
            var frame = new JpegBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            Assert.Equal(960, frame.PixelWidth);
            Assert.Equal(600, frame.PixelHeight);
            Assert.InRange(image.Jpeg.Length, 1, 1024 * 1024);
            // The synthetic camera is uniformly blue. Its JPEG stays blue even beneath
            // the target dots: the image contains no rendered desktop UI or overlays.
            var rgb = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
            var pixels = new byte[960 * 600 * 3];
            rgb.CopyPixels(pixels, 960 * 3, 0);
            var samples = new[] { (X: 0, Y: 0), (X: 959, Y: 599), (X: 480, Y: 300) }
                .Concat(map.Targets.Select(point => (X: (int)point.X, Y: (int)point.Y)));
            foreach (var point in samples)
            {
                var offset = (point.Y * 960 + point.X) * 3;
                Assert.InRange(pixels[offset], 170, 190);
                Assert.InRange(pixels[offset + 1], 30, 50);
                Assert.InRange(pixels[offset + 2], 10, 30);
            }
            Assert.Null(await bridge.ReadBoardImageAsync(Guid.NewGuid().ToString("N"), token));

            var placement = Assert.IsType<PlacementInstruction>(model.Table.Placement);
            typeof(GameScreenViewModel).GetMethod("ShowUnverifiedTrainSpaces", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(model.Game, [placement.RouteId, placement.TrainCount, 1]);
            var correction = (await bridge.ReadPublicAsync(token)).BoardMap!;
            Assert.Single(correction.Targets);
            Assert.Equal(model.Game.PlacementTargets.Select(point => new CompanionMapPoint(point.X, point.Y, point.Number)),
                correction.Targets);
            Assert.Equal(map.ImageId, correction.ImageId);
            Assert.True(updates.Changes.Reader.TryRead(out _));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Theory]
    [InlineData("orientation", true)]
    [InlineData("camera", true)]
    [InlineData("preview", true)]
    [InlineData("pause", false)]
    [InlineData("practical", false)]
    [InlineData("menu", false)]
    [InlineData("shutdown", false)]
    public async Task Companion_map_drops_old_images_when_the_view_becomes_unavailable(string change, bool waitForCamera)
    {
        var token = TestContext.Current.CancellationToken;
        var model = await CreateComputerPlacementCompanionAsync();
        try
        {
            var bridge = GetCompanionBridge(model);
            SetCompanionMapPreview(model);
            await WaitUntilAsync(() => CompanionMapImage(model) is not null);
            var before = (await bridge.ReadPublicAsync(token)).BoardMap!;
            switch (change)
            {
                case "orientation": model.Camera.IsGameTablePreviewUpright = false; break;
                case "camera": model.Camera.IsRunning = false; break;
                case "preview": model.Camera.GameTablePreview = null; break;
                case "pause": model.IsGameExitMenuOpen = true; break;
                case "practical": model.Connection.UsePractical = true; break;
                case "menu": model.Screen = Screen.Setup; break;
                case "shutdown": await model.DisposeToolsAsync(); break;
            }
            var after = (await bridge.ReadPublicAsync(token)).BoardMap;
            if (waitForCamera)
            {
                Assert.NotNull(after);
                Assert.Null(after.ImageId);
                Assert.Empty(after.Targets);
                Assert.Empty(after.Cities!);
            }
            else Assert.Null(after);
            Assert.Null(await bridge.ReadBoardImageAsync(before.ImageId!, token));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Companion_map_frames_are_rate_limited_and_only_two_recent_images_are_retained()
    {
        var token = TestContext.Current.CancellationToken;
        var model = await CreateComputerPlacementCompanionAsync();
        try
        {
            var bridge = GetCompanionBridge(model);
            SetCompanionMapPreview(model);
            await WaitUntilAsync(() => CompanionMapImage(model) is not null);
            var first = (await bridge.ReadPublicAsync(token)).BoardMap!.ImageId!;
            using var updates = ObserveCompanionUpdates(model);
            // Many ordinary camera frames inside one publishing interval do not
            // encode, invalidate SSE or accumulate a work queue.
            for (var index = 0; index < 12; index++) model.Camera.GameTablePreview = NewCompanionMapFrame();
            Assert.Equal(first, (await bridge.ReadPublicAsync(token)).BoardMap!.ImageId);
            Assert.False(updates.Changes.Reader.TryRead(out _));

            await Task.Delay(1050, token);
            model.Camera.GameTablePreview = NewCompanionMapFrame();
            await WaitUntilAsync(() => CompanionMapImage(model)?.Id != first);
            var second = (await bridge.ReadPublicAsync(token)).BoardMap!.ImageId!;
            Assert.NotNull(await bridge.ReadBoardImageAsync(first, token));
            await Task.Delay(1050, token);
            model.Camera.GameTablePreview = NewCompanionMapFrame();
            await WaitUntilAsync(() => CompanionMapImage(model)?.Id != second);
            var third = (await bridge.ReadPublicAsync(token)).BoardMap!.ImageId!;
            Assert.Null(await bridge.ReadBoardImageAsync(first, token));
            Assert.NotNull(await bridge.ReadBoardImageAsync(second, token));
            Assert.NotNull(await bridge.ReadBoardImageAsync(third, token));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Companion_map_remains_through_computer_scoring_then_restarts_for_the_human()
    {
        var token = TestContext.Current.CancellationToken;
        var model = await CreateComputerPlacementCompanionAsync();
        try
        {
            SetCompanionMapPreview(model);
            await WaitUntilAsync(() => CompanionMapImage(model) is not null);
            var bridge = GetCompanionBridge(model);
            var game = GetCoordinator(model);
            var placement = Assert.IsType<PlacementInstruction>(model.Table.Placement);
            var accept = typeof(MainViewModel).GetMethod("AcceptPhysicalPlacementAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var claim = Assert.IsAssignableFrom<Task>(accept.Invoke(model,
                [placement, EvidenceKind.CameraAutomatic, "synthetic-test-model", "Two stable route observations."]));
            await WaitUntilAsync(() => model.Game.GuidanceInstruction == "Thank you");
            Assert.NotEqual(placement.SeatId, game.Public.ActiveSeatId);
            Assert.NotNull((await bridge.ReadPublicAsync(token)).BoardMap);
            await claim;
            var scoring = (await bridge.ReadPublicAsync(token)).BoardMap!;
            Assert.NotNull(scoring);
            Assert.Equal(model.Game.PlacementTargets.Select(point => new CompanionMapPoint(point.X, point.Y, point.Number)),
                scoring.Targets);
            var target = game.Public.SeatOf(placement.SeatId).RouteScore % 100 + 1;
            var color = Enum.Parse<MarkerColor>(placement.Color.ToString());
            var at = DateTimeOffset.UtcNow;
            PublishScore(model.Camera, 1, at, color, target);
            PublishScore(model.Camera, 2, at.AddSeconds(1.1), color, target);
            await WaitUntilAsync(() => model.Game.GuidanceTurn != "Scoring");
            await WaitUntilAsync(() => (bool)typeof(MainViewModel).GetProperty("CanCompanionControl",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!);
            // Score-only fixture frames omit the newly claimed trains. Card clicks
            // still award immediately; inventory verification waits for the turn boundary.
            var ready = await bridge.ReadPublicAsync(token);
            Assert.NotNull(ready.BoardMap);
            Assert.Empty(ready.BoardMap.Targets);
            Assert.True(ready.CanControl);
            var drawingSeat = game.Public.ActiveSeatId;
            var originalCards = game.Public.SeatOf(drawingSeat).TrainCardCount;
            for (var draw = 0; draw < 2; draw++)
            {
                var receipt = await bridge.ExecuteAsync(drawingSeat,
                    new(Guid.NewGuid().ToString("N"), game.SessionId.Value,
                        game.Public.StateVersion, "drawTrain"), token)
                    .WaitAsync(TimeSpan.FromSeconds(2), token);
                Assert.True(receipt.Accepted);
            }
            Assert.Equal(originalCards + 2, game.Public.SeatOf(drawingSeat).TrainCardCount);
            Assert.True(model.IsCheckingBoardBeforeNextTurn);
            at = DateTimeOffset.UtcNow.AddSeconds(2);
            PublishScore(model.Camera, 3, at, color, target);
            // The post-turn gate must expose missing spaces on both board maps,
            // even though the next human cannot reveal cards or take an action yet.
            var missing = await bridge.ReadPublicAsync(token);
            Assert.NotNull(missing.BoardMap);
            Assert.False(missing.CanControl);
            Assert.Equal(placement.TrainCount, missing.BoardMap.Targets.Count);
            Assert.Equal(model.Game.PlacementTargets.Select(point =>
                new CompanionMapPoint(point.X, point.Y, point.Number)), missing.BoardMap.Targets);
            PublishTrains(model.Camera, placement.RouteId.Value, 4, at.AddSeconds(1.1), color);
            Assert.True(model.IsCheckingBoardBeforeNextTurn);
            PublishTrains(model.Camera, placement.RouteId.Value, 5, at.AddSeconds(2.2), color);
            await WaitUntilAsync(() => !model.IsCheckingBoardBeforeNextTurn && model.CanRevealPrivateSeat);
            var human = await bridge.ReadPublicAsync(token);
            Assert.NotNull(human.BoardMap);
            Assert.Empty(human.BoardMap.Targets);
            Assert.True(human.CanControl);
            Assert.Null(human.Guidance);
            if (scoring.ImageId is not null) Assert.Null(await bridge.ReadBoardImageAsync(scoring.ImageId, token));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Human_map_publishes_all_public_cities_and_keeps_its_frame_during_a_remote_draw()
    {
        var token = TestContext.Current.CancellationToken;
        var model = await StartCompanionMatchAsync();
        try
        {
            var bridge = GetCompanionBridge(model);
            SetCompanionMapPreview(model);
            await WaitUntilAsync(() => CompanionMapImage(model) is not null);
            var initial = await bridge.ReadPublicAsync(token);
            Assert.True(initial.CanControl);
            var map = Assert.IsType<CompanionBoardMap>(initial.BoardMap);
            Assert.NotNull(map.Cities);
            Assert.Empty(map.Targets);
            Assert.Equal(TestManifest.Manifest.Cities.Select(city => (city.StableId.Value, city.DisplayName)),
                map.Cities.Select(city => (city.Id, city.Name)));
            Assert.All(map.Cities, city =>
            {
                Assert.InRange(city.X, 0, 960);
                Assert.InRange(city.Y, 0, 600);
            });
            var held = await GetCoordinator(model).GetSeatViewAsync(GetCoordinator(model).Public.ActiveSeatId, token);
            var heldCities = DestinationBoardOverlay.BuildKept(TestManifest.Manifest, held.Tickets);
            Assert.True(map.Cities.Count > heldCities.Count);
            var serialized = JsonSerializer.Serialize(map);
            Assert.DoesNotContain("Ticket", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Payment", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Hand", serialized, StringComparison.OrdinalIgnoreCase);

            // Exercise the actual bridge's busy gate, which is held through a tablet
            // first-card draw. It must not reset the same human's map between snapshots.
            var begin = typeof(MainViewModel).GetMethod("BeginRemoteCommand", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var end = typeof(MainViewModel).GetMethod("EndRemoteCommand", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.True((bool)begin.Invoke(model, null)!);
            try
            {
                var drawing = await bridge.ReadPublicAsync(token);
                Assert.True(drawing.CanControl);
                Assert.Equal(map.ImageId, drawing.BoardMap!.ImageId);
                Assert.Equal(map.Cities, drawing.BoardMap.Cities);
                Assert.NotNull(await bridge.ReadBoardImageAsync(map.ImageId!, token));
            }
            finally { end.Invoke(model, null); }
            Assert.Equal(map.ImageId, (await bridge.ReadPublicAsync(token)).BoardMap!.ImageId);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    [Fact]
    public async Task Companion_city_coordinates_match_solo_alignment_on_the_exact_published_camera_frame()
    {
        var token = TestContext.Current.CancellationToken;
        var model = await StartCompanionMatchAsync();
        try
        {
            var bridge = GetCompanionBridge(model);
            var solo = DestinationBoardOverlay.BuildKept(TestManifest.Manifest, [new TicketId("t-denver--el-paso")]);
            var denver = Assert.Single(solo, city => city.CityId.Value == "denver");
            var targetX = (int)Math.Round(denver.CenterX) + 5;
            var targetY = (int)Math.Round(denver.CenterY) - 3;
            var frame = NewCompanionCityDotFrame(targetX, targetY);
            DestinationBoardOverlay.AlignToPreview(frame, solo);
            SetCompanionMapPreview(model, frame);
            await WaitUntilAsync(() => CompanionMapImage(model) is not null);
            var map = (await bridge.ReadPublicAsync(token)).BoardMap!;
            var published = Assert.Single(map.Cities!, city => city.Id == "denver");
            Assert.InRange(published.X, targetX - 1, targetX + 1);
            Assert.InRange(published.Y, targetY - 1, targetY + 1);
            Assert.Equal(denver.CenterX, published.X);
            Assert.Equal(denver.CenterY, published.Y);
            foreach (var city in solo)
            {
                var counterpart = Assert.Single(map.Cities!, candidate => candidate.Id == city.CityId.Value);
                Assert.Equal(city.CenterX, counterpart.X);
                Assert.Equal(city.CenterY, counterpart.Y);
            }
            var image = Assert.IsType<CompanionBoardImage>(await bridge.ReadBoardImageAsync(map.ImageId!, token));
            using var input = new MemoryStream(image.Jpeg);
            var decoded = new JpegBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var rgba = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[960 * 600 * 4];
            rgba.CopyPixels(pixels, 960 * 4, 0);
            var dot = (targetY * 960 + targetX) * 4;
            Assert.True(pixels[dot + 2] > pixels[dot + 1] + 60);
            Assert.True(pixels[dot + 1] > pixels[dot] + 20);
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static void SetCompanionMapPreview(MainViewModel model, BitmapSource? frame = null)
    {
        typeof(CameraViewModel).GetField("_gameTablePreviewRequested", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(model.Camera, true);
        model.Camera.IsRunning = true;
        model.Camera.IsGameTablePreviewUpright = true;
        model.Camera.GameTablePreview = frame ?? NewCompanionMapFrame();
    }

    private static BitmapSource NewCompanionMapFrame()
    {
        var pixels = new byte[960 * 600 * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 180;
            pixels[offset + 1] = 40;
            pixels[offset + 2] = 20;
            pixels[offset + 3] = 255;
        }
        var frame = BitmapSource.Create(960, 600, 96, 96, PixelFormats.Bgra32, null, pixels, 960 * 4);
        frame.Freeze();
        return frame;
    }

    private static CompanionBoardImage? CompanionMapImage(MainViewModel model) =>
        (CompanionBoardImage?)typeof(MainViewModel).GetField("_companionBoardImage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(model);

    private static BitmapSource NewCompanionCityDotFrame(int targetX, int targetY)
    {
        var pixels = new byte[960 * 600 * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 205;
            pixels[offset + 1] = 212;
            pixels[offset + 2] = 215;
            pixels[offset + 3] = 255;
        }
        for (var dy = -7; dy <= 7; dy++)
        for (var dx = -7; dx <= 7; dx++)
        {
            var squared = dx * dx + dy * dy;
            if (squared > 49) continue;
            var offset = ((targetY + dy) * 960 + targetX + dx) * 4;
            var inner = squared <= 36;
            pixels[offset] = inner ? (byte)48 : (byte)41;
            pixels[offset + 1] = inner ? (byte)97 : (byte)52;
            pixels[offset + 2] = inner ? (byte)198 : (byte)67;
        }
        var frame = BitmapSource.Create(960, 600, 96, 96, PixelFormats.Bgra32, null, pixels, 960 * 4);
        frame.Freeze();
        return frame;
    }
}
