using GoldenTicket.Testing;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class CameraGameTablePhotoTests
{
    private static readonly NormalizedPoint[] Corners =
        [new(.05, .05), new(.95, .05), new(.95, .95), new(.05, .95)];

    [Fact]
    public async Task Accepted_game_table_crop_captures_board_without_technical_crop_or_scene_reference()
    {
        await using var fixture = new Fixture();
        fixture.AcceptGameTable();
        Assert.False(fixture.Camera.HasBoardCrop);
        Assert.False(fixture.Camera.CanCapturePhoto);
        Assert.True(fixture.Camera.SafetyHeld);
        Assert.True(fixture.Camera.CanCaptureGameTablePhoto);

        var frame = fixture.Camera.Capture.LatestFrame!;
        var photo = await fixture.Camera.CaptureGameTablePhotoAsync(TestContext.Current.CancellationToken);
        try
        {
            using var stream = new MemoryStream(photo.PngBytes, writable: false);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.Equal(3456, decoder.Frames[0].PixelWidth);
            Assert.Equal(2160, decoder.Frames[0].PixelHeight);
            Assert.Equal(frame.Sequence, photo.FrameSequence);
            Assert.Equal(frame.Epoch, photo.CameraEpoch);
            Assert.Equal(frame.CapturedAt, photo.CapturedAt);
            Assert.Equal("game-table-test-camera", photo.CameraId);
            Assert.Equal(7, photo.BoardCropRevision);
            Assert.True(photo.BoardCropped);
        }
        finally { CryptographicOperations.ZeroMemory(photo.PngBytes); }
    }

    [Fact]
    public async Task Invalidated_or_replaced_game_table_crop_cannot_capture()
    {
        await using var fixture = new Fixture();
        fixture.AcceptGameTable();
        fixture.Camera.EndGameTablePreview();
        Assert.False(fixture.Camera.CanCaptureGameTablePhoto);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Camera.CaptureGameTablePhotoAsync(TestContext.Current.CancellationToken));

        fixture.AcceptGameTable();
        fixture.SetCameraField("_gameTableCropRevision", 8L);
        Assert.False(fixture.Camera.CanCaptureGameTablePhoto);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Camera.CaptureGameTablePhotoAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_or_restarted_camera_rejects_prior_game_table_analysis(bool restart)
    {
        await using var fixture = new Fixture();
        fixture.AcceptGameTable();
        fixture.Refresh(stale: !restart, epoch: restart ? 2 : 1);
        Assert.False(fixture.Camera.CanCaptureGameTablePhoto);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Camera.CaptureGameTablePhotoAsync(TestContext.Current.CancellationToken));
    }

    // Synthetic owned frames only. These tests never open the physical webcam.
    private sealed class Fixture : IAsyncDisposable
    {
        private long _sequence;
        public CameraViewModel Camera { get; } = new(capture: new FakeCameraCapture());
        public FakeCameraCapture Capture => (FakeCameraCapture)Camera.Capture;

        public Fixture()
        {
            Capture.ActiveDevice = new CameraDevice("game-table-test-camera", "Synthetic board camera");
            Refresh();
            Camera.IsRunning = true;
        }

        public void AcceptGameTable()
        {
            Refresh();
            var frame = Camera.Capture.LatestFrame!;
            var registration = BoardRegistration.Create(frame, Corners);
            SetCameraField("_gameTableRegistration", registration);
            SetCameraField("_gameTableCropRevision", 7L);
            Camera.IsGameTablePreviewUpright = true;
            var board = registration.Rectify(frame, LearnedPieceDetector.BoardWidth, LearnedPieceDetector.BoardHeight);
            typeof(CameraViewModel).GetProperty(nameof(CameraViewModel.GameTableAnalysis))!
                .SetValue(Camera, new GameTableAnalysis(board, [], [], 7, 0, "synthetic-test-model"));
        }

        public void Refresh(bool stale = false, long epoch = 1)
        {
            const int width = 96, height = 60;
            var bytes = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                bytes[offset] = (byte)(x * 2);
                bytes[offset + 1] = (byte)(y * 3);
                bytes[offset + 2] = 180;
                bytes[offset + 3] = 255;
            }
            var frame = CameraFrame.CopyFromBgra32(width, height, bytes, ++_sequence, epoch);
            if (stale)
                frame = (CameraFrame)typeof(CameraFrame).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()
                    .Invoke([width, height, bytes, _sequence, epoch, DateTimeOffset.UtcNow.AddSeconds(-5),
                        Stopwatch.GetTimestamp() - 5 * Stopwatch.Frequency]);
            Capture.Epoch = epoch;
            Capture.IsRunning = true;
            Capture.LatestFrame = frame;
        }

        public void SetCameraField(string name, object value) => typeof(CameraViewModel)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Camera, value);

        public ValueTask DisposeAsync() => Camera.DisposeAsync();
    }
}
