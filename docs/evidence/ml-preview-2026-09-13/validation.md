# Experimental ML piece preview — September 13, 2026

The Piece outlines feature now runs a local learned detector on the current board crop. It
does not need an empty-board reference. This is an experiment for collecting visible failures;
manual route confirmation and game rules remain authoritative.

## Model and data

- Architecture: official YOLOX-Nano source revision
  `6ddff4824372906469a7fae2dc3206c7aa4bbaee`, two classes (`train`, `player-marker`).
- ONNX SHA-256: `420318a5cf2953b597aaf35fd627f737c64c8aa579bcc51d3c87f1d9be042211`.
- Training: 18 September 12 photos, 179 trains and 64 markers; four images are empty boards.
- Validation: 11 September 13 photos, 656 trains and 55 markers. No empty-board or explicit
  glare trial is present in that validation group.
- Training ran locally on the RTX 4080 Laptop GPU: 40 epochs, 256 sampled tiles per epoch,
  batch 16, seed 20260913. Epoch 20 was selected. The capture-date groups remained intact.
- The 29 reviewed originals and labels retain their recorded hashes. Photos, labels, weights,
  the isolated Python environment and diagnostic images remain ignored local artifacts.

The operating point is confidence 0.30, classwise NMS IoU 0.45, and midpoint tile ownership.
The graph takes BGR float32 0–255 tiles `[1,3,640,640]` and returns decoded `[1,8400,7]`.
The complete board is normalized to 1920 × 1200 and analyzed in 12 overlapping tiles.
These are two object classes, not color or route-ownership predictions.

**The validation group was used to select the checkpoint, threshold and overlap handling.**
Dates from the same physical setup are not an independent camera/lighting test. Report these
results as tuned validation, not production accuracy. Four training empty boards returned zero
false positives at the final threshold; that is an in-sample diagnostic only.

## Measured predictions

Matches require the same class and bounding-box IoU ≥ 0.50, with one-to-one matching.

| September 13 photo | Matched | False positives | Missed |
|---|---:|---:|---:|
| 112309 | 89 | 0 | 7 |
| 121656 | 109 | 1 | 4 |
| 123216 | 117 | 0 | 4 |
| 124753 | 32 | 0 | 2 |
| 125240 | 34 | 0 | 0 |
| 125701 | 51 | 0 | 1 |
| 130218 | 71 | 0 | 0 |
| 130653 | 64 | 1 | 0 |
| 130924 | 29 | 0 | 0 |
| 131247 | 46 | 0 | 0 |
| 131822 | 51 | 1 | 0 |
| **Total** | **693** | **3** | **18** |

Precision is **99.57%**, recall **97.47%**. All 55 score markers matched; all 18 misses were
trains: 15 yellow, 2 red and 1 green. This breakdown uses reviewed color metadata, not predicted
colors. The Python evaluation and final C# CPU/DirectML runs found the same object matches and
errors. White prediction outlines are saved in the local runtime smoke output; review images
also show missed labels and false positives separately.

The historical difference detector on the same 11 photos, with empty reference
`GoldenTicket-board-20260912-181352.png`, produced 371 matches, 398 false positives and 340 misses.
Four `SceneChanged` holds were scored as zero visible predictions. This comparison uses an old
reference rather than a fresh lighting-matched capture, and scores bounding rectangles around
its rotated outlines. It is a bounded comparison of this collection, not a general glare study.

## Actual runtime and hardware

ONNX Runtime DirectML 1.24.4 ran on the NVIDIA GeForce RTX 4080 Laptop GPU. Every diagnostic
startup profile recorded actual `DmlExecutionProvider` graph execution. Module audits confirmed
app-local DirectML **1.15.4**, SHA-256
`9c9e6d822561c6c41b90e6994b3e8857cf1d66dbfb1e0c4c799c7c89b4e92da1`.
The production UI still
states that CPU fallback is allowed; provider selection alone is not treated as GPU evidence.

- All 696 CPU/GPU predictions matched: minimum IoU 0.99999594, maximum confidence difference
  0.00000701.
- Original-resolution Python versus C# resizing had minimum matched IoU 0.92457 and maximum
  confidence difference 0.02574, with identical object matches and error totals. The paths are
  numerically close, not bit-identical; OpenCV byte rounding differs from floating interpolation.
