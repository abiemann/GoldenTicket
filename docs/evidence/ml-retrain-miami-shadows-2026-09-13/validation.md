# Shifted-light training example, September 13, 2026

The user requested training with `GoldenTicket-board-20260913-202835.png` after moving
the light and observing intermittent extra outlines, especially near Miami. The original
photo is 3456 × 2160 pixels. It is a related capture from the existing board and camera,
not an independent capture session.

## Reviewed data

Independent native-image reviews identify **86 physical trains and five score markers**.
The left half contains 42 trains and the right half 44. The train placements match the
previous `202150` photo; marker positions have changed. Marker readings are yellow 20,
blue 15, red 11, black 11 and green 50. Red and black occupy the same score row.

The Miami approaches contain eight real trains: three blue on Atlanta–Miami, two blue
on Charleston–Miami, and three red on New Orleans–Miami. Intervening printed route
spaces and the cast shadows remain background. Ten source-reviewed diagnostic polygons
record seven empty Miami slots, two red-train shadow interiors and the empty third
yellow Atlanta–New Orleans slot. These polygons are lookup regions, not new object labels;
nearby real-piece boxes may overlap their rectangular neighborhoods.

Collection `07` appends this photo to all 35 unchanged entries in collection `06`, giving
**36 photos and 1,481 labels: 1,327 trains and 154 markers**, including four empty-board
controls. Every source hash and dimension was checked. The related September 13 capture
group is preserved. Photo `202727` remains outside training and serves as a separate,
related-photo score-reading check.

- New source SHA-256: `24117cdb936a775c6d9d29f6c8508a3c926cbca15cd02a49105b45305a439dc2`.
- Collection `07` SHA-256: `c048c8ef79d267fae2df77c942208cca76dd8c095811bbe47849b33d8d5aac61`.
- Frozen fourth model SHA-256: `4497e7c1f214bfd1e491788f9b83f5a00073a81ef09087302aacf2fad2182c27`.
- Initial epoch-40 checkpoint SHA-256: `d0c573440c9de70a79c5d8484507631f5df76a7705a855fac6aa4c89218d0133`.

## Frozen result and scope of the report

The installed fourth model already matches all **91 labels, with no extras or misses**, in
this saved photo. CPU and actual DirectML agree, and the exact Python runtime path reproduces
the CPU candidates in the same order with identical confidences. The full 36-photo baseline
also matches all 1,481 labels without extras. Previously cached outputs for all 35 earlier
photos and the new frozen capture were reproduced before comparison.

The reported live false positives are therefore **not reproduced by this saved frame**.
This photo supplies another reviewed lighting example; it cannot establish that retraining
fixed the intermittent live behavior. A future failure capture with its analyzed image and
predictions is needed to measure that particular behavior directly.

## Training and acceptance protocol

The recorded run uses the existing two-class YOLOX-Nano trainer, initialized from the
installed fourth model's final checkpoint. It trains for 40 epochs, 512 samples per epoch,
batch size 16 and seed 20260917. All 36 reviewed photos are used for training. Augmentation,
tile geometry, midpoint ownership and runtime thresholds remain unchanged: confidence
0.30, classwise NMS IoU 0.45 and one-to-one same-class evaluation IoU 0.50.

The trainer selects its earliest strict best threshold-swept F1 checkpoint. The recorded
fallback is to evaluate the saved final checkpoint if that selection fails the fixed
runtime checks. Each attempt and its outputs are preserved. Acceptance requires no
additional false positives or misses on any reviewed photo, distinct parallel black
trains in `202009` and `202150` with both IoUs at least 0.75, and native CPU/DirectML
checks on ten labeled fixtures. Both `202835` and the untrained `202727` must retain the
five expected score readings. Installation also requires visual review and preserved
rollback copies of the preceding model and manifest.

All post-training scores on the 36-photo collection are in-sample regression diagnostics.
They do not establish accuracy in new sessions or reliability in every live frame.

## Checkpoint comparison

Training completed in approximately 379 seconds on the RTX 4080 Laptop GPU. The trainer
selected epoch 20 using its threshold-swept criterion, but that checkpoint adds two
false positives to the earlier `202150` photo at the fixed 0.30 threshold. It matches
all 1,481 labels but fails the no-regression requirement. Its model, checkpoint and
complete evaluation remain preserved; it was not installed.

Following the recorded fallback, the final epoch-40 checkpoint was exported separately
without further training or threshold changes. Its fixed-runtime result is:

