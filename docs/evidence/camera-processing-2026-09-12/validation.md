# Camera processing validation, September 12, 2026

This record covers the CPU/GPU preprocessing, native-format selection, camera UI and experimental
empty-board comparison implementation described in [camera processing](../../camera-processing.md).
Checks ran on Windows 11 using .NET SDK 10.0.401 and the local Windows user profile. It is a source
validation record, not a release or full physical-recognition acceptance report. User photographs
and annotated copies are not committed with this record.

## Build and automated checks

| Check | Result |
|---|---|
| Locked solution restore | Passed |
| Release build | Passed; final incremental build: 0 warnings, 0 errors |
| Full automated .NET test run | 546 passed, 0 failed, 0 skipped |
| Production WPF views rendered with synthetic fixtures | 50 cases; 0 binding warnings/errors |
| NuGet advisory audit, including transitive solution dependencies | Passed; no known vulnerabilities reported by the configured NuGet feed |

Existing xUnit analyzer warnings appear when the affected test projects are recompiled. The
zero-warning result above describes the final incremental build, not a claim that a clean test
recompilation is warning-free. The UI runner uses an isolated STA dispatcher and synthetic data;
it does not operate the user's camera, native dialogs or physical input.
The 546-test count includes the regression for expiring published outlines after two seconds
even while the camera itself continues delivering fresh frames.

Reproduce from the repository root on Windows:

```powershell
dotnet restore GoldenTicket.sln --locked-mode --configfile NuGet.config
dotnet build GoldenTicket.sln -c Release --no-restore
dotnet test GoldenTicket.sln -c Release --no-build --no-restore
dotnet run --project tools/GoldenTicket.UiSmoke -c Release --no-build --no-restore -- artifacts/camera-processing-ui
dotnet list GoldenTicket.sln package --vulnerable --include-transitive --no-restore --format json
```

Local logs are `artifacts/camera-processing-restore.log`, `camera-processing-build.log`,
`camera-processing-tests.log`, and `camera-processing-ui.log` in the same directory. The final
test log reports 546 tests and the final UI log reports 50 cases. Artifacts may be absent in a
fresh checkout; rerun the commands to produce current evidence.
The advisory audit output is `artifacts/camera-processing-dependency-audit.json`, using
`https://api.nuget.org/v3/index.json`. It records no known vulnerabilities at the time of the check;
it is not a claim that all dependency vulnerabilities have been ruled out. This developer audit
contacts the package feed, independently from the installed application's local runtime.

## Actual GPU, synthetic images

The final `artifacts/gpu-processing-benchmark.json` reports `passed: true`, backend `Gpu`,
adapter **NVIDIA GeForce RTX 4080 Laptop GPU**, and no fallback reason.

| Operation | GPU | CPU | Maximum CPU/GPU channel difference |
|---|---|---|---|
| Synthetic 1920 × 1080 to 3840 × 2160 | 30.4903 ms first pass; 30.3162 ms warmed | 119.8445 ms | 1/255 |
| Filtering synthetic input already 3840 × 2160 | 35.5632 ms | 33.8895 ms | 0/255 |

These are preprocessing timings, including GPU transfer/readback. They exclude camera acquisition,
cropping, piece detection, WPF presentation and end-to-end latency. GPU upscaling was faster in
this run; native-size filtering was slightly faster on CPU. Auto validates shader execution and
correct output rather than selecting the fastest processor for every frame or resolution.
Synthetic 4K pixels do not establish support for a physical native-4K camera.

A subsequent diagnostic-report check (`artifacts/gpu-processing-backend-check.json`) also passed
and recorded GPU as the backend that completed the first, warmed and already-4K frames, with no
fallback. Reports use the effective frame backend rather than initialization success alone.

```powershell
dotnet run --project tools/GoldenTicket.VisionCheck -c Release --no-build --no-restore
```

On a machine without a validated hardware GPU, the diagnostic may report a valid CPU fallback.
Record the actual backend rather than treating every successful run as GPU evidence.

## Supplied board-photo pair

The developer diagnostic compared the supplied empty-board and populated-board photos through
the production candidate detector. These are image-pair checks, not a continuous live game or a
held-out recognition dataset.

| Comparison | Train candidates | Player-marker candidates |
|---|---|---|
| Original reference and original populated image | 15 | 5 |
| Original imported reference and enhanced populated image | 15 | 5 |
| Enhanced reference and enhanced populated image | 15 | 5 |
| Original empty reference and enhanced version of the same empty image | 0 | 0 |

Local diagnostic logs are `artifacts/piece-detection-actual.log`,
`piece-detection-enhanced-import.log`, `piece-detection-enhanced-both.log`, and
`piece-detection-enhanced-negative.log` in the same directory. Counts are candidates, not
authoritative train inventories, route ownership or scoring verification. The unchanged-image
result checks one processing-induced false-positive case; it does not prove zero false positives
under varying lighting, shadows, printed tracks, touching pieces or camera motion.

### Subsequent failed exploratory comparison

`GoldenTicket-board-20260912-160803.png` from the local `C:/temp` directory was compared with the
older empty-board image identified by timestamp `152343`, using the enhanced-current/imported-
reference path. The detector returned `Ready` with **20 train and 19 marker candidates**. Visual
inspection sees **15 trains and 5 markers**; extras include printed score numbers and tracks.
Repeating without additional enhancement returned `Ready` with **24 train and 19 marker
candidates**, so the extras are not specific to enhancement.
Per-object false positives and misses were not fully annotated, so the excess counts alone are
not a complete error measurement.

