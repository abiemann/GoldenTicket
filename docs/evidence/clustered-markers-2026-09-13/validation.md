# Clustered black score marker: September 13, 2026

The user supplied `GoldenTicket-board-20260913-171132.png` after observing that a black score
marker was outlined when isolated, but missed when surrounded by other markers. The full-board
photo contains red, blue and yellow markers touching/partly occluding the black marker at the
lower-left, on dark printed artwork. The green marker is separate at the upper-right.
The supplied screenshots document the isolated and clustered preview states; only the unpainted
full-board photo is used for training annotations and reproducible inference.

Source dimensions: 3456 × 2160. Source SHA-256:
`c2da1340c0ca66923cfe9f201b145a77245ffada95ed2a9b02cbb803c199567a`.
Frozen model SHA-256:
`420318a5cf2953b597aaf35fd627f737c64c8aa579bcc51d3c87f1d9be042211`.

## Diagnosis

This photo reproduces a low-confidence model miss before overlap filtering:

- The strongest matching black-marker proposal in its owning tile `(0, 560)` scores
  **0.00620153**: objectness 0.0151284 multiplied by marker probability 0.409925. The runtime
  threshold is **0.30**. The proposal substantially overlaps the manually inspected black body.
- Its best proposal from the other overlapping tile `(0, 512)` also scores only **0.00908051**.
  Removing tile ownership therefore would not make the marker pass the threshold.
- Maximum IoU of the owning proposal with any retained marker is **0.12336**, below the
  classwise NMS threshold of **0.45**. None of the retained markers would suppress that box.
- The other four markers score approximately **0.857–0.866**. The .NET CPU runtime returns
  **53 train predictions and four marker predictions**. These are predictions, not an inventory
  or a whole-board accuracy measurement.
- Python inference using the runtime's half-pixel resize agrees with all 57 .NET predictions
  in classes, order and scores. Maximum coordinate difference is about 1.14e-13 board pixels.

This rules out NMS and tile ownership as the cause of this particular miss. Crowding, partial
occlusion and black-on-dark contrast are plausible contributing factors; this single image
does not isolate their individual effects. No threshold, runtime or model weights were changed.
The train-orientation fitter does not create or remove detections and does not fit score markers.

## Collection and next comparison

The user chose **collect more examples before retraining**. Fresh source-pixel annotations cover
**51 trains and all five markers**. Marker boxes describe visible plastic bodies, including
visible sides; the partially occluded black marker is not given an invented complete disk.
Regional train reviews and an independent marker review precede the complete outlined-photo check.

**Later correction:** the `172806` comparison revealed one yellow train omitted from the initial
`171132` labels. Collection version `03` corrects this photo to **52 trains and five markers**.
The marker diagnosis is unchanged. See the [yellow-train review](../yellow-train-2026-09-13/validation.md).
The version `02` counts below describe the initial saved review and remain historical provenance.

The addition is stored in a new local collection version, `annotation-progress-02.json` and
`labels-reviewed-02.json`: **30 photos, 886 trains and 124 markers (1,010 labels)**. All 30 source
hashes and label geometries pass the dataset checks; the new annotated overview and native regions
were visually inspected. The original 29-photo inputs, their annotations and the installed model
remain frozen. The new photo stays with the existing September 13 group because it repeats much
of the `131822` layout. It is a known failure for future training, not an independent test photo.
When assigning training data for the next run, move related capture groups together and keep
independent new sessions for evaluation; do not place near-duplicate layouts across splits.

Next useful captures keep the camera steady and vary one factor at a time:

1. The same cluster on lighter and darker board artwork.
2. Small gaps, touching markers, then partial overlaps with the black body still visible.
3. Different neighboring colors and a different color in the center.
4. An isolated-marker control paired with each clustered arrangement.

Original photos, labels and diagnostic images remain local under ignored `artifacts/`. The case
record and collection checks are in `artifacts/piece-training/clustered-markers-171132/`:
`summary.json`, `runtime-half-pixel-report.json`, raw tile outputs, `dotnet-cpu/inference-report.json`,
`review.json`, `review-full.png`, and `collection-update.json`. No training was run for this addition.

Subsequent [user-requested retraining](../ml-retrain-2026-09-13/validation.md) detects the crowded
black marker in all three recent saved photos. Those photos were used in training; new arrangements
are still needed to test generalization. The raw confidence diagnosis above describes the baseline.
