# Offline camera processing dependencies

This inventory covers image enhancement. The separate learned piece detector and its license
notices are listed in [ML dependencies](ml-dependencies.md).

The GPU preprocessing path uses Direct3D 11 compute shaders through the following
NuGet packages. Package versions and content hashes are recorded in the committed
development and `win-x64` lock files. All listed libraries declare the MIT license;
no account, subscription, online inference endpoint, model download, CUDA toolkit,
or vendor SDK installation is required by this feature.

| Package | Resolved version | Role |
|---|---|---|
| Vortice.Direct3D11 | 3.8.3 | Hardware compute, buffers, and readback |
| Vortice.D3DCompiler | 3.8.3 | Compile the fixed, shipped shader source locally |
| Vortice.DXGI | 3.8.3 | Enumerate and identify hardware adapters |
| Vortice.DirectX | 3.8.3 | Shared DirectX types |
| Vortice.Mathematics | 2.1.0 | Transitive math types used by the bindings |
| SharpGen.Runtime | 2.4.2-beta | Transitive interop runtime required by Vortice 3.8.3 |
| SharpGen.Runtime.COM | 2.4.2-beta | Transitive COM interop runtime |

Windows 11 supplies Direct3D 11, DXGI, and `D3DCompiler_47.dll`. The installed
graphics driver supplies the hardware implementation. Failure to initialize or
validate a hardware device selects the equivalent CPU implementation. The app does
not request the WARP software renderer and never labels it GPU processing.

## Upstream sources and notices

Sources below are pinned to the repository commits identified by the restored
NuGet specifications. Checked on 2026-09-12. Retain this file with distributed
copies of these libraries; the Vision project copies it to `licenses/` in build
and publish output. This inventory covers the added image-processing libraries,
not every dependency of GoldenTicket.

- [Vortice.Windows 3.8.3 license](https://github.com/amerkoleci/Vortice.Windows/blob/9e609cb9439c9872aa1b339f177e40ec96f77239/LICENSE)
- [Vortice.Mathematics 2.1.0 license](https://github.com/amerkoleci/Vortice.Mathematics/blob/fa05ec6dcba48f3f7331791da6dc7f3d866b2ad6/LICENSE)
- [SharpGen runtime source license](https://github.com/SharpGenTools/SharpGenTools/blob/6990bcafe124a4c22515ad19cee5a081da8db67b/LICENSE.txt)

### Vortice.Windows and Vortice.Mathematics

The MIT License (MIT)

Copyright (c) Amer Koleci and Contributors

Permission is hereby granted, free of charge, to any person obtaining a
copy of this software and associated documentation files (the "Software"),
to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.

### SharpGen.Runtime and SharpGen.Runtime.COM

Package copyright metadata additionally identifies:
(c) 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer Koleci.

MIT License

Copyright (c) 2010-2017 Alexandre Mutel, 2017 Jeremy Koritzinsky
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
