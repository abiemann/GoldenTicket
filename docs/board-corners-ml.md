# Experimental ML board corners

The technical camera preview uses a separate learned model to select the four **outer** board
corners, including the score track. It runs once when a fresh camera/format session starts.
**Detect board corners** beside zoom retries on the current camera image. The existing numbered
handles, keyboard nudges, zoom and pan remain available for corrections. **Select four board
corners** starts manual placement.

The **Before we begin** game screen uses the same model and camera stream for a separate live
framing check, roughly once per second while that screen is open. Four accepted corners appear as
small white plus signs over the preview. No orientation click is required: the app rectifies the
board in four rotations and runs the existing piece detector and score-marker reader. **PLAY!**
remains disabled until the current camera epoch has a recent, confident four-corner result with
every corner inside the frame, no detected trains, and one marker for every chosen train color
near the printed **1** area. The notice names missing colors or tells the operator to remove
trains; it disappears when all checks pass. A single missed corner detection keeps the recent
result; a second miss on a
fresh frame at least one second later clears the markers and shows board-position guidance.
A stale frame, camera restart or leaving the screen clears readiness immediately. A previous
manual crop does not satisfy this gate. This check does not overwrite technical crop handles.
The four-rotation comparison uses marker placement to choose a likely orientation; it does not
independently read the printed numeral. A marker cluster at another score-track corner could
therefore pass this proximity check. Rotated-board and marker checks have synthetic coverage;
real overhead-camera accuracy still needs validation.

Automatic selection adds a narrow outward margin before setting the handles: a 0.5% expansion about
the detected quadrilateral's center, equivalent to 0.25% of each board dimension on each side for a
rectangular board. This leaves a thin border beyond the board edge, approximately 4 pixels for a
1600-pixel-wide board, following the user's manually adjusted crop examples. The
visible yellow outline is the actual crop used by preview, export and piece inference. Padding
is applied once to each new model result; retries do not accumulate it and manual adjustments
remain exact. Expansion is limited to available camera pixels and must contain the original board.
The preview reports when the camera boundary limits the margin, so the camera can be framed wider.

Technical crop selection is deliberately stationary after the initial attempt. Moving the camera
requires a retry or handle adjustment; this is not continuous board tracking. Manual edits take priority
over work in progress. A rejected or failed retry preserves an existing valid crop, and a missing
model leaves manual placement available. The corner status names the active inference backend.
The next attempt applies the current CPU/GPU preference. Piece outlines start using the accepted
crop without an empty-board reference; corners do not verify game state or route ownership.

## Local deployment

Build with `artifacts/board-corners/model/board-corners.onnx` and `manifest.json` present. The
Desktop project copies that pair into `models/board-corners/` beside the executable. The separately
trained piece model remains in `models/pieces/`. Photos, weights, checkpoints and Python environments
remain ignored local artifacts; the app never downloads or trains a model. A fresh source checkout
needs a compatible local deployment pair to enable automatic selection and the game-setup PLAY
gate. Without it, the game screen shows a model-unavailable status and cannot start a new match.

## Model and limits

The compact U-Net learns four corner heatmaps from manually rectified board photographs placed
into synthetic camera views with projective changes, backgrounds and lighting variation. Image
boundaries supply the known corner targets. Empty/partial views provide negative examples. The
runtime consumes the uncropped raw camera image, with no existing crop or reference photograph.
It uses RGB float32 values 0–1, a centered bilinear 384 × 384 letterbox, four 192 × 192 sigmoid
heatmaps in TL/TR/BR/BL order, and a 5 × 5 weighted centroid around each peak. Outputs are mapped
back through the letterbox into full-camera normalized coordinates.

The local manifest fixes that contract and checks the ONNX hash, graph operators, embedded
tensors and shapes. Confidence, in-image coordinates, clockwise order and stable quadrilateral
geometry are checked before acceptance. ONNX Runtime uses the same pinned DirectML library as
the piece model, with an actual warm-up and CPU fallback. Inference runs off the UI thread.
Camera epoch/format, crop-edit revision, cancellation and source age are rechecked before the
four handles replace the crop. Model loading happens before taking the frame used for inference.

Synthetic held-out examples and supplied screenshot camera regions are development diagnostics.
They do not establish independent real-camera accuracy. The source photographs were manually
cropped, and the model can inherit their boundary error. Severe glare, cut-off corners, hands,
unfamiliar backgrounds, camera rotation and different board editions still need real capture
tests. Inspect the outline and use zoom to refine it before exporting a precisely cropped photo.

See [reproduction commands](../tools/board-corners/README.md) and
[validation evidence](evidence/board-corners-2026-09-13/validation.md).

## Offline runtime diagnostic

The existing ML smoke tool accepts full camera images with:

```powershell
dotnet run --project tools/GoldenTicket.MlPieceSmoke -c Release -- --corners artifacts/board-corners/model full-camera-image.png artifacts/board-corners/runtime-check --compare
```

It writes numbered corner overlays and JSON containing model identity, confidence/rejection,
CPU/GPU timing and coordinate parity, actual operator providers, and loaded native library hashes.
Separate `board-crop-padded-*.png` overlays and `CropCorners` show the final margin used by the app;
`board-corners-*.png` and `Corners` retain the original model predictions for model evaluation.
It reads only explicit local files and never opens the camera.
