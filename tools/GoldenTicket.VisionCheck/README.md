# Synthetic GPU preprocessing check

Run from the repository root on Windows 11:

```powershell
dotnet restore tools/GoldenTicket.VisionCheck/GoldenTicket.VisionCheck.csproj --locked-mode --configfile NuGet.Config
dotnet run --project tools/GoldenTicket.VisionCheck/GoldenTicket.VisionCheck.csproj --configuration Release --no-restore
```

The tool creates a synthetic 1920×1080 frame, processes it twice using Auto, then
processes it with CPU selected. It then compares both backends on an already
3840×2160 input, which must preserve that size without upscaling. It verifies preservation of
source metadata and original pixels, and a maximum CPU/GPU difference of two byte
levels per channel. Output is JSON; `passed: false` produces exit code 1. If no
hardware device passes its initialization check, the report explicitly says CPU
and provides the fallback reason. CPU fallback is a valid result on headless CI.

The tool never opens a camera, user save, or network listener. Reported times cover
preprocessing and GPU upload/readback, not capture, WPF rendering, piece detection,
or end-to-end frame rate. Upscaling does not prove improved recognition accuracy.

For reference, a 2026-09-12 run on an NVIDIA GeForce RTX 4080 Laptop GPU processed
the synthetic image in approximately 33 ms after warmup versus 136 ms on the CPU.
The greatest GPU/CPU channel difference was 1/255. Performance varies with hardware
and load; these are measured examples, not minimum hardware guarantees.
