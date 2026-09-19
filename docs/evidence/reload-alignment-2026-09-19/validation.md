# Reload crop alignment — September 19, 2026

## Reproduced failure

The saved game requested two blue trains on Duluth–Omaha lane A although both were present.
The production detector found them as blue with approximately 95.8% and 96.4% confidence.
The initial live crop shifted the upper detection to reference-board coordinates
`(1095.09, 448.48)`, just across the fixed geometry's parallel-lane boundary. Lane A therefore
matched only one train. The fitted display rectangle did not correct that displacement.

Three frames were captured through shared read-only access to the existing 1920×1080 Android
Webcam stream, without changing camera settings. Replaying each frame with the two original
crop definitions from the diagnostic log reproduced the failure in all six cases. The running
application eventually recovered after another corner estimate, before this fix was applied.

## Change

Reload retains an alignment reference from the required saved board photo. After determining
orientation, the camera worker refines small corner errors using normalized image agreement
across the interior artwork. Corrections are bounded to 1% per board axis at each corner;
weak or unrelated evidence retains the original crop. The worker checks cancellation, camera
identity, photo identity and freshness before publishing.

The reference does not take route ownership, expected train positions, or model detections as
inputs. ML weights, detection confidence thresholds and lane-separation rules are unchanged.
Each expected train still requires a separate detection of the correct color, and whole-board
verification still rejects missing, duplicate and unexpected pieces. Reload failures now log
nearby candidate positions, confidence and color alongside the rejected route.

## Results

- All three captured frames pass under both formerly failing crop definitions: 20 trains across
  11 saved routes. Duluth–Omaha lane A matches 2/2; the adjacent empty lane B matches 0/2.
- CPU inference passes all six replays. A DirectML replay on the RTX 4080 Laptop GPU also verifies
  all 20 trains and rejects lane B.
- Alignment took approximately 171–215 ms in these Debug diagnostic runs. The work runs outside
  the UI thread during corner checks, rather than on every rendered preview frame.
- Focused tests cover crop refinement with lighting changes and moved local patches, unrelated
  and inverted photos, bounded corrections, cancellation, camera changes, desktop reload
  integration, route matching and saved-board verification.
- All 85 focused tests and the full 861-test Release suite pass. The Release configuration
  allowed verification while the user's existing Debug game remained open.

Photos and detailed diagnostics remain local under `artifacts/recognition-20260919/`. These are
replays of three frames from one physical setup, not evidence of accuracy across every route,
camera position or lighting condition. The running game was not restarted to install this change.
