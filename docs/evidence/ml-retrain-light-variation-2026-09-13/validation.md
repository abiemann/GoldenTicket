# Additional lighting variation, September 13, 2026

The user requested training with `GoldenTicket-board-20260913-203020.png`, confirming
that the pieces have not moved and only the light and shadows have changed. The original
photo is 3456 × 2160 pixels. Source-image reviews confirm that the previous labels still
bound the same **86 trains and five score markers**, without including the moved shadows.
No presence or coordinate edits were required after review. In particular, the eight
Miami trains and the separate parallel black trains near Denver remain correctly labeled.

The markers remain yellow 20, blue 15, red 11, black 11 and green 50. Red and black share
the same score row. Marker labels were reviewed separately from the train labels; model
predictions were not used as ground truth.

## Data and protocol

Collection `08` contains **37 photos and 1,572 labels: 1,413 trains and 159 markers**,
including four empty-board controls. All 36 entries from collection `07` are unchanged.
Every source hash and image dimension was verified before training. The new photo retains
the related September 13 capture group; `202727` remains outside training as a separate
score-reading fixture from the same physical setup.

- New source SHA-256: `ae5c7497dd51a0128854f5ad2ca0afb47be8a22148e607a3183c1698e3c958d3`.
- Collection `08` SHA-256: `026e0cc2266504628031d775ebb001d8198825a0614da2b4926ec6108db8fb73`.
- Frozen fifth model SHA-256: `eced4756836959ce99cbb9992058a34a81fa3f6dc88c36c8707b4c741184083f`.
- Initial checkpoint SHA-256: `a8b3c2872cf308b03efdf6087e9853d678d14663984b0ffafaa48e3b58e868b8`.

The existing YOLOX-Nano two-class trainer uses all 37 reviewed photos, initialized from
the installed fifth model's final checkpoint. The bounded run uses 40 epochs, 512 samples
per epoch, batch size 16 and seed 20260918. Existing augmentation and runtime geometry
remain unchanged. Runtime confidence stays at 0.30 and classwise NMS IoU at 0.45.

The protocol chooses the final epoch-40 checkpoint for fixed-runtime evaluation first.
The earlier two runs showed that the trainer's threshold-swept winner can regress at
the actual preview threshold. Its automatic export and checkpoint are still preserved,
but are a fallback only if the final checkpoint fails; no operating threshold is changed.
Neither the trainer nor the application code is modified for this iteration.

Acceptance requires all 37 photos to match reviewed objects one-to-one at same-class
IoU at least 0.50, with no added extras or misses. Both parallel black trains in `202009`
and `202150` must also have distinct predictions with IoU at least 0.75 for each body.
Eleven labeled native CPU/DirectML fixtures and the separate `202727` score fixture are
required. The three photos `202835`, `203020` and `202727` must retain all five expected
score readings. Earlier Miami background polygons remain checks on their original
`202835` photo; changed shadows in `203020` are reviewed from its own source image.

All 37 labeled photos become training examples. These are in-sample regression checks,
not independent-session accuracy or proof of reliability through every live frame.

## Frozen comparison and rejected first run

The frozen fifth model matches all 1,572 labels in the 37-photo collection, with no
extras or misses. All previous 36 cached outputs and the new pretraining capture are
reproduced exactly. The new `203020` photo is also correct on actual CPU and DirectML,
including all five expected marker scores.

The first 40-epoch run took approximately 406 seconds. Its final checkpoint fails
fixed-runtime evaluation with 1,571 matches and one missed red marker in `130924`.
Its two strong overlapping-tile proposals fall on opposite sides of the ownership
boundary: the tile starting at x512 predicts center x1088.256, while the tile starting
at x1024 predicts x1087.732. Both proposals are rejected as non-owning before NMS,
despite confidences above 0.91. Native CPU and DirectML reproduce the miss. This is a
known tile-ownership edge case; retraining does not repair the underlying runtime rule.

The saved automatic epoch-20 checkpoint restores that marker but adds an extra train
in `203020`. The extra box straddles the already separately matched blue and red trains
on Kansas City–Saint Louis. Its IoUs with the real pieces are approximately 0.306 and
0.437, so it is a duplicate spanning neighboring pieces, not another physical train.
Native CPU and DirectML both reproduce 91 matches plus this extra. Both first-run
models and their complete evaluation and failure crops are preserved and were rejected.

