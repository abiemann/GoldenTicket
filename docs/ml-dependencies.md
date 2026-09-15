# Local ML preview dependencies

The ML preview runs offline. The application does not install Python,
download weights or execution providers, or send images to an inference service. Training
dependencies are isolated developer tools; only the ONNX models, matching manifests and native/managed runtime
are needed in the Windows application.

| Component | Pinned version | Role / notice |
|---|---|---|
| Microsoft.ML.OnnxRuntime.DirectML | 1.24.4 | ONNX native runtime and DirectML provider, [MIT notice](licenses/ml/onnxruntime-LICENSE.txt) |
| Microsoft.ML.OnnxRuntime.Managed | 1.24.4 | C# API, same ONNX Runtime source/license |
| Microsoft.AI.DirectML | 1.15.4 | DirectML native library, [Microsoft license terms](licenses/ml/directml-LICENSE.txt), [code notices](licenses/ml/directml-LICENSE-CODE.txt) and [third-party notices](licenses/ml/directml-ThirdPartyNotices.txt) |
| System.Numerics.Tensors | 9.0.0 | Managed runtime dependency, .NET MIT license |
| YOLOX-Nano source | 6ddff4824372906469a7fae2dc3206c7aa4bbaee | Training architecture, [Apache-2.0 license](licenses/ml/yolox-LICENSE.txt) |

ONNX Runtime package metadata pins upstream commit
`2d924974ef147392ced8409d36bd6d2e7fcc8a74`. DirectML is a Windows redistributable under its own
terms; it is not described as MIT. The existing offline packaging process additionally extracts
resolved package notices and inventories dependencies. No release is created by this experiment.

Training starts from the official YOLOX-Nano COCO checkpoint:
[upstream release asset](https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_nano.pth),
SHA-256 `cd28f55fbbc1829f99d9ac9b38a16d259a22889739c8728ea877610201feff7b`.
The local run manifest records source/weight hashes, commands, data split, seed and environment.
Accepted ONNX files include the trained weights and are tracked with their matching manifests
under `assets/models/pieces/` and `assets/models/board-corners/`. Desktop build and publish copy
these required pairs beside the application. A fresh checkout needs no separate model download
or retraining. Photographs, reviewed labels, PyTorch checkpoints, environments and diagnostic
logs remain ignored local artifacts. Future promotion copies only validated ONNX/manifest pairs
into the tracked directories; see the [model inventory](../assets/models/README.md).

Runtime uses sequential sessions and disables memory-pattern optimization as required by the
[DirectML provider](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html).
The provider is in sustained engineering; the broader Windows ML deployment plan remains a
future evaluation. CPU fallback is provided by the same package, avoiding competing native
ONNX runtime packages. Inference provider status is separate from image enhancement status.

The Vision project explicitly copies the x64 `DirectML.dll` into development output and publish
items, including consumers built through project references. It omits the debug layer and PDB.
Before GPU initialization, the detector verifies and loads that app-local DLL by absolute path;
ordinary dependency search in a `dotnet`-hosted tool can otherwise select the Windows system copy.
The pinned DLL is version `1.15.4+241025-1615.1.dml-1.15.fac7597`, SHA-256
`9c9e6d822561c6c41b90e6994b3e8857cf1d66dbfb1e0c4c799c7c89b4e92da1`.
A missing, changed or unloadable local GPU library selects CPU inference with a reason. A loaded
library stays resident for the process lifetime while ONNX environments may still use it.
The ML smoke tool records actual native module paths, versions and hashes alongside provider profiles.

The [corner detector](board-corners-ml.md) uses the same pinned runtime and native loader. Its
small convolutional heatmap network is trained locally from scratch; it does not add a pretrained
third-party checkpoint or a second native runtime. Its accepted runtime pair is tracked separately
from the piece model; training outputs remain under ignored `artifacts/board-corners/`.

See [training setup](../tools/piece-training/README.md) and
[the experiment record](piece-recognition-ml.md) for reproduction and current limits.
