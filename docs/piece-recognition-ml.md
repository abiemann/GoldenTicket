# Learning to recognize physical pieces

Updated September 13, 2026. **Piece outlines now uses a locally trained ML detector** when the
experimental model is installed. This replaces empty-board differencing in the camera preview.
The model supplies visual observations only: manual game verification remains in force.

## Try it in the camera preview

1. Build the desktop project with the local model deployment pair present under
   `artifacts/piece-training/model/`: `piece-detector.onnx` and `manifest.json`. The project copies
   these into `models/pieces/` beside the executable. A fresh source checkout has no weights;
   it reports ML unavailable until an appropriate local model is installed. The application
   does not download models.
2. Start the camera. With the separate [corner model](board-corners-ml.md) installed, ML selects
   the four outer corners automatically. Check the complete score track is inside the outline;
   adjust the handles, retry **Detect board corners**, or choose **Select four board corners**.
   No empty-board or camera-framing reference is required for outlines. Clear hands and inspect
   the white train rectangles and score-marker squares. Train outlines follow the visible train
   angle when a local image fit is reliable; uncertain fits keep the original upright model box.
3. Read the separate **ML** backend/model status under **Piece outlines**. The top CPU/GPU badge
   describes image enhancement. **Reload ML model** reloads the locally installed pair and
   prefers GPU, unless **CPU only** is selected above. Changing enhancement alone does not
   silently replace the inference session.
4. Enter an optional note about a missing, extra or merged outline and choose **Save detection
   example…**. The ZIP contains the exact analyzed, unpainted board image and `predictions.json`,
   including model SHA-256, confidence, source identity, explicit coordinate units and your note.
   Original model boxes and optional image-fitted `orientedOutline` polygons are stored separately.
   Capture continues; the saved image and predictions stay paired. Existing files are not replaced.
   Predictions are marked unreviewed and are never automatically treated as training labels.

Disabling outlines stops inference for subsequent frames and clears old predictions. Camera,
crop, processing or model changes invalidate in-flight results. Published outlines expire after
two seconds even while the camera is still supplying newer frames. Loading/inference failures
leave the normal preview and manual game available and report the ML failure.

## First training experiment

The first training experiment used **29 photos and 954 labels: 835 trains and 119 score markers**.
Each individual train is boxed, including touching trains. Four photos are reviewed empty boards.
Original photographs and labels are read-only inputs; their hashes are rechecked after training.

The original capture-date split is preserved: 18 September 12 photos / 243 objects train the
model; 11 September 13 photos / 711 objects are used for validation and threshold selection.
All five physical colors occur in both groups. The dates share the same camera/board setup,
and all empty-board and glare examples are in training. **There is no untouched independent
test session.** Validation here is tuning evidence, not a general accuracy or glare guarantee.

The experiment fine-tunes official YOLOX-Nano COCO weights into two classes, `train` and
`player-marker`. Source revision, starting weights, seed, optimizer, commands, source hashes and
installed Python packages are recorded in the local run. Forty epochs of 256 random tiles were
run; the best validation checkpoint was selected. The fixed runtime contract is:

- A complete 8:5 board is normalized to 1920 × 1200 with half-pixel bilinear resizing.
- Twelve overlapping 640 × 640 tiles, stride 512 with edge-anchored final tiles, use BGR float32
  values 0–255 in NCHW order. No additional mean/variance normalization is applied.
- The ONNX opset 17 graph decodes `[1,8400,7]` rows containing box geometry, objectness and the two
  class probabilities. Confidence is objectness multiplied by the strongest class probability.
- Midpoints of tile overlaps assign each detection center to one tile region. This avoids
  partial boxes from cut-off pieces at tile edges. Global classwise NMS uses IoU 0.45.
- The selected preview threshold is 0.30. Selection considers validation performance and the
  explicitly in-sample empty-board diagnostic. These selection data are not a final test set.

For the live model input, the raw camera frame is rectified to 3456 × 2160, then gently enhanced
using the same processing order as the exported training photographs. The detector subsequently
normalizes that crop as above. Unchecking **Enhanced 4K preview** changes display only. This first
model does not exploit every pixel of native 4K; the camera/export path retains 4K dimensions.
Upscaling cannot recover missing sensor detail. Future native 4K/tile-size comparisons require
new measurements and a versioned manifest.

Physical plastic colors are annotation metadata used to break down errors. This two-class
model **does not recognize player color or ownership**. Its axis-aligned boxes do not establish
learned rotation or segmentation. After ML detection and NMS, a bounded local image fit estimates
an oriented rectangle for each train whose visible shape supports one. This display refinement
uses the current analyzed image, without an empty-board reference or board-route lookup; it does
not add detections or change their classes or confidence. Low-contrast, ambiguous or clipped fits
fall back to the original box. Marker squares remain a display transform.

