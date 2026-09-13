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
weights and review artifacts stay ignored and are not automatically committed or uploaded.
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
