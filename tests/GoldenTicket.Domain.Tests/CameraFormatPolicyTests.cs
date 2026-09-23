using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class CameraFormatPolicyTests
{
    [Fact]
    public void Native_720p_preserves_existing_preference_values()
    {
        Assert.Equal(0, (int)CameraCapturePreference.Balanced1080p);
        Assert.Equal(1, (int)CameraCapturePreference.HighDetail2160p);
        Assert.Equal(2, (int)CameraCapturePreference.SharedCurrent);
        Assert.Equal(3, (int)CameraCapturePreference.Native720p);
    }

    [Fact]
    public void Native_720p_uses_only_advertised_exact_720p_formats()
    {
        CameraFormat[] formats =
        [
            new(1920, 1080, 30, "MJPG"), new(3840, 2160, 30, "MJPG"),
            new(640, 480, 30, "MJPG"), new(1280, 721, 30, "MJPG"),
            new(1281, 720, 30, "MJPG"), new(720, 1280, 30, "MJPG"),
            new(1280, 720, 30, "NV12")
        ];

        Assert.Equal(formats[6], Assert.Single(CameraFormatPolicy.RankFormats(formats, CameraCapturePreference.Native720p)));
    }

    [Fact]
    public void Native_720p_never_falls_back_when_no_usable_exact_format_is_advertised()
    {
        CameraFormat[] formats =
        [
            new(1920, 1080, 30, "MJPG"), new(640, 480, 30, "MJPG"),
            new(1280, 720, 4, "NV12"), new(1280, 720, 61, "NV12"),
            new(1280, 720, double.NaN, "NV12"), new(1280, 720, double.PositiveInfinity, "NV12")
        ];

        Assert.Empty(CameraFormatPolicy.RankFormats(formats, CameraCapturePreference.Native720p));
        Assert.Empty(CameraFormatPolicy.RankFormats([], CameraCapturePreference.Native720p));
    }

    [Fact]
    public void Native_720p_prefers_30fps_and_retains_native_frame_rate_and_subtype_fallbacks()
    {
        CameraFormat[] formats =
        [
            new(1280, 720, 60, "MJPG"), new(1280, 720, 15, "MJPG"),
            new(1280, 720, 29.97, "MJPG"), new(1280, 720, 30, "NV12"),
            new(1280, 720, 30, "MJPG"), new(1280, 720, 30, "NV12"),
            new(1280, 720, 5, "MJPG")
        ];

        var ranked = CameraFormatPolicy.RankFormats(formats, CameraCapturePreference.Native720p);

        Assert.Equal(new[] { formats[4], formats[3], formats[2], formats[1], formats[6], formats[0] }, ranked);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(1280, 721)]
    [InlineData(1281, 720)]
    [InlineData(640, 480)]
    [InlineData(720, 1280)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Native_720p_rejects_mismatched_negotiated_or_delivered_dimensions(int width, int height)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            CameraFormatPolicy.ValidateCapturedResolution(width, height, CameraCapturePreference.Native720p));

        Assert.Contains("720p option", error.Message);
        Assert.Contains("native 1280 × 720", error.Message);
        Assert.Contains($"camera provided {width} × {height}", error.Message);
    }

    [Fact]
    public void Native_720p_accepts_exact_negotiated_and_delivered_dimensions() =>
        CameraFormatPolicy.ValidateCapturedResolution(1280, 720, CameraCapturePreference.Native720p);

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
        Assert.DoesNotContain(formats[0], ranked);
        Assert.All(ranked, format => Assert.Contains(format, formats));
    }

    [Fact]
    public void Balanced_mode_caps_native_resolution_at_1080p()
    {
        CameraFormat[] formats = [new(3840, 2160, 15, "NV12"), new(2560, 1440, 15, "NV12"), new(1920, 1080, 30, "MJPG")];
        Assert.Equal(formats[2], Assert.Single(CameraFormatPolicy.RankFormats(formats, CameraCapturePreference.Balanced1080p)));
    }

    [Fact]
    public void Balanced_mode_prefers_exact_full_hd_at_30fps()
    {
        CameraFormat[] formats =
        [
            new(1440, 1440, 30, "NV12"),
            new(1920, 1080, 15, "NV12"),
            new(1920, 1080, 60, "NV12"),
            new(1920, 1080, 30, "NV12"),
            new(1600, 1200, 30, "NV12"),
        ];

        var ranked = CameraFormatPolicy.RankFormats(formats, CameraCapturePreference.Balanced1080p);

        Assert.Equal(formats[3], ranked[0]);
        Assert.All(ranked.Take(3), format => Assert.Equal((1920, 1080), (format.Width, format.Height)));
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

    [Theory]
    [InlineData(CameraCapturePreference.Balanced1080p)]
    [InlineData(CameraCapturePreference.HighDetail2160p)]
    [InlineData(CameraCapturePreference.Native720p)]
    public void A_720p_only_webcam_is_usable_but_cannot_fall_back_to_sub_hd(CameraCapturePreference preference)
    {
        CameraFormat[] formats = [new(640, 480, 30, "MJPG"), new(1280, 720, 30, "MJPG"), new(1024, 768, 30, "MJPG")];

        Assert.Equal(formats[1], Assert.Single(CameraFormatPolicy.RankFormats(formats, preference)));
        Assert.False(CameraFormatPolicy.Supports1080p(formats));
        Assert.False(CameraFormatPolicy.Supports4K(formats));
    }

    [Theory]
    [InlineData(640, 480)]
    [InlineData(1279, 1080)]
    [InlineData(1920, 719)]
    [InlineData(3840, 360)]
    [InlineData(720, 1280)]
    [InlineData(4096, 2160)]
    [InlineData(3840, 2161)]
    public void Both_native_dimensions_must_fit_supported_gameplay_limits(int width, int height)
    {
        var format = new CameraFormat(width, height, 30, "MJPG");

        Assert.Equal(CameraResolutionTier.Incompatible, CameraFormatPolicy.GetResolutionTier(width, height));
        Assert.False(CameraFormatPolicy.IsUsableFormat(format));
        Assert.Empty(CameraFormatPolicy.RankFormats([format], CameraCapturePreference.HighDetail2160p));
    }

    [Theory]
    [InlineData(1280, 720, CameraResolutionTier.Hd720p)]
    [InlineData(1600, 1200, CameraResolutionTier.Hd720p)]
    [InlineData(1920, 1079, CameraResolutionTier.Hd720p)]
    [InlineData(1919, 1080, CameraResolutionTier.Hd720p)]
    [InlineData(1920, 1080, CameraResolutionTier.FullHd1080p)]
    [InlineData(2560, 1440, CameraResolutionTier.FullHd1080p)]
    [InlineData(3840, 2159, CameraResolutionTier.FullHd1080p)]
    [InlineData(3840, 2160, CameraResolutionTier.UltraHd4K)]
    public void Resolution_tiers_require_width_and_height_instead_of_total_pixels(int width, int height,
        CameraResolutionTier expected)
    {
        Assert.Equal(expected, CameraFormatPolicy.GetResolutionTier(width, height));
        CameraFormatPolicy.ValidateCapturedResolution(width, height);
    }

    [Fact]
    public void Only_usable_native_4k_formats_enable_the_4k_option()
    {
        CameraFormat[] lowerResolution = [new(1920, 1080, 30, "MJPG"), new(2560, 1440, 30, "NV12")];
        Assert.False(CameraFormatPolicy.Supports4K(lowerResolution));
        Assert.True(CameraFormatPolicy.Supports1080p(lowerResolution));
        Assert.False(CameraFormatPolicy.Supports4K([.. lowerResolution, new(3840, 2160, 4, "MJPG")]));
        Assert.False(CameraFormatPolicy.Supports4K([.. lowerResolution, new(3840, 2160, 120, "MJPG")]));
        Assert.False(CameraFormatPolicy.Supports4K([.. lowerResolution, new(3840, 2160, double.NaN, "MJPG")]));
        Assert.True(CameraFormatPolicy.Supports4K([.. lowerResolution, new(3840, 2160, 5, "MJPG")]));
        Assert.True(CameraFormatPolicy.Supports4K([.. lowerResolution, new(3840, 2160, 60, "MJPG")]));
    }

    [Fact]
    public void An_advertised_but_unusable_full_hd_format_does_not_hide_the_720p_warning()
    {
        CameraFormat[] formats = [new(1280, 720, 30, "MJPG"), new(1920, 1080, 0, "NV12")];

        Assert.False(CameraFormatPolicy.Supports1080p(formats));
        Assert.False(CameraFormatPolicy.Supports1080p([]));
        Assert.True(CameraFormatPolicy.Supports1080p([new(3840, 2160, 30, "MJPG")]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(61)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void Shared_current_format_must_also_have_a_usable_frame_rate(double framesPerSecond) =>
        Assert.False(CameraFormatPolicy.IsUsableFormat(new(1920, 1080, framesPerSecond, "NV12")));

    [Fact]
    public void Unexpected_sub_hd_delivered_frames_are_rejected_with_a_compatibility_message()
    {
        var error = Assert.Throws<InvalidOperationException>(() => CameraFormatPolicy.ValidateCapturedResolution(640, 480));

        Assert.Equal(CameraFormatPolicy.MinimumResolutionMessage, error.Message);
        Assert.Contains("720p", error.Message);
        Assert.Contains("1080p", error.Message);
    }

    [Fact]
    public void Delivered_frames_larger_than_4k_are_rejected_before_allocating_the_copy()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            CameraFormatPolicy.ValidateCapturedResolution(int.MaxValue, int.MaxValue));

        Assert.Contains("3840 × 2160", error.Message);
    }
}
