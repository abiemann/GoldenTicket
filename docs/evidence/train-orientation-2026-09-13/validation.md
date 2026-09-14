# Train outline orientation: September 13, 2026

The user requested white train rectangles that follow the pieces' orientation. This update keeps
the existing two-class YOLOX detector and adds an optional local image fit after global NMS.
It does not train an angle model or change detection thresholds, classes, confidence or counts.
Model SHA-256 remains `420318a5cf2953b597aaf35fd627f737c64c8aa579bcc51d3c87f1d9be042211`.

## Display geometry

`TrainOutlineFitter` examines the current 1920 x 1200 analyzed board inside each train detection.
It chooses central connected chromatic/dark foreground and fits its principal axis with a bounded
minimum-area search. The angle search uses boundary samples; outlier trimming occurs only for the
chosen angle. Crop size and candidate count are bounded and cancellation is checked during work.
Elongation, fill, crop coverage and frame bounds gate the optional fit. Low-contrast, ambiguous,
tiny or clipped cases keep the original box. There is no empty-board reference or route lookup.

The four fitted corners pass through the same board-to-sensor mapping as the original box.
The existing polygon renderer preserves that shape during zoom and pan. Score markers remain
screen-aligned squares. Original ML `Outline` geometry remains available for evaluation; review
ZIPs additionally store nullable `orientedOutline`, tagged `local-image-fit`, in normalized board
coordinates. Both remain unreviewed predictions. Total detection time and fitting time are recorded
separately from model inference in the diagnostic and review reports.

## Checks performed

- **51 automated tests passed** covering fitting, learned-detector geometry, legacy comparison
  behavior, camera mapping, cancellation/freshness and review ZIP export. The 16 fitting cases
  include five colors, horizontal/vertical/positive/negative diagonal trains, identical ML boxes
  with opposite pixel orientations, ambiguous foreground and bounded fallback. Source-coordinate
  angle error is checked against synthetic known angles within four degrees.
- **Saved photo 20260913-131822, CPU:** 47 trains and five markers, with 31 fitted train outlines.
  Remaining trains keep original boxes. Last sampled fit time was 12.07 ms and total detection
  was 238.47 ms. The rendered preview was visually inspected.
- **Dense photo 20260913-123216, CPU and DirectML:** both returned 112 trains and five markers,
  with 81 fitted train outlines. Last sampled fitting cost was 24.23 ms on the CPU-first path and
  15.13 ms on the warmed DirectML path; the fitter itself always runs on CPU. DirectML total
  detection was 111.71 ms. Both providers use the same CPU geometry fitting code.
- Original model candidate geometry, confidence, order and classes were compared with the
  previously recorded runs for both photos/providers and were **identical**. Thus the display
  refinement did not alter the underlying ML predictions on these checks.

Logs, reports and rendered full-size/1600-pixel previews remain ignored local artifacts:

- `artifacts/train-orientation/tests/train-orientation-final.trx`
- `artifacts/train-orientation/131822-boundary/`
- `artifacts/train-orientation/123216-final/`
- `artifacts/train-orientation/original-predictions-check.json`

These photos were used during development and are not independent angle-accuracy validation.
Timings are three samples per provider on this development machine, not an end-to-end live-camera
frame-rate guarantee. Some same-color printed tracks merge with foreground; some ML boxes clip
train tips. Their conservative fallbacks were retained instead of forcing an unsupported angle.
Live-camera review across lighting, touching trains, glare and motion remains necessary.
