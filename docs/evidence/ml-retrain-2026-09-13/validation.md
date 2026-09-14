# Reviewed failure examples: model retraining, September 13, 2026

The user requested retraining after collecting the crowded black score marker, intermittent
yellow-train outline and Boston printed-route false positive. Collection `04` contains 32 photos
and 1,126 reviewed objects: 992 trains and 134 score markers. All photos, labels, weights and raw
outputs remain local ignored artifacts.

## Training and evaluation scope

The run explicitly uses **all 32 reviewed photos**, including the four empty controls. The
September 13 group contains the new failures; retaining the old ordinary train/validation split
would have excluded them from training. Original annotations and capture-group assignments remain
unchanged, with `--all-reviewed` recorded in the run and deployment manifest.

There is **no independent test group**. Results below are in-sample regression diagnostics of what
the new model has learned. Neither a random photo split nor reversing the two date groups would
make these reused images an independent test. Freeze this candidate and collect new capture
sessions to measure generalization, including changed lighting and crowded pieces.

- Source architecture/dependencies remain the pinned YOLOX-Nano experiment with two classes,
  `train` and `player-marker`; physical color is annotation metadata only.
- Initialized from baseline checkpoint epoch 20, SHA-256
  `33ca737a7f977fdc11ffa278b46cc84b4ce2e99973b10cd9694ece354c1c664d`.
- Local RTX 4080 Laptop GPU run: 40 epochs, 512 augmented tiles per epoch, batch 16, seed 20260914.
  The existing optimizer/augmentation recipe is unchanged. Training took about 362 seconds.
- The existing trainer selected epoch 30 using threshold-swept F1 on the reused September 13
  photos. This is explicitly in-sample selection. Final comparison fixes confidence at **0.30**,
  classwise NMS IoU at **0.45**, midpoint tile ownership and one-to-one matching IoU at **0.50**.
- Label SHA-256: `53077b7f0e24bd3399c600b38cb4a34880b5deb0d1cbb6b4ae6c22e116917fc1`.
  Original source hashes and labels were checked before and after training.

## Fixed comparison

Both ONNX models were evaluated on identical reviewed labels using the app's half-pixel bilinear
resize and filtering. The original model is
`420318a5cf2953b597aaf35fd627f737c64c8aa579bcc51d3c87f1d9be042211`.
The retrained model is
`b02c33b035ed529afca7be08e05d4d5cd7e25e9ad30e54728be09feaae1f4ad6`.

| Photo group | Old matched / extra / missed | New matched / extra / missed |
|---|---:|---:|
| All 32 photos | 1,105 / 8 / 21 | 1,126 / 0 / 0 |
| Original 29 photos | 936 / 4 / 18 | 954 / 0 / 0 |
| Three new failure photos | 169 / 4 / 3 | 172 / 0 / 0 |

All four empty controls remain at zero detections. The new model matches all 992 train labels
and all 134 score-marker labels, with no per-image regression at this operating point. These are
training-photo results, not a claim of perfect live recognition. The baseline reproduces the
historical 11-photo score of 693 matches, three extras and 18 misses.

Targeted saved-photo checks:

- **Crowded black score marker:** now matched in `171132`, `172806` and `173706`. Scores change
  from 0.0062 / 0.0564 / 0.0928 to 0.8023 / 0.8134 / 0.8175. Neighboring markers remain detected.
- **Boston empty yellow printing:** the extra box disappears in `173706`; the real red train
  remains detected at 0.9138. No new object class or route-color rule was added.
- **Yellow New Orleans–Atlanta train near Little Rock:** retained in all three photos at
  0.9236 / 0.9240 / 0.9253. It was already detected in these saved images by the baseline;
  an exact missed live frame is still needed to resolve the intermittent preview report.

## Runtime and deployment checks

ONNX checker and PyTorch/ONNX CPU parity passed for the fixed `[1,8400,7]` output. Maximum absolute
difference was 0.0076294 within the existing combined relative/absolute tolerance; no parity
tolerance was changed. Three training geometry/matching tests passed.

The existing .NET smoke tool loaded the candidate with the production hash/graph/tensor checks
on CPU and DirectML. Five fixtures covered all three new photos, dense photo `112309`, and empty
photo `145957`. All **268 predictions** matched between providers: minimum IoU 0.99999644 and
maximum confidence difference 0.000000234. Actual `DmlExecutionProvider` graph execution was
recorded with no provider fallback. The empty fixture returned zero candidates on both providers.
Outlined native crops of the three reported cases were visually checked. Orientation remains the
existing optional local image fit, not a newly learned angle output.

The selected model/manifest pair is installed in the canonical local model directory and existing
Debug/Release development app outputs. Destination hashes are verified. An already open preview
must use **Reload ML model**; a newly started app reads the installed pair normally. No application
code, corner model, thresholds, published release bundle or camera state was changed.

The preceding canonical model directory is preserved as `artifacts/piece-training/model-before-reviewed-04/`.
A separate complete baseline snapshot remains in `artifacts/piece-training/runs/retrain-reviewed-04-baseline/`.
For rollback, restore the old ONNX and manifest together to the canonical and intended app model
directories, then reload. Never mix manifests and model bytes.

## Local provenance

- `artifacts/piece-training/retrain-reviewed-04-protocol.json`: pre-comparison scope and checks.
- `artifacts/piece-training/runs/retrain-reviewed-04-r1/`: command, package lock, history,
  checkpoints, ONNX/manifest and trainer's 14-photo diagnostic. `evaluation.json` there covers
  September 13 only; the separate report below covers all 32.
- `artifacts/piece-training/retrain-reviewed-04-evaluation/`: exact-resize evaluator, baseline and
  candidate per-object results, raw tile outputs, `comparison-summary.json` and full comparison.
- `artifacts/piece-training/retrain-reviewed-04-runtime/`: actual provider reports, outlined
  images, target crops and `summary.json`.
- `artifacts/piece-training/retrain-reviewed-04-deployment.json`: installed file hashes and paths.

The evaluator's baseline outputs match the cached .NET CPU results on all three new photos,
including identical confidence and coordinate differences below 0.000000000001 board pixels.
