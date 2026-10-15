# Roadmap

Planned work, roughly in priority order. [FEATURES.md](FEATURES.md) tracks parity with macOS Preview.

## Next release (v0.2), in the repository now

- Password protection for PDFs (AES-256), with an optional permissions password
- Reduce File Size (lossless, balanced and smallest)
- Add Page / Remove buttons in the sidebar
- Text boxes have a transparent background while typing
- Fix: password-protected PDFs given on the command line failed to open

## In progress

### Code signing
Signing releases through SignPath Foundation to remove the SmartScreen warning. The build pipeline and
configuration are in place; the application to SignPath and the first signed release are next.
See [docs/CODE_SIGNING.md](docs/CODE_SIGNING.md).

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
