using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Vortice.DXGI;

namespace GoldenTicket.Vision;

/// <summary>Shared process-wide native runtime selection for the local learned detectors.</summary>
internal static class LocalOnnxRuntime
{
    private static readonly object NativeRuntimeGate = new();
    // The native provider retains this library for the process lifetime. Never release it while an
    // ORT environment or another detector may still own a DirectML device.
    private static nint _directMlLibrary;
    private const string DirectMlSha256 = "9c9e6d822561c6c41b90e6994b3e8857cf1d66dbfb1e0c4c799c7c89b4e92da1";

    internal static (int Index, string Name) PreferredAdapter()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var choices = new List<(int Index, string Name, ulong Memory)>();
        for (uint index = 0; index < 32 && factory.EnumAdapters1(index, out var adapter).Success; index++)
        {
            using (adapter)
            {
                var description = adapter.Description1;
                if (!D3D11FrameBackend.IsSoftwareAdapter(description))
                    choices.Add(((int)index, description.Description.TrimEnd('\0').Trim(), description.DedicatedVideoMemory));
            }
        }
        var selected = choices.OrderByDescending(item => item.Memory).FirstOrDefault();
        if (selected.Name is null) throw new NotSupportedException("No hardware graphics adapter is available.");
        return (selected.Index, selected.Name);
    }

    internal static bool IsProviderFailure(Exception error) => error is OnnxRuntimeException or
        DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException or
        System.Runtime.InteropServices.ExternalException or TypeInitializationException;

    internal static void LoadLocalDirectMl()
    {
        lock (NativeRuntimeGate)
        {
            if (_directMlLibrary != 0) return;
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "DirectML.dll"));
            if (!File.Exists(path)) throw new FileNotFoundException("The app-local DirectML.dll is missing.", path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(DirectMlSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("DirectML.dll does not match the pinned 1.15.4 x64 runtime.");
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
                if (module.ModuleName.Equals("DirectML.dll", StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetFullPath(module.FileName).Equals(path, StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("A different DirectML runtime is already loaded. Restart the app to use its bundled runtime.");
            // Absolute loading is essential for dotnet-hosted tools: ORT lives in runtimes/win-x64/native,
            // so Windows' ordinary dependency search otherwise finds System32 instead of this local DLL.
            _directMlLibrary = NativeLibrary.Load(path);
        }
    }

    internal static string Brief(Exception error) => error.Message.Length <= 240 ? error.Message : error.Message[..240];

}
