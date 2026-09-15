# Pixel webcam HQ training capture — 2026-09-14

Status: completed. The final epoch-40 export passed fixed-runtime, native CPU/DirectML and visual checks, and is installed locally with verified rollback copies.

## Source and reviewed labels

The user supplied `C:/temp/GoldenTicket-board-20260914-182629.png` and reported that it was captured using the Pixel webcam's HQ mode. The original file is **3456 × 2160**; the chat preview is resized. HQ mode is user-reported capture metadata, not a camera setting independently measured by this work.

Independent top- and bottom-half source inspections, followed by full-source and overlay review, identified **43 physical trains and no scoring markers**:

| Train color | Count |
| --- | ---: |
| Black | 14 |
| Blue | 7 |
| Green | 12 |
| Red | 4 |
| Yellow | 6 |

Parallel pieces near Seattle and the crowded El Paso branches were labeled individually. Printed route slots, printed train symbols and cast shadows were excluded. The score-track edges contain no markers, and the Miami area contains no physical pieces.

`labels-reviewed-10.json` contains **39 photos and 1,706 objects: 1,542 trains and 164 markers**, including four empty-board controls. The prior 38 image entries remain unchanged, and all source-file hashes were verified. Colors are annotation metadata; the detector still has only the `train` and `player-marker` classes.

## Frozen baseline

The accepted reviewed-09 model and manifest were copied unchanged into `artifacts/piece-training/runs/retrain-reviewed-10-baseline/`. Its accepted epoch-30 `best.pth` was frozen alongside them as the training initialization; the previously rejected reviewed-09 epoch-40 checkpoint was not used.

Before training, this model already matched **all 1,706 objects with zero false positives and zero misses** across the 39-photo collection. The original 38 photos reproduced 1,663 matches; the new HQ photo added 43 matches. Matching uses the original axis-aligned model boxes, one-to-one same-class IoU ≥ 0.50, confidence 0.30 and NMS IoU 0.45. Runtime-compatible half-pixel bilinear resizing and center-midpoint tile ownership remain unchanged.

The new HQ photo also passed native CPU and actual DirectML inference: **43 trains, zero markers, zero extras or misses, and 32 rotated train outlines** on each backend. The GPU profile recorded `DmlExecutionProvider`, with no fallback. CPU/GPU detection matching covered all 43 pieces, with minimum matched IoU 0.9999989. Rotation is a separate display fit; eleven ordinary rectangles do not represent missed detections.

Native baseline evidence uses the existing smoke-tool binaries, which have changed since the reviewed-09 evidence. This work performed no application or smoke-tool build. The invocation records binary hashes and confirms that this baseline run neither opened the camera nor installed a model; it should not be presented as a same-binary comparison with the previous day's runtime evidence.

## Training and acceptance protocol

Run `retrain-reviewed-10-r1` completed 40 epochs, 512 samples per epoch, batch size 16 and seed 20260921, initialized from the frozen accepted reviewed-09 checkpoint. All 39 reviewed photos entered training. Training used PyTorch 2.8.0 with CUDA 12.8 on the RTX 4080 Laptop GPU and took about 371 seconds through epoch 40.

The frozen protocol evaluates the explicit final epoch-40 export first, and permits the trainer-selected best checkpoint only if the final export fails. Thresholds stay fixed. Acceptance requires the full 39-photo regression, the earlier Denver parallel-pair and Miami hard-negative checks, native CPU/actual DirectML checks on 14 labeled fixtures plus the separate untrained score fixture, expected score values, parity, hashes and a verified rollback before local installation.

The HQ image was new to the frozen baseline model. Once included in training, its results are saved-photo regression evidence, **not independent-session accuracy or proof of reliable live detection**. Since the baseline already finds every reviewed piece, no detection-count improvement should be attributed to this training without additional evidence.

## Final-checkpoint evaluation

The separately exported final epoch-40 checkpoint (`retrain-reviewed-10-r1-final`) passes the fixed-runtime comparison. The fallback checkpoint has not been needed.

| Saved-photo group | Before: TP / FP / FN | After: TP / FP / FN |
| --- | --- | --- |
| All 39 photos | 1,706 / 0 / 0 | 1,706 / 0 / 0 |
| Previous 38 photos | 1,663 / 0 / 0 | 1,663 / 0 / 0 |
| New HQ photo | 43 / 0 / 0 | 43 / 0 / 0 |
| Four empty-board controls | 0 / 0 / 0 | 0 / 0 / 0 |

All matches are one-to-one; no previously matched label was lost. Both reviewed Denver parallel-black pairs remain distinct. On `202009`, their IoUs are 0.9614 and 0.9721; on `202150`, 0.9542 and 0.9780. Neither upper prediction spills below the reviewed upper body, and each covers less than 21% of the lower body's reviewed area. All ten previously reviewed Miami background regions in `202835` remain free of false detections.

The export manifest's 1,420-object evaluation summary is the trainer's diagnostic subset. The acceptance result above comes from the separate full 1,706-object runtime-compatible evaluation. Export-only provenance records a null initializer; the actual training initializer is recorded in `runs/retrain-reviewed-10-r1/run.json`, linked to the explicit export by its checkpoint SHA-256.

