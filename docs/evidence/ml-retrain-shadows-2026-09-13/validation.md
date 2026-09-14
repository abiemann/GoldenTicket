# Retraining with stronger shadows, September 13, 2026

The user requested retraining with `GoldenTicket-board-20260913-202150.png` after
reporting unreliable detection in stronger shadows. The run also includes the separately
reviewed `202009` example of overlapping boxes around parallel black trains near Denver.
Both original files are 3456 × 2160 pixels, regardless of the attachment preview size.

## Reviewed data and frozen baseline

Collection `06` contains **35 photos and 1,390 labels: 1,241 trains and 149 markers**,
including four empty-board controls. The original 33 entries in collection `05` are unchanged.
Both new photos retain their related September 13 capture group. Native regional reviews
and a separate marker review label plastic bodies without including cast shadows or printed
track. The `202150` photo has 86 trains and five markers; relative to `202009`, it adds a
black train on Los Angeles–El Paso and a red train on Kansas City–Saint Louis.

The frozen third model finds all 89 labels in `202009`, but its upper parallel black-train
box has IoU 0.507 with the reviewed body and extends approximately 30 source pixels over
the lower train. This is a localization problem despite passing IoU 0.50 matching. See the
[original shadow review](../parallel-black-shadows-2026-09-13/validation.md).
On `202150`, the frozen model matches all 91 labels and produces one additional train
detection. The saved photos do not measure intermittent behavior in other live frames.

The extra prediction is over the empty second printed black Los Angeles–El Paso slot,
between separately detected real trains. It has confidence 0.829710 and survives the
normal confidence and overlap filters. The printed space remains background in the
reviewed labels. The fixed 35-photo baseline has **1,390 matches, one extra and no misses**;
all previous 33 cached results and both new frozen captures are reproduced exactly.

Source and label provenance:

- `202009` source SHA-256: `dc5672da26e97e4367516033a89e39385df9b541c8ded11541bb2ec7a68fd01c`.
- `202150` source SHA-256: `ac35cdf69d142b200d96f0711b5fee047ae4db305d3fbb2120a79c0fc6ec0aa7`.
- Collection `06` SHA-256: `a05b5cbcc79264003a9ea820e9eff22a68497a0619a10e1daea751e3883c3f6a`.
- Frozen third model SHA-256: `c6ea0bf92894a7d3531e77c1488cd5c968530d8eaa279636fc17cd9024cf1751`.
- Initial checkpoint SHA-256: `58bd2da9e3ab86ec21acc055c5c8338ce20ebc98ac50fb6e7d50bfd56a408a7b`.

## Training protocol

The recorded run uses the existing YOLOX-Nano two-class trainer, initialized from the
third model's checkpoint, with 40 epochs, 512 samples per epoch, batch size 16 and seed
20260916. All 35 reviewed photos are used for training. Existing augmentation, optimizer,
tiling, midpoint ownership and runtime thresholds remain unchanged. The exported runtime
cutoff is 0.30 with classwise NMS IoU 0.45.

The trainer selects `best.pth` by its existing threshold-swept F1 criterion, retaining the
earliest strict best. The protocol requires a separate exact-runtime comparison of all
35 photos, no added false positives or misses, and upper parallel-train IoU of at least
0.75 on both new photos. If the selected checkpoint fails those checks, the recorded
fallback is to compare the saved final checkpoint before considering further training.
Detection counts alone are insufficient for the parallel-piece case.

Before local installation, the candidate must also pass ONNX export checks, actual .NET
CPU/DirectML checks on eight fixtures, and visual review of the new photo overlays and
parallel pair. The preceding model must remain available for rollback.

## Checkpoint selection and fixed comparison

Training completed in approximately 382 seconds on the RTX 4080 Laptop GPU. The initial
automatic selection was epoch 20, the first checkpoint with perfect threshold-swept F1.
At the fixed 0.30 operating point it removes the `202150` false positive but adds an extra
train in the older `125240` photo. Its 1,390 matches, one extra and zero misses therefore
fail the per-photo regression requirement. This model was not installed; its export and
all 35-photo results remain preserved.

Following the recorded fallback, the final epoch-40 checkpoint was exported separately,
without further training or threshold changes. It passes the exact-runtime comparison:

| Reviewed set | Previous model TP / FP / FN | Final checkpoint TP / FP / FN |
| --- | --- | --- |
| All 35 photos | 1,390 / 1 / 0 | 1,390 / 0 / 0 |
| Previous 33 photos | 1,210 / 0 / 0 | 1,210 / 0 / 0 |
| Both new shadow photos | 180 / 1 / 0 | 180 / 0 / 0 |
| Four empty controls | 0 / 0 / 0 | 0 / 0 / 0 |

The upper parallel black-train box improves from IoU 0.507 to approximately **0.851** in
`202009`, and from 0.509 to **0.838** in `202150`. Its bottom edge no longer spills below
the reviewed body in either photo, compared with about 30 source pixels previously.
Both trains retain distinct predictions. Previous crowded-marker, Denver yellow-train,
Little Rock yellow-train and Boston false-positive cases remain correct.

The selected checkpoint SHA-256 is
`d0c573440c9de70a79c5d8484507631f5df76a7705a855fac6aa4c89218d0133`.
The exported model is `goldenticket-yolox-nano-retrain-reviewed-06-r1-final`, SHA-256
`4497e7c1f214bfd1e491788f9b83f5a00073a81ef09087302aacf2fad2182c27`.
ONNX checking and PyTorch/ONNX Runtime CPU parity pass with output shape `[1,8400,7]`;
maximum absolute difference is 0.00402832 within combined relative tolerance 0.001 and
absolute tolerance 0.002. Training geometry tests pass (3/3), and source hashes, label
bounds, related capture groups and unchanged prior entries were independently checked.

The rejected epoch-20 regression adds `125240` to the originally planned eight actual
CPU/DirectML fixtures, bringing the required final runtime check to nine photos.

## Native runtime checks

All nine fixtures match every reviewed label without extra detections on both CPU and
actual DirectML graph execution, with no GPU fallback. Minimum CPU/GPU box IoU is
0.999996604, maximum source-pixel edge difference is 0.00011673 and maximum confidence
difference is below 0.000000341. Both new parallel pairs retain separate predictions at
IoU at least 0.75 on both providers. The exact Python resize/inference path also reproduces
the production CPU results. Native whole-board overlays and before/after crops of the
parallel pair and empty printed Los Angeles slot were visually inspected.

The selected model and manifest are installed as a pair in the canonical local model
folder and existing Debug/Release app output folders. Exact prior pairs are preserved in
`artifacts/piece-training/model-before-reviewed-06/canonical/` and `app-pairs/` beneath
that archive. The application runtime, thresholds, corner model and camera settings are
unchanged. An already open app needs **Reload ML model** to use the replacement files.
The installation record is `artifacts/piece-training/retrain-reviewed-06-deployment.json`.

All post-training scores on these 35 photos are **in-sample regression diagnostics**.
They do not establish accuracy on an independent camera session or reliable detection
through every live frame. Future acceptance still requires new captures and lighting.

Local ignored provenance is in `artifacts/piece-training/labels-reviewed-06.json`,
`retrain-reviewed-06-protocol.json`, `runs/retrain-reviewed-06-baseline/`,
`runs/retrain-reviewed-06-r1/`, `runs/retrain-reviewed-06-r1-final/`,
`retrain-reviewed-06-evaluation/`, and the separate `parallel-black-shadows-202009/` and
`strong-shadows-202150/` review folders. Photos and experimental model weights remain local.
