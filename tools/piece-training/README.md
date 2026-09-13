# Piece-recognition dataset tools

These are **developer tools**, separate from the Windows app and its gameplay Training mode.
They prepare data for a learned train/score-marker detector. No model is trained or enabled by
opening the workbench or exporting a dataset. See [the ML sequence](../../docs/piece-recognition-ml.md).

The annotation workbench needs only a local browser. The exporter needs Python 3.12 or later
and uses the standard library only. Neither tool uploads files, downloads packages, or runs a
server. Python and the workbench are not application runtime dependencies.

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
The .NET Windows app is unchanged by this tooling.

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
