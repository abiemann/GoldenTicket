# Experimental board-corner model

This separate learned model proposes the four **outer board corners, including
the score track**, in an uncropped camera frame. It does not use the piece
detector or an empty-board image difference. Manual handle adjustments remain
necessary when its proposal is inaccurate.

The training script runs locally, with no network calls, camera access, or
uploads. It trains a small U-Net from scratch on projective placements of the
user's existing rectified board photographs. The mapped image boundaries give
synthetic corner labels. Random textured backgrounds, exposure, blur, compression,
partial boards, and absent boards provide variation and rejection examples.

**Limit:** synthetic placement accuracy is not real camera accuracy. Every photo
shows the same physical board. The held-out capture-date photographs and random
seed check new layouts and transforms, but cannot establish reliability under
new camera angles, backgrounds, glare, or substantial occlusion. Screenshot
preview crops are diagnostic examples, not an independent labelled test set.

## Local workflow

Use the ignored `artifacts/piece-training/.venv` environment described in the
piece-training README, or install the exact versions in
`requirements-training.txt` (a CUDA-enabled PyTorch build is recommended).
From the repository root:

```powershell
& artifacts/piece-training/.venv/Scripts/python.exe tools/board-corners/train_board_corners.py --photos C:/temp --output artifacts/board-corners/model --steps 1400 --batch 16
& artifacts/piece-training/.venv/Scripts/python.exe -m unittest discover -s tools/board-corners -p 'test_*.py'
& artifacts/piece-training/.venv/Scripts/python.exe tools/board-corners/evaluate_board_corners.py artifacts/board-corners/model artifacts/board-corners/diagnostics --screenshots
& artifacts/piece-training/.venv/Scripts/python.exe tools/board-corners/validate_synthetic.py artifacts/board-corners/model artifacts/board-corners/fresh-synthetic-validation --seed 20260915 --count 600
```

The script records hashes of all input photos, the optional screenshot background
strip, architecture, library versions, seeds, steps, GPU, and synthetic validation.
Photos, checkpoints, ONNX output, and diagnostic images remain local under ignored
`artifacts/`; original images are never edited.

## Runtime contract

- Input `images`: float32 NCHW `[1,3,384,384]`, RGB in `0..1`.
- Preserve aspect ratio, round resized dimensions half-up, byte bilinear resize
  using half-pixel centers, then center-pad with byte value 114.
- Output `heatmaps`: float32 `[1,4,192,192]` sigmoid corner probabilities, ordered
  top-left, top-right, bottom-right, bottom-left in image coordinates.
- Decode each maximum with a probability-weighted centroid of its bounded 5x5
  neighborhood. In input pixels: `(centroid + 0.5) * 2 - 0.5`.
- Invert the centered letterbox and half-pixel resize to return sensor coordinates.
- All four corners must pass the manifest confidence threshold. Runtime must also
  validate finite coordinates, in-frame corners, nondegenerate clockwise geometry,
  sufficient area, and current camera/crop identity before applying a proposal.
- Standard ONNX opset17; no custom operators or external initializers.

The model confidence is a heatmap peak, not a calibrated probability of a correct
crop. Do not treat model output as verified board registration.

## Local experiment, 13 September 2026

The selected local model is 2,082,435 bytes, SHA-256
`09fce75df40d3253c4dfc744a79a24dcedd5ec7319679e83bfb117e6e4e2aea5`.
Its confidence threshold is 0.55. Its ONNX graph uses only Conv, Relu, Resize,
Concat, Constant, and Sigmoid operators. The exported graph passed ONNX checking
and numerical comparison against PyTorch.

Training used 19 photographs from 12 September; 11 photographs from 13 September
supplied held-out synthetic textures. The initial 1,400-step run was followed by
a 1,000-step refinement with more boards near the image edges, selecting the
800-step refinement checkpoint by the fixed synthetic validation score. The
original checkpoint and manifest remain in
`artifacts/board-corners/baseline-model`; the selected refinement also remains in
`artifacts/board-corners/model-edge`. The deployed local copy is in
`artifacts/board-corners/model`.

To repeat the refinement from that recorded local checkpoint:

```powershell
& artifacts/piece-training/.venv/Scripts/python.exe tools/board-corners/train_board_corners.py --output artifacts/board-corners/repeated-refinement --resume artifacts/board-corners/baseline-model/best.pth --steps 1000 --batch 16
```

The selected manifest records both training stages and the warm-start checkpoint
hash. The command above repeats the recorded refinement; training the current
script from scratch also includes the added near-edge examples and therefore
does not reproduce the earlier initial run byte for byte.

On 600 fresh synthetic placements (seed 20260915), with procedural backgrounds
instead of the screenshot carpet strip, the final model and geometric acceptance
checks:

- Accepted 418 of 433 complete boards.
- Rejected all 167 incomplete or absent boards.
- Had median corner error 0.753 pixels and 95th percentile 1.713 pixels at the
  384-pixel input scale among accepted boards.

The synthetic training-selection report uses confidence alone; the fresh-seed
report additionally checks in-frame coordinates, corner order, convexity, and
minimum board area. These are synthetic measurements, not deployment accuracy.

Two full preview regions from user-provided screenshots produced four accepted
corners, including one with glare and existing yellow editing handles. A zoomed
region showing only the top of the board was rejected. The first screenshot's
carpet strip was also used for training backgrounds, and its observed margin
informed the refinement. Consequently these are development diagnostics, not
independent tests. Neither diagnostic reads the camera or changes the running app.
Detailed source regions, source hashes, corner coordinates, confidence values,
and overlays remain in `artifacts/board-corners/diagnostics-final/`.
