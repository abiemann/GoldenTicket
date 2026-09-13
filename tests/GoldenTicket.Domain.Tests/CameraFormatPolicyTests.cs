using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class CameraFormatPolicyTests
{
    [Fact]
    public void High_detail_prefers_native_4k_even_when_preview_1080p_is_listed_first()
    {
        CameraFormat[] formats = [new(1920, 1080, 15, "NV12"), new(3840, 2160, 30, "MJPG"), new(1280, 720, 30, "NV12")];
        var ranked = CameraFormatPolicy.RankFormats(formats, CameraCapturePreference.HighDetail2160p);
        Assert.Equal(formats[1], ranked[0]);
        Assert.Equal(formats[0], ranked[1]);
        Assert.Equal(formats[2], ranked[2]);
    }

    [Fact]
    public void High_detail_on_1080p_phone_uses_advertised_1080p_without_inventing_4k()
    {
        CameraFormat[] formats = [new(640, 480, 30, "YUY2"), new(1920, 1080, 30, "MJPG"), new(1280, 720, 30, "MJPG")];
        var ranked = CameraFormatPolicy.RankFormats(formats, CameraCapturePreference.HighDetail2160p);
        Assert.Equal(formats[1], ranked[0]);
        Assert.All(ranked, format => Assert.Contains(format, formats));
    }

    [Fact]
    public void Balanced_mode_caps_native_resolution_at_1080p()
    {
        CameraFormat[] formats = [new(3840, 2160, 15, "NV12"), new(2560, 1440, 15, "NV12"), new(1920, 1080, 30, "MJPG")];
        Assert.Equal(formats[2], Assert.Single(CameraFormatPolicy.RankFormats(formats, CameraCapturePreference.Balanced1080p)));
    }

    [Fact]
    public void Equal_resolution_prefers_15fps_but_preserves_native_format_fallbacks()
    {
        CameraFormat[] formats = [new(3840, 2160, 60, "MJPG"), new(3840, 2160, 30, "MJPG"), new(3840, 2160, 15, "NV12"),
            new(3840, 2160, 15, "MJPG"), new(3840, 2160, 15, "NV12")];
        var ranked = CameraFormatPolicy.RankFormats(formats, CameraCapturePreference.HighDetail2160p);
        Assert.Equal(4, ranked.Count);
        Assert.Equal(15, ranked[0].FramesPerSecond);
        Assert.Equal(15, ranked[1].FramesPerSecond);
        Assert.Equal(30, ranked[2].FramesPerSecond);
        Assert.Equal(60, ranked[3].FramesPerSecond);
        Assert.NotEqual(ranked[0].Subtype, ranked[1].Subtype);
    }

    [Fact]
    public void Filters_invalid_or_unsupported_dimensions_and_frame_rates()
    {
        CameraFormat[] formats = [new(0, 1080, 30, "NV12"), new(-1, 1080, 30, "NV12"), new(1920, 0, 30, "NV12"),
            new(4096, 2048, 30, "MJPG"), new(2160, 3840, 30, "MJPG"), new(int.MaxValue, int.MaxValue, 30, "MJPG"),
            new(1920, 1080, 0, "NV12"), new(1920, 1080, 4, "NV12"), new(1920, 1080, 120, "NV12"),
            new(1920, 1080, double.NaN, "NV12"), new(1920, 1080, double.PositiveInfinity, "NV12")];
        Assert.Empty(CameraFormatPolicy.RankFormats(formats, CameraCapturePreference.HighDetail2160p));
    }

    [Theory]
    [InlineData(CameraCapturePreference.SharedCurrent)]
    [InlineData((CameraCapturePreference)999)]
    public void Format_policy_does_not_negotiate_shared_current_mode(CameraCapturePreference preference) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CameraFormatPolicy.RankFormats([], preference));

    [Fact]
    public void Five_fps_native_mode_remains_available_for_board_capture() =>
        Assert.Single(CameraFormatPolicy.RankFormats([new(3840, 2160, 5, "MJPG")], CameraCapturePreference.HighDetail2160p));
}
