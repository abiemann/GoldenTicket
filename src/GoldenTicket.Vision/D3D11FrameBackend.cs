using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GoldenTicket.Vision;

/// <summary>Hardware-only Direct3D11 compute. No WARP/reference/software device is ever requested.</summary>
internal sealed class D3D11FrameBackend : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private ID3D11ComputeShader? _filter;
    private ID3D11ComputeShader? _resize;
    private ID3D11Buffer? _constants;
    private ID3D11Buffer? _source;
    private ID3D11Buffer? _filtered;
    private ID3D11Buffer? _output;
    private ID3D11Buffer? _staging;
    private ID3D11ShaderResourceView? _sourceView;
    private ID3D11ShaderResourceView? _filteredView;
    private ID3D11UnorderedAccessView? _filterTarget;
    private ID3D11UnorderedAccessView? _outputTarget;
    private (int SourceWidth, int SourceHeight, int Width, int Height) _size;
    private bool _disposed;
    public string AdapterName { get; }

    private D3D11FrameBackend(IDXGIAdapter1 adapter, CancellationToken token)
    {
        AdapterName = adapter.Description1.Description.TrimEnd('\0').Trim();
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None,
            [FeatureLevel.Level_11_0], out _device, out _context).CheckError();
        try
        {
            token.ThrowIfCancellationRequested();
            _filter = _device.CreateComputeShader(Compiler.Compile(Shader, "Filter", "GoldenTicket preprocessing",
                "cs_5_0", ShaderFlags.OptimizationLevel3).Span);
            token.ThrowIfCancellationRequested();
            _resize = _device.CreateComputeShader(Compiler.Compile(Shader, "Resize", "GoldenTicket preprocessing",
                "cs_5_0", ShaderFlags.OptimizationLevel3).Span);
            token.ThrowIfCancellationRequested();
            _constants = _device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer));
        }
        catch { Dispose(); throw; }
    }

    internal static D3D11FrameBackend CreateValidated(CancellationToken token)
    {
        // Cooperative overall budget. An individual operating-system driver call cannot be interrupted.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        var probeToken = budget.Token;
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var adapters = new List<IDXGIAdapter1>();
        Exception? lastError = null;
        try
        {
            for (uint index = 0; index < 32 && factory.EnumAdapters1(index, out var adapter).Success; index++)
            {
                if (probeToken.IsCancellationRequested) { adapter.Dispose(); probeToken.ThrowIfCancellationRequested(); }
                if ((adapter.Description1.Flags & AdapterFlags.Software) != 0) adapter.Dispose();
                else adapters.Add(adapter);
            }
            foreach (var adapter in adapters.OrderByDescending(candidate => candidate.Description1.DedicatedVideoMemory))
            {
                probeToken.ThrowIfCancellationRequested();
                D3D11FrameBackend? backend = null;
                try
                {
                    backend = new(adapter, probeToken);
                    var bytes = new byte[17 * 11 * 4];
                    for (var pixel = 0; pixel < bytes.Length / 4; pixel++)
                    {
                        bytes[pixel * 4] = (byte)((pixel * 17) % 256);
                        bytes[pixel * 4 + 1] = (byte)((pixel * 37) % 256);
                        bytes[pixel * 4 + 2] = (byte)((pixel * 53) % 256);
                        bytes[pixel * 4 + 3] = 255;
                    }
                    var frame = CameraFrame.CopyFromBgra32(17, 11, bytes);
                    var actual = backend.Process(frame, 31, 19, probeToken);
                    var expected = FrameProcessingKernel.Process(frame, 31, 19, probeToken);
                    for (var index = 0; index < expected.Length; index++)
                        if (Math.Abs(expected[index] - actual[index]) > 2)
                            throw new InvalidOperationException($"GPU image validation failed at {index}: {actual[index]} versus {expected[index]}.");
                    return backend;
                }
                catch (Exception error) when (FrameProcessor.IsGpuFailure(error))
                {
                    lastError = error;
                    backend?.Dispose();
                }
                catch { backend?.Dispose(); throw; }
            }
            throw new NotSupportedException("No hardware adapter passed the image processing check.", lastError);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Hardware image processing validation exceeded its ten-second budget.");
        }
        finally { foreach (var adapter in adapters) adapter.Dispose(); }
    }

    internal byte[] Process(CameraFrame frame, int width, int height, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureBuffers(frame.Width, frame.Height, width, height);
        _context.UpdateSubresource(frame.Bgra32.Span, _source!);
        var dimensions = new Dimensions((uint)frame.Width, (uint)frame.Height, (uint)width, (uint)height);
        _context.UpdateSubresource(in dimensions, _constants!);
        _context.CSSetConstantBuffer(0, _constants);
        try
        {
            _context.CSSetShader(_filter);
            _context.CSSetShaderResource(0, _sourceView);
            _context.CSSetUnorderedAccessView(0, _filterTarget);
            _context.Dispatch((uint)(frame.Width + 15) / 16, (uint)(frame.Height + 15) / 16, 1);
            _context.CSSetUnorderedAccessView(0, null);
            _context.CSSetShaderResource(0, null);
            if (frame.Width == width && frame.Height == height) _context.CopyResource(_staging!, _filtered!);
            else
            {
                _context.CSSetShader(_resize);
                _context.CSSetShaderResource(0, _filteredView);
                _context.CSSetUnorderedAccessView(0, _outputTarget);
                _context.Dispatch((uint)(width + 15) / 16, (uint)(height + 15) / 16, 1);
                _context.CSSetUnorderedAccessView(0, null);
                _context.CSSetShaderResource(0, null);
                _context.CopyResource(_staging!, _output!);
            }
            _context.Flush();
            var started = Stopwatch.GetTimestamp();
            MappedSubresource mapped;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var result = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out mapped);
                if (result.Success) break;
                // DXGI_ERROR_WAS_STILL_DRAWING. Other failures, including device loss, fall back to CPU.
                if (result.Code != unchecked((int)0x887A000A)) result.CheckError();
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(3))
                    throw new TimeoutException("The GPU did not finish the image within three seconds.");
                Thread.Sleep(1);
            }
            try
            {
                var output = new byte[checked(width * height * 4)];
                Marshal.Copy(mapped.DataPointer, output, 0, output.Length);
                return output;
            }
            finally { _context.Unmap(_staging!, 0); }
        }
        finally
        {
            _context.CSSetUnorderedAccessView(0, null);
            _context.CSSetShaderResource(0, null);
            _context.CSSetConstantBuffer(0, null);
            _context.CSSetShader(null);
        }
    }

    private void EnsureBuffers(int sourceWidth, int sourceHeight, int width, int height)
    {
        var size = (sourceWidth, sourceHeight, width, height);
        if (size == _size) return;
        ReleaseBuffers();
        try
        {
            var sourceBytes = checked((uint)(sourceWidth * sourceHeight * 4));
            var outputBytes = checked((uint)(width * height * 4));
            _source = Structured(sourceBytes, BindFlags.ShaderResource);
            _filtered = Structured(sourceBytes, BindFlags.ShaderResource | BindFlags.UnorderedAccess);
            _output = Structured(outputBytes, BindFlags.UnorderedAccess);
            _staging = _device.CreateBuffer(new BufferDescription(outputBytes, BindFlags.None,
                ResourceUsage.Staging, CpuAccessFlags.Read));
            _sourceView = _device.CreateShaderResourceView(_source);
            _filteredView = _device.CreateShaderResourceView(_filtered);
            _filterTarget = _device.CreateUnorderedAccessView(_filtered);
            _outputTarget = _device.CreateUnorderedAccessView(_output);
            _size = size;
        }
        catch { ReleaseBuffers(); throw; }
    }

    private ID3D11Buffer Structured(uint bytes, BindFlags bindings) => _device.CreateBuffer(
        new BufferDescription(bytes, bindings, ResourceUsage.Default, CpuAccessFlags.None,
            ResourceOptionFlags.BufferStructured, 4));

    private void ReleaseBuffers()
    {
        _sourceView?.Dispose(); _sourceView = null;
        _filteredView?.Dispose(); _filteredView = null;
        _filterTarget?.Dispose(); _filterTarget = null;
        _outputTarget?.Dispose(); _outputTarget = null;
        _source?.Dispose(); _source = null;
        _filtered?.Dispose(); _filtered = null;
        _output?.Dispose(); _output = null;
        _staging?.Dispose(); _staging = null;
        _size = default;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // A removed device must not prevent the caller from enabling CPU processing.
        try { _context.ClearState(); }
        catch (Exception error) when (FrameProcessor.IsGpuFailure(error)) { }
        ReleaseBuffers();
        _constants?.Dispose(); _constants = null;
        _filter?.Dispose(); _filter = null;
        _resize?.Dispose(); _resize = null;
        _context.Dispose();
        _device.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Dimensions(uint SourceWidth, uint SourceHeight, uint Width, uint Height);

    private const string Shader = """
        StructuredBuffer<uint> Source : register(t0);
        RWStructuredBuffer<uint> Target : register(u0);
        cbuffer Dimensions : register(b0) { uint SourceWidth; uint SourceHeight; uint Width; uint Height; };
        float3 Read(int2 pixelPosition) {
            pixelPosition = clamp(pixelPosition, int2(0, 0), int2(SourceWidth - 1, SourceHeight - 1));
            uint value = Source[pixelPosition.y * SourceWidth + pixelPosition.x];
            return float3(value & 255, (value >> 8) & 255, (value >> 16) & 255);
        }
        uint Pack(float3 color) {
            uint3 value = (uint3)floor(clamp(color, 0, 255) + 0.5);
            return value.x | (value.y << 8) | (value.z << 16) | 0xff000000;
        }
        [numthreads(16,16,1)] void Filter(uint3 pixelPosition : SV_DispatchThreadID) {
            if (pixelPosition.x >= SourceWidth || pixelPosition.y >= SourceHeight) return;
            float3 center = Read(pixelPosition.xy);
            float3 blur = 0;
            [unroll] for (int dy = -1; dy <= 1; dy++)
            [unroll] for (int dx = -1; dx <= 1; dx++)
                blur += Read(int2(pixelPosition.xy) + int2(dx,dy)) * ((dx == 0 ? 2 : 1) * (dy == 0 ? 2 : 1));
            float detail = dot(center - blur / 16, float3(0.114, 0.587, 0.299));
            float adjustment = abs(detail) < 3 ? -0.35 * detail : clamp(0.35 * detail, -8, 8);
            Target[pixelPosition.y * SourceWidth + pixelPosition.x] = Pack(center + adjustment);
        }
        float Cubic(float distance) {
            float x = abs(distance);
            return x <= 1 ? (1.5 * x - 2.5) * x * x + 1 :
                x < 2 ? ((-0.5 * x + 2.5) * x - 4) * x + 2 : 0;
        }
        [numthreads(16,16,1)] void Resize(uint3 pixelPosition : SV_DispatchThreadID) {
            if (pixelPosition.x >= Width || pixelPosition.y >= Height) return;
            float2 position = (float2(pixelPosition.xy) + 0.5) * float2(SourceWidth, SourceHeight) / float2(Width, Height) - 0.5;
            // Keep CPU and GPU neighborhoods equal at mathematically exact pixel centers.
            position.x = abs(position.x - round(position.x)) < 0.0001 ? round(position.x) : position.x;
            position.y = abs(position.y - round(position.y)) < 0.0001 ? round(position.y) : position.y;
            int2 basePoint = (int2)floor(position);
            float3 color = 0;
            float3 minimum = 255;
            float3 maximum = 0;
            [unroll] for (int dy = -1; dy <= 2; dy++)
            [unroll] for (int dx = -1; dx <= 2; dx++) {
                float3 colorSample = Read(basePoint + int2(dx, dy));
                color += colorSample * (Cubic(position.x - (basePoint.x + dx)) * Cubic(position.y - (basePoint.y + dy)));
                if (dx >= 0 && dx <= 1 && dy >= 0 && dy <= 1) {
                    minimum = min(minimum, colorSample);
                    maximum = max(maximum, colorSample);
                }
            }
            Target[pixelPosition.y * Width + pixelPosition.x] = Pack(clamp(color, minimum, maximum));
        }
        """;
}
