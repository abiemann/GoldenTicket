# Classic-US route-coordinate alignment — September 19, 2026

## Reproduced failure

The game requested three blue trains on Los Angeles–San Francisco lane B. Camera diagnostics
already detected all three as blue at approximately 95–96% confidence. Their centers were near
`(160, 783)`, `(193, 844)` and `(239, 896)` in the 1996 × 1248 reference axes, but the placement
verifier rejected them at the boundary between the two lanes.

Inspection of the empty printed spaces in a subsequent frame found the board artwork itself
10–11 reference pixels left of the original geometry reference. Refinement against the saved
game photo recovered less than one pixel: that saved photo preserved the same baseline offset.
The route geometry was checked against its original photo and was not moved to fit detections.

Three fresh shared-read-only webcam captures with the trains replaced reproduced the issue.
The original app crop incorrectly matched all three to lane A and none to B. Refinement against
the saved photo still matched none to B. Alignment against the original geometry artwork put all
three in B and rejected A. Camera settings and the running game were not changed by the replay.

## Change

The Vision assembly embeds a 320 × 200 grayscale reference (64,000 bytes) derived from the exact
photo used to measure `ClassicUsRouteGeometry`. Its original and derived hashes and extraction
method are recorded in [calibration provenance](../../../src/GoldenTicket.Vision/Calibration/README.md).
The original photo, training inputs, and diagnostic captures remain local and ignored.

After checking orientation against the setup or saved photo, game-table alignment first matches
the fixed reference. Saved-photo alignment is a fallback if the fixed artwork match is weak.
Initial setup and manually adopted crops also wait for the asynchronous alignment check before
piece inference. Preview, overlays and inference use the same registration. Camera identity,
freshness, reference identity and crop revision prevent obsolete work from replacing a newer crop.

The correction bounds remain 1% per board axis at each corner. Model weights, confidence and
lane-separation thresholds are unchanged. Expected routes and train detections are not inputs to
alignment; every occupied space still requires a distinct detection with the correct color.

## Real-frame replay

The following results use `ClassicUsBoardAlignment.Reference.TryRefine` with the embedded runtime
bytes, followed by the production detector, color reader, placement and inventory verifiers:

| Case | Result |
| --- | --- |
| Three current frames, CPU | LA–SF lane B confirms 3/3; lane A matches 0/3 |
| First current frame, DirectML / RTX 4080 Laptop GPU | Same result as CPU |
| Current Atlanta–New Orleans route | Lane A confirms 4/4; lane B matches 0/4 |
| Current full inventory, all three frames | All 55 trains across 25 routes confirm: 29 blue and 26 yellow |
| Earlier three restore frames, each under both failing crops | All 20 trains across 11 routes confirm in all six replays |
| Earlier Duluth–Omaha parallel-lane check | Lane A confirms 2/2; lane B matches 0/2 |

Replays include the required stability interval using distinct observation sequences. They validate
recorded image handling, not uninterrupted live application behavior. Detailed local reports and
rectified images are in `artifacts/la-sf-20260919/embedded-replay/`; the earlier and negative controls
remain under the sibling capture directories.

The current full inventory includes 24 committed routes and the pending Los Angeles–San Francisco
lane B claim. Route ownership and player colors were extracted from the session journal through a
read-only SQLite connection; the replay did not change the save or advance the game.

## Automated validation

`dotnet test GoldenTicket.sln -c Release --no-restore` passed all 872 tests with no failures or
skips. New regressions cover calibration integrity, a crop bias shared by the saved photo,
weak-match rejection, cancellation, initial canonical alignment, blocking inference while manual
crop alignment is pending, and preventing delayed periodic results from replacing a newer crop.

## Refocus recovery follow-up

A subsequent Boston–New York report came from the earlier 08:34 desktop build. Its log first
reported that route missing immediately after a crop revision. Offline screenshot analysis found
both yellow trains at 95–96% detector confidence; one center was rejected near the parallel-lane
boundary. This distinguishes a coordinate mismatch from a missing train detection.

Raw-frame replays exposed a remaining recovery gap in the initial alignment gate: logged crops
53 and 54 scored below its 0.55 minimum, so local refinement never ran. Weak initial matches now
get a coarse translation search before rejection. Coarse and fine adjustments share the original
one-percent budget; the final 0.65 artwork threshold and lane-separation checks are unchanged.
Periodic recovery also freshly refines both the previous and proposed registrations and keeps
the previous one only when its current artwork match is better. This handles corner jumps that
cannot be fully corrected from the new proposal within the bound.

Using the production recovery path, three recorded raw frames replayed with each of the three
logged crop proposals confirm both yellow trains on Boston–New York lane A. Lane B matches zero
trains in all nine cases. The full inventory also confirms all 55 trains across 25 routes in each
case, including Los Angeles–San Francisco lane B. These are offline replays with a previously accepted registration,
not observations from a restarted live game. Detailed reports remain local and ignored under
`artifacts/boston-blur-20260919/`.

A desktop regression now covers temporary loss of board detail, removal of stale piece evidence,
reacquisition from a biased corner proposal, fresh canonical Boston–New York coordinates, and
confirmation after two stable observations. The adjacent lane remains rejected. Additional
regressions cover low initial similarity, actual board movement, and obsolete camera registrations.
All 107 related tests pass, and the normal Debug desktop build succeeds with no warnings or errors.
An already running older process must be restarted to use the updated desktop build.

## Limits

These captures share one physical board and camera setup. They do not establish accuracy across
every route, camera angle, board printing, lighting condition or obstruction. Broader live-board
validation remains open. The running game was not restarted to install the change.
