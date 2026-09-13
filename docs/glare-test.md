# Controlled glare test

Purpose: determine whether glare prevents piece detection, separately from a stale reference,
changed crop, camera motion or changed processing. This is a physical experiment; do not change
detector thresholds while collecting its comparisons.

1. Fix the camera, board and four crop corners. Keep source resolution, zoom, focus, processor
   and enhancement settings unchanged. Record any automatic exposure/focus changes that cannot
   be held fixed. Include the complete score track and keep hands out of every captured image.
2. With all trains and scoring markers removed, capture a **fresh empty-board reference** under
   normal lighting (`E0`). Also capture empty-board images with reproducible moderate (`E1`) and
   strong (`E2`) glare, moving only the light. Record the light position and glare locations for
   each condition. An old empty-board image is not the controlled baseline.
3. Place a fixed set of trains and scoring markers, noting each object's location and kind.
   Capture normal (`P0`), moderate-glare (`P1`) and strong-glare (`P2`) images using the same light
   positions, directing the reflection across actual piece positions. Keep every piece, the board,
   camera and crop stationary across these captures.
4. Run both reference variants below with the same processor and detector settings. Retain
   original images and labeled overlays locally. Record whether the files were already enhanced
   exports; a diagnostic without additional enhancement is not necessarily raw sensor data.

| Variant | Comparisons | What it probes |
|---|---|---|
| Lighting changed after reference | `E0/P0`, `E0/P1`, `E0/P2` | Combined effect of glare and reference-lighting mismatch |
| Empty reference matches lighting | `E0/P0`, `E1/P1`, `E2/P2` | Glare with the intended empty baseline for that lighting |
| Empty-board negative controls | `E0/E0`, `E1/E1`, `E2/E2` | Whether processing or printed artwork produces candidates without pieces |

Inspect each overlay against the object list. Record correct individual detections, missed
objects, false candidates on empty board regions, duplicate/split outlines, merged pieces and
wrong object kinds. **Counts alone are insufficient:** a missed train and a false train outline
can cancel numerically. Record detector state too: a scene/motion hold is an abstention, not a
successful zero-false-positive detection. Record whether a missed object lies inside a glare
patch, and whether its detail is still visible in the source image.

Repeat the conditions before attributing a change to glare. If mismatch cases fail but matching
references work, reference sensitivity is implicated; this does not show that glare alone caused
the failure. If matching-glare cases repeatedly miss visible objects in the glare area, that is
evidence of a detector limitation under those conditions. Saturated pixels have lost captured
detail; sharpening/upscaling cannot restore it. Reduce or redirect the glare and repeat.

Use the existing [piece diagnostic](../tools/GoldenTicket.PieceDetectionSmoke/README.md) with
explicit local paths. Keep photos and annotated results in ignored `artifacts/` directories;
commit only the protocol and bounded observations, not user images.

## Exploratory result before this controlled test

The image `GoldenTicket-board-20260912-160803.png`, compared with the older empty-board image
identified by timestamp `152343`, returned **20 train and 19 marker candidates** on the enhanced
path and **24 train and 19 marker candidates** without additional enhancement. Visual inspection
sees **15 trains and 5 markers**; false candidates include printed score
numbers and tracks. Detector state was `Ready`, so this was a failed exploratory detection case,
not a scene hold. Per-object misses and false-positive totals were not fully annotated.

The cause has not been isolated: crop/geometry, lighting and reference age may differ. This result
must not be attributed to glare. Extras also occur without enhancement. Local outputs are in
`artifacts/glare-baseline-160803/` and `artifacts/glare-baseline-160803-raw/`; the
[validation record](evidence/camera-processing-2026-09-12/validation.md) preserves this limitation
alongside earlier successful photo-pair checks.

## Fresh-reference series, September 12, 2026

The user supplied four equally sized 3456 × 2160 board exports from `C:/temp`. Framing and piece
positions appear consistent on visual review. File names share the prefix `GoldenTicket-board-20260912-`:

- `164319.png`: empty board, labeled no glare; the fresh nominal-lighting reference.
- `164557.png`: populated board, labeled minimal glare.
- `164624.png`: the same populated board, labeled stronger glare.
- `164718.png`: the same populated board, labeled strongest glare.