This failed case limits the earlier photo-pair result. Its cause is not isolated: crop/geometry,
lighting and reference age may have changed. It is not evidence that glare caused the errors.
Local JSON, overlay and log outputs remain under `artifacts/glare-baseline-160803/` and
`artifacts/glare-baseline-160803-raw/`; no user images
are committed. The [controlled glare test](../../glare-test.md) specifies fresh/matched references
and normal/moderate/strong glare conditions. Its first nominal-reference series follows below;
the full physical matrix remains incomplete.

### Fresh nominal-reference glare series

The user supplied 3456 × 2160 exports `164319` (empty/no glare), `164557` (minimal glare),
`164624` (stronger) and `164718` (strongest), using the filename prefix
`GoldenTicket-board-20260912-` in `C:/temp`. Framing and populated-board piece positions appeared
consistent on visual review. Independent visual ground truth was 8 trains (3 yellow Salt Lake City–Denver,
2 black New York–Washington, 3 black Nashville–Raleigh) and 4 markers (yellow 20, red 50,
black 70, green 1).

With no additional enhancement on the already exported images, the unchanged detector returned
8/4, 19/4 and 35/6 train/marker candidates for minimal, stronger and strongest glare respectively.
The empty control returned 0/0. Overlay review found all 8 actual trains and 4 markers covered in
each populated image, with false train/marker outlines of 0/0, 11/0 and 27/2. Every result was
`Ready`; no scene hold occurred. Repeating with additional GPU enhancement on the RTX 4080 Laptop
gave exactly the same counts across the four cases.

This series shows false positives rather than misses for these placements. The strongest
reflection mainly affected western/central printed board areas; yellow train bodies remained
visible and black groups were mostly outside the hotspot. Matching-lighting empty references,
direct-glare coverage of those black pieces, photometry, live video and jog behavior were not
tested. The series cannot establish general glare tolerance or isolate all reference/lighting
effects. No detector tuning was performed during measurement.

Local JSON/overlay outputs are under `artifacts/glare-test-1643/` in `minimal`, `stronger`,
`strongest` and `empty-control` directories; additional-enhancement runs use matching names with
`-enhanced`. Logs remain in their parent directory. No user photos are committed. See the
[protocol and result table](../../glare-test.md#fresh-reference-series-september-12-2026).
Independent per-object review confirmed all piece coverage and false-outline counts. The local
`artifacts/glare-test-1643/summary.json` records input/source SHA-256 hashes, results and limitations.

In a subsequent user-reported trial, the board was cleared and lights were positioned with some
glare before **Capture empty board**. Pieces added under that unchanged lighting were reportedly
detected reliably. This is evidence for the matching-lighting workflow, separate from the counted
image series above. No new images or per-object counts were supplied for this follow-up. See the
[matching-lighting observation](../../glare-test.md#user-reported-matching-lighting-glare-follow-up).

To reproduce with your own equally cropped board images, set the two paths before running:

```powershell
$emptyBoardPath = 'C:\path\to\empty-board.png'
$populatedBoardPath = 'C:\path\to\board-with-pieces.png'
dotnet run --project tools/GoldenTicket.PieceDetectionSmoke -c Release --no-build --no-restore -- $emptyBoardPath $populatedBoardPath artifacts/pieces-raw
dotnet run --project tools/GoldenTicket.PieceDetectionSmoke -c Release --no-build --no-restore -- $emptyBoardPath $populatedBoardPath artifacts/pieces-imported --enhanced
dotnet run --project tools/GoldenTicket.PieceDetectionSmoke -c Release --no-build --no-restore -- $emptyBoardPath $populatedBoardPath artifacts/pieces-captured --enhanced --enhanced-reference
dotnet run --project tools/GoldenTicket.PieceDetectionSmoke -c Release --no-build --no-restore -- $emptyBoardPath $emptyBoardPath artifacts/pieces-empty --enhanced
```

The tool creates local annotated output and JSON reports. Keep private or user-provided images
out of committed evidence. Its [usage and limitations](../../../tools/GoldenTicket.PieceDetectionSmoke/README.md)
describe the matching preparation paths.

## Camera inventory and remaining physical work

Shared-read-only inspection of the connected **Android Webcam** reported one color Record source,
currently 1920 × 1080 at 15 fps NV12. The advertised sizes were 1920 × 1080, 1280 × 720,
640 × 480 and 640 × 360. No 3840 × 2160 mode was advertised. Inspection initialized shared access
without setting formats or starting a reader; it did not change the running camera session.

```powershell
dotnet run --project tools/GoldenTicket.CameraDiagnostics -c Release --no-build --no-restore -- Pixel
```

Native-4K physical capture, general piece recognition, real camera jog/reorientation, full-game
latency, actual device-loss/fallback and additional GPU families remain open. Complete the
[physical checklist](../../camera-processing.md#physical-acceptance-still-required) before claiming
those capabilities. Installed runtime remains local; this record does not substitute for the
full physical WAN-disconnected PWA/device acceptance matrix.
