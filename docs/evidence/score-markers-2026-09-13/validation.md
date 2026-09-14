# Score marker readings, September 13, 2026

The user requested score values for each marker color in **Piece outlines**, with markers
beside the same row sharing the same value. The camera panel now presents five color-named
cards under **Score track**, using the same fresh crop and detections as the outlines.

## Reading behavior

The existing two-class ML model locates score markers. `ScoreMarkerReader` samples each
marker's central pixels for blue, red, green, yellow or black, then uses normalized perimeter
geometry of the upright classic USA board to read the printed 1–100 track. This is a separate
local color/position estimate, not a newly trained model, OCR, or train-ownership classifier.
Keep the crop close to all four outer board edges. Completed laps cannot be inferred.

Markers aligned with the same row or column can share a score; distinct scores are never
forced. Nearby inward markers may use an outer marker's row/column as evidence. A clear
direct reading on the printed track takes precedence, so a marker on a corner cannot
invalidate the next numbered cell. A lone marker too far inside the board remains off-track.

Missing colors show **Not detected**. Unclear color, off-track or ambiguous positions produce
no numeric score. More than one detection of the same color shows **Multiple markers**.
Fresh-frame publication uses the existing camera, crop, processor and model revision checks.
Scores clear with expired outlines, toggles, changes, errors, stop and disposal. Stop and
disposal clear observations before waiting for camera teardown. Game scores remain unchanged.

## Supplied-photo check

Source: `C:/temp/GoldenTicket-board-20260913-202727.png`, 3456 × 2160 pixels.
SHA-256: `099c3c36900ed4331d763717ad2789d0e196244473672c4119206ba0037598c2`.

Native visual review established these expected values before running the reader:

| Marker | Expected | CPU | DirectML |
| --- | --- | --- | --- |
| Yellow | 20 | 20 | 20 |
| Blue | 15 | 15 | 15 |
| Red | 11 | 11 | 11 |
| Black | 11 | 11 | 11 |
| Green | 50 | 50 | 50 |

The red and black centers differ horizontally but occupy the same score row. Both providers
find 86 trains and five markers. The final reader returns all five expected color/value pairs;
actual DirectML execution is recorded with no CPU fallback. All original detector predictions
are exactly equal to the frozen pre-change outputs. Model SHA-256 remains
`4497e7c1f214bfd1e491788f9b83f5a00073a81ef09087302aacf2fad2182c27`, and collection `06`
is unchanged. The supplied photo was not added to training during this feature update.

## Automated and visual checks

- Debug build and **57 focused tests pass**: perimeter/corners, clipped markers, shared rows
  and columns, occupied-corner neighbors, color ambiguity, duplicate/missing states, exact
  analyzed-image use, stale results, in-flight invalidation, and delayed stop/disposal.
- **Four production WPF render cases pass**: valid and uncertain cards at 1280 × 800 and
  1000 × 620. All five values/names/accessibility bindings are checked, with zero horizontal
  overflow and zero binding warnings/errors. Focused card images were visually inspected.
  These UI fixtures use explicitly labeled synthetic readings, not a live camera.
- `GoldenTicket.MlPieceSmoke` now reports `ScoreMarkers` for offline checks alongside unchanged
  original and image-fitted detection geometry.

Commands:

```powershell
dotnet test tests/GoldenTicket.Domain.Tests/GoldenTicket.Domain.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~ScoreMarkerReaderTests|FullyQualifiedName~CameraMarkerScoreTests|FullyQualifiedName~CameraLearningFlowTests|FullyQualifiedName~CameraProcessingFlowTests"
dotnet run --project tools/GoldenTicket.MlPieceSmoke -c Debug --no-restore -- artifacts/piece-training/model C:/temp/GoldenTicket-board-20260913-202727.png artifacts/score-markers-202727/score-readings-final --compare
dotnet run --project tools/GoldenTicket.UiSmoke -c Debug --no-restore -- --marker-scores artifacts/score-markers-202727/ui
```

Ignored local evidence is under `artifacts/score-markers-202727/`: `marker-review.json`,
`baseline/`, `score-readings-final/inference-report.json`, `tests/marker-scores-final.trx`,
`ui/marker-score-checks.json`, render PNGs, and `final-checks.json`. No model weights or
training photos changed. Reopen the rebuilt application to load the new score-card code;
reloading only the ML model does not update application code.

This is one saved photo from the current setup plus synthetic geometry, lifecycle and UI
checks. New lighting, crowded positions, crop variation and live stability remain to be tested.
