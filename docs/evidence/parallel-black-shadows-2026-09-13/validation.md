# Parallel black trains under stronger shadows, September 13, 2026

The user supplied `GoldenTicket-board-20260913-202009.png` as a training example after
observing unclear outlines around two parallel black trains near Denver. The new lighting
produces larger cast shadows. This photo was not used to train the installed third model,
but it is a related layout from the same board and capture setup, not an independent
capture-session test.

The saved photo and the preview screenshot are separate captures. Results below describe
the saved training photo; they do not establish which predictions existed in the earlier
preview frame.

This is the preserved pre-training diagnosis. The user subsequently requested training
with the related `202150` photo, and both examples were included in the
[fourth-model retraining](../ml-retrain-shadows-2026-09-13/validation.md). That follow-up
improves the parallel boxes; results on these photos are now in-sample.

## Frozen model check

The original 3456 × 2160 photo was processed using the existing model, confidence cutoff
0.30, classwise NMS IoU 0.45 and midpoint tile ownership. Actual .NET CPU and DirectML
executions both return 84 train detections and five markers. DirectML graph execution is
verified, without fallback. Minimum paired-box IoU is 0.99999669; maximum confidence
difference is below 0.00000029. Exact app-resize Python inference reproduces every CPU
confidence and original box within 0.000000000001 board pixels.

Independent left/right train reviews and a separate score-marker review label **84 trains
and five markers**. Train colors are review metadata: 20 yellow, 18 blue, 17 red, 15 black
and 14 green. The New Orleans–Miami fourth red train was checked against native occupied
and empty-board crops to distinguish its smooth plastic body from printed track.

One-to-one same-kind matching of original prediction boxes at IoU at least 0.50 gives
**89 matches, zero extra detections and zero missed labels**. This count does not establish
that the boxes are good enough for the preview: the upper parallel black train only just
passes, with IoU **0.5070**, while the lower train has IoU **0.8121**.

Two predictions are retained for the middle parallel black pair, with confidence about
0.9010 and 0.8963. The upper box extends over the lower train. Thus the saved photo shows
poor box placement around closely spaced pieces, rather than a single retained detection
for the pair. Neither piece is removed by overlap filtering in this capture. Rotated
display fitting is separate from detection and must not be treated as evidence of a
missing object.

The upper prediction is 69.83 source pixels tall, compared with the reviewed body's
43 pixels, and its bottom edge extends **29.98 pixels** below that body. The two prediction
boxes overlap at IoU **0.448467**, just below the 0.45 NMS cutoff. The oversized upper box
has a higher score than tighter alternative upper proposals (about 0.8786 and 0.8616)
and suppresses those alternatives. Small differences in another frame could change this
near-cutoff result, but that possibility was not reproduced on the saved photo.

Both native `OrientedOutline` values are null, leaving the original model rectangles in
the display. A bounded diagnostic mirror of the current fitter's early gates finds that
connected dark foreground fills 97.88% of the upper crop and 96.64% of the lower crop,
above its 90% ambiguity limit. This explains the display fallback in the diagnostic;
the actual .NET output independently confirms that neither rectangle was rotated.

This records the observable failure, not a proven explanation of the model's internal
features. The changed shadows are relevant capture conditions; one photograph does not
isolate their effect from spacing and placement.

## Provenance and next training run

- Source SHA-256: `dc5672da26e97e4367516033a89e39385df9b541c8ded11541bb2ec7a68fd01c`.
- Reviewed-label SHA-256: `d51335cf05cfe40747aed0ddec59f005fb4e51b3a697263c2f60bf37a359f3b5`.
- Installed model SHA-256: `c6ea0bf92894a7d3531e77c1488cd5c968530d8eaa279636fc17cd9024cf1751`.
- Existing 33-photo collection `05` SHA-256: `2fef12435d45166d360c82b431c30b8f39720a81f7f410cbe4f786d3862ce21d`.

Keep this source and its separately reviewed labels for the next requested training run,
with distinct boxes for each visible plastic train body and no labels on shadows or empty
printed track. Preserve its related capture group. Keep the frozen outputs for before/after
comparison and inspect the parallel pair's localization, not just detection counts or a
permissive IoU match. Once included in training, this case becomes an in-sample regression
check. Independent capture sessions are still needed for acceptance.

Local ignored evidence is in `artifacts/piece-training/parallel-black-shadows-202009/`:
`fullreview.json`, regional visual reviews, marker review, `training-ready.json`,
`frozen-capture.json`, `raw-runtime-tiles.npz`, `reviewed-evaluation.json`,
`parallel-localization-diagnosis.json`, the visually checked
`parallel-black-source-predictions-review.png` and `dotnet-compare/inference-report.json`.
The training-ready record validates that appending this example would produce 34 photos
and 1,299 labels, while keeping the current collection unchanged.

No model training, model replacement, runtime change or threshold adjustment was performed
during this initial diagnosis. The [third-model training record](../ml-retrain-denver-2026-09-13/validation.md)
describes the model used for these frozen outputs.