Visual ground truth is **8 trains and 4 score markers**: three yellow trains on Salt Lake
City–Denver, two black on New York–Washington and three black on Nashville–Raleigh; yellow marker
at 20/top-left, red at 50/top-right, black at 70/bottom-right and green at 1/lower-left.

The detector and thresholds were unchanged. The main run applied **no additional enhancement**
because the inputs were already exported images. Overlay inspection checked the actual pieces,
not just whether candidate totals matched ground truth.

| Condition | Train candidates | Marker candidates | Actual pieces covered | False train / marker outlines | State |
|---|---|---|---|---|---|
| Empty reference against itself | 0 | 0 | No pieces | 0 / 0 | Ready |
| Minimal glare | 8 | 4 | All 8 trains and 4 markers | 0 / 0 | Ready |
| Stronger glare | 19 | 4 | All 8 trains and 4 markers | 11 / 0 | Ready |
| Strongest glare | 35 | 6 | All 8 trains and 4 markers | 27 / 2 | Ready |

A secondary run with additional GPU enhancement on the RTX 4080 Laptop produced exactly the
same counts in all four cases. No scene hold occurred. In this set the observed failure is
**false positives, not missed pieces**: additional printed-board regions were outlined in the
stronger-lighting images while the actual objects remained covered. Independent per-object review
confirmed that coverage and the false-outline totals.

The strongest reflection was mainly across western/central printed board areas. Yellow train
bodies were still visible, and the black train groups were mostly outside the hotspot. This
does not establish detection of black trains under direct glare, nor recovery of saturated detail.
Only a nominal-lighting empty reference was supplied; empty images matching the stronger lighting
are still needed to separate reference mismatch from glare effects. There was no measured
photometry, complete live video, jog test or broad collection of piece arrangements.

Local outputs are `artifacts/glare-test-1643/{minimal,stronger,strongest,empty-control}/` with
`piece-candidates.json` and annotated PNGs. Secondary directories use the `-enhanced` suffix;
logs are in the parent directory. User photos remain local. The matched-lighting branch and
direct-glare-on-piece conditions in the protocol remain open; no detector tuning was performed
during this measurement.
`artifacts/glare-test-1643/summary.json` records input/source SHA-256 hashes, per-case results and
review limitations locally.

## User-reported soft-light follow-up

On September 12, 2026, after reviewing the glare results, the user reported recreating soft
lighting and observing reliable piece detection. This confirms a usable lighting setup for
that reported trial. No new photo series, per-object counts or test duration accompanied the
report, so it is recorded separately from the measured image-pair results above. Recommend soft,
even lighting and avoiding bright board reflections for current use. Glare/illumination
robustness and broader physical acceptance remain open.

## User-reported matching-lighting glare follow-up

The user subsequently reported this successful sequence: clear the board, position the lights
with some glare, select **Capture empty board**, then add pieces without changing the lighting.
Piece detection was reported as reliable even with that glare. No new image pair, per-object
counts, exposure measurements or test duration accompanied this report.

This observation supports sensitivity to differences between reference and playing illumination:
a stable reflection included in the empty-board reference may produce less difference than a
reflection introduced afterward. The earlier measured series changed lighting after a nominal
reference, so it tested both glare and a reference-lighting mismatch. It did not establish that
all glare prevents detection. The successful matching-lighting trial is user-reported evidence;
the full counted matrix and direct-glare-on-piece checks remain open.

Setup guidance is now: position camera and lights, capture the empty board, then add pieces and
keep the lighting stable. Soft, even light remains useful for preserving visible detail.
Handling lighting changes during play remains a separate detector improvement.

The subsequent two-spotlight screenshot showed two train outlines near Kansas City/Saint Louis
and one marker outline on the right score track, with no obvious spurious boxes in the glare
areas. The user also confirmed that turning the second spotlight off after reference capture
causes false positives. This extends the observation to lighting removed as well as added;
it remains a screenshot/user-report check without a new labelled reference/current pair.
The [learned-recognition sequence](piece-recognition-ml.md) now includes these changing-light
conditions in grouped training and held-out evaluation data.
