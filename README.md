# Win Preview

A Windows app for viewing and marking up PDFs and images, modeled on macOS Preview.
Built with C#, WinUI 3 (Windows App SDK) and PDFium.

This is a clean-room implementation. Features are built from Apple's public
[Preview User Guide](https://support.apple.com/guide/preview/welcome/mac) and observed behavior.
No Apple code, binaries or artwork are used, and none were decompiled.

## Requirements

- Windows 10 1809 or later (Windows 11 recommended)
- [.NET 10 SDK](https://dotnet.microsoft.com/download): `winget install Microsoft.DotNet.SDK.10`

You don't need Visual Studio. Everything builds with the `dotnet` CLI.

## Build, test, run

```bash
dotnet build src/WinPreview.App -p:Platform=x64
dotnet test tests/WinPreview.Core.Tests
src/WinPreview.App/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/WinPreview.exe path/to/file.pdf
```

The app is unpackaged and bundles the Windows App SDK runtime, so the build output folder runs as-is.

To get sample files, run `dotnet run --project tools/SampleGen -- samples`. It writes `samples/sample.pdf` (text
pages plus a fillable form) and `samples/photo.png`.

For diagnostics, set `WINPREVIEW_LOG=1` and the app appends to `%TEMP%\winpreview.log`.

## Layout

| Path | What it is |
|---|---|
| `src/WinPreview.Core` | UI-free engine: PDFium P/Invoke (`Pdf/`), markup model, hit-testing, rasterizing, signatures. Unit-tested. |
| `src/WinPreview.App` | WinUI 3 app: `Session/` (open document, undo, save/export, image codecs), `Views/` (document surface, page rendering, input), `MainWindow*` (toolbar, sidebar, commands). |
| `tests/WinPreview.Core.Tests` | xUnit tests run against real PDFium. They check round-trips, rendering, forms and page operations. |
| `tools/SampleGen` | Generates sample documents. `SampleGen render <pdf> <page> <png>` renders a page the way any other viewer would. |

## How markups work

- On open, every annotation the tools can edit is read into a `MarkupItem` and removed from the in-memory PDF.
  The app then draws it as an editable XAML overlay.
- On save, markups are written back as real PDF annotations with appearance streams, so Acrobat, Edge and
  Chrome show them identically:
  - highlight, underline, strikeout
  - ink
  - rectangle and oval
  - note
  - lines, arrows and text boxes, which are written as stamps
- Annotations the app can't represent are left untouched.
- Image markups are flattened into the pixels on save, using the same PDFium renderer. A markup therefore looks
  the same in a PNG as in a PDF.

See [FEATURES.md](FEATURES.md) for parity status against Preview.
