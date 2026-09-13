# Learning to recognize physical pieces

Status: developer data preparation, September 12, 2026. No trained recognition model is
bundled or running yet. The Windows preview still uses `PieceCandidateDetector` and manual
game verification. The tools in [piece-training](../tools/piece-training/README.md) prepare
human-reviewed data; they do not improve recognition by themselves.

## Why change the detector

The current detector compares colors and shapes against an empty-board photograph. Its
lighting sensitivity is demonstrated by the [glare experiments](glare-test.md): the same
8 trains and 4 score markers produced 8/4, 19/4, and 35/6 candidate counts as lighting changed.
All real pieces were covered in those reviewed images, but printed routes gained false boxes.
The user subsequently found that capturing the empty board under the new lighting removes
false positives, including with two spotlights. Switching a spotlight off afterward also
produces false positives. These latter observations are user reports, not a new counted dataset.

An independently trained object detector should learn the appearance of plastic trains and
physical score markers from the current image. It must distinguish them from flat route
artwork, printed score numbers, shadows, seams, and reflections. This is the proposed
improvement, not a guarantee of glare immunity. Clipped highlights and occlusion can remove
the evidence needed to identify a piece; such regions must remain uncertain.

Do not limit ML to accepting or rejecting the current subtraction candidates. That would
inherit the comparator's missing candidates and empty-reference requirement. Use subtraction
as an optional diagnostic signal; train and evaluate independent image detection.

## First model experiment

Start with two detection classes: `train` and `player-marker`. Each physical train receives
its own bounding box, including touching trains. Record the physical plastic color separately
as `black`, `blue`, `green`, `red`, `yellow`, or `unknown`. Color metadata is available for
coverage and error analysis; a two-class detector does not automatically learn player color
or ownership. Printed route color is not the owning player's train color.

