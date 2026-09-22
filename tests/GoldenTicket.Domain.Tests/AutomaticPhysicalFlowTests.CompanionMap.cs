using System.IO;
using System.Reflection;
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
    public async Task Companion_map_remains_through_computer_scoring_then_clears_for_the_human()
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
            Assert.Null((await bridge.ReadPublicAsync(token)).BoardMap);
            if (scoring.ImageId is not null) Assert.Null(await bridge.ReadBoardImageAsync(scoring.ImageId, token));
        }
        finally { await model.DisposeToolsAsync(); }
    }

    private static void SetCompanionMapPreview(MainViewModel model)
    {
        typeof(CameraViewModel).GetField("_gameTablePreviewRequested", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(model.Camera, true);
        model.Camera.IsRunning = true;
        model.Camera.IsGameTablePreviewUpright = true;
        model.Camera.GameTablePreview = NewCompanionMapFrame();
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
}
