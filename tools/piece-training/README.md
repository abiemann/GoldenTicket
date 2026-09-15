# Piece-recognition dataset tools

These are **developer tools**, separate from the Windows app and its gameplay Training mode.
They prepare data for a learned train/score-marker detector. No model is trained or enabled by
opening the workbench or exporting a dataset. See [the ML sequence](../../docs/piece-recognition-ml.md).

The annotation workbench needs only a local browser. The exporter needs Python 3.12 or later
and uses the standard library only. Neither tool uploads files, downloads packages, or runs a
server. Python and the workbench are not application runtime dependencies.

The separate `train_piece_detector.py` now runs an explicitly requested local GPU experiment
and exports the model used by the experimental ML preview. It never trains merely because
an image is annotated. See **Train and evaluate locally** below.

The application uses the accepted tracked runtime pair in `assets/models/pieces/`:
`piece-detector.onnx` contains its learned weights and `manifest.json` defines and verifies
the runtime contract. Desktop build and publish copy both files into `models/pieces/`.
A fresh source checkout needs no model download, Python setup or retraining to use detection.
Training photos, reviewed labels, PyTorch checkpoints, environments and logs stay ignored.
After validation, promote only the accepted ONNX file and matching manifest together into
`assets/models/pieces/`; training commands below continue to write local candidates under
`artifacts/`. See the [tracked model inventory](../../assets/models/README.md).

## Annotate photos

1. Open `tools/piece-training/annotate.html` in Edge or Chrome. Opening the local file directly
   is supported; no web hosting is needed.
2. Choose **Open board photos** and select the original, unannotated PNG/JPEG board crops.
3. Set **Capture group** and **Dataset split**. Keep the same session and repeated layout,
   including its empty board and lighting variants, in one group and split.
4. Choose a kind and physical color. Drag a tight rectangle around **each individual piece**.
   Use the box list to select a box, change its class/color, or delete it and redraw it.
   Do not label printed routes, score numbers, glare or shadows as pieces.
5. Check the review box after inspecting every actual piece and annotation. Empty images
   have zero boxes and still need review. Editing labels or group/split clears review.
6. **Export labels JSON** saves your work through the browser download dialog. Labels live
   in memory until export. There is no autosave; export before closing or clearing the page.

To resume, select the same photos, then **Import saved labels**. Import validates all records
before replacing matching annotations. Coordinates always refer to the source image, even
when its preview is smaller. Source photos are never modified by drawing boxes.

Draft export is always available even when groups or reviews are unfinished. Strict training
validation happens in the exporter. Group every related image before using it for training.
Use upright photos; JPEGs requiring an EXIF orientation transform must first be exported as
upright PNGs so browser coordinates and training pixels agree.

Keyboard: Escape cancels a drag. Delete removes the selected box when the image canvas has
focus; it does not delete boxes while typing a capture group. Pointer drawing supports mouse,
pen and touch. Use the labelled box list for selection and class/color editing.

The JSON schema is version 1:

```json
{
  "version": 1,
  "images": [{
    "fileName": "board-01.png", "width": 1920, "height": 1200,
    "group": "session-01", "split": "train", "reviewed": true,
    "boxes": [{
      "kind": "train", "color": "blue",
      "x": 100, "y": 250, "width": 60, "height": 22
    }]
  }]
}
```

`x`/`y` are the top-left corner in source pixels. All boxes must fit entirely inside the image.
`kind` is `train` or `player-marker`; `color` is `black`, `blue`, `green`, `red`, `yellow`, or
`unknown`. Splits are `train`, `validation`, and `test`. An optional image `sha256` binds labels
to exact photo bytes. The exporter always records source hashes in its dataset summary.

## Build a COCO dataset

From the repository root, using a local Python 3.12+ installation:

Optionally create an initial unreviewed inventory from a directory containing only board photos:

```powershell
python tools/piece-training/prepare_dataset.py --images C:/BoardPhotos --init-labels artifacts/piece-training/labels.json
```