Original detector geometry stays in `outline` and the pixel box fields for evaluation and review.
The optional `orientedOutline` uses the same normalized-board coordinates and is explicitly tagged
`local-image-fit` in review ZIPs. It is not a learned rotation prediction or reviewed training label.
The preview projects its four corners through the selected board crop, preserving alignment under
camera perspective, zoom and pan. The preview reports total detection time; review and smoke
reports separate model inference from outline fitting. No model retraining or additional runtime is required.
See the [orientation checks and limitations](evidence/train-orientation-2026-09-13/validation.md).

## Results and the next review loop

For the original model, the Python validation path matches 693 of 711 labeled pieces at
IoU 0.50, with 18 misses and 3 false positives: 99.6% precision and 97.5% recall. All 55 validation
score markers are matched; most misses are yellow trains. Known empty training photos produce
zero detections at this threshold. See the [validation record](evidence/ml-preview-2026-09-13/validation.md)
for C# provider parity, timing, the reference-comparison baseline, and the limits of these checks.

Use the running preview on new layouts and lighting. Save failures with a short note, review
the complete image and correct labels, then add useful examples to a new recorded training run.
Keep entire sessions, repeated layouts and lighting variants together. Reserve new independent
test sessions before tuning. Compare each candidate model on the same fixed evaluation set,
including misses, duplicates and false detections per board, rather than counts alone.

The first new recorded failure is a **black score marker surrounded by other markers** in photo
`20260913-171132`. Its score is below threshold before overlap filtering; .NET and Python reproduce
the miss. The user chose to collect more examples before retraining. Reviewed additions are saved
in local collection versions, preserving the original 29-photo training experiment. See the
[clustered-marker diagnosis and capture plan](evidence/clustered-markers-2026-09-13/validation.md).

Photo `20260913-172806` records an intermittent yellow-train outline report. The saved photo itself
detects the train on CPU and DirectML; an exact missed-frame example is still needed. Label review
also corrected an omitted yellow train in `171132`. See the
[yellow-train check and correction](evidence/yellow-train-2026-09-13/validation.md).

Photo `20260913-173706` reproduces an extra train prediction over the empty printed yellow slot
near Boston on both CPU and DirectML. The printed slot remains background while the real red train
beside it is labeled. Current local collection version `04` contains **32 photos and 1,126 labels**,
with related layouts kept together. See the
[Boston false-positive check](evidence/boston-false-positive-2026-09-13/validation.md).

The user then requested retraining. The [second model](evidence/ml-retrain-2026-09-13/validation.md)
uses all 32 reviewed photos and is installed for local preview. At the unchanged 0.30 threshold,
the fixed comparison improves from 21 misses and eight extras to zero of each across these photos.
The crowded black markers are detected and the Boston printed-slot extra box is gone; the real
yellow train near Little Rock remains detected. CPU/DirectML fixture checks agree.
**These are in-sample results:** the photos were used in training. New capture sessions are still
needed to judge generalization and intermittent live failures. The previous model is retained for
rollback. Use **Reload ML model** in an already open app to load the new installed pair.

The current difference detector remains available to developer diagnostics for comparison;
the live Piece outlines feature no longer calls it. The old lighting failure observations are
preserved in [the glare record](glare-test.md).

## Runtime and remaining acceptance

`LearnedPieceDetector` loads only local verified model bytes. It checks the manifest, hash,
standard operator allowlist, embedded tensor restriction and fixed input/output shapes before
inference. ONNX Runtime DirectML 1.24.4 prefers the hardware adapter with most dedicated memory;
CPU fallback is included in the same package. A warm-up runs before activation. DirectML uses
sequential sessions without memory-pattern optimization. Runtime telemetry events are disabled.
Provider status is reported separately from image enhancement, and diagnostics record actual
kernel assignment.

The view model has one active frame job, background inference, cancellation, source-clock
preservation, revision checks, and orderly disposal. Review files are local only. End users do
not install Python or train models. See [dependency notices](ml-dependencies.md),
[training commands](../tools/piece-training/README.md), and [DESIGN §17.4.2](../DESIGN.md#1742-planned-learned-model-inference).

Remaining work includes independent capture-session testing; held-out empty boards and direct
glare; hands/occlusion, focus and motion; complete camera-to-outline timing; other GPU/CPU
hardware, timeout and device-loss behavior; and clean-machine offline distribution. Model
performance does not prove automatic route assignment, color/owner recognition, temporal
confirmation, or whole-board rule verification. Those gates remain separate.
