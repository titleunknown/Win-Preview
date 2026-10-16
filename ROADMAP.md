# Roadmap

Planned work, roughly in priority order. [FEATURES.md](FEATURES.md) tracks parity with macOS Preview.

## Next release (v0.3), in the repository now

- Better crop: adjustable selection with handles, dimmed surroundings, thirds grid and size readout, a floating
  Crop button, an Aspect ratio bar (1:1, 4:5, 2:3, 3:2, 16:9, 9:16 and more, with a portrait/landscape swap), crop all PDF pages, and Reset Crop
- Optional tabs: open files as tabs in one window (More → Open Files in Tabs), move a tab to its own window
- **C** is now the shortcut for crop
- Zoom tool: press **Z** (or the magnifier button), then double-click to jump between 100% and fit-to-window, zoomed around the point you clicked. When the picture is bigger than the window the cursor becomes a hand and dragging pans in the Zoom tool (Select keeps the standard arrow). Custom magnifier and hand cursors.
- The whole window accepts dropped files (not just the middle), with a highlight while dragging
- The window can't be made smaller than 780 × 480
- Fix: a leftover empty message bar could stay on screen after a notice closed

Known gap: opening a file from File Explorer while Win-Preview is already running starts a second window rather
than adding a tab (each launch is its own process). Single-instance handling would fix that.

## Later

### Code signing
Signing releases through SignPath Foundation to remove the SmartScreen warning. The build pipeline and
configuration are in place; the application to SignPath will happen once the app has an active user base
(SignPath requires an actively maintained, released project). See [docs/CODE_SIGNING.md](docs/CODE_SIGNING.md).

## To do

### Text boxes with system fonts
Movable, scalable text boxes that can use any font installed on Windows.

- Font picker listing installed system fonts (today it's only Helvetica, Times and Courier).
- Embed the chosen font (subset) in saved PDFs via PDFium's `FPDFText_LoadFont`, so the text looks the same on
  other computers and supports any language, not just Latin-1.
- Scaling: dragging a corner handle scales the text with the box (Shift keeps proportions), in addition to
  today's resize-and-rewrap.
- Bold, italic and alignment options.

### Redaction (permanent black-out)
A black "redact" mode for the highlighter that permanently removes what's underneath.

- Select text (or drag a rectangle over any area) to mark it for redaction; marks are reviewable before applying.
- Applying **deletes** the underlying content rather than covering it: text characters and vector paths inside
  the area are removed from the page, and image pixels are blacked out. The black box is then drawn into the
  page content.
- Also strip the redacted text from the document's metadata, bookmarks and annotations, and save
  non-incrementally so earlier versions of the file can't be recovered.
- Clear warning that redaction can't be undone after saving.