The labels parent directory must already exist. Import this inventory into the workbench after
selecting the photos, then label, assign groups/splits, review and export it. The initializer never
overwrites an existing label file and does not invent boxes or mark images reviewed.

Build a new dataset from completed labels:

```powershell
python tools/piece-training/prepare_dataset.py --labels artifacts/piece-training/labels.json --images C:/temp --output artifacts/piece-training/coco-v1
```

The output directory must not already exist. Unreviewed labels, missing files, invalid boxes,
unsafe filenames, mismatched image dimensions/hashes, and capture groups assigned to multiple
splits are rejected. Keep photos and generated labels/datasets under ignored `artifacts/` or
another private location; user photographs are not source-code fixtures.

The generated dataset contains unchanged source photos in `train2017`, `val2017`, and
`test2017`, plus `annotations/instances_train2017.json`, `instances_val2017.json`, and
`instances_test2017.json`. Category IDs are 1 = train and 2 = player-marker. Physical colors
are extra annotation metadata, not additional trained detection classes. `dataset-summary.json`
records counts, capture groups and image hashes. This validates dataset structure, not label
correctness or model accuracy. PNG/JPEG validation checks bounded file headers and dimensions;
it is not a full decoder check. Training must decode each file and report failures. An export
can contain an empty split; do not train or claim validation until the required independent
splits and class coverage have been collected and reviewed.

## Checks

```powershell
python -m unittest discover -s tools/piece-training -p "test_*.py"
$env:NODE_PATH = "$PWD/tools/ci/node_modules"
node tools/piece-training/annotation-smoke.cjs
```

The browser check uses the project's existing locked Playwright development dependency and
installed Edge. Set `GOLDENTICKET_TEST_BROWSER` to another compatible Chromium executable if
needed. It uses synthetic PNGs and an offline browser context. Its screenshots and report go
to ignored `artifacts/piece-annotation-smoke/`. It does not open a camera or read user photos.
The annotation browser check itself does not alter the .NET Windows app.

## Verified first slice · September 12, 2026

- 27 Python dataset tests passed. One further real-symlink test was skipped because the local
  Windows account lacks symlink creation privilege; the separate simulated guard check passed.
- 12 real Edge browser checks passed in an offline context, with zero JavaScript errors and
  zero HTTP/HTTPS requests. These cover source-pixel coordinates, train/marker labels, draft
  export, review invalidation, atomic invalid imports, photo hashes, EXIF orientation rejection,
  keyboard deletion and empty-image labels. Desktop/narrow screenshots were visually inspected.
- A browser-exported two-image negative fixture successfully passed the Python CLI and produced
  unchanged copies and COCO annotations. It is a tooling fixture, not recognition evidence.
- Ten supplied board photos were inventoried locally in
  `artifacts/piece-training/starter-labels.json`, with hashes and one conservative capture group.
  Every entry is unreviewed; no ground-truth boxes were inferred from detector output. The images
  and starter project are not committed. No .NET runtime changes or model training occurred.

## Train and evaluate locally

