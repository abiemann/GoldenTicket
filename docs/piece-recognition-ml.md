# Learning to recognize physical pieces

Updated September 22, 2026. The game uses a locally trained ML detector whose accepted runtime
weights are included in the source checkout. This replaces empty-board differencing in board
analysis. The model supplies visual observations; separate position, color and freshness checks
verify game actions. The experimental Piece outlines panel and score cards have been removed from
the technical Camera screen.

## Setup and developer checks

1. Build the desktop project. Its required tracked deployment pair is
   `assets/models/pieces/piece-detector.onnx` and `assets/models/pieces/manifest.json`.
   Build and publish copy them into `models/pieces/` beside the executable. A fresh source
   checkout includes the trained weights and needs neither model downloads nor retraining.
   The application does not download models.
2. Start the camera. With the separate [corner model](board-corners-ml.md) installed, ML selects
   the four outer corners automatically. Check the complete score track is inside the crop;
   adjust the handles, retry **Detect board corners**, or choose **Select four board corners**.
   No empty-board or camera-framing reference is required for the learned detector.
3. The Camera screen's CPU/GPU badge describes image enhancement, not piece-model inference.
   Gameplay board analysis uses the model independently. Developer tools and internal logs expose
   model/provider details. Restart the app after replacing an installed model pair; **Apply
   processor** also reloads the shared piece model when changing the processing preference.
4. For model investigation, developer review ZIPs retain the exact analyzed, unpainted board image
   and `predictions.json`, including model SHA-256, confidence, source identity, coordinate units
   and an optional note. Original model boxes and optional image-fitted `orientedOutline` polygons
   are stored separately. Predictions remain unreviewed and never become training labels
   automatically. The Camera screen no longer provides the Save detection example control.

Camera, crop, processing or model changes invalidate in-flight results. Diagnostic preview
detections, when enabled internally, expire after two seconds. Loading or inference failures leave the normal camera
preview and manual game available and report the ML failure.

Training photos, reviewed labels, PyTorch checkpoints, environments and diagnostic logs remain
ignored local artifacts. After a future candidate passes validation, promote only its accepted
ONNX file and matching manifest together into `assets/models/pieces/`; these are the source
build's deployment inputs. See the [tracked model inventory](../assets/models/README.md).

### Weak train detections

After the standard tiled pass, at most two train proposals below the gameplay confidence floor
receive a centered 640×640 retry using the same unmodified board frame. A retry must independently
detect a strong train matching the proposal's position and size, entirely inside its tile and
separate from already strong detections. Unrelated or ambiguous retry outputs are ignored.
The usual merge, route geometry, colors and fresh-frame checks still apply; no game-state or
historical occupancy is substituted for missing detections. Retry and recovery counts appear in
the board-interaction diagnostics. A frame with no usable proposal remains conservative.

Route matching uses a validated visible train-body center when the independent image fitter
produces a reliable rectangle. The original ML box remains unchanged and supplies the fallback
center. Size, shape and displacement limits reject unsuitable fits; lane-separation margins and
the requirement for distinct current detections remain in effect. A September 22 Boston–New York
case showed why this matters: the model detected both blue trains strongly, but the upper box's
center was too close to the neighboring lane. The fitted body resolved that positional error
without retraining the detector. The [three-image replay and regression results](evidence/boston-lane-centers-2026-09-22/validation.md)
record the measured scope and the remaining live-play check.

Chicago–Duluth live logs contain intermittent weak/missing middle-train predictions. The supplied
screenshot detects all three after resampling, so it cannot establish a live recovery rate. Existing
training/evaluation metrics describe the original tiled detector; broader retry accuracy still needs
live-camera validation and exact-frame examples.

Validation on September 19: 120 focused detector, camera-flow and board-verification tests passed.
Both screenshot crops retained the same 92 train and three marker detections. In a diagnostic
test with an injected weak proposal at the logged middle-train location, the retry independently
detected the train at approximately 0.95 confidence. The same proposal on the empty-board reference
was rejected, and that reference retained zero detections. This injected-proposal check does not
reproduce the original raw-camera dropout.

### Score markers by color

