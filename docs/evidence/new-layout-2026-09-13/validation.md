# New-layout missed train, September 13, 2026

The user reported missing train outlines in `GoldenTicket-board-20260913-184806.png` after
installing the retrained model. This exact photo was not among its 32 training photos. It is a
changed layout from the same board, camera setup and capture session, so this check does not
replace an independent capture-session test.

This record describes the frozen second model before additional training. The user later
requested retraining with this photo; the [third-model follow-up](../ml-retrain-denver-2026-09-13/validation.md)
recovers the missed train and documents the now in-sample comparison. Original review and raw
baseline outputs remain unchanged.

## Review and result

Two regional visual reviews and a separate score-marker review labeled the original 3456 × 2160
image before scoring. The review contains **79 trains and five markers**. Many trains match the
printed route color; only visible physical pieces are annotated. Train colors, used as review
metadata rather than model classes, are 18 yellow, 18 blue, 16 red, 14 green and 13 black.

The installed model matches **78 of 79 trains and all five markers, with zero extra detections**.
Matching uses original model boxes, same-kind one-to-one IoU of at least 0.50, confidence 0.30,
classwise NMS IoU 0.45 and existing tile ownership. Optional rotated display rectangles are not
used for scoring.

The missed object is the **yellow Salt Lake City–Denver train at the Denver end of the lower
lane**, source box `(1173, 1111, 129, 78)` in `(x, y, width, height)` pixels. The strongest matching
raw proposal has score **0.2992613093**, just below the 0.30 operating cutoff, with IoU 0.80973.
It comes from its owning tile `(512, 512)`, row 6730, and selects the correct `train` class.
Other overlapping tile views are weaker. The proposal is rejected by confidence filtering
before NMS or orientation fitting. This establishes the immediate filtering cause; it does not
establish which visual cue caused the model's low score.

## Runtime checks and provenance

- Model SHA-256: `b02c33b035ed529afca7be08e05d4d5cd7e25e9ad30e54728be09feaae1f4ad6`.
- Source SHA-256: `4b446b56165008eb3a24f4ac6144b2e05d17609ec10688af68cd81fcb71516ff`.
- Actual .NET CPU and DirectML executions return the same 83 detections. DirectML graph
  execution is verified, with no fallback. Minimum matched-box IoU is 0.9999971 and maximum
  confidence difference is below 0.00000028.
- A raw ONNX diagnostic using the app's exact half-pixel resize reproduces the .NET CPU kinds,
  order and scores; maximum coordinate difference is below 0.000000000001 board pixels.
- Source image, installed model and training collection `04` hashes remain unchanged. The new
  labels are saved separately as a post-training evaluation record; this photo has not been
  trained into the model. No runtime or threshold change was made for this case.

Local ignored evidence is in `artifacts/piece-training/new-layout-184806/`: `fullreview.json`,
regional labels and checked overlays, `frozen-capture.json`, `raw-runtime-tiles.npz`,
`reviewed-evaluation.json` and `dotnet-compare/inference-report.json`.

## Follow-up

Keep this result as the frozen-model baseline for future training or threshold comparisons.
Evaluate any threshold adjustment against both occupied and empty examples for added false
positives. Additional exact-frame preview examples can reveal intermittent misses beyond the
single miss reproduced in this saved photo. New camera sessions and lighting are still needed
for acceptance. The preceding perfect scores on the reused training photos remain explicitly
[in-sample diagnostics](../ml-retrain-2026-09-13/validation.md).
