# Printed route mistaken for a train near Boston: September 13, 2026

The user reported an extra preview outline over the upper yellow Boston–New York route slot,
next to a real red train, and explicitly confirmed that no yellow train was there. Native-pixel
inspection agrees: the yellow slot is board printing. The unpainted full photo
`GoldenTicket-board-20260913-173706.png` is the annotation source; the preview screenshot is
observation evidence only.

The source is 3456 × 2160 with SHA-256
`bd75a854e0c2f2ac32bf27ecc59286005c1d06eb13333982c317b38c035af8f7`.
The installed model remains
`420318a5cf2953b597aaf35fd627f737c64c8aa579bcc51d3c87f1d9be042211`.

## Saved-photo reproduction

The false positive reproduces on both .NET CPU and DirectML. The extra **train** proposal scores
**0.482637** (objectness 0.560653 × train probability 0.860848), above the unchanged 0.30 cutoff.
Its source-pixel bounds are approximately `[3155.74, 464.80, 3255.09, 588.94]`. The model has only
train and player-marker classes; “yellow” describes the printed region, not an inferred color.

The neighboring real red train scores **0.869627**. Both predictions belong to tile `(1280, 0)`.
Their box intersection-over-union is **0.434838**, below the existing 0.45 overlap-filtering cutoff,
so both survive. The extra box spans yellow printing and part of the neighboring red train; these
measurements do not establish which visual cue caused the error. They explain why the box remains,
but do not establish that lowering the overlap cutoff would be appropriate for touching real pieces.

CPU and DirectML agree on all **59 predictions: 55 trains and four markers**, with maximum score
difference 0.00000282. These are prediction counts, not the reviewed piece inventory. The model,
thresholds and runtime were not modified, and no training was run.

## Annotation policy

Keep the empty printed yellow slot without a positive object label. Label the adjacent real red
train and every other visible physical piece. This is a targeted background example within a
populated board photo, not an empty image or a new negative object class. Predictions are diagnostic
evidence and are not automatically accepted as ground truth.

The complete reviewed photo contains **53 trains and five score markers**. Compared with `172806`,
an additional black train occupies the middle of the three neighboring black trains on
Sault Ste Marie–Montreal. Minor image registration shifts were checked against current pixels.
The real yellow train near Little Rock and the partly occluded black score marker remain labeled.

Collection version `04` (`annotation-progress-04.json` / `labels-reviewed-04.json`) contains
**32 photos, 992 trains and 134 markers (1,126 labels)**. The preceding 31 entries and version `03`
are unchanged. All 32 source hashes and label geometries pass the dataset checks. Regional,
targeted and whole-image outlined previews were visually checked. The empty Boston slot also has
a metadata-only polygon to preserve the review target; it is not exported as a positive object.

Keep this image in the same September 13 capture group and split as its related layouts. If it is
used in a future training run, move whole related groups together and evaluate on an independent
capture group. Retraining remains paused while the user collects more failure examples.

Local ignored evidence is under `artifacts/piece-training/boston-false-positive-173706/`, including
native source crops, raw tile outputs and filtering records, `summary.json`, .NET provider
comparison, `review.json`, outlined images and `collection-update.json`. Original photos and
installed weights remain unchanged.

Subsequent [user-requested retraining](../ml-retrain-2026-09-13/validation.md) removes this saved-photo
extra box while retaining the real red train. This photo was used in training; new captures must
check whether the improvement generalizes. The measurements above describe the frozen baseline.
