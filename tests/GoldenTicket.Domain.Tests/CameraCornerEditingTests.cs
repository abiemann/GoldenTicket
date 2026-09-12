using System.Diagnostics;
using System.Reflection;
using GoldenTicket.Desktop.ViewModels;
using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class CameraCornerEditingTests
{
    private static readonly NormalizedPoint[] Corners = [new(.05, .05), new(.95, .05), new(.95, .95), new(.05, .95)];

    [Fact]
    public async Task Partial_selection_moves_existing_corner_without_advancing_placement()
    {
        await using var fixture = new CaptureFixture();
        var camera = fixture.Camera;
        camera.AddBoardCorner(Corners[0]);
        camera.AddBoardCorner(Corners[1]);
        Assert.True(camera.MoveBoardCorner(0, new(.1, .15)));
        Assert.Equal(2, camera.SelectedCorners.Count);
        Assert.Equal(new NormalizedPoint(.1, .15), camera.SelectedCorners[0]);
        Assert.True(camera.SelectingCorners);
        Assert.False(camera.HasBoardCrop);
        Assert.Contains("Click 3", camera.CropText);
    }

    [Fact]
    public async Task Completed_crop_rebuilds_and_invalidates_old_geometry_before_notifying_view()
    {
        await using var fixture = new CaptureFixture();
        fixture.Complete();
        var camera = fixture.Camera;
        var oldRegistration = Read<BoardRegistration>(camera, "_registration");
        var oldPreview = camera.BoardPreview;
        var oldRevision = Read<long>(camera, "_cropRevision");
        var notified = false;
        camera.SelectedCorners.CollectionChanged += (_, _) =>
        {
            notified = true;
            Assert.Null(Read<BoardRegistration?>(camera, "_registration"));
            Assert.False(camera.HasBoardCrop);
            Assert.False(camera.CanCapturePhoto);
        };
        fixture.Refresh();
        Assert.True(camera.MoveBoardCorner(0, new(.15, .1)));
        Assert.True(notified);
        Assert.False(camera.SelectingCorners);
        Assert.True(camera.HasBoardCrop);
        Assert.NotSame(oldPreview, camera.BoardPreview);
        Assert.Equal(Corners[0], oldRegistration.Corners[0]);
        Assert.Equal(new NormalizedPoint(.15, .1), Read<BoardRegistration>(camera, "_registration").Corners[0]);
        Assert.True(Read<long>(camera, "_cropRevision") > oldRevision);
    }

    [Fact]
    public async Task Crossed_crop_retains_handles_and_scene_reference_and_can_be_repaired()
    {
        await using var fixture = new CaptureFixture();
        fixture.Complete();
        var camera = fixture.Camera;
        camera.SetReferenceCommand.Execute(null);
        Assert.Null(camera.Problem);
        var monitor = Read<SceneReferenceMonitor>(camera, "_monitor");
        var evidenceRevision = monitor.Current.EvidenceRevision;
        Assert.True(camera.MoveBoardCorner(0, new(.99, .99)));
        Assert.Equal(4, camera.SelectedCorners.Count);
        Assert.False(camera.HasBoardCrop);
        Assert.False(camera.CanCapturePhoto);
        Assert.Null(camera.BoardPreview);
        Assert.NotNull(camera.Problem);
        Assert.Equal(evidenceRevision, monitor.Current.EvidenceRevision);
        fixture.Refresh();
        Assert.True(camera.MoveBoardCorner(0, Corners[0]));
        Assert.True(camera.HasBoardCrop);
        Assert.Null(camera.Problem);
        Assert.Equal(evidenceRevision, monitor.Current.EvidenceRevision);
    }

    [Fact]
    public async Task Invalid_fourth_corner_can_be_repaired_without_reselecting_other_three()
    {
        await using var fixture = new CaptureFixture();
        foreach (var point in Corners.Take(3)) fixture.Camera.AddBoardCorner(point);
        fixture.Camera.AddBoardCorner(Corners[1]);
        Assert.Equal(4, fixture.Camera.SelectedCorners.Count);
        Assert.False(fixture.Camera.HasBoardCrop);
        fixture.Refresh();
        fixture.Camera.MoveBoardCorner(3, Corners[3]);
        Assert.Equal(Corners, fixture.Camera.SelectedCorners.ToArray());
        Assert.True(fixture.Camera.HasBoardCrop);
    }

    [Theory]
    [InlineData(double.NaN, .1)]
    [InlineData(.1, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, .1)]
    public async Task Nonfinite_edits_leave_the_valid_crop_unchanged(double x, double y)
    {
        await using var fixture = new CaptureFixture();
        fixture.Complete();
        var original = Read<BoardRegistration>(fixture.Camera, "_registration");
        Assert.False(fixture.Camera.MoveBoardCorner(0, new(x, y)));
        Assert.Same(original, Read<BoardRegistration>(fixture.Camera, "_registration"));
        Assert.Equal(Corners, fixture.Camera.SelectedCorners.ToArray());
    }

    [Fact]
    public async Task Drag_outside_image_clamps_to_edge_and_invalid_indices_are_ignored()
    {
        await using var fixture = new CaptureFixture();
        fixture.Complete();
        Assert.False(fixture.Camera.MoveBoardCorner(-1, new(0, 0)));
        Assert.False(fixture.Camera.MoveBoardCorner(4, new(0, 0)));
        Assert.True(fixture.Camera.MoveBoardCorner(0, new(-2, -3)));
        Assert.Equal(new NormalizedPoint(0, 0), fixture.Camera.SelectedCorners[0]);
        Assert.True(fixture.Camera.HasBoardCrop);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Camera_restart_or_format_change_clears_even_an_invalid_pending_crop(bool changeFormat)
    {
        await using var fixture = new CaptureFixture();
        fixture.Complete();
        fixture.Camera.MoveBoardCorner(0, new(.99, .99));
        fixture.Refresh(epoch: changeFormat ? 1 : 2, width: changeFormat ? 100 : 96);
        Assert.False(fixture.Camera.MoveBoardCorner(0, Corners[0]));
        Assert.Empty(fixture.Camera.SelectedCorners);
        Assert.False(fixture.Camera.HasBoardCrop);
        Assert.True(fixture.Camera.SafetyHeld);
        Assert.Contains("changed", fixture.Camera.Problem);
    }

    [Fact]
    public async Task Stale_frame_cannot_rebuild_crop_and_handles_remain_repairable()
    {
        await using var fixture = new CaptureFixture();
        fixture.Complete();
        fixture.Refresh(stale: true);
        fixture.Camera.MoveBoardCorner(0, new(.1, .1));
        Assert.Equal(4, fixture.Camera.SelectedCorners.Count);
        Assert.False(fixture.Camera.HasBoardCrop);
        Assert.Null(Read<BoardRegistration?>(fixture.Camera, "_registration"));
        Assert.NotNull(fixture.Camera.Problem);
        fixture.Refresh();
        fixture.Camera.MoveBoardCorner(0, new(.11, .1));
        Assert.True(fixture.Camera.HasBoardCrop);
    }

    [Fact]
    public async Task Busy_or_unchanged_edit_does_not_invalidate_photo_geometry()
    {
        await using var fixture = new CaptureFixture();
        fixture.Complete();
        var revision = Read<long>(fixture.Camera, "_cropRevision");
        Assert.False(fixture.Camera.MoveBoardCorner(0, Corners[0]));
        fixture.Camera.IsBusy = true;
        Assert.False(fixture.Camera.MoveBoardCorner(0, new(.1, .1)));
        Assert.True(fixture.Camera.HasBoardCrop);
        Assert.Equal(revision, Read<long>(fixture.Camera, "_cropRevision"));
    }

    private static T Read<T>(object target, string field) => (T)target.GetType()
        .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    // Seed owned synthetic frames without opening hardware or adding a production test bypass.
    private sealed class CaptureFixture : IAsyncDisposable
    {
        public CameraViewModel Camera { get; } = new();
        private long _sequence;

        public CaptureFixture()
        {
            Refresh();
            Camera.IsRunning = true;
            Camera.BeginCornerSelectionCommand.Execute(null);
            Assert.True(Camera.SelectingCorners);
        }

        public void Complete()
        {
            Refresh();
            foreach (var point in Corners) Camera.AddBoardCorner(point);
            Assert.True(Camera.HasBoardCrop, Camera.Problem);
        }

        public void Refresh(long epoch = 1, int width = 96, bool stale = false)
        {
            const int height = 60;
            var bytes = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                bytes[offset] = bytes[offset + 1] = bytes[offset + 2] = (byte)(((x / 4 + y / 4) % 2) * 220 + 20);
                bytes[offset + 3] = 255;
            }
            var frame = CameraFrame.CopyFromBgra32(width, height, bytes, ++_sequence, epoch);
            if (stale)
                frame = (CameraFrame)typeof(CameraFrame).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()
                    .Invoke([width, height, bytes, _sequence, epoch, DateTimeOffset.UtcNow.AddSeconds(-5), Stopwatch.GetTimestamp() - 5 * Stopwatch.Frequency]);
            Set("_epoch", epoch);
            Set("_running", true);
            Set("_latest", frame);
        }

        private void Set(string name, object value) => typeof(CameraCaptureService)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Camera.Capture, value);

        public ValueTask DisposeAsync() => Camera.DisposeAsync();
    }
}
