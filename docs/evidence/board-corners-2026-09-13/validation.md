# Board-corner ML validation — September 13, 2026

This records an experimental initial-crop assistant. It does not establish independent
real-camera accuracy, continuous tracking, or automatic game verification. The running user
camera/app was not opened, stopped or restarted during these checks.

## Frozen local model

- Deployment: `artifacts/board-corners/model/board-corners.onnx` plus `manifest.json`.
- SHA-256: `09fce75df40d3253c4dfc744a79a24dcedd5ec7319679e83bfb117e6e4e2aea5`.
- Model size: 2,082,435 bytes; heatmap-peak threshold 0.55.
- Compact U-Net trained locally from scratch, using September 12 rectified board textures;
  September 13 textures and separate random seeds supply synthetic validation.
- Two stages: 1,400 initial steps, then 1,000 steps emphasizing boards near camera-frame edges.
  The 800-step refinement checkpoint was selected by synthetic validation. Source photos,
  background strip, checkpoints, scripts, libraries and model hashes are recorded locally.
- RGB 0–1, centered 384-square letterbox, four 192-square heatmaps, opset 17. Only Concat,
  Constant, Conv, Relu, Resize and Sigmoid occur. The ONNX/PyTorch maximum absolute output
  difference at export was `2.98e-7`.

The screenshot carpet strip was used in training, and the observed near-edge framing informed
refinement. Those screenshots are development diagnostics, not untouched test data. Training
selection's report uses confidence alone; the separate fresh-seed report also applies the
runtime's in-image/order/convexity/area checks.

## Synthetic and screenshot diagnostics

On 600 fresh-seed synthetic frames (seed 20260915, procedural backgrounds):

| Result | Measured |
|---|---:|
| Complete boards accepted | 418 / 433 |
| Incomplete or absent boards rejected | 167 / 167 |
| Median accepted corner error at 384-pixel input | 0.753 pixels |
| 95th percentile accepted corner error at 384-pixel input | 1.713 pixels |

These are synthetic placement errors, not physical-camera pixel accuracy. Complete photos used
as textures were originally manually cropped, so their boundary errors can be inherited.
Source report: `artifacts/board-corners/fresh-synthetic-validation/summary.json`.

Three user screenshot camera regions were run through the final **C#** detector on CPU and GPU:

| Screenshot region | CPU / GPU outcome | Maximum CPU–GPU corner difference |
|---|---|---:|
| `b5342d93…` complete original preview | Both accepted all 4 corners | `4.73e-6` pixels |
| `4594a476…` complete preview with glare/handles | Both accepted all 4 corners | `3.51e-6` pixels |
| `5d144efd…` zoomed, incomplete board | Both rejected | No crop selected |

Visual inspection of the first C# GPU overlay confirms that the outline follows the outer
score-track boundary. The model is an initial proposal; use zoom/handles for precise correction.
Source regions, hashes and Python diagnostics are in `artifacts/board-corners/diagnostics-final/`;
the C# reports and numbered overlays are in `artifacts/board-corners/runtime-final/`.

For the original 1198 × 673 preview region, three GPU inference runs took 3.04, 3.09 and 2.76 ms.
The first CPU run took 93.18 ms and subsequent runs 6.35 and 5.99 ms. These include preprocessing,
network evaluation and decoding, but exclude camera capture, model loading, WPF presentation
and subsequent piece inference. They are a small static-image measurement, not a live latency
guarantee. GPU profiles record `DmlExecutionProvider`; CPU profiles record 31 CPU nodes. Both use
the pinned ONNX Runtime 1.24.4 and verified app-local DirectML 1.15.4 on RTX 4080 Laptop GPU.

## Application checks

- 613 .NET tests passed, including 20 new camera-flow cases and 13 new corner contract/geometry cases.
  Camera fixtures inject fake models and owned frames; they never open hardware. Tests cover
  raw-frame input, ordered/editable handles, preservation on rejection/load/inference failure,
  manual edits while inference is running, camera epoch/format changes, source freshness,
  once-per-capture automatic work, explicit retries, duplicate ticks, cancellation and disposal.
- 54 actual WPF view render cases passed at 1280 × 800 and 1000 × 620, with no binding warnings/errors
  or horizontal control overflow. The toolbar checks the bound detection command/status and that
  manual selection remains available while ML is busy. Existing pan/zoom/click placement checks pass.
- Three Python geometry tests pass; training/evaluation scripts compile.
- Solution build passes. The test project still emits its existing xUnit cancellation-token
  analyzer warnings; the Desktop development build has no warnings/errors.
- No packages were added. Both detectors share the same synchronized app-local native-runtime
  loader, with separate inference sessions and CPU fallback.

The full .NET suite runs under the normal Windows profile because existing DPAPI/certificate
tests require it. Logs and TRX remain local under `artifacts/corner-test-results/` and
`artifacts/board-corners/`; WPF images are in `artifacts/ui-smoke-board-corners/`.

Remaining acceptance: live overhead camera use, independent uncropped corner labels, other
backgrounds/cameras/editions, rotation, hands and occlusion, severe glare/focus/motion, device loss,
and broader GPU/CPU hardware coverage. Confidence is not a calibrated correctness probability.

## Follow-up: outer crop padding

The user observed score pieces close to the selected boundary. Automatic selection now expands
each model proposal by 4% about its center (approximately 2% per side), limited to camera bounds.
The resulting four visible handles define the preview, export and piece-analysis crop. Convexity
and containment of the original board are verified; padding never accumulates on repeated detection
or after manual edits. When the frame limits the margin, the preview advises widening the camera view.

The frozen ML model and the unpadded model-evaluation metrics above are unchanged. The diagnostic
tool records `Corners` separately from `CropCorners` and writes a padded-outline image for each
provider. CPU/GPU runs on the original full-preview screenshot both produced valid padded crops;
visual inspection confirms added space beside the left-edge score pieces. This example reaches
the top/bottom/left camera boundary, correctly reporting limited padding. Reports and images are
under `artifacts/board-corners/runtime-padded/`.

All 66 relevant padding, camera-selection, manual-editing, photo-export and piece-preview tests
passed in `artifacts/corner-test-results/crop-padding.trx`, including 10 padding geometry cases
and 22 automatic-corner flow cases. The earlier full-suite and WPF results above predate this
follow-up. The Desktop development build was rebuilt successfully with zero warnings/errors.