A recorded second run uses the same data, initializer, training settings and acceptance
criteria, changing only the seed to 20260919. The final checkpoint is checked first,
with the trainer-selected checkpoint retained as a fallback. The `130924` photo is
added to native regression fixtures, raising acceptance to **12 labeled fixtures plus
the separate `202727` score fixture**, on both CPU and DirectML.

## Second-run fixed result

The second run completed in approximately 383 seconds. Its separately exported final
checkpoint matches **all 1,572 labels without extras or misses** across all 37 photos,
including all 91 objects in the new photo and the red marker missed by the rejected
first run. All 36 earlier photos and the four empty controls remain correct at the
unchanged runtime threshold. The trainer-selected checkpoint is retained but was not
needed as a fallback. Intermediate evaluated checkpoints are preserved for diagnostics.

The selected checkpoint is `runs/retrain-reviewed-08-r2/last.pth`, SHA-256
`e4fbf2f87105fc6dea9f654d97b4ebb05885af204cef1d27181341adeb5149a4`.
The final model is `goldenticket-yolox-nano-retrain-reviewed-08-r2-final`, SHA-256
`93f53555883d1aa45c0db794c100da8dcb10d14d33d81f5a360505256b75c487`;
manifest SHA-256 is `60be21d459dd184fe7ed5d940106baca3faa76be630bcc3105179d507642f0ed`.
The model remains 3,728,476 bytes. ONNX checking and PyTorch/ONNX Runtime CPU parity
pass with output shape `[1,8400,7]`; maximum absolute difference is 0.0125732421875
within the combined relative tolerance 0.001 and absolute tolerance 0.002.

The seed-20260919 retry protocol is `retrain-reviewed-08-r2-protocol.json`.
Its explanation was corrected after native crop review established that the first-run
extra spans two real trains. The initial protocol is preserved separately; data, seed,
training settings and acceptance requirements did not change with that clarification.

## Native and visual checks

All 12 labeled fixtures pass with no extras or misses on both CPU and actual DirectML,
including the earlier `130924` marker regression and all 91 objects in `203020`.
The separate untrained `202727` fixture also passes its score check. All five expected
scores match on `202835`, `203020` and `202727` for both providers. Across these 13
native fixtures, minimum GPU/Python box IoU is 0.999996732, maximum source-edge
difference is 0.000123596 pixels and maximum confidence difference is 2.925e-7.
Model, label and executable hashes remain unchanged through validation.

The earlier parallel black pairs retain distinct predictions: upper/lower IoUs are
0.924666/0.977492 in `202009` and 0.940575/0.979373 in `202150`. All ten reviewed
Miami background polygons in `202835` remain clear of extra detections. Full-photo,
Miami and Denver visual comparisons of `203020` show no extras or merged black pair.
The display fitter produces 51 rotated outlines instead of the baseline's 54: five
pieces fall back to upright boxes and two gain fitted boxes. Three new fallback boxes
contain visibly diagonal trains. This is a display limitation, not a missed detection;
no concerning rotations were observed in the fitted outlines.

## Local installation

The verified sixth model and manifest are installed in the canonical local model folder
and both Debug and Release application model folders. Their previous fifth-model pairs
are preserved exactly under `artifacts/piece-training/model-before-reviewed-08/`.
The installation receipt is `artifacts/piece-training/retrain-reviewed-08-deployment.json`.
No application source, runtime threshold or training label was changed by installation.
Use **Reload ML model** in an already open preview to load the installed pair.

The final verifier checks installed and rollback hashes, all source images, selected
checkpoint provenance, rejected-run results, native fixtures, score readings and affected
documentation links. Its output is `artifacts/piece-training/retrain-reviewed-08-final-checks.json`.

## Local evidence

Native source reviews, complete annotations and frozen outputs are under
`artifacts/piece-training/light-shadows-203020/`. Collection and protocol files are
`labels-reviewed-08.json`, `annotation-progress-08.json` and `retrain-reviewed-08-protocol.json`.
The preceding model and trainable checkpoint are frozen in `runs/retrain-reviewed-08-baseline/`;
new runs and comparisons are in `runs/retrain-reviewed-08-r1/`, `runs/retrain-reviewed-08-r2/` and
`retrain-reviewed-08-evaluation/`. Images, annotations, raw outputs and weights remain
ignored local artifacts.

Related evidence: [previous lighting retraining](../ml-retrain-miami-shadows-2026-09-13/validation.md),
[score readings](../score-markers-2026-09-13/validation.md), and
[training commands](../../../tools/piece-training/README.md).