The internal score-marker reader estimates blue, red, green, yellow and black marker values from
the same analyzed image. The former **Score track** preview cards have been removed. After ML
locates a `player-marker`, a separate local
reader samples its interior color and maps its center to the perimeter of the upright classic
USA board. It reads the printed 1–100 track, including 20, 50, 70 and 100 at its corners;
it cannot determine how many full laps a player has completed. Train colors and route
ownership remain unread, and no score reading changes game state.

Keep the crop close to all four outer board edges. Side-by-side markers aligned with the
same score-track row or column can share a value; readings are not assigned distinct scores.
A marker diagonally inward beside a clearly read corner marker may share 20, 50, 70 or 100.
This requires a known-color anchor independently agreeing on both adjoining edges, a small
bounded inward displacement, and no direct reading of a different cell. It does not widen
ordinary score-cell boundaries or infer a score from the game's requested target.
Unknown color, off-track or ambiguous positions do not produce a numeric value. Missing colors
are marked **Not detected**, and multiple detections of one color are marked **Multiple markers**
rather than choosing one. Changing camera/crop/model, stopping capture or letting results expire
clears internal detections and readings.

The score reader does not retrain or alter the detector, its boxes, confidence or thresholds.

Board inventory checks resolve one specific classification conflict: a compact train box
strongly overlapping a confidently detected scoring marker with a clear score and matching
pixel color is treated as a second label for that marker. Raw model detections remain intact
for diagnostics and training review. This does not ignore the score-track area: separate,
elongated, ambiguously positioned or differently colored train detections still count, and
missing route trains still block verification. Reload diagnostics also record the score-marker
readings and outlines alongside unexpected-train details to make future conflicts identifiable.

Scoring moves and reload checks verify the requested marker's color and position. An unrelated
unknown-color detection elsewhere on the board does not block a clearly read target. Unknown
outlines overlapping or immediately beside it still require clarification, as do duplicate target
colors or unclear target positions. Reload logs all marker readings while checking markers and
shows the relevant detection centers as yellow spheres. **Check board myself** opens the explicit
manual reconstruction and confirmation workflow if the camera cannot resolve the scene. This is
operator attestation, not a successful camera reading; the required saved photo remains mandatory.

`GoldenTicket.MlPieceSmoke` includes `ScoreMarkers` alongside its original detections for
offline photo checks. These are experimental readings; new lighting and crowded-marker
examples still need review.
See the [score-marker validation](evidence/score-markers-2026-09-13/validation.md), including
the supplied photo, shared score 11 and occupied-corner regressions.

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
normalizes that crop as above. The technical Camera preview displays the original camera frame;
it does not change this model input. This first
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
beside it is labeled. At that stage, local collection version `04` contained **32 photos and 1,126 labels**,
with related layouts kept together. See the
[Boston false-positive check](evidence/boston-false-positive-2026-09-13/validation.md).

The user then requested retraining. The [second model](evidence/ml-retrain-2026-09-13/validation.md)
uses all 32 reviewed photos and was installed for the then-current local preview. At the unchanged 0.30 threshold,
the fixed comparison improves from 21 misses and eight extras to zero of each across these photos.
The crowded black markers are detected and the Boston printed-slot extra box is gone; the real
yellow train near Little Rock remains detected. CPU/DirectML fixture checks agree.
**These are in-sample results:** the photos were used in training. New capture sessions are still
needed to judge generalization and intermittent live failures. The previous model is retained for
rollback. Restart the app to load a new installed pair in all sessions.

A subsequent changed layout, photo `184806`, was reviewed separately without further training.
The frozen second model matches **78 of 79 trains and all five markers, with no extras**. A yellow
train at the Denver end of Salt Lake City–Denver scores 0.2993, just below the unchanged 0.30 cutoff.
CPU and DirectML agree. See the [new-layout check](evidence/new-layout-2026-09-13/validation.md);
this is an unseen layout from the same capture setup, not an independent capture-session test.

The user subsequently requested another retrain including that photo. Collection `05` now has
**33 photos and 1,210 labels**. That third model matches all labels at the unchanged
cutoff, with no extras; the Denver yellow train's score rises from 0.2993 to 0.9290. Six actual
CPU/DirectML fixtures match all 352 reviewed objects, with one documented subpixel box difference
from nearly tied duplicate proposals. All 33 photos, including `184806`, are now training examples.
See the [Denver retraining and deployment record](evidence/ml-retrain-denver-2026-09-13/validation.md).

