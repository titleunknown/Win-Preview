# Feature parity with macOS Preview

Organized by the chapters of Apple's [Preview User Guide](https://support.apple.com/guide/preview/welcome/mac).

✅ done · 🟡 partial · ⬜ not started

## Open, view and navigate

| Preview feature | Status | Notes |
|---|---|---|
| Open PDFs and images (multiple at once) | ✅ | Opens PDF, PNG, JPEG, GIF, TIFF, BMP, ICO, HEIC/HEIF, AVIF, WebP, JPEG XR and camera RAW (some formats need Windows codec extensions). Images opened together share one window. Drag and drop is supported. |
| View PDFs and images: zoom, fit, actual size | ✅ | Continuous scroll; zoom with Ctrl+wheel or touchpad pinch (touchscreen pinch not yet); fit to width or page. |
| Single page, two pages and contact sheet views | ⬜ | |
| Sidebar: thumbnails | ✅ | Drag to reorder; Delete key removes pages; context menu. |
| Sidebar: table of contents | ✅ | Built from the PDF outline. |
| Sidebar: highlights and notes | ✅ | Click an entry to jump to it. |
| Bookmark PDF pages | ⬜ | |
| Find text in PDFs | ✅ | Results list with context; Ctrl+G and Enter for next match. Also searches recognized text in images. |
| Interact with text in a photo (Live Text) | ✅ | Windows' built-in on-device OCR. Select, copy, double-click a word and search. Uses the OCR languages installed in Windows. |
| Inspector (document info) | 🟡 | File, metadata, page size, encryption, form presence, image DPI. No EXIF or GPS tabs yet. |
| See where a photo was taken | ⬜ | Needs EXIF GPS and a map. |
| Import images from a camera | ⬜ | |
| Slideshow | ✅ | F5; arrows, space and click to advance. |
| Tabs | ✅ | Optional: turn on **Open Files in Tabs** in the More menu and files join the current window as tabs (also the **+** button and drag-and-drop). Each tab keeps its own zoom, scroll position, sidebar and unsaved changes; Ctrl+Tab switches, Ctrl+W closes. Right-click a tab to move it to its own window. Off by default (one window per file). |
| Drag and drop to open | ✅ | The whole window is a drop target and highlights while files are dragged over it. |
| Animated GIF frames | ✅ | Composited frames appear as pages. |

## Edit PDFs

| Preview feature | Status | Notes |
|---|---|---|
| Fill out PDF forms | ✅ | Interactive AcroForm text fields and checkboxes, verified; clipboard works. Radio buttons, combo boxes and list boxes go through PDFium form-fill but are untested. On flat forms, use the Text tool. |
| Sign PDFs | 🟡 | Draw with mouse, pen or touch. Signatures are saved locally and reusable, with resize and move. No camera capture or image import yet. |
| Select and copy text | 🟡 | Drag, double-click a word, Ctrl+A per page. Selections don't cross page boundaries yet. |
| Highlight, underline, strike out | ✅ | Five highlight colors. Saved as standard PDF annotations. |
| Notes | ✅ | Sticky notes with a popup editor. |
| Speech bubbles | ⬜ | |
| Shapes: rectangle, oval, line, arrow | ✅ | Shift constrains. Border color, fill color and line width. |
| Star, polygon, speech bubble, loupe, mask | ⬜ | |
| Sketch (shape recognition) and Draw | 🟡 | Freehand Draw is done; Sketch's shape recognition isn't. |
| Text boxes | ✅ | Grows as you type. Helvetica, Times or Courier, with size and color. |
| Combine PDFs | ✅ | Insert from file, or drop files on the thumbnails. |
| Add, delete, move and rotate pages | ✅ | Add Page and Remove buttons under the thumbnails (blank page or pages from a file), plus drag to reorder. All undoable. |
| Crop PDF pages | ✅ | Press **C** or click the Crop button (or use the rectangular selection tool) and drag over the area to keep. Adjust with the handles or move it; the rest is dimmed, with a size readout. Crop one page or **All pages** from the floating button (Enter also works). Non-destructive: **Reset Crop** in the More menu restores the full page. Works on rotated pages. |
| Quartz filter effects | ⬜ | |
| Redaction | ⬜ | Planned; see [ROADMAP.md](ROADMAP.md). |

## Edit images

| Preview feature | Status | Notes |
|---|---|---|
| Crop, rotate | ✅ | Crop has a live preview, handles, a thirds grid and aspect-ratio presets (free, same as page, square, 4:3, 3:2, 16:9, 5:4). Markups move with the crop. |
| Resize (Adjust Size) | ✅ | Proportional scaling, resolution, Fant resampling. |
| Flip | ⬜ | |
| Annotate images | ✅ | Same tools as PDFs; flattened on save. |
| Convert file types (Export) | ✅ | PNG, JPEG (with quality), HEIF (needs the codec), TIFF (multi-page), GIF, BMP, JPEG XR, PDF. |
| Adjust Color | ⬜ | |
| Remove background | ✅ | On-device ML (ONNX Runtime). U²-Net-p is built in; the higher-quality ISNet is an optional 179 MB download. Offers to save as PNG when the format can't store transparency. |
| Instant Alpha | ✅ | Press on a color and drag to widen the tolerance, with a live preview. Undoable. |
| Color profiles and soft proofing | ⬜ | Images are converted to sRGB on open. |

## Share, manage, print, save

| Preview feature | Status | Notes |
|---|---|---|
| Share | ✅ | Windows share sheet; unsaved markups are included. |
| Print | ✅ | Windows print dialog with live preview; markups are printed. |
| Save, Save As, close prompt | ✅ | Explicit save (Preview autosaves). Atomic writes. |
| Undo and redo | ✅ | All markup and page edits. |
| Revert to earlier versions | ⬜ | |
| Duplicate, lock | ⬜ | |
| Password-protect a PDF | ✅ | AES-256 via the bundled qpdf. Optional permissions password to restrict printing, copying and editing. Applied on save; opened protected PDFs keep their password when saved. Change or remove it from the More menu. |
| Reduce file size | ✅ | PDFs only. Balanced, Smallest or Lossless; saves a smaller copy. Images are downsampled and re-encoded as JPEG, except images with transparency. The result is checked against the original and falls back to lossless if pages would look different. |
| New from clipboard | ✅ | Ctrl+N. |
| 3D files (USDZ and others) | ⬜ | |
| Settings | ⬜ | |
| Keyboard shortcuts | ✅ | Windows equivalents (Ctrl for Cmd); listed in More → Keyboard Shortcuts. |

## Known limitations

- Text boxes on rotated PDF pages display upright on screen, but are saved in unrotated page space.
- Text box text is saved with the PDF standard fonts, which only cover Latin-1 characters. Embedded Unicode fonts are planned.
- PDF thumbnails show page content without the markup overlay.
- The whole of a large print job is rendered up front, at 150 DPI.
