# Boston–New York train positioning, September 22, 2026

The user reported repeated failures to verify two blue trains on Boston–New York lane A.
The live diagnostics showed confident train predictions, but the upper prediction's
axis-aligned box center fell inside the parallel-lane ambiguity margin. Changing the
lighting did not resolve the reported blockage.

Three images were captured from the running Android Webcam through a separate shared
reader, without changing the camera format or stopping the game. The existing piece
detector and image fitter were then replayed locally against those images. Each image
contained ten trains: six red and four blue across four recorded routes.

| Captured image | Original box-center route match | Validated body-center route match | Full-board sequence |
| --- | --- | --- | --- |
| 1 | 1 of 2; upper space unverified | 2 of 2 | Stabilizing |
| 2 | 1 of 2; upper space unverified | 2 of 2 | Confirmed: 6 red, 4 blue |
| 3 | 1 of 2; upper space unverified | 2 of 2 | Confirmed: 6 red, 4 blue |

The full-board replay used the three distinct captured images in order, with replay
timestamps 1.1 seconds apart. This verifies recognition and the stability state machine
offline; it is not a completed live turn in the rebuilt application. Raw captures and
before/after diagnostic reports remain under the local ignored directory
`artifacts/boston-lane-20260922/`.

The implementation uses the independently fitted body center only when its rectangle
passes the existing shape, coverage and displacement limits used for interior color
sampling. Otherwise it uses the original model-box center. Model boxes, confidence,
parallel-lane margins, distinct-detection requirements and freshness checks are unchanged.
The inventory result also retains the failed-space mask so the board highlights only
the affected spaces.

Validation:

- Windows Release build with warnings treated as errors: zero warnings or errors.
- Focused position, color, inventory and board-marker tests: 139 passed.
- Full Windows integration suite: 1,055 passed, none failed or skipped.
- Recorded-geometry regressions cover all three samples. Negative cases cover missing
  and duplicate trains, the neighboring lane, wrong color, invalid fits and a valid
  fit pointing toward the wrong lane. Synthetic pixels isolate geometry in these tests;
  the separate image replay above used the actual camera pixels and detector.

No retraining was needed for this reproduced case. These three images do not establish
accuracy for every route, camera angle or lighting condition, or rule out unrelated
false detections. Physical turn continuation with the updated executable remains to
be checked after the user restarts it.