## Native preview and local installation

The same frozen existing smoke-tool binaries passed **14 labeled fixtures plus the separate untrained `202727` score fixture**, each on CPU and actual DirectML. Every labeled fixture matched all reviewed objects without misses or extras: 911 objects per provider. Profiled GPU execution used `DmlExecutionProvider`, with no fallback. The separate score photo remains outside the 39-image training collection.

The expected scores passed on `202835`, `203020`, `203656` and `202727`: Yellow 20, Blue 15, Red 11, Black 11 and Green 50. Both Denver parallel-black pairs and all ten prior Miami background checks passed natively as well.

The HQ photo produced **43 trains and no markers** on both providers, with minimum CPU/GPU matched IoU 0.9999981. Its original model boxes have minimum reviewed-label IoU 0.8856. Visual review confirms separate boxes for adjacent northwest trains and the crowded El Paso branches, with no extras in Miami or on the empty score track. The unchanged display fitter supplies 36 oriented outlines, compared with 32 under the baseline model on this photo; seven remain ordinary rectangles. This observation is limited to the reviewed photo and does not establish orientation accuracy for new captures.

The accepted model is `goldenticket-yolox-nano-retrain-reviewed-10-r1-final`. Only its ONNX file and manifest were replaced in the local canonical model directory and the existing Debug/Release desktop model directories. All three previous model pairs were hash-checked before copying to `artifacts/piece-training/model-before-reviewed-10/{canonical,Debug,Release}`. The deployment receipt records source/evidence hashes and verifies installed pairs. The training and installation step performed no application rebuild, camera capture, runtime code or threshold change, or release. Use **Reload ML model** in Piece outlines to activate the replacement in an already-running app.

Continue future training from `runs/retrain-reviewed-10-r1/last.pth`, the accepted epoch-40 checkpoint. The separate `-final` folder contains export artifacts. The prior accepted model remains available for rollback; the trainer's automatically selected checkpoint was not needed for fallback acceptance.

## Local evidence and identities

Local artifacts remain under the Git-ignored `artifacts/piece-training/` tree:

- `hq-pixel-182629/reviewed-image.json`, `reviewed-overlay.png` and `native-baseline-label-score.json`.
- `hq-pixel-182629/native-baseline/`: invocation, inference report, CPU/GPU profiles, previews and baseline summary.
- `runs/retrain-reviewed-10-baseline/freeze-receipt.json`.
- `retrain-reviewed-10-evaluation/baseline/evaluation.json` and `baseline-reproduction.json`.
- `retrain-reviewed-10-protocol.json` and `runs/retrain-reviewed-10-r1/`.
- `runs/retrain-reviewed-10-r1-final/`: explicit epoch-40 export and manifest.
- `retrain-reviewed-10-evaluation/candidate-r1-final/`: fixed evaluation and comparison summary.
- `retrain-reviewed-10-evaluation/candidate-r1-final-native/`: 15 native fixture runs, profiles, previews and summary.
- `retrain-reviewed-10-evaluation/candidate-r1-final-native/visual-review/review.json`: manual visual acceptance with hashed review crops.
- `retrain-reviewed-10-deployment.json` and `model-before-reviewed-10/`: installation and rollback evidence.

| Artifact | SHA-256 |
| --- | --- |
| Original HQ photo | `4df1122270a62a51c4608bf91a6f59d9e00814ee2c9f6c9ec45d58b46c27b46f` |
| Reviewed-10 labels | `fc398519b6abd5b9e84dcdc11fc5f9f7b62ef336a78749e0c9e691f86405b69f` |
| Frozen protocol | `09bf55849b8180a281634a9f1d0d37fb48ed3bfc6205e85babf5a87ebd2e4f85` |
| Frozen ONNX model | `4a111ee5027516fafa1a9a039f54bff16e376486298b7d71bae88410e154dc40` |
| Frozen manifest | `3f3b53686f0ed087153748a0f85b4e00d7f3c0b96583a0c01d0d006386c3610a` |
| Initial epoch-30 checkpoint | `cebddfa224a03a1f9b91f25c49aea0638d8b53aa951ef431d21485b0bd618893` |
| 39-photo baseline evaluation | `96a447976d2c0e93aeba33d782213de40d5d3837378efc7a22381d8c390715af` |
| Native HQ baseline inference report | `29cf053e44d8c1dc75b66530723e4107b671ae42732736b90081c1fd25692fbe` |
| Accepted epoch-40 checkpoint | `8907c41d04a066ed7a31c20fdd4b9b4daf1ab3f50e8f5945ef48a5a44ad2f596` |
| Installed ONNX model | `81cfec6a8e423f4106e2aee067376136a8255d4855da97d4464a2b22006fa8bb` |
| Installed manifest | `70dfda5bbe57ea2786b00efc120cd6ee4635a7d065176277448361cd4e874caa` |
| Final native summary | `a0cf38aa0be932fcdcebaa965fa29fc4f6056c24800aec95cb02b46af6afbe21` |
| Final visual review | `5bbc76712e5fc2553de068a3e2becd4898f647a7326cae7276c1bd3c0b57732a` |