Photo `202009` adds a stronger-shadow example of two parallel black trains near Denver.
In this saved photo both middle trains have retained predictions, but the upper box extends
over its neighbor. CPU and DirectML agree. Separate body labels and frozen outputs were kept
before further training, preserving collection `05`. See the
[parallel-train localization review](evidence/parallel-black-shadows-2026-09-13/validation.md).

The requested [fourth-model retraining](evidence/ml-retrain-shadows-2026-09-13/validation.md)
includes both `202009` and `202150` in collection `06`: **35 photos and 1,390 labels**.
The selected final checkpoint matches all labels without extras at the unchanged thresholds,
and upper parallel-train IoUs improve from about 0.51 to 0.85 / 0.84 with no lower-edge spill.
An earlier checkpoint was rejected because it added a false positive to an older photo.
Nine actual CPU/DirectML fixtures pass, including that regression case. These are in-sample
results; they do not establish reliability through every live frame. Reload the ML model
in an already open preview after installing the new local pair.

The [fifth-model training](evidence/ml-retrain-miami-shadows-2026-09-13/validation.md) adds
the shifted-light `202835` photo to collection `07`: **36 photos and 1,481 labels**.
Empty printed Miami slots and cast shadows remain background. The frozen model already
matches all pieces in this saved frame; the reported live extras are not reproduced here.
An early checkpoint adds two extras to `202150` and is rejected. The final checkpoint
preserves all 1,481 matches without extras, retains distinct parallel black trains, and
passes ten labeled CPU/DirectML fixtures plus the separate untrained `202727` score check.
Both `202835` and `202727` read yellow 20, blue 15, red 11, black 11 and green 50. This
training inclusion does not establish that intermittent live outlines are fixed.

The [sixth-model training](evidence/ml-retrain-light-variation-2026-09-13/validation.md) adds
`203020` with unchanged pieces and another shadow direction: **37 photos and 1,572 labels**.
Both first-run checkpoints are rejected for a missed older marker or a duplicate train box.
A recorded seed-only retry passes every label without extras, plus 12 labeled CPU/DirectML
fixtures and the separate `202727` score fixture. All five expected scores match on `202835`,
`203020` and `202727`. The installed final checkpoint preserves separate parallel black trains.
The rejected marker case reveals a tile-ownership boundary gap; this training does not change
that runtime rule. The latest photo was already detected correctly before training, and these
checks remain in-sample rather than independent evidence of live reliability.

The [seventh-model training](evidence/ml-retrain-large-lighting-2026-09-13/validation.md)
adds the major-lighting photo `203656`: **38 photos and 1,663 labels**. Source review
retains the same 86 trains and five markers with cast shadows excluded. The final
checkpoint repeats the earlier marker seam miss and is rejected; the saved epoch-30
fallback matches every label without extras and passes 13 labeled CPU/DirectML fixtures
plus the untrained score fixture. All five scores match on all four score-check photos.
The installed model keeps distinct parallel black trains. Display orientation still
falls back to upright boxes for some trains; training does not change the fitter or
tile-ownership rule. Independent-session and live-camera acceptance remain open.

The [Pixel webcam HQ training](evidence/ml-retrain-pixel-hq-2026-09-14/validation.md)
adds `20260914-182629`, a different layout with **43 trains and no scoring markers**.
The collection now has **39 photos and 1,706 labels**. The final epoch-40 export
matches every reviewed object without extras and passes 14 labeled native CPU/DirectML
fixtures plus the separate untrained score fixture. The installed model preserves the
earlier parallel-black and Miami background checks. On the HQ photo, 36 train outlines
receive an orientation fit and seven retain ordinary rectangles. The previous model
already detected all 43 trains; this adds HQ training coverage and preserves saved-photo
results, rather than establishing improved independent-session accuracy.

The current difference detector remains available to developer diagnostics for comparison;
the learned detection pipeline does not call it. The old lighting failure observations are
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