- Static-board inference median: **82.4 ms GPU**, **224.6 ms CPU**; maxima 86.1 ms and 239.8 ms.
  These timings include normalization, tiles and NMS, but exclude camera acquisition, live
  rectification/enhancement, presentation and model startup. They are not a live frame-rate claim.
- The final packaging audit found that AnyCPU library builds omitted the DirectML DLL, and
  ordinary native dependency search could use Windows' System32 copy. The project now copies
  the pinned production DLL explicitly. The detector verifies its hash and preloads its absolute
  application path before ORT initialization, retaining the handle for the process lifetime.
  Unavailable/mismatched local GPU runtimes report CPU fallback. The final measurements above
  were repeated with the bundled DLL; earlier system-DLL diagnostics are retained separately.
- ONNX checker and PyTorch/ONNX CPU output parity passed. Runtime checks include manifest/hash,
  operator/input contract, tile ownership, postprocessing bounds, cancellation and disposal.
- The runtime disables ONNX Runtime telemetry and uses only packaged CPU/DirectML providers.
  No production inference profile files are emitted unless diagnostics are explicitly enabled.

## Integration and reproduction

The camera path rectifies a raw frame to 3456 × 2160, enhances that crop in the same order as
the training-photo export, then runs the model. Preview display enhancement remains independently
switchable. Crop, camera, processing, model and outline-toggle revisions prevent late results
from appearing; predictions older than two seconds are discarded.

**Save detection example…** captures the exact analyzed PNG, prediction geometry/confidences,
model hash, frame/crop identity and optional note in a local ZIP marked unreviewed. It does not
train a model or change a game. Model loading/inference failures leave the camera/manual game
available with an explicit status.

See [training commands](../../../tools/piece-training/README.md#train-and-evaluate-locally),
[user workflow](../../piece-recognition-ml.md) and [dependency notices](../../ml-dependencies.md).
Example runtime diagnostic from the repository root:

```powershell
dotnet run --project tools/GoldenTicket.MlPieceSmoke -c Release -- artifacts/piece-training/model C:/temp/GoldenTicket-board-20260913-131822.png artifacts/piece-training/runtime-check --compare
```

Local evidence locations:

- `artifacts/piece-training/runs/baseline-session-01/`: training history and selected checkpoint.
- `artifacts/piece-training/model/`: pinned deployment pair, provenance, evaluation,
  threshold/empty-board diagnostics, historical comparison and visual review.
- `artifacts/piece-training/runtime-smoke-pinned/summary.json`: all 11 final C# CPU/GPU measurements;
  per-photo subdirectories contain outlined PNGs and opt-in provider profiles.

Integration checks:

- Python tooling: **30 passed, 1 existing symlink-privilege skip**.
- Full .NET suite: **580 passed**, including 12 learned-camera flow tests and 10 model runtime
  contract/postprocessing tests. The first restricted run could not use Windows profile data
  protection; rerunning with normal profile access passed all tests.
- Synthetic WPF rendering: **54 cases**, no binding errors/warnings or horizontal control overflow.
  Covers white outlines, square markers, fresh-result toggle behavior and the review controls.
- Development desktop build: zero warnings/errors. Model and manifest copies match their source
  hashes; the build includes model/runtime notices. It contains no training photos, Python
  environment or training checkpoints (the normal companion app icons remain).
- Development and `win-x64` dependency lock restores passed. Documentation relative file links
  and `git diff --check` passed. No release was created and the user's running app was not restarted.

The stale-result test also verifies that slow inference displays the current raw camera frame
while discarding expired outlines. A blocked reload regression verifies that model loading
cannot reuse old weights or block the normal preview.

## Remaining acceptance

The mounted camera has not been driven by this diagnostic. Test newly arranged boards without
retraining first, then save and review failures. Include new empty boards, touching yellow
trains, dark trains on dark printed routes, score markers, hands, lighting changes and glare.
New independent sessions and other GPU/CPU devices are still needed. Actual device-loss fallback
was not forced. These outlines do not provide calibrated confidence, verified inventory, piece
color, automatic registration, route ownership or automatic game-state updates.