Evaluate a small YOLOX model as the initial experiment. Its source is Apache-2.0 and its
official workflow supports custom COCO datasets and ONNX export. The exact source revision,
training environment, and any starting weights must be pinned and their redistribution
provenance recorded before training a distributable model. Generic pretrained object weights
alone are not a model of these game pieces. [YOLOX source and license](https://github.com/Megvii-BaseDetection/YOLOX),
[custom training](https://yolox.readthedocs.io/en/latest/train_custom_data.html),
[ONNX export](https://yolox.readthedocs.io/en/latest/demo/onnx_readme.html).

Prefer overlapping tiles from the unannotated, rectified native camera image, with tile size
and overlap fixed in the model manifest. Benchmark candidate tile sizes such as 640 and 960
pixels rather than shrinking the entire board to a tiny network input. Merge predictions in
board coordinates and remove duplicate boxes at tile boundaries. Preserve individual touching
trains. Native 4K can supply additional captured detail; upscaling a 1080p frame cannot recover
missing evidence. Compare raw and gently enhanced inputs on validation data and use exactly
the chosen preprocessing during both training and inference.

Axis-aligned boxes are sufficient for the first detection experiment. Rotated train outlines
or instance masks require further labels/model output; do not report them as learned from
axis-aligned labels. The display can draw white rectangles and square marker outlines while
preserving the detector's original boxes in evaluation output.

## Local annotation workflow implemented now

Open [the annotation workbench](../tools/piece-training/annotate.html) in Edge or Chrome.
It reads only image files explicitly selected by the developer. There is no server, upload,
account, CDN, analytics, or cloud annotation service. The workbench uses source-image pixel
coordinates regardless of its on-screen scale. Exported label JSON is saved through the
browser's normal download flow; keep a copy before closing the page.

For each image:

1. Select its capture group and train/validation/test split.
2. Draw a tight box around every visible physical train and score marker. Include the body,
   excluding cast shadows. Assign the piece type and physical color.
3. Review the whole image for missed, duplicate, and false labels. Mark the image reviewed
   only when every visible piece is labelled. An empty board is a reviewed image with zero
   boxes, not an image omitted from the dataset.
4. Export the label project. Build the dataset with `prepare_dataset.py` as described in the
   [tool instructions](../tools/piece-training/README.md).

The exporter checks reviewed status, image dimensions and hashes, finite in-bounds boxes,
safe filenames, and capture-group isolation before creating a new COCO dataset directory.
It copies source photographs unchanged. Player color is retained as annotation metadata.
All photographs, labels, and generated datasets should remain under ignored `artifacts/` or
another explicitly selected private data directory. Do not commit user photographs or trained
weights automatically.

## Capture plan

Existing photos are useful starting examples, but represent very few physical layouts. They
cannot establish that a model works on an unseen game. An initial collection target is roughly
200–500 distinct photographs across at least 30 varied layouts and several separate setup
sessions. This is a planning estimate, not a promised sample count for acceptable accuracy.
Expand the collection based on errors on validation sessions.

- Cover all five plastic colors, especially black trains on printed black routes and markers
  on dark score-track numbers. Include every board region and score-track corner.
- Include isolated trains, touching trains, partial routes, dense neighboring routes, and a
  nearly full board. A group of three trains needs three labels.
- Capture empty boards under soft light and one/two spotlights as negative examples. Include
  changing-light populated scenes, not only glare matched to an empty-board reference.
- Repeat layouts with modest camera angles, focus/exposure variation, shadows and ordinary
  room lighting. Record native source resolution; exported/upscaled pixel dimensions are not
  proof of native 4K capture.
- Keep the whole board visible and hands clear for this initial detector dataset. Collect
  hand/occlusion and genuinely unrecognizable glare scenes separately for the later uncertainty
  policy; do not silently treat hidden pieces as reviewed background.

Keep every frame from a capture session, its empty reference, lighting variants, and near
duplicates in the same capture group and split. In particular, `164319`, `164557`, `164624`,
and `164718` from September 12 must not be split across training and evaluation. A new light
setting on the same layout is not an independent test game. Choose the split before training;
keep final test sessions untouched while choosing thresholds or preprocessing.

Do not generate ground truth by trusting the current detector. Its useful suggestions still
need review, especially on glare images where its false boxes are already known.

## Training and acceptance still required

Training runs on the developer's machine. End users receive a tested, bundled ONNX model;
they do not label pieces, install Python, train, or download models. The existing game
**Training mode** is story guidance and remains unrelated to developer ML training.

Before activating ML in the application:

1. Lock a reproducible local training environment and reviewed data manifest. Include source
   hashes, group splits, seed, class order, tile geometry, preprocessing, model source revision,
   starting-weight provenance, and training/export commands in the model record.
2. Measure per-object precision/recall at stated overlap thresholds, misses, duplicate boxes,
   and false positives per board image, including empty boards. Break results down by color,
   score marker/train, lighting, board region, and source resolution. Counts alone can hide a
   missed piece replaced by a false positive.
3. Compare the learned detector with the current baseline on the exact same held-out images.
   Include lights added and removed, both with and without an empty-board reference. Require a
   measured reduction in false positives without an unacceptable increase in missed pieces;
   record thresholds before evaluating the final test set.
4. Export and validate ONNX inference on CPU and GPU with the same labelled images. Report
   cold startup, per-frame latency including tiling/transfers/merging, memory, and fallback.
5. Run live camera tests for occlusion, motion, camera jog/recovery, stopped capture, stale
   results and crowded boards. Static-photo detection is not automatic move verification.

No trained weights, training run, ML accuracy gain, or general glare tolerance is claimed by
the annotation-tool checks. Dataset preparation is the completed first dependency; the items
above remain open.

## Runtime integration contract

Keep the self-contained Windows ML/ONNX provider plan in [DESIGN §17.4.2](../DESIGN.md#1742-planned-learned-model-inference).
All model/runtime/provider files must ship locally. Never acquire execution providers or models
from the Internet at launch. A pinned DirectML ONNX Runtime package remains the documented
fallback if the Windows ML spike fails. C# and DirectML inference are supported by ONNX Runtime;
DirectML has sequential-session constraints and is in sustained engineering, so use the current
Windows ML guidance when selecting the final runtime. [C# runtime](https://onnxruntime.ai/docs/get-started/with-csharp.html),
[DirectML constraints](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html),
[current installation guidance](https://onnxruntime.ai/docs/install/).

The future detector receives a fresh unannotated board image and returns normalized outlines,
class scores and model identity. It must work without `HasPieceReference`. Add a model revision
to the existing camera epoch, crop revision and processing revision checks. Drop work older
than two seconds and clear stale outlines. Preserve one active frame job, bounded buffers,
cancellation and orderly model disposal. Do not publish results from a replaced model or crop.

Report inference backend separately from image enhancement: a GPU enhancement badge alone
does not prove the model ran on GPU. Validate the loaded model's hash, bounded input/output
contract, allowed classes and operators; do not expose arbitrary ONNX loading through the PWA.
Inference failures should preserve manual gameplay and show the actual fallback or unavailable
state rather than quietly claiming learned detection succeeded.

Piece detection still supplies observations only. Assigning those observations to routes,
recognizing player color, temporal confirmation, and complete-board rule verification are
separate acceptance gates. The current board manifest has no production pixel geometry yet.
