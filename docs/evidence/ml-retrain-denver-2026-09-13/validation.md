# Denver yellow-train retraining, September 13, 2026

The user explicitly requested another retraining run after confirming the missed yellow train
at the Denver end of Salt Lake City–Denver. Its original frozen-model score was 0.2992613, below
the unchanged 0.30 operating cutoff. The [prior diagnostic](../new-layout-2026-09-13/validation.md)
is preserved as the before-training result.

## Data and run protocol

Collection `05` appends the fully reviewed `184806` photo, with 79 trains and five score markers,
to the previous 32-photo collection. All prior 32 entries and original images are unchanged.
The resulting **33 photos contain 1,210 labels: 1,071 trains and 139 markers**, including four
empty-board controls. Existing capture-group and split metadata are preserved.

The new layout is now explicitly included in training with `--all-reviewed`. All resulting
scores on these 33 photos are **in-sample regression diagnostics**, including `184806`.
The historical unseen-layout result remains valid for the preceding model; it is not an
untouched test of this newly trained candidate. Independent new capture sessions remain needed.

- Labels SHA-256: `2fef12435d45166d360c82b431c30b8f39720a81f7f410cbe4f786d3862ce21d`.
- Previous model SHA-256: `b02c33b035ed529afca7be08e05d4d5cd7e25e9ad30e54728be09feaae1f4ad6`.
- Initial checkpoint: prior run `retrain-reviewed-04-r1/best.pth`, SHA-256
  `d52ac1e10de17d3a32532c7967d99afd664149cb8f88f97ee7d975be43de1e84`.
- New run: `artifacts/piece-training/runs/retrain-reviewed-05-r1/`.
- Protocol: 40 epochs, 512 augmented tiles per epoch, batch 16, seed 20260915, local RTX 4080
  Laptop CUDA training. Architecture and augmentation recipe are unchanged.
- `--initialize` loads model weights but starts a new AdamW/EMA schedule: three-epoch warmup
  toward learning rate 0.001, then cosine decay toward 0.00005. It is not an optimizer-state resume.
- The existing trainer selects the best checkpoint by threshold-swept F1 on reused September 13
  photos every ten epochs, retaining earlier checkpoints on a tie. Final comparison separately
  fixes confidence at 0.30, classwise NMS IoU at 0.45, midpoint tile ownership and one-to-one
  same-class box matching IoU at 0.50.

## Training and fixed comparison

The fresh baseline comparison on all 33 photos gives **1,209 matches, zero extras and one miss**.
The original 32 photos remain at 1,126 matches with no extras or misses; only the Denver yellow
train in `184806` is missed. All four empty controls remain clear. Three training geometry,
ownership and duplicate-matching tests passed.

Training completed all 40 epochs in 344.7 seconds and selected epoch 20. The selected checkpoint
SHA-256 is `58bd2da9e3ab86ec21acc055c5c8338ce20ebc98ac50fb6e7d50bfd56a408a7b`.
The exported model is `goldenticket-yolox-nano-retrain-reviewed-05-r1`, SHA-256
`c6ea0bf92894a7d3531e77c1488cd5c968530d8eaa279636fc17cd9024cf1751`.
No retry or training-protocol change was required.

| Photo group | Previous matched / extra / missed | New matched / extra / missed |
|---|---:|---:|
| All 33 photos | 1,209 / 0 / 1 | 1,210 / 0 / 0 |
| Previous 32 photos | 1,126 / 0 / 0 | 1,126 / 0 / 0 |
| New layout `184806` | 83 / 0 / 1 | 84 / 0 / 0 |

The exact-runtime-resize ONNX comparison matches all **1,071 trains and 139 markers**, with no
new miss or extra prediction on any photo. The four empty controls remain clear. The Denver
yellow train's score rises from **0.2992613 to 0.9290207** at the unchanged 0.30 cutoff. All three
crowded black-marker examples and the real yellow New Orleans–Atlanta train remain detected;
the empty Boston printed slot remains without an extra detection. Fresh baseline outputs exactly
reproduce the prior cached outputs for the original 32 photos and the frozen new-layout capture.

ONNX checker and PyTorch/ONNX CPU comparison passed. Maximum absolute tensor difference is
0.0145111 within the existing combined relative tolerance 0.001 and absolute tolerance 0.002;
the output shape remains `[1,8400,7]`. The trainer's final report covers the 15 September 13
photos (967 labels), while the separate fixed comparison above covers all 33.

## Production runtime and deployment

Six production .NET smoke cases cover `184806`, the three earlier failure photos, dense photo
`112309` and empty photo `145957`. Both CPU and DirectML match **all 352 reviewed objects**,
with zero misses or extras. Actual DirectML graph execution and no fallback are verified.
The Denver recovery, crowded markers, real yellow train near Little Rock and Boston printing
were visually checked in the native outlined crops.

One numerical difference was investigated rather than reported as exact parity. The initial
diagnostic requiring every CPU/GPU box IoU above 0.999 failed for one blue Duluth–Toronto train
in `184806`: minimum IoU was 0.979177. Two redundant proposals, rows 3174 and 3175 in tile
`(1024, 0)`, have CPU scores differing by only 0.000000040. Provider rounding changes their
order and therefore the NMS winner. The alternate archived proposal matches the GPU box;
both boxes correctly match the same reviewed train. Maximum edge displacement is only
**0.627 original-image pixels**. Visual inspection confirms the outlines coincide at normal scale.

The initial strict failure log and raw-proposal evidence are preserved. Final checks require
one-to-one same-kind CPU/GPU IoU above 0.95, less than one original-image pixel of edge displacement,
confidence differences below 0.0001, and all ground-truth objects matched without extras on both
providers. They pass for all six photos. Maximum observed confidence difference is 0.000000277.
Only the diagnostic acceptance check was adjusted after investigating this subpixel difference;
model output, application thresholds, NMS and runtime code are unchanged.

The verified ONNX/manifest pair is installed in the canonical local model directory and existing
Debug/Release development app outputs. Every destination hash was checked. The previous pair is
preserved in `artifacts/piece-training/model-before-reviewed-05/`; the separate baseline snapshot
also retains its training checkpoint. No corner model, application code, release bundle or camera
state was changed. Use **Reload ML model** in an already open app to activate the installed pair.
Source photo and collection hashes remain unchanged after training, evaluation and installation.

## Local provenance

Photos, labels, checkpoints, models and raw outputs remain in ignored local artifacts:

- `artifacts/piece-training/retrain-reviewed-05-protocol.json`: scope, input hashes and checks.
- `artifacts/piece-training/new-layout-184806/training-inclusion-05.json`: explicit new-photo inclusion.
- `artifacts/piece-training/runs/retrain-reviewed-05-baseline/`: preserved prior model pair and checkpoint.
- `artifacts/piece-training/runs/retrain-reviewed-05-r1/`: training provenance, environment, history and export.
- `artifacts/piece-training/retrain-reviewed-05-evaluation/`: exact runtime resize, fixed comparison and raw tiles.
- `artifacts/piece-training/retrain-reviewed-05-runtime/`: actual .NET CPU/DirectML fixture reports.
- `artifacts/piece-training/retrain-reviewed-05-runtime/redundant-proposal-evidence.json`: subpixel NMS-winner investigation.
- `artifacts/piece-training/retrain-reviewed-05-deployment.json`: installed pair hashes and rollback location.
