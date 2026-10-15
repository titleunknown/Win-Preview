# Third-party notices

Win-Preview builds on the following components. Their licenses apply to those components only.

## PDFium

<https://pdfium.googlesource.com/pdfium/>, distributed as `pdfium.dll`.

```
Copyright 2014 The PDFium Authors

Redistribution and use in source and binary forms, with or without modification, are permitted
provided that the following conditions are met:

   * Redistributions of source code must retain the above copyright notice, this list of conditions
     and the following disclaimer.
   * Redistributions in binary form must reproduce the above copyright notice, this list of
     conditions and the following disclaimer in the documentation and/or other materials provided
     with the distribution.
   * Neither the name of Google Inc. nor the names of its contributors may be used to endorse or
     promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER
IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT
OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

PDFium itself incorporates third-party libraries, including FreeType, libjpeg-turbo, OpenJPEG,
Little CMS, zlib, libpng and AGG, each under its own permissive license. The complete license texts
are published with the binary builds at <https://github.com/bblanchon/pdfium-binaries>.

## pdfium-binaries

<https://github.com/bblanchon/pdfium-binaries>: prebuilt PDFium, NuGet package `bblanchon.PDFium.Win32`.
Copyright © Benoît Blanchon. Licensed under the Apache License 2.0
(<https://www.apache.org/licenses/LICENSE-2.0>).

## qpdf

<https://github.com/qpdf/qpdf> (version 12.4.2, `qpdf\` folder next to the app): used as a separate program to
encrypt PDFs and to repack them losslessly. Copyright (c) 2005-2021 Jay Berkenbilt, (c) 2022-2026 Jay Berkenbilt and
Manfred Holger. Licensed under the Apache License 2.0 (<https://www.apache.org/licenses/LICENSE-2.0>).
The unmodified Windows build is vendored in `third_party/qpdf`; its SHA-256 is recorded in `third_party/qpdf/README.txt`.
It is accompanied by the Microsoft Visual C++ runtime DLLs (`msvcp140*.dll`, `vcruntime140*.dll`, `concrt140.dll`),
which are redistributable under Microsoft's Visual C++ Redistributable license terms.

## Windows App SDK

<https://github.com/microsoft/WindowsAppSDK>: the WinUI 3 runtime bundled with the app.
© Microsoft Corporation. Redistributed under the Microsoft Software License Terms for the
Windows App SDK (included in the `Microsoft.WindowsAppSDK` NuGet package).

## ONNX Runtime

<https://github.com/microsoft/onnxruntime>: runs the background-removal models.
© Microsoft Corporation. MIT License.

## Background-removal models

- **U²-Net-p** (`src/WinPreview.App/Models/u2netp.onnx`, bundled): from U²-Net by Xuebin Qin et al.,
  <https://github.com/xuebinqin/U-2-Net>. Apache License 2.0.
- **ISNet (DIS), general use** (optional download, not in this repository): from "Highly Accurate Dichotomous
  Image Segmentation" by Xuebin Qin et al., <https://github.com/xuebinqin/DIS>. Apache License 2.0.

Both ONNX conversions are distributed by the rembg project (<https://github.com/danielgatis/rembg>, MIT License),
where the app downloads the optional model from. Its SHA-256 is pinned in `BackgroundRemover.cs`.

## .NET

<https://github.com/dotnet/runtime>: © .NET Foundation and Contributors. MIT License.

## Development only (not redistributed)

xUnit (Apache 2.0) is used by the test project.
