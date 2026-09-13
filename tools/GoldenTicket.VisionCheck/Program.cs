using System.Text.Json;
using GoldenTicket.Vision;

// Uses synthetic pixels only: never opens a camera, network listener, or saved match.
var source = new byte[1920 * 1080 * 4];
for (var y = 0; y < 1080; y++)
for (var x = 0; x < 1920; x++)
{
    var i = (y * 1920 + x) * 4;
    var edge = x % 113 > 56 ? 30 : 0;
    source[i] = (byte)(80 + edge + x % 7);
    source[i + 1] = (byte)(110 + edge + y % 9);
    source[i + 2] = (byte)(140 + edge);
    source[i + 3] = 255;
}
var frame = CameraFrame.CopyFromBgra32(1920, 1080, source, sequence: 73, epoch: 5);
await using var processor = new FrameProcessor();
await processor.InitializeAsync();
var accelerated = await processor.ProcessAsync(frame);
var detected = accelerated.Status;
var second = await processor.ProcessAsync(frame);
await processor.InitializeAsync(FrameComputeMode.Cpu);
var cpu = await processor.ProcessAsync(frame);
var maximumDifference = 0;
long difference = 0;
for (var i = 0; i < cpu.Frame.Bgra32.Length; i++)
{
    var delta = Math.Abs(cpu.Frame.Bgra32.Span[i] - accelerated.Frame.Bgra32.Span[i]);
    difference += delta;
    maximumDifference = Math.Max(maximumDifference, delta);
}
await processor.InitializeAsync(FrameComputeMode.Auto);
var nativeFourK = await processor.ProcessAsync(cpu.Frame);
await processor.InitializeAsync(FrameComputeMode.Cpu);
var nativeFourKCpu = await processor.ProcessAsync(cpu.Frame);
var nativeMaximumDifference = 0;
for (var i = 0; i < nativeFourK.Frame.Bgra32.Length; i++)
    nativeMaximumDifference = Math.Max(nativeMaximumDifference,
        Math.Abs(nativeFourK.Frame.Bgra32.Span[i] - nativeFourKCpu.Frame.Bgra32.Span[i]));
var passed = cpu.Frame.Width == 3840 && cpu.Frame.Height == 2160 && maximumDifference <= 2 &&
    !nativeFourK.IsUpscaled && nativeMaximumDifference <= 2 &&
    nativeFourK.Frame.Width == 3840 && nativeFourK.Frame.Height == 2160 &&
    cpu.Frame.Sequence == 73 && cpu.Frame.Epoch == 5 &&
    cpu.Frame.MonotonicTimestamp == frame.MonotonicTimestamp && source.AsSpan().SequenceEqual(frame.Bgra32.Span);
Console.WriteLine(JsonSerializer.Serialize(new
{
    passed,
    backend = detected.Backend.ToString(),
    detected.AdapterName,
    detected.FallbackReason,
    source = "1920x1080",
    output = $"{cpu.Frame.Width}x{cpu.Frame.Height}",
    firstMilliseconds = accelerated.ProcessingTime.TotalMilliseconds,
    warmedMilliseconds = second.ProcessingTime.TotalMilliseconds,
    warmedBackend = second.Status.Backend.ToString(),
    cpuMilliseconds = cpu.ProcessingTime.TotalMilliseconds,
    nativeFourKMilliseconds = nativeFourK.ProcessingTime.TotalMilliseconds,
    nativeFourKBackend = nativeFourK.Status.Backend.ToString(),
    nativeFourKFallbackReason = nativeFourK.Status.FallbackReason,
    nativeFourKCpuMilliseconds = nativeFourKCpu.ProcessingTime.TotalMilliseconds,
    nativeMaximumDifference,
    maximumDifference,
    averageDifference = (double)difference / cpu.Frame.Bgra32.Length
}, new JsonSerializerOptions { WriteIndented = true }));
return passed ? 0 : 1;
