# Major lighting change, September 13, 2026

The user requested training with `GoldenTicket-board-20260913-203656.png` after making
large lighting adjustments. The original photo is 3456 × 2160 pixels, SHA-256
`fad29dfc4f69b8e262716e815de7969e3ab08470f39fa7d4abfaf89b92041750`.
Train bodies and score markers are reviewed against the source image, excluding cast
shadows and empty printed route spaces. Model predictions are not ground truth.

Source review confirms **86 trains and five score markers**, with all previous body
positions and bounds retained. Collection `09` contains **38 photos and 1,663 labels:
1,499 trains and 164 markers**, including four empty-board controls. All 37 earlier
records remain unchanged. Every source hash and dimension is verified.

- Collection SHA-256: `f03b3a1b16a1a439585ed6579203edbdd04f218368415ed83c105c538a7f3dac`.
- Protocol SHA-256: `1da981705058f6b6fddc5e4e3648c2bf79caad3cbc6ed59a76f730a8b86d4605`.

The frozen sixth model is `goldenticket-yolox-nano-retrain-reviewed-08-r2-final`, SHA-256
`93f53555883d1aa45c0db794c100da8dcb10d14d33d81f5a360505256b75c487`.
Its model/manifest pair and final trainable checkpoint are preserved before training.
The initializer is `runs/retrain-reviewed-08-r2/last.pth`, SHA-256
`e4fbf2f87105fc6dea9f654d97b4ebb05885af204cef1d27181341adeb5149a4`.

## Training and acceptance protocol

The existing two-class YOLOX-Nano trainer uses 40 epochs, 512 samples per epoch,
batch size 16 and seed 20260920. The final epoch-40 export is evaluated first; the
trainer's threshold-swept selection is preserved as a fallback only if the final export
fails. Labels, runtime geometry, confidence threshold 0.30 and NMS IoU 0.45 stay fixed.

Acceptance requires every reviewed object to match one-to-one at same-class IoU at least
0.50 with no extras or misses. The two earlier Denver parallel black pairs additionally
require separate upper/lower predictions, each with IoU at least 0.75. The ten reviewed
Miami background polygons remain checks on their original `202835` source. The new
photo's shadows are inspected separately rather than reusing those polygons.

Native validation covers 13 labeled photos on CPU and actual DirectML, including the
earlier `130924` tile-boundary regression, plus the separate untrained `202727` score
fixture. `202835`, `203020`, `203656` and `202727` must each read yellow 20, blue 15,
red 11, black 11 and green 50. Red and black share the same score row.

All reviewed photos are used for training. Saved-photo regression results do not measure
independent-session accuracy or establish reliability through every live frame. The
known tile-ownership edge case documented in the prior run is not changed by training.

The pretraining native capture finds 86 trains and five markers. CPU output agrees
with the Python evaluator's exact runtime geometry; CPU/DirectML count agreement is
91 of 91, with minimum matched IoU 0.99999708 and maximum confidence difference
1.759e-7. This frozen capture is preserved before label-based scoring and training.

The complete baseline comparison matches **all 1,663 labels with no extras or misses**.
All previous 37 saved outputs and the new frozen capture reproduce exactly. Native
visual review also confirms the new frame's 91 matches: eight Miami trains are present
without shadow/printing extras, and the central Denver black trains remain separate.
Their baseline upper/lower IoUs are 0.923915/0.793838; the lower box is approximately
6.6 source pixels short at its bottom. The baseline display fitter rotates 50 train
outlines and retains original boxes for the other 36. These measurements are recorded
separately from detection counts.

## Checkpoint comparison

The 40-epoch run completed in approximately 351 seconds. Its final export is rejected:
1,662 matches, no extras and one missed red score marker in `130924`. This repeats
the known tile-ownership seam case. The tile at x512 predicts center x1088.383 with
confidence 0.93104; the tile at x1024 predicts x1087.673 with confidence 0.94104.
Both fall across the opposite tile's ownership boundary and are discarded before NMS.
The new photo still matches all 91 objects. Full results and raw proposal diagnosis are
preserved under `retrain-reviewed-09-evaluation/candidate-r1-final/`.