This first experiment uses the official [YOLOX](https://github.com/Megvii-BaseDetection/YOLOX)
Nano architecture (Apache-2.0 source), with two classes: `train`, `player-marker`.
It transfers the official COCO checkpoint into a new two-class head. Color labels remain
evaluation metadata; this model does not infer player color, routes, ownership, or legal moves.
The training loop, augmentation and diagnostics run locally with no telemetry or network calls.
Package/source/starting-weight downloads happen separately during environment setup.

Use an isolated Python 3.12 environment under ignored `artifacts/piece-training/.venv`.
Install `torch==2.8.0` and `torchvision==0.23.0` from the official CUDA 12.8 wheel index
(`https://download.pytorch.org/whl/cu128`), then install `requirements-training.txt`.
The experiment verifies CUDA before training. Record the complete environment with `pip freeze`;
each run writes its own `environment-lock.txt` automatically. The local CUDA setup is not an
application deployment dependency.

Download the official repository, check out exactly
`6ddff4824372906469a7fae2dc3206c7aa4bbaee`, and place it at
`artifacts/piece-training/vendor/YOLOX`. Download
[the official Nano checkpoint](https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_nano.pth)
to `artifacts/piece-training/pretrained/yolox_nano.pth`; its required SHA-256 is
`cd28f55fbbc1829f99d9ac9b38a16d259a22889739c8728ea877610201feff7b`.
The trainer refuses a different source revision or pretrained hash. It also verifies every
original photo's label hash/dimensions before decoding, and verifies the originals and label
file again after export. No source image is modified.

The recorded first run used 18 September 12 photos for training (179 trains, 64 markers,
including four empty boards) and 11 September 13 photos for validation (656 trains, 55 markers).
It kept the two capture groups intact. Dates from one physical camera/board setup are only a
provisional separation: these results are validation used for model and threshold selection,
not an untouched independent test. There are no empty or explicit glare examples in validation.

Run from the repository root, using new output directories for future experiments:

```powershell
& 'artifacts/piece-training/.venv/Scripts/python.exe' tools/piece-training/train_piece_detector.py --labels artifacts/piece-training/annotation-progress-01.json --images C:/temp --yolox-source artifacts/piece-training/vendor/YOLOX --pretrained artifacts/piece-training/pretrained/yolox_nano.pth --output artifacts/piece-training/runs/baseline-session-01 --epochs 40 --samples-per-epoch 256 --batch-size 16
```

The source crops are normalized to 1920 x 1200 with OpenCV `INTER_LINEAR` (half-pixel
bilinear). Training samples 640 x 640 tiles with random 512..768 source crop sizes, flips,
quarter-turn rotations, exposure changes and occasional gentle blur. Samples include both
positive-focused crops and random background. There is no empty-reference image input.
The 40-epoch baseline selected its best checkpoint at epoch 20; seed, commands, source image
hashes, splits, optimizer and environment are recorded in `run.json` and `history.json`.

Export the selected local checkpoint with the preview's overlap handling and operating point:

```powershell
& 'artifacts/piece-training/.venv/Scripts/python.exe' tools/piece-training/train_piece_detector.py --labels artifacts/piece-training/annotation-progress-01.json --images C:/temp --yolox-source artifacts/piece-training/vendor/YOLOX --pretrained artifacts/piece-training/pretrained/yolox_nano.pth --output artifacts/piece-training/model --epochs 40 --samples-per-epoch 256 --batch-size 16 --export-checkpoint artifacts/piece-training/runs/baseline-session-01/best.pth --tile-ownership --confidence 0.30
```

The manifest fixes input `images` float32 BGR, raw 0..255, NCHW `[1,3,640,640]`; output
`detections` `[1,8400,7]` contains decoded center-x, center-y, width, height, objectness,
train probability and marker probability. The exporter uses ONNX opset 17 and verifies it
with ONNX checker and ONNX Runtime CPU against PyTorch CPU. Fixed-point OpenCV resizing and
the app's floating bilinear resize can differ by one byte; a pre-resized local parity fixture
is supplied separately from the app's original-resolution resize check.

Inference tiles use stride 512 with the last tile anchored to the far edge: x starts
`[0,512,1024,1280]`, y starts `[0,512,560]`. Box coordinates are clipped to each tile first.
Keep a box only if its center belongs to that tile's overlap midpoint region, then apply
per-class NMS at IoU 0.45. This removes partial duplicate boxes at tile boundaries. Keep at
most 4096 proposals before NMS and 512 detections afterwards; reject boxes below one pixel.
Train outlines remain rectangles; marker squares are a display choice after evaluation.

At confidence 0.30, the first local model matched **693 of 711** reviewed validation objects
at IoU >= 0.50, with **3 false positives and 18 misses**: precision 99.57%, recall 97.47%.
All 55 validation score markers matched; the misses were trains. This threshold was chosen
using the validation sweep plus the known empty-board diagnostic, which has zero false
positives at 0.30. Those four empty boards were training images, so this is not an independent
empty-board score. The paired historical-reference comparison produced 371 matches,
398 false positives and 340 misses; four of eleven frames were held as `SceneChanged` and
counted as no visible predictions. That comparison uses an older empty reference, not a
new lighting-matched reference, and scores bounding rectangles of its rotated outlines.

After recording legacy outputs with `GoldenTicket.PieceDetectionSmoke`, the paired report is
reproducible using the same labels and final ML evaluation:

```powershell
& 'artifacts/piece-training/.venv/Scripts/python.exe' tools/piece-training/compare_piece_detectors.py --labels artifacts/piece-training/annotation-progress-01.json --images C:/temp --legacy-results artifacts/piece-training/comparison-baseline --ml-evaluation artifacts/piece-training/model/evaluation.json --output artifacts/piece-training/model/comparison-report.json
```

Local output includes `piece-detector.onnx`, `manifest.json`, per-object `evaluation.json`,
`empty-board-diagnostic.json`, and `review/*-review.png`. The diagnostic images show reviewed
boxes on the left; matches, false positives and missed labels on the right. Photos, labels,
training checkpoints and review artifacts stay ignored and are not automatically committed or
uploaded. Candidate exports remain local until an accepted ONNX/manifest pair is promoted
to `assets/models/pieces/`.
Preserve this baseline and use newly captured layouts to record genuine new failures before
changing training data. `--all-reviewed` exists only for explicitly labelled all-data experiments;
its output identifies all validation numbers as in-sample diagnostics.

Run training geometry and matching checks in the isolated environment:

```powershell
& 'artifacts/piece-training/.venv/Scripts/python.exe' -m unittest discover -s tools/piece-training -p test_training_geometry.py -v
```

These verify labels stay attached to pixels through crop/resize/rotation augmentation,
overlap ownership covers every board point once, and duplicate predictions cannot inflate
true positives. Standard-library-only test environments skip these optional training checks.

## Reviewed-failure retraining · September 13, 2026

The second experiment fine-tunes the preserved baseline checkpoint on all 32 reviewed photos
in collection `04`, including the three new failure examples and four empty controls:

```powershell
& 'artifacts/piece-training/.venv/Scripts/python.exe' tools/piece-training/train_piece_detector.py --labels artifacts/piece-training/labels-reviewed-04.json --images C:/temp --yolox-source artifacts/piece-training/vendor/YOLOX --pretrained artifacts/piece-training/pretrained/yolox_nano.pth --output artifacts/piece-training/runs/retrain-reviewed-04-r1 --epochs 40 --samples-per-epoch 512 --batch-size 16 --seed 20260914 --initialize artifacts/piece-training/runs/baseline-session-01/best.pth --all-reviewed --tile-ownership --confidence 0.30
```

Use a new output directory for any further run. The ordinary labels place the new failures in
the September 13 validation group; `--all-reviewed` explicitly includes them in training without
inventing a split between related captures. All resulting photo scores are **in-sample diagnostics**.
The trainer still selects its best epoch by threshold-swept F1; `--confidence` applies only to the
final evaluation/export. Epoch 30 was selected in this run. The trainer's `evaluation.json` covers
the 14 September 13 photos; a separate fixed-threshold ONNX comparison covers all 32 photos.

The comparison changes from 1,105 matches / eight extras / 21 misses to 1,126 / zero / zero, with
four empty controls still clear. Actual .NET CPU/DirectML fixture checks pass. The local preview
pair is updated and the prior pair is preserved for rollback. Independent new captures remain
necessary; these figures do not establish general accuracy. See the
[complete training and deployment record](../../docs/evidence/ml-retrain-2026-09-13/validation.md).

## Denver yellow-train follow-up

The third model adds reviewed photo `184806` after recording its failure against the frozen second
model. Collection `05` has 33 photos and 1,210 objects; prior 32 annotations and capture groups are
unchanged. This run initializes model weights from the preceding best checkpoint, while AdamW,
EMA updates and the learning-rate schedule restart:

```powershell
& 'artifacts/piece-training/.venv/Scripts/python.exe' tools/piece-training/train_piece_detector.py --labels artifacts/piece-training/labels-reviewed-05.json --images C:/temp --yolox-source artifacts/piece-training/vendor/YOLOX --pretrained artifacts/piece-training/pretrained/yolox_nano.pth --output artifacts/piece-training/runs/retrain-reviewed-05-r1 --epochs 40 --samples-per-epoch 512 --batch-size 16 --seed 20260915 --initialize artifacts/piece-training/runs/retrain-reviewed-04-r1/best.pth --all-reviewed --tile-ownership --confidence 0.30
```

The existing selection rule chose epoch 20. At fixed confidence 0.30 and exact runtime resize,
all 1,210 labels match without extras, including the Denver yellow train (score 0.9290). Six
production CPU/DirectML fixtures match all 352 reviewed objects, with one investigated subpixel
box difference due to nearly tied NMS candidates. The validated local pair is installed with
the preceding pair preserved. All 33 photos are now in-sample, including the added photo.
See the [training, parity and deployment record](../../docs/evidence/ml-retrain-denver-2026-09-13/validation.md).

The fourth model adds the reviewed stronger-shadow photos `202009` and `202150` in
collection `06`: 35 photos and 1,390 objects. It preserves all previous 33 entries and
related capture groups. The recorded training command was:

```powershell
& 'artifacts/piece-training/.venv/Scripts/python.exe' tools/piece-training/train_piece_detector.py --labels artifacts/piece-training/labels-reviewed-06.json --images C:/temp --yolox-source artifacts/piece-training/vendor/YOLOX --pretrained artifacts/piece-training/pretrained/yolox_nano.pth --output artifacts/piece-training/runs/retrain-reviewed-06-r1 --epochs 40 --samples-per-epoch 512 --batch-size 16 --seed 20260916 --initialize artifacts/piece-training/runs/retrain-reviewed-05-r1/best.pth --all-reviewed --tile-ownership --confidence 0.30
```

The automatically selected epoch-20 checkpoint adds a false positive to older photo
`125240` at the fixed preview threshold, despite improving the new cases. Its export and
comparison are retained as a rejected candidate. The predeclared fallback exports the
final epoch-40 checkpoint separately:

```powershell
& 'artifacts/piece-training/.venv/Scripts/python.exe' tools/piece-training/train_piece_detector.py --labels artifacts/piece-training/labels-reviewed-06.json --images C:/temp --yolox-source artifacts/piece-training/vendor/YOLOX --pretrained artifacts/piece-training/pretrained/yolox_nano.pth --output artifacts/piece-training/runs/retrain-reviewed-06-r1-final --epochs 40 --samples-per-epoch 512 --batch-size 16 --seed 20260916 --export-checkpoint artifacts/piece-training/runs/retrain-reviewed-06-r1/last.pth --all-reviewed --tile-ownership --confidence 0.30
```

The final export matches all 1,390 reviewed labels without extras at 0.30, keeps distinct
parallel black-train predictions, and improves the upper boxes from IoU about 0.51 to
0.85 / 0.84. Nine production CPU/DirectML fixtures pass, including `125240`. The selected
training weights remain in `retrain-reviewed-06-r1/last.pth`; the `-final` folder is a
separate export, not another training run. All 35 photos are in-sample after inclusion.
See [selection, validation and installation evidence](../../docs/evidence/ml-retrain-shadows-2026-09-13/validation.md).

Collection `07` adds the shifted-light photo `202835`, with 86 physical trains and five
markers. It contains 36 photos and 1,481 objects, preserving all 35 previous entries.
The user reported intermittent Miami extras, but the frozen fourth model already matches
every piece in this saved frame without extras. Empty printed slots and cast shadows are
reviewed background. The related `202727` score-reading fixture is excluded from training.

```powershell
& 'artifacts/piece-training/.venv/Scripts/python.exe' tools/piece-training/train_piece_detector.py --labels artifacts/piece-training/labels-reviewed-07.json --images C:/temp --yolox-source artifacts/piece-training/vendor/YOLOX --pretrained artifacts/piece-training/pretrained/yolox_nano.pth --output artifacts/piece-training/runs/retrain-reviewed-07-r1 --epochs 40 --samples-per-epoch 512 --batch-size 16 --seed 20260917 --initialize artifacts/piece-training/runs/retrain-reviewed-07-baseline/last.pth --all-reviewed --tile-ownership --confidence 0.30
```

The initializer is a verified copy of `retrain-reviewed-06-r1/last.pth`, which produced
the installed fourth model. Fixed evaluation must preserve all reviewed detections and
parallel black-train localization, then check CPU/DirectML and score readings. The
[shifted-light validation record](../../docs/evidence/ml-retrain-miami-shadows-2026-09-13/validation.md)
distinguishes this in-sample training check from the still-unmeasured live behavior.

The automatic epoch-20 selection adds two extras to `202150` at the fixed 0.30 threshold
and is rejected. Exporting the saved final epoch-40 checkpoint gives 1,481 matches without
extras on all 36 photos, with parallel-train and score-reading checks passing:

```powershell
& 'artifacts/piece-training/.venv/Scripts/python.exe' tools/piece-training/train_piece_detector.py --labels artifacts/piece-training/labels-reviewed-07.json --images C:/temp --yolox-source artifacts/piece-training/vendor/YOLOX --pretrained artifacts/piece-training/pretrained/yolox_nano.pth --output artifacts/piece-training/runs/retrain-reviewed-07-r1-final --epochs 40 --samples-per-epoch 512 --batch-size 16 --seed 20260917 --export-checkpoint artifacts/piece-training/runs/retrain-reviewed-07-r1/last.pth --all-reviewed --tile-ownership --confidence 0.30
```

The `-final` directory is an export from the same training run. Continue future training
from the verified `retrain-reviewed-07-r1/last.pth`, not from an absent checkpoint inside
the export directory. All 36 labeled evaluation photos are now training examples.

Collection `08` adds photo `203020`, whose pieces are unchanged while the shadows have
moved again. Native source review confirms all 91 prior body labels still align, yielding
37 photos / 1,572 labels with all previous 36 entries preserved. The recorded first run
uses seed 20260918; both saved checkpoints fail fixed runtime checks. Its final checkpoint
misses the `130924` red marker at a tile-ownership boundary, and its automatic selection
adds a duplicate spanning real blue/red trains in `203020`. Both exports remain preserved.

The recorded retry changes only the seed to 20260919 and starts from the same frozen
fifth-model initializer. It evaluates the final checkpoint first, using the same operating
threshold and unchanged labels:

```powershell
& 'artifacts/piece-training/.venv/Scripts/python.exe' tools/piece-training/train_piece_detector.py --labels artifacts/piece-training/labels-reviewed-08.json --images C:/temp --yolox-source artifacts/piece-training/vendor/YOLOX --pretrained artifacts/piece-training/pretrained/yolox_nano.pth --output artifacts/piece-training/runs/retrain-reviewed-08-r2 --epochs 40 --samples-per-epoch 512 --batch-size 16 --seed 20260919 --initialize artifacts/piece-training/runs/retrain-reviewed-08-baseline/last.pth --all-reviewed --tile-ownership --confidence 0.30
& 'artifacts/piece-training/.venv/Scripts/python.exe' tools/piece-training/train_piece_detector.py --labels artifacts/piece-training/labels-reviewed-08.json --images C:/temp --yolox-source artifacts/piece-training/vendor/YOLOX --pretrained artifacts/piece-training/pretrained/yolox_nano.pth --output artifacts/piece-training/runs/retrain-reviewed-08-r2-final --epochs 40 --samples-per-epoch 512 --batch-size 16 --seed 20260919 --export-checkpoint artifacts/piece-training/runs/retrain-reviewed-08-r2/last.pth --all-reviewed --tile-ownership --confidence 0.30
```

The initializer is a verified copy of `retrain-reviewed-07-r1/last.pth`. The retry preserves
its evaluated epoch-10/20/30/40 checkpoints for diagnostics, in addition to the automatic
selection and final export. See the [lighting-variation record](../../docs/evidence/ml-retrain-light-variation-2026-09-13/validation.md)
for fixed-runtime, native-provider and score-reading acceptance and the tile-boundary limitation.

The retry's final export is installed locally after all 1,572 labels and 13 native CPU/DirectML
fixtures pass. Continue training from `retrain-reviewed-08-r2/last.pth` (SHA-256
`e4fbf2f87105fc6dea9f654d97b4ebb05885af204cef1d27181341adeb5149a4`),
not from the export-only `retrain-reviewed-08-r2-final` directory. These 37 photos are all
training examples; their regression results do not measure independent-session accuracy.

Collection `09` adds `203656` after major lighting adjustments. Source review retains
the same 86 train bodies and five markers, excluding larger shadows. The collection has
38 photos and 1,663 labels, with all 37 earlier entries preserved. The recorded run uses
seed 20260920 and initializes from the verified sixth model's final checkpoint:

```powershell
& 'artifacts/piece-training/.venv/Scripts/python.exe' tools/piece-training/train_piece_detector.py --labels artifacts/piece-training/labels-reviewed-09.json --images C:/temp --yolox-source artifacts/piece-training/vendor/YOLOX --pretrained artifacts/piece-training/pretrained/yolox_nano.pth --output artifacts/piece-training/runs/retrain-reviewed-09-r1 --epochs 40 --samples-per-epoch 512 --batch-size 16 --seed 20260920 --initialize artifacts/piece-training/runs/retrain-reviewed-09-baseline/last.pth --all-reviewed --tile-ownership --confidence 0.30
& 'artifacts/piece-training/.venv/Scripts/python.exe' tools/piece-training/train_piece_detector.py --labels artifacts/piece-training/labels-reviewed-09.json --images C:/temp --yolox-source artifacts/piece-training/vendor/YOLOX --pretrained artifacts/piece-training/pretrained/yolox_nano.pth --output artifacts/piece-training/runs/retrain-reviewed-09-r1-final --epochs 40 --samples-per-epoch 512 --batch-size 16 --seed 20260920 --export-checkpoint artifacts/piece-training/runs/retrain-reviewed-09-r1/last.pth --all-reviewed --tile-ownership --confidence 0.30
```

The final export is checked first at the unchanged 0.30 preview threshold. The trainer's
automatic selection is preserved as a fallback only if that final checkpoint fails.
See the [major-lighting record](../../docs/evidence/ml-retrain-large-lighting-2026-09-13/validation.md)
for fixed-runtime and native CPU/DirectML results. All 38 labeled photos are used in training.

The final epoch-40 checkpoint misses the older `130924` red marker at a tile-ownership
boundary and is rejected. The saved trainer-selected epoch-30 checkpoint is exported
separately, retaining explicit provenance for the fallback evaluation:

```powershell
& 'artifacts/piece-training/.venv/Scripts/python.exe' tools/piece-training/train_piece_detector.py --labels artifacts/piece-training/labels-reviewed-09.json --images C:/temp --yolox-source artifacts/piece-training/vendor/YOLOX --pretrained artifacts/piece-training/pretrained/yolox_nano.pth --output artifacts/piece-training/runs/retrain-reviewed-09-r1-best --epochs 40 --samples-per-epoch 512 --batch-size 16 --seed 20260920 --export-checkpoint artifacts/piece-training/runs/retrain-reviewed-09-r1/best.pth --all-reviewed --tile-ownership --confidence 0.30
```

The explicit epoch-30 export is installed locally after all 1,663 labels match without
extras and 14 native CPU/DirectML fixtures pass. Continue training from the accepted
`retrain-reviewed-09-r1/best.pth`, SHA-256
`cebddfa224a03a1f9b91f25c49aea0638d8b53aa951ef431d21485b0bd618893`.
The `-best` directory contains export artifacts; the rejected `last.pth` is preserved
for diagnostics and is not the accepted continuation checkpoint.

## Pixel webcam HQ retraining · September 14, 2026

Collection `10` adds `GoldenTicket-board-20260914-182629.png`, a 3456 × 2160 source
captured in Pixel webcam HQ mode as reported by the user. Visual review identifies
43 trains and no scoring markers: 14 black, 7 blue, 12 green, 4 red and 6 yellow.
Printed route slots and shadows remain background, including the empty Miami area.
The collection contains 39 photos and 1,706 labels (1,542 trains and 164 markers),
including four empty-board controls, with all 38 previous entries unchanged.

The run uses 40 epochs, 512 samples per epoch, batch size 16 and seed 20260921.
Its initializer is a frozen, verified copy of the accepted reviewed-09 epoch-30
`best.pth`, not the rejected reviewed-09 final checkpoint. The exact recorded
training and explicit epoch-40 export commands are:

```powershell
& 'artifacts/piece-training/.venv/Scripts/python.exe' tools/piece-training/train_piece_detector.py --labels artifacts/piece-training/labels-reviewed-10.json --images C:/temp --yolox-source artifacts/piece-training/vendor/YOLOX --pretrained artifacts/piece-training/pretrained/yolox_nano.pth --initialize artifacts/piece-training/runs/retrain-reviewed-10-baseline/best.pth --output artifacts/piece-training/runs/retrain-reviewed-10-r1 --epochs 40 --samples-per-epoch 512 --batch-size 16 --seed 20260921 --all-reviewed --tile-ownership --confidence 0.30
& 'artifacts/piece-training/.venv/Scripts/python.exe' tools/piece-training/train_piece_detector.py --labels artifacts/piece-training/labels-reviewed-10.json --images C:/temp --yolox-source artifacts/piece-training/vendor/YOLOX --pretrained artifacts/piece-training/pretrained/yolox_nano.pth --export-checkpoint artifacts/piece-training/runs/retrain-reviewed-10-r1/last.pth --output artifacts/piece-training/runs/retrain-reviewed-10-r1-final --epochs 40 --samples-per-epoch 512 --batch-size 16 --seed 20260921 --all-reviewed --tile-ownership --confidence 0.30
```

The final candidate matches all 1,706 labels across all 39 photos with zero extras
and zero misses at the unchanged confidence 0.30 / NMS 0.45 / matching IoU 0.50.
The prior 38-photo results reproduce, and the Denver parallel-pair and Miami
hard-negative checks pass. The frozen baseline already matched every reviewed
object, including all 43 trains in this HQ photo, so these counts establish
regression preservation rather than a measured increase in detection accuracy.

Candidate ONNX SHA-256:
`81cfec6a8e423f4106e2aee067376136a8255d4855da97d4464a2b22006fa8bb`.
The final export is installed locally after 14 labeled native CPU/actual DirectML
fixtures and the separate untrained score fixture passed. Every labeled fixture
matched without extras or misses on both providers; expected scores and prior
parallel-piece/background checks passed. The accepted continuation checkpoint is
`artifacts/piece-training/runs/retrain-reviewed-10-r1/last.pth` (epoch 40), SHA-256
`8907c41d04a066ed7a31c20fdd4b9b4daf1ab3f50e8f5945ef48a5a44ad2f596`.
The `retrain-reviewed-10-r1-final` directory contains only the separate export.
The reviewed-09 model and manifest are preserved under `model-before-reviewed-10`
for rollback. That training validation installed only the local canonical and Debug/Release
piece model pairs; no application rebuild or runtime threshold change was needed at that stage.
The same accepted ONNX/manifest pair is now the tracked `assets/models/pieces/` deployment input.

All 39 labeled photos enter this training run, so the resulting checks are saved-photo
regressions, not independent-session or live-reliability measurements. See the
[Pixel HQ validation record](../../docs/evidence/ml-retrain-pixel-hq-2026-09-14/validation.md)
for source provenance, frozen identities, baseline details and final acceptance status.
