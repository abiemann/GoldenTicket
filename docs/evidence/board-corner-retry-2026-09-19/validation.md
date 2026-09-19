# Board-corner retry — September 19, 2026

The reconnect screenshot showed the whole board with spare blue and yellow trains beside its
left edge. Its original SHA-256 is
`79b78baf757825958c62b24472c3a689c8c2041af0fcf90b9131b80ca6ce78ea`.
The source and all derived diagnostic images remain ignored under `artifacts/board-edge-20260919/`.

The displayed camera region `(72, 233, 1162, 654)` passed the existing model, but its bottom-left
confidence was only 0.649. Removing one pixel from each edge changed the resampling enough to
reduce that corner below the required 0.55, despite its position remaining correct. Removing the
spare-piece strip also increased bottom-left confidence. This supports a context/resampling
sensitivity diagnosis; the screenshot is not the original raw camera frame and includes a
notification over the bottom edge, so it cannot establish the exact live failure by itself.

The subsequent live reconnect log explains the reported one-second success followed by loss:
one of 258 checks passed via the focused retry; the other 257 were rejected. The bottom-left
confidence fell as low as 0.311 while the other three corners remained confident. Those readings
never qualified for the original 0.45 proposal floor, so the closer inspection was not attempted.
The copied log is `framing-live-20260919.jsonl` in the ignored artifact directory. It records
confidence and geometry, not raw camera images.

## Runtime change

The normal accepted full-frame path is unchanged. A rejected frame can receive one focused
retry only if three corners pass the normal threshold and the remaining corner has a peak
of at least 0.30 for the shipped 0.55 threshold. A valid initial quadrilateral supplies a bounding
box padded by 3% of each board span; the original pixels and capture age are preserved.

Every retried corner must pass the original confidence threshold and remain inside the cropped
image, within 2% of its original board span in each axis. Mapping back to the full camera image
rechecks geometry and area. The proposed crop must also match the embedded classic-US artwork
in one of four orientations. A failed retry retains the original rejection. The trained model
and weights are unchanged. The proposal floor is shared between heatmap decoding and retry
eligibility. A weak peak can select a region for a fresh learned check but cannot itself authorize
acceptance. Each check is independent; previous corners, miss grace and readiness expiry are
unchanged.

## Initial offline comparison

The original and updated production detectors ran on the same 23 inputs, three times each,
using DirectML on the NVIDIA RTX 4080 Laptop GPU. Reports are
`validation-before.json` and `validation-after.json` in the ignored artifact directory.

| Cases | Before and after |
| --- | --- |
| Marginal one-pixel-adjusted user crop | Rejected at 0.545; now accepted via retry at 0.892 minimum confidence |
| Original user crop | Accepted unchanged |
| Three earlier screenshot camera regions | Outcomes unchanged |
| Eight crops removing 3% or 5% of the actual board at an edge | All still rejected by the desktop's combined detector and edge-margin gates |
| Four artificially covered corners | Three rejected in both versions; top-right coverage remains a pre-existing acceptance limitation |
| Carpet, three blank levels, landscape and portrait | All rejected unchanged |

The marginal case succeeds consistently across all three runs. The retry takes 58–115 ms
(84 ms average); ordinary detection remains about 3–4 ms on this machine. No negative case
became newly accepted. One bottom-clipped case is accepted by the model in both versions but
correctly rejected by the desktop's 0.005 frame-edge margin. The unchanged covered-top-right
case means these checks do not establish general occlusion safety or independent live-camera
accuracy. No live camera, game state, or save was changed by this replay.

## Initial automated checks

100 focused corner, alignment, setup and reconnect tests passed. The new retry-region cases
cover marginal-confidence eligibility, crop copying, capture age, camera identity, cancellation,
odd-sized coordinate mapping, full-sensor area, clipped edges, and movement of both strong and
weak corners. Existing decoder, crop editing, alignment and reconnect checks remain green.
The local test report is `artifacts/turn-timing-tests-20260919/results/board-corner-retry.trx`.

## Follow-up: intermittent reconnect detection

The final 0.30 proposal policy was replayed against the original 23 controls plus four variants
of the follow-up reconnect screenshot. All 27 outcomes remained unchanged, including the
pre-existing covered-top-right limitation. The weaker proposal did cause the covered-top-left
control to receive another inspection, but its fresh retry confidence remained insufficient and
the frame was still rejected. See `validation-after030.json` in the artifact directory.

A bounded set of 16 additional camera-region insets reproduced four bottom-left scores below
the former proposal floor. These are resampled real photographs, not original webcam frames:

| Camera-region variant | Initial bottom-left confidence | Final minimum confidence | Result |
| --- | ---: | ---: | --- |
| Original screenshot, 5-pixel inset | 0.389 | 0.698 | Accepted by the fresh focused retry |
| Original screenshot, 6-pixel inset | 0.344 | 0.885 | Accepted by the fresh focused retry |
| Follow-up screenshot, 7-pixel inset | 0.383 | 0.868 | Accepted by the fresh focused retry |
| Follow-up screenshot, 6-pixel inset | 0.335 | — | Rejected: retry corner moved 6.9% of board width |

The three recovered variants took 72–105 ms per production detection. The agreement limit
remains 2%; it was not relaxed to accept the fourth case. Reports are
`sweep-production030.json` and `low-floor-report.json` in the artifact directory. This validates
the newly eligible path and explains the live missed retries; it does not establish complete
live-camera recovery without rebuilding and running the updated app on fresh captures.

104 corner, alignment, setup and reconnect tests passed after this change. Added checks cover
weak float32 peaks at 0.30, 0.31, 0.35 and 0.38 as proposals only, rejection below the floor or
with multiple weak corners, and the unchanged final confidence threshold. The report is
`artifacts/turn-timing-tests-20260919/results/board-corner-stability.trx`.

## User-confirmed live check

After rebuilding, the user confirmed that reconnect detection was working. Their follow-up
screenshot shows all four detected corner markers and the enabled **Reload Game** button, with
spare trains visible beside both board edges. This confirms recovery in the reported setup;
broader camera and occlusion validation remains open.