| Reviewed set | Frozen fourth model TP / FP / FN | Final checkpoint TP / FP / FN |
| --- | --- | --- |
| All 36 photos | 1,481 / 0 / 0 | 1,481 / 0 / 0 |
| Previous 35 photos | 1,390 / 0 / 0 | 1,390 / 0 / 0 |
| New `202835` photo | 91 / 0 / 0 | 91 / 0 / 0 |
| Four empty controls | 0 / 0 / 0 | 0 / 0 / 0 |

The final checkpoint SHA-256 is
`a8b3c2872cf308b03efdf6087e9853d678d14663984b0ffafaa48e3b58e868b8`.
Its model is `goldenticket-yolox-nano-retrain-reviewed-07-r1-final`, SHA-256
`eced4756836959ce99cbb9992058a34a81fa3f6dc88c36c8707b4c741184083f`;
the manifest SHA-256 is
`7737405ff2ffee60955a773ecdb4e3327f0cec32835b4fb09f612c67759d2f4e`.
The ONNX checker and PyTorch/ONNX Runtime CPU comparison pass with output shape
`[1,8400,7]` and maximum absolute difference 0.005859375 under combined relative
tolerance 0.001 and absolute tolerance 0.002. Model size remains 3,728,476 bytes.

## Native runtime and visual checks

All ten labeled fixtures pass on CPU and actual DirectML, with no GPU fallback:
`202009`, `202150`, `184806`, `171132`, `172806`, `173706`, `112309`, the empty
`20260912-145957`, `125240`, and the new `202835`. The separate untrained `202727`
fixture also passes its score-reading check. Both providers read yellow 20, blue 15,
red 11, black 11 and green 50 on both score fixtures. Missing markers are not filled in
from expected values; these are the actual runtime outputs.

Every CPU result matches the exact Python runtime path. The worst matched CPU/GPU
box IoU is 0.999996626, maximum source-edge difference is 0.000116730 pixels, and
maximum confidence difference is below 0.000000290. Native providers are substantiated
by recorded execution profiles. Both parallel black trains retain distinct predictions;
upper/lower IoUs are approximately 0.934 / 0.936 in `202009` and 0.940 / 0.939 in
`202150`, with no lower-edge spill from the upper prediction.

All eight Miami trains remain matched and none of the ten reviewed background polygons
intersects an unmatched retained prediction. Intersections from correctly matched real-train
boxes are excluded from that false-positive count. This geometric distinction matters when
an axis-aligned box around a diagonal piece includes some nearby printing or shadow.

Native full-photo and Miami previews were inspected. There are no extra boxes on empty
slots or shadows. The conservative orientation fitter still leaves some train outlines
axis-aligned; the blue Atlanta–Miami train nearest Miami now uses that fallback. This
display change does not alter its accepted detection. The fitter produces 53 angled train
outlines in this photo, compared with 52 before training; its code is unchanged.

## Local installation

The verified final model and manifest were installed in the canonical local model directory
and both existing Debug and Release application model directories. All three installed
pairs match the selected hashes. Exact prior canonical and application pairs are retained
under `artifacts/piece-training/model-before-reviewed-07/` for rollback. The deployment
receipt is `retrain-reviewed-07-deployment.json`; final verification is recorded in
`retrain-reviewed-07-final-checks.json`.

Final verification confirms all 36 source hashes and dimensions, unchanged prior 35
annotation entries, selected checkpoint, fixed and native checks, preserved evidence and
native binaries, and 88 local documentation links. The runtime detection threshold,
orientation fitter, corner model and score-reader code were not changed for this training
iteration. No application process was restarted. Use **Reload ML model** in an already
open preview to load the new local pair. No commit, push or release was requested here.

## Local artifacts

Source reviews, native crops and the frozen outputs are under
`artifacts/piece-training/miami-shadows-202835/`. Collection and protocol files are
`labels-reviewed-07.json`, `annotation-progress-07.json` and
`retrain-reviewed-07-protocol.json`. Training runs and fixed evaluation reports are under
`artifacts/piece-training/runs/` and `retrain-reviewed-07-evaluation/`.
Images, labels, raw predictions and experimental weights remain ignored local artifacts.

Related evidence: [previous shadow retraining](../ml-retrain-shadows-2026-09-13/validation.md),
[score readings](../score-markers-2026-09-13/validation.md), and
[training commands](../../../tools/piece-training/README.md).
