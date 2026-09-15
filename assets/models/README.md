# Runtime models

These reviewed deployment pairs are versioned with the application. Each ONNX file
contains its complete network and trained weights; neither references external tensor
files. A fresh checkout can build the desktop app with both detectors, without a
Python environment, training data, `.pth` checkpoint or model download.

| Detector | ONNX file | Bytes | SHA-256 |
| --- | --- | ---: | --- |
| Pieces | `pieces/piece-detector.onnx` | 3,728,476 | `81cfec6a8e423f4106e2aee067376136a8255d4855da97d4464a2b22006fa8bb` |
| Board corners | `board-corners/board-corners.onnx` | 2,082,435 | `09fce75df40d3253c4dfc744a79a24dcedd5ec7319679e83bfb117e6e4e2aea5` |

The paired manifests retain their runtime contract, model hash and training provenance:

- Piece manifest SHA-256: `70dfda5bbe57ea2786b00efc120cd6ee4635a7d065176277448361cd4e874caa`.
- Corner manifest SHA-256: `0795df0fab36c802ba0e5ce70e825320c0ff88e01aadb32f9b06d0b50533c1be`.

Git attributes preserve model and manifest bytes across checkout line-ending settings.

The desktop project copies these exact files into `models/pieces/` and
`models/board-corners/` beside the executable during build and publish. The piece
model's [YOLOX license](../../docs/licenses/ml/yolox-LICENSE.txt) is copied with it.
See [dependency notices](../../docs/ml-dependencies.md), the
[piece validation](../../docs/evidence/ml-retrain-pixel-hq-2026-09-14/validation.md)
and the [corner validation](../../tools/board-corners/README.md#local-experiment-13-september-2026).
Both models remain experimental; saved-photo or synthetic checks do not establish
reliability for every live camera scene.

## Updating a model

Train and evaluate under ignored `artifacts/`. Preserve training photos, labels,
continuation checkpoints, environments, intermediate exports and review images there
or outside the repository. After acceptance, copy only the selected ONNX file and its
matching manifest into the corresponding directory here, preserving a local rollback.
Verify model/manifest hashes, run the relevant regression checks and update this
inventory and validation record before committing the replacement pair.

Changing an ignored `artifacts/*/model/` export alone no longer changes what a build
ships. The `.pth` checkpoint is useful for future training but is not an inference
dependency and remains ignored. The manifests contain provenance metadata, not the
training photographs or annotations themselves.
