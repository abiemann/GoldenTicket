# Intermittent yellow-train outline: September 13, 2026

The user supplied a preview screenshot without an outline around the yellow train on the
New Orleans–Atlanta route nearest Little Rock: the third yellow-lane slot down from Atlanta,
beside the two red trains on the parallel orange lane. This is separate from the outlined yellow
train on Atlanta–Miami. The unpainted `GoldenTicket-board-20260913-172806.png` is the collection
source; the screenshot is observation evidence only.

The native source is 3456 × 2160 with SHA-256
`1209790cef33fa90b76302972ac213719ff8b9163d70e321ec0a3f42ce0fb25a`.
The installed model remains
`420318a5cf2953b597aaf35fd627f737c64c8aa579bcc51d3c87f1d9be042211`.

## Saved-photo check

The current detector **does detect this train in the saved PNG**, so the PNG does not reproduce
the reported intermittent preview miss. The owning tile `(1024, 560)` gives its retained proposal
a score of **0.654793** (objectness 0.776122 × train probability 0.843673), above threshold 0.30.
The proposal survives tile ownership and NMS. Its source-pixel box is approximately
`[2410.4, 1531.9, 2489.6, 1661.5]`. The same body is also detected in the preceding `171132` photo,
at **0.846058**. These measurements do not establish that detection is reliable across live frames.

Actual .NET CPU and DirectML runs both retain this train (GPU score 0.654800), with all 57
predictions matched between providers. The optional angle fit is absent, so the original upright
rectangle remains its display geometry; orientation fitting does not remove the detection.

An exact missed frame is still needed to distinguish confidence variation, crop/input differences
and any other live-path issue. **Save detection example…** preserves the analyzed image and paired
predictions. No threshold, model, runtime or outline-fitting change was made for this case.

## Label review and correction

The new photo has **52 trains and five score markers** reviewed in source coordinates, including
the camouflaged yellow body. The surrounding train layout matches the preceding photo; slight
export registration shifts were checked and boxes adjusted against the current pixels.

The targeted comparison exposed a **missed annotation in `171132`**: that same physical yellow
train was present but absent from the earlier 51-train inventory. Its corrected inventory is
**52 trains and five markers**. It is corrected in the new collection version, with the original
review retained for provenance. A direct check of the older `131822` photo shows the printed slot
icon at this position, so no corresponding change is made to that baseline photo.

Collection version `03` (`annotation-progress-03.json` / `labels-reviewed-03.json`) includes the
new image and this explicit prior-image correction: **31 photos, 939 trains and 129 markers
(1,068 labels)**. The original 29 entries are unchanged. All 31 source hashes and label geometries
pass the dataset checks; regional and full annotated previews were visually checked. Versions
`01` and `02`, original photos and installed weights remain unchanged. Related layouts remain in
the same September 13 capture group. The user is collecting examples before retraining; none was
run for this addition.

Local ignored evidence is under `artifacts/piece-training/yellow-train-172806/`: raw tile outputs,
`172806-target-raw.json`, `171132-target-raw.json`, `summary.json`, native target comparisons,
`previous-photo-correction.json`, `review.json`, outlined review images, and `collection-update.json`.
Prediction boxes were not automatically accepted as ground-truth labels.

Subsequent [user-requested retraining](../ml-retrain-2026-09-13/validation.md) retains this target
on the saved photos. This does not establish that the intermittent live-frame miss is resolved.