The final model SHA-256 is `78773bb3e3014265818fa0da76e84376cf3c4dd440cf0866881a64c4ebb66750`.
The fallback is the trainer's saved epoch-30 checkpoint, explicitly re-exported with
checkpoint provenance as `retrain-reviewed-09-r1-best`. It is evaluated against the same
fixed thresholds and acceptance requirements, without additional training or label edits.

The epoch-30 fallback passes the complete fixed comparison: **1,663 matches, zero
extras and zero misses**, including all 91 objects in `203656`. All 37 earlier photos
and four empty controls remain correct. Both earlier parallel pairs pass their tighter
localization gates: upper/lower IoUs are 0.957426/0.948320 in `202009` and
0.965920/0.951996 in `202150`, with distinct boxes and no downward spill. All ten
original Miami background polygons remain free of extra detections.

- Model: `goldenticket-yolox-nano-retrain-reviewed-09-r1-best`.
- Model SHA-256: `4a111ee5027516fafa1a9a039f54bff16e376486298b7d71bae88410e154dc40`.
- Manifest SHA-256: `3f3b53686f0ed087153748a0f85b4e00d7f3c0b96583a0c01d0d006386c3610a`.
- Trainable checkpoint: `runs/retrain-reviewed-09-r1/best.pth`, SHA-256
  `cebddfa224a03a1f9b91f25c49aea0638d8b53aa951ef431d21485b0bd618893`.

ONNX checking and PyTorch/ONNX Runtime CPU parity pass with output shape `[1,8400,7]`.
Maximum absolute difference is 0.0059814453125 within the combined relative tolerance
0.001 and absolute tolerance 0.002. The two-class model remains 3,728,476 bytes.

## Native and visual validation

All 13 labeled native fixtures match every object without extras on both CPU and actual
DirectML, with no GPU fallback. The separate `202727` score fixture also passes. All
five expected scores are correct on `202835`, `203020`, `203656` and `202727` for both
providers. Across the 14 native fixtures, minimum GPU/Python IoU is 0.999996869,
maximum source-edge difference is 0.00011673 pixels and maximum confidence difference
is 2.357e-7. Model, labels and executable hashes remain unchanged through validation.

The new photo's full preview and native Miami, Denver and Los Angeles comparisons were
visually reviewed. All 86 trains and five markers are present without extras. The
central Denver pair remains separate; upper/lower IoUs improve from 0.9239/0.7938 to
0.9263/0.9436. All eight Miami trains match, with IoUs from 0.8176 to 0.9664.

The display fitter now rotates 42 outlines instead of the baseline's 50: 11 new upright
fallbacks and three new fits. Some visibly diagonal trains, including the red train
nearest Miami, consequently retain ordinary rectangles. These objects remain detected;
10 of the 11 new fallback boxes improve their overlap with the reviewed body bounds.
All 14 changed-fit close-ups were inspected. No angle changes above five degrees occur
where both versions retain fitted outlines, and no spurious new diagonal fit was seen.
This is a display limitation; the fitter is unchanged by training.

## Local installation

The accepted epoch-30 model/manifest pair is installed in the canonical model folder and
both Debug and Release application model folders. Exact copies of the previous sixth
model pairs are retained under `artifacts/piece-training/model-before-reviewed-09/`.
The receipt is `artifacts/piece-training/retrain-reviewed-09-deployment.json`.
Use **Reload ML model** in an already open preview to activate the installed pair.

Final verification checks all three installed pairs and three rollback pairs, dataset
and source hashes, selected checkpoint provenance, the rejected final checkpoint,
fixed/native results, score readings and affected documentation links. Its record is
`artifacts/piece-training/retrain-reviewed-09-final-checks.json`. Application source and
runtime thresholds remain unchanged; no release or push is performed in this iteration.

## Local evidence

Source review, frozen predictions and native comparison are under
`artifacts/piece-training/large-lighting-203656/`. Collection and protocol files are
`labels-reviewed-09.json`, `annotation-progress-09.json` and `retrain-reviewed-09-protocol.json`.
The frozen pair/checkpoint is under `runs/retrain-reviewed-09-baseline/`; training and
evaluation outputs remain in ignored local artifact directories. No release is produced.

Related: [previous lighting retraining](../ml-retrain-light-variation-2026-09-13/validation.md),
[score readings](../score-markers-2026-09-13/validation.md),
and [training commands](../../../tools/piece-training/README.md).
