using WinPreview.Core.Pdf;

namespace WinPreview.Core.Markup;

/// <summary>
/// Flattens markups into raster images using the same PDFium annotation renderer that draws saved PDFs,
/// so a markup looks identical whether it ends up in a PDF or burned into a PNG/JPEG.
/// </summary>
public static class MarkupRasterizer
{
    /// <summary>Renders markups given in y-down pixel space to a straight-alpha BGRA buffer.</summary>
    public static byte[] Render(IEnumerable<MarkupItem> markups, int width, int height)
    {
        var buf = new byte[width * height * 4];
        var flip = new Affine(1, 0, 0, -1, 0, height);
        var items = markups.Where(m => m is not NoteMarkup).Select(m =>
        {
            var c = m.Clone();
            c.Transform(flip);
            return c;
        }).ToList();
        if (items.Count == 0) return buf;

        using var doc = PdfDocument.CreateEmpty();
        doc.InsertBlankPage(0, width, height);
        lock (Pdfium.Lock) PdfMarkupWriter.Write(doc, 0, items);
        doc.Render(0, width, height, 0, 0, width, height, buf, transparent: true);
        return buf;
    }

    /// <summary>Composites a straight-alpha BGRA overlay onto a straight-alpha BGRA image in place.</summary>
    public static void Composite(Span<byte> image, ReadOnlySpan<byte> overlay)
    {
        for (int i = 0; i + 3 < image.Length && i + 3 < overlay.Length; i += 4)
        {
            int ao = overlay[i + 3];
            if (ao == 0) continue;
            if (ao == 255)
            {
                overlay.Slice(i, 4).CopyTo(image.Slice(i, 4));
                continue;
            }
            double a = ao / 255.0, ai = image[i + 3] / 255.0;
            double outA = a + ai * (1 - a);
            for (int c = 0; c < 3; c++)
                image[i + c] = (byte)Math.Round((overlay[i + c] * a + image[i + c] * ai * (1 - a)) / outA);
            image[i + 3] = (byte)Math.Round(outA * 255);
        }
    }

    /// <summary>Rotates a BGRA buffer by 90° clockwise (or counter-clockwise).</summary>
    public static byte[] Rotate90(ReadOnlySpan<byte> src, int width, int height, bool clockwise)
    {
        var dst = new byte[src.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                // New image is height x width.
                int nx = clockwise ? height - 1 - y : y;
                int ny = clockwise ? x : width - 1 - x;
                src.Slice((y * width + x) * 4, 4).CopyTo(dst.AsSpan((ny * height + nx) * 4, 4));
            }
        return dst;
    }

    /// <summary>Copies a sub-rectangle out of a BGRA buffer.</summary>
    public static byte[] Crop(ReadOnlySpan<byte> src, int width, int x, int y, int w, int h)
    {
        var dst = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
            src.Slice(((y + row) * width + x) * 4, w * 4).CopyTo(dst.AsSpan(row * w * 4, w * 4));
        return dst;
    }

    /// <summary>
    /// The transform that maps image-space markups along with a 90° rotation of a
    /// <paramref name="width"/> x <paramref name="height"/> image.
    /// </summary>
    public static Affine RotationTransform(int width, int height, bool clockwise) => clockwise
        ? new Affine(0, 1, -1, 0, height, 0)   // (x, y) → (H - y, x)
        : new Affine(0, -1, 1, 0, 0, width);   // (x, y) → (y, W - x)
}
