using GoldenTicket.Vision;

namespace GoldenTicket.Domain.Tests;

public sealed class FrameProcessorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1920, 1080, 3840, 2160)]
    [InlineData(3840, 2160, 3840, 2160)]
    [InlineData(1920, 1200, 3456, 2160)]
    [InlineData(3456, 2160, 3456, 2160)]
    [InlineData(640, 480, 2880, 2160)]
    [InlineData(1080, 1920, 1215, 2160)]
    public void Four_k_output_preserves_aspect_ratio(int width, int height, int outputWidth, int outputHeight) =>
        Assert.Equal((outputWidth, outputHeight), FrameProcessor.GetOutputSize(width, height));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(3841, 2160)]
    [InlineData(3840, 2161)]
    public void Output_size_rejects_invalid_input_before_allocating(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameProcessor.GetOutputSize(width, height));

    [Fact]
    public void Constant_colors_remain_exact_and_opaque_through_noninteger_upscale()
    {
        var frame = Solid(17, 11, 43, 97, 171);
        var output = FrameProcessingKernel.Process(frame, 31, 19, Token);
        for (var i = 0; i < output.Length; i += 4)
            Assert.Equal(new byte[] { 43, 97, 171, 255 }, output.AsSpan(i, 4).ToArray());
    }

    [Fact]
    public void Mild_noise_is_reduced_without_changing_flat_color_baseline()
    {
        var input = Solid(9, 9, 128, 128, 128).Bgra32.ToArray();
        var middle = (4 * 9 + 4) * 4;
        input[middle] = input[middle + 1] = input[middle + 2] = 131;
        var output = FrameProcessingKernel.Process(CameraFrame.CopyFromBgra32(9, 9, input), 9, 9, Token);
        Assert.InRange(output[middle], (byte)129, (byte)130);
        Assert.Equal(128, output[0]);
    }

    [Fact]
    public void Sharpening_is_capped_and_does_not_wrap_bright_or_dark_pixels()
    {
        var input = Solid(9, 9, 255, 255, 255).Bgra32.ToArray();
        for (var y = 0; y < 9; y++)
        for (var x = 0; x < 4; x++)
        {
            var index = (y * 9 + x) * 4;
            input[index] = input[index + 1] = input[index + 2] = 0;
        }
        var output = FrameProcessingKernel.Process(CameraFrame.CopyFromBgra32(9, 9, input), 18, 18, Token);
        Assert.Equal(0, output[(8 * 18 + 2) * 4]);
        Assert.Equal(255, output[(8 * 18 + 15) * 4]);
        Assert.All(Enumerable.Range(0, output.Length / 4), i => Assert.Equal(255, output[i * 4 + 3]));
    }

    [Fact]
    public async Task Cpu_processing_preserves_source_evidence_identity_and_reports_upscale()
    {
        await using var processor = new FrameProcessor();
        Assert.Equal(FrameProcessingBackend.Cpu, (await processor.InitializeAsync(FrameComputeMode.Cpu, Token)).Backend);
        var frame = Solid(1920, 1, 80, 120, 160);
        var original = frame.Bgra32.ToArray();
        var result = await processor.ProcessAsync(frame, Token);
        Assert.Equal((3840, 2), (result.Frame.Width, result.Frame.Height));
        Assert.True(result.IsUpscaled);
        Assert.Equal(frame.Sequence, result.Frame.Sequence);
        Assert.Equal(frame.Epoch, result.Frame.Epoch);
        Assert.Equal(frame.CapturedAt, result.Frame.CapturedAt);
        Assert.Equal(frame.MonotonicTimestamp, result.Frame.MonotonicTimestamp);
        Assert.True(frame.Bgra32.Span.SequenceEqual(original));
        Assert.Null(result.Status.FallbackReason);
    }

    [Fact]
    public async Task Cancellation_does_not_publish_a_processed_frame_and_service_can_continue()
    {
        await using var processor = new FrameProcessor();
        await processor.InitializeAsync(FrameComputeMode.Cpu, Token);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessAsync(Solid(1920, 1, 0, 0, 0), canceled.Token));
        var result = await processor.ProcessAsync(Solid(1920, 1, 0, 0, 0), Token);
        Assert.Equal(3840, result.Frame.Width);
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_rejects_processing_and_reinitialization()
    {
        var processor = new FrameProcessor();
        await processor.InitializeAsync(FrameComputeMode.Cpu, Token);
        await processor.DisposeAsync();
        await processor.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => processor.ProcessAsync(Solid(1, 1, 0, 0, 0), Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => processor.InitializeAsync(FrameComputeMode.Auto, Token));
    }

    [Fact]
    public async Task Auto_reports_only_validated_hardware_or_an_explicit_cpu_fallback_and_can_switch_to_cpu()
    {
        await using var processor = new FrameProcessor();
        var status = await processor.InitializeAsync(FrameComputeMode.Auto, Token);
        Assert.Equal(FrameComputeMode.Auto, status.RequestedMode);
        if (status.Backend == FrameProcessingBackend.Gpu)
        {
            Assert.NotEqual("CPU", status.AdapterName);
            Assert.DoesNotContain("Microsoft Basic Render", status.AdapterName);
            Assert.Null(status.FallbackReason);
            var frame = Solid(1920, 1, 41, 83, 127);
            var actual = await processor.ProcessAsync(frame, Token);
            var expected = FrameProcessingKernel.Process(frame, 3840, 2, Token);
            Assert.Equal(expected, actual.Frame.Bgra32.ToArray());
        }
        else Assert.NotEmpty(status.FallbackReason!);
        var cpu = await processor.InitializeAsync(FrameComputeMode.Cpu, Token);
        Assert.Equal(FrameProcessingBackend.Cpu, cpu.Backend);
        Assert.Null(cpu.FallbackReason);
    }

    [Fact]
    public async Task Invalid_compute_mode_is_rejected()
    {
        await using var processor = new FrameProcessor();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => processor.InitializeAsync((FrameComputeMode)99, Token));
    }

    private static CameraFrame Solid(int width, int height, byte blue, byte green, byte red)
    {
        var bytes = new byte[width * height * 4];
        for (var i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = blue; bytes[i + 1] = green; bytes[i + 2] = red; bytes[i + 3] = 255;
        }
        return CameraFrame.CopyFromBgra32(width, height, bytes, sequence: 81, epoch: 7);
    }
}
