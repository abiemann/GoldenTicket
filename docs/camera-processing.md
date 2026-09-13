# Camera processing and experimental piece outlines

Updated September 12, 2026. This describes the current implementation, its measured hardware
observations and the acceptance work still needed. It supplements [DESIGN §17.3–17.4](../DESIGN.md)
and [TODO](../TODO.md); it does not replace the full automatic-verification requirements.

## What is available

The Windows app can prefer a native 4K camera mode, process images on the CPU or a validated
hardware GPU, compare raw and enhanced previews, and outline experimental piece candidates
against an empty-board reference. All of this runs locally. No trained model, cloud service,
subscription, Internet connection or runtime asset download is involved.

The game still uses explicit manual verification. Outlines never spend cards, claim a route,
identify its owner, score points or advance a turn. The CPU/GPU indicator describes actual image
resizing/enhancement; rules, AI and the current piece comparison run on the CPU.

## Set up the camera

1. Secure the phone above the whole board, including the score track, with soft, even lighting
   and slack in the USB cable. Position lights to avoid bright reflections across the board.
   The user reported reliable piece detection after restoring soft lighting in the
   [September 12 follow-up](glare-test.md#user-reported-soft-light-follow-up).
   On Pixel, choose **Webcam** in USB preferences. Use the phone's webcam
   preview to select the intended camera, framing and focus. Google documents the
   [Pixel USB webcam workflow](https://support.google.com/pixelcamera/answer/14274129?hl=en).
2. In **Camera**, select the device and **4K preferred · best available**, then **Start preview**.
   The app requests the largest usable mode advertised by the camera up to 3840 × 2160. An
   unavailable mode falls back to a smaller native mode; **Balanced 1080p** remains available.
   **Shared current format** reads the camera's existing mode without changing its owner's format.
3. Check the reported camera dimensions and processing dimensions separately. A 1920 × 1080
   source enhanced to 3840 × 2160 is explicitly identified as upscaled. Larger output pixels do
   not add captured detail or make that source native 4K.
4. Select the four board corners in clockwise order, including the complete score track. Drag
   any numbered corner before or after selection to adjust it. Keyboard users press **1–4** and
   arrow keys; hold **Shift** for larger steps. Invalid corners remain editable.
5. Use **Auto · prefer GPU**, **CPU only**, or **GPU · CPU fallback**, then **Apply processor** to
   change the processor. The setting is saved locally for the next launch. The status and tooltip
   report the active adapter and any fallback, separately from the selected preference.

**Enhanced 4K preview** is enabled by default. Uncheck it to compare the original camera image;
analysis continues on the enhanced path. **Show piece outlines** controls the overlay visibility.

### Zoom and position the preview

Use **+** and **−**, or hold **Ctrl** while scrolling over the image, to zoom from **Fit (100%)**
up to **800%**. Ctrl-scroll keeps the image point beneath the pointer in place. To move around a
zoomed image, drag it with the left mouse button. You can do this while selecting crop corners:
a click places the next corner, while a drag moves the view. Dragging a numbered handle adjusts
that corner. **Pan**, **Space** while dragging and the middle mouse button also let you pan.
Select **Fit** or press **0** while the preview has focus to return to the whole image.

Numbered corner handles keep the same screen size and remain adjustable before or after selecting
the crop. With the preview focused, press **1–4** to select a corner and bring it into view when
zoomed. Arrow keys make finer adjustments at higher zoom; **Shift** still increases the step.
Zooming and panning only change the view. They do not change the selected crop, source resolution,
image processing or exported photo.

## Try piece outlines

For the first experiment, start with the board empty of plastic trains and scoring markers.
Finish positioning the camera and lights, let the image settle, then select the crop and use
**Capture empty board**. Keep that lighting in place when adding pieces and playing. This reference is a local image
comparison baseline, not model training. It must contain the complete board, including its score
track, and no hands or private cards. Capturing the current empty board is the preferred way to
match the live framing and lighting.

The user also reported reliable detection with some glare when that same glare was present
during empty-board capture, before adding pieces. This supports treating lighting mismatch as
a source of false candidates; it does not establish general glare tolerance. See the
[matching-lighting follow-up](glare-test.md#user-reported-matching-lighting-glare-follow-up).
If lighting changes after capture, restore the reference lighting or capture a new reference
with the board empty. Capturing a new empty-board reference while pieces remain will make those
unchanged pieces part of the comparison baseline.

Alternatively, use **Load empty-board photo…** with an upright, matching empty-board crop
previously exported by GoldenTicket. The loader accepts one PNG/JPEG with an approximately 8:5
shape, at least 640 × 400 pixels and within 3840 × 2160, with a 48 MB file limit. It does not accept
a screenshot of the application as a board crop. An imported reference is resized without a
second enhancement pass. Different lighting, crop framing or prior processing can still affect
the comparison; reuse remains experimental.

Add one or more trains or score markers, then clear hands and let the image settle. Train
candidates appear as white rotated rectangles; player-marker candidates appear as white squares.
The count describes candidates in that frame. It is not a verified inventory of trains on routes.
A reference containing a piece cannot identify that unchanged piece by differencing later.

Changing the crop, camera session or processor clears the piece reference and outlines. Capture
or load an appropriate empty-board reference again. A temporary camera jog suppresses candidates
when alignment or scene-change checks fail. Return it to its previous view and let the scene
settle to resume comparisons. Automatic registration at an arbitrary new usable pose remains
future work. The framing reference used for checkpoint capture is a separate scene-safety check.

## Output and evidence

The processor preserves aspect ratio inside a 3840 × 2160 bounding size. A 16:9 camera image can
fill that size; the board's 8:5 photo crop is **3456 × 2160**. This preserves the board shape.

**Export board photo…** creates an enhanced PNG of the current crop using the chosen processor.
It requires a fresh camera frame and valid crop but does not require the scene-reference gate
and does not save a game. Export remains a manual local file operation.

**Capture reference photo** attaches a source-derived, unsharpened crop to the selected validated
checkpoint. It retains the separate stable-scene, operator-attestation, crop/camera identity,
encrypted-storage and authenticated-readback checks. It does not substitute the enhanced preview
or painted outlines as evidence. A digital save alone still contains no photograph.

## Processing implementation

`CameraCaptureService` ranks native source modes across color Record/Preview streams. It tries
advertised modes within the chosen pixel bound and 5–60 fps, preferring 15 fps when resolution is
equal. Rejected formats or reader starts can fall through to another advertised candidate within
the startup deadline. Shared mode never sets a format. Source negotiation and the dimensions of
the bitmap actually delivered by Windows are distinct metadata.

`FrameProcessor` has matching C# CPU and hardware Direct3D 11 compute implementations. A small
Gaussian luminance neighborhood supports mild noise smoothing and bounded edge enhancement;
the edge correction is capped at eight channel levels. Aspect-preserving Catmull–Rom bicubic
resizing clamps interpolation to local source-channel bounds to limit ringing. There is no
generative super-resolution, learned sharpening model or reconstruction of missing detail.

Auto enumerates local hardware adapters, prefers larger dedicated video memory, and excludes
software adapters. Microsoft Basic Render Driver is excluded by its software flag, documented
adapter identity or driver name, including on hosted Windows machines with incomplete flags.
A synthetic 17 × 11 to 31 × 19 shader execution must agree with the CPU
reference within two levels per channel before GPU status is enabled. Probe work has a ten-second
cooperative budget; GPU readback waits are bounded at three seconds. An individual native driver
call cannot be forcibly interrupted. Expected GPU initialization/execution failures fall back to
CPU and disclose the active backend; explicit CPU mode avoids GPU initialization.

The camera pipeline processes one frame at a time and drops superseded work. It checks camera
epoch, age, crop, processor and reference revisions before displaying results. A backend change
invalidates the detector reference. Published outlines expire after two seconds independently of
whether the camera continues supplying fresh frames, so stalled processing cannot leave an old
overlay presented as current. No stale result is allowed to become a game command.

Derived frames retain the source capture timestamp and monotonic clock through enhancement and
rectification. Production capture uses the system clock. Camera flow tests use an explicitly
advanced clock so slow CI processing does not accidentally turn a fresh-frame test into a stale
one; separate tests verify the unchanged two-second expiry and scene-stability timing.

`PieceCandidateDetector` samples equally rectified images, aligns small reference translations,
rejects major scene changes/motion, and evaluates changed color components by shape. Its sampling
is bounded at 960 × 640 and area-averages pixels; it does not treat interpolation as additional
sensor evidence. This is an experimental baseline designed to withhold doubtful results, with
no calibrated confidence or measured physical false-positive guarantee.

The graphics bindings are pinned to `Vortice.Direct3D11` and `Vortice.D3DCompiler` **3.8.3** under
the [Vortice MIT license](https://github.com/amerkoleci/Vortice.Windows/blob/main/LICENSE).
Direct3D 11 is a local Windows API. Dependency inventory and notices are tracked in
[camera processing dependencies](camera-processing-dependencies.md). Windows ML/ONNX inference
remains the conditional future model path in DESIGN §17.4.2, separate from this implementation.

## Measured observations

| Check | Result | Limit of the evidence |
|---|---|---|
| Connected Pixel UVC format inventory | `Android Webcam`, one Record source, current 1920 × 1080 at 15 fps NV12; advertised sizes 1920 × 1080, 1280 × 720, 640 × 480 and 640 × 360 | Shared-read-only format inspection. No reader was started and no camera mode changed; this is not a fresh-frame measurement |
| 1080p/720p UVC modes | NV12 and MJPG at 15, 24, 30 and 60 fps | The current connection advertises no 4K mode. This does not describe every Pixel or its built-in recording capability |
| Hardware GPU preprocessing | NVIDIA GeForce RTX 4080 Laptop GPU processed synthetic 1920 × 1080 input to 3840 × 2160 | Actual local compute, not a physical board recognition test |
| Upscaling microbenchmark | GPU 30.49 ms first pass, 30.32 ms warmed; CPU 119.84 ms | Synthetic 1080p-to-4K workload; excludes acquisition, crop, detection, presentation and end-to-end latency |
| Native-size filtering microbenchmark | Already-4K input: GPU 35.56 ms; CPU 33.89 ms | CPU was slightly faster in this measured case. Auto validates hardware capability; it does not promise the GPU is fastest for every workload |
| CPU/GPU output comparison | Maximum channel difference 1/255 when upscaling; 0 for the already-4K fixture | Does not establish recognition accuracy, every input or every GPU family |
| Final integrated automated/UI checks | Locked restore/build passed; 546 tests passed; 50 WPF render cases with zero binding warnings/errors | The final incremental build had zero warnings/errors; recompilation reports existing xUnit analyzer warnings. Rendering is synthetic, not physical mouse/camera acceptance |
| Dependency advisory audit | No known vulnerabilities reported for solution packages, including transitive dependencies, by the NuGet feed | Advisory result at the time of this check, not a proof that dependencies have no vulnerabilities |
| Supplied board-photo pair | 15 train and 5 marker candidates in raw comparison, enhanced-current/imported-reference comparison, and both-enhanced comparison | One supplied pair, not a general physical recall/false-positive measurement; no user photos are committed |
| Unchanged empty-board pair | Original reference versus enhanced current image: 0 train and 0 marker candidates | A bounded negative case for processing-induced false candidates, not every lighting/camera condition |
| Later exploratory image with old empty reference | Enhanced comparison returned 20 train/19 marker candidates; no-additional-enhancement comparison returned 24/19. Visual inspection sees 15 trains/5 markers, with extras on printed numbers/tracks | Both returned Ready. Crop/geometry, lighting and reference age were not controlled; extras are not specific to enhancement, and the unisolated cause must not be attributed to glare |
| Fresh-reference glare series | Minimal: 8 train/4 marker candidates; stronger: 19/4; strongest: 35/6. All 8 actual trains and 4 markers remained covered; false outlines rose from 0/0 to 11/0 and 27/2 | No scene hold; same counts with additional GPU enhancement. This set shows false positives rather than misses, with only a nominal-lighting empty reference and incomplete direct-glare coverage of the actual pieces |
| Physical native-4K capture and mounted recognition acceptance | Pending | This Pixel connection cannot establish native-4K camera support |

The GPU microbenchmark's local diagnostic output is
`artifacts/gpu-processing-benchmark.json`; artifacts may not be present in a fresh source checkout.
The [validation record](evidence/camera-processing-2026-09-12/validation.md) records checks and
reproduction commands without committing user photographs. GPU timing is workload-dependent:
the measured upscaling was faster on this GPU, while native-size filtering was slightly faster
on the CPU in the same final diagnostic.
To reproduce the camera inventory without changing a running camera:

```powershell
dotnet run --project tools/GoldenTicket.CameraDiagnostics -c Release -- Pixel
```

[Android's webcam configuration guide](https://source.android.com/docs/core/camera/webcam?hl=en)
explains that vendors configure the UVC resolutions/rates advertised to the host. It describes
4K MJPEG as technically possible under its stated compression assumptions; it does not promise
that every Pixel advertises that mode. The actual advertised modes determine what GoldenTicket
can request.

## Physical acceptance still required

Use the [controlled glare protocol](glare-test.md) to separate lighting/reference mismatch from
glare-related misses. Its first fresh-reference series is recorded, with false positives under
stronger lighting despite coverage of all actual pieces. Matching-lighting empty references and
direct reflections over the black train groups remain untested. Compare individual outlines and
scene holds, not just candidate totals.

- Confirm an unchanged empty board produces no train or marker candidates across normal focus,
  exposure and lighting conditions; repeat after loading its exported reference.
- Place each supported train color at varied angles and board locations, including near matching
  printed tracks. Count misses and false candidates. Test touching trains, glare, shadows,
  scoring markers, partial occlusion and hands entering/leaving the image.
- Jog the camera, verify outlines are withheld, return it to the reference view, and verify fresh
  results. Then test crop changes, stop/restart, resolution changes, and processor changes; each
  must require an appropriate new reference and reject stale overlays.
- Compare raw/enhanced views and CPU/GPU outputs on the mounted board. Check responsiveness over
  a complete game; the synthetic microbenchmark is not an end-to-end frame-rate promise.
- Test actual GPU failure/fallback and additional integrated/discrete adapter families. Confirm
  the persisted CPU preference and GPU preference on a CPU-only machine report the correct state.
- Use a camera that really advertises/delivers 3840 × 2160 to verify native-4K acquisition, frame
  dimensions, cropping and export. Upscaling this Pixel's 1080p stream does not satisfy that gate.
- Recheck exported PNG dimensions and the unsharpened checkpoint-photo path, including stale-frame
  and mid-capture changes. Confirm both workflows operate with the Internet disconnected.

Printed artwork, shadows, color similarity and touching pieces remain known sources of ambiguity.
There is no claim of reliable whole-board inventory, automatic route ownership, general camera
reorientation, gesture wakeup, or a production-trained recognition model. Those remain tracked
in [TODO](../TODO.md).
