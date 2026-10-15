using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinPreview.Core.Imaging;
using static WinPreview.Core.Pdf.PdfiumNative;

namespace WinPreview.Core.Pdf;

/// <summary>
/// Shrinks the images in a PDF: downsamples those above a target resolution and re-encodes them as JPEG.
/// An image is replaced only when the result is clearly smaller, and never when it has transparency
/// (masks, soft masks and stencil masks), so appearance is preserved.
/// </summary>
internal static unsafe class ImageRecompressor
{
    private const int MinPixels = 40_000;       // ignore icons and tiny images
    private const double MustSaveFraction = 0.85; // replace only if the new data is under 85% of the old

    public static int Process(PdfDocument doc, int targetDpi, int quality, IProgress<double>? progress, CancellationToken ct)
    {
        int replaced = 0, pages = doc.PageCount;
        for (int p = 0; p < pages; p++)
        {
            ct.ThrowIfCancellationRequested();
            int onPage = ProcessPage(doc, p, targetDpi, quality, ct);
            if (onPage > 0)
                lock (Pdfium.Lock)
                {
                    // The replaced image is a new XObject; regenerate this page's content so it points at it.
                    FPDFPage_GenerateContent(doc.GetPage(p));
                    doc.InvalidateText(p);
                }
            replaced += onPage;
            progress?.Report((p + 1) / (double)pages);
        }
        return replaced;
    }

    private static int ProcessPage(PdfDocument doc, int pageIndex, int targetDpi, int quality, CancellationToken ct)
    {
        // Collect image objects first (under the lock), then recompress them one at a time.
        var images = new List<nint>();
        lock (Pdfium.Lock)
        {
            var page = doc.GetPage(pageIndex);
            for (int i = 0, n = FPDFPage_CountObjects(page); i < n; i++) Collect(FPDFPage_GetObject(page, i), images, 0);
        }
        int replaced = 0;
        foreach (var image in images)
        {
            ct.ThrowIfCancellationRequested();
            if (TryRecompress(doc, pageIndex, image, targetDpi, quality)) replaced++;
        }
        return replaced;
    }

    private static void Collect(nint obj, List<nint> into, int depth)
    {
        int type = FPDFPageObj_GetType(obj);
        if (type == FPDF_PAGEOBJ_IMAGE) into.Add(obj);
        else if (type == FPDF_PAGEOBJ_FORM && depth < 8)
            for (uint i = 0, n = (uint)Math.Max(0, FPDFFormObj_CountObjects(obj)); i < n; i++)
                Collect(FPDFFormObj_GetObject(obj, i), into, depth + 1);
    }

    private static bool TryRecompress(PdfDocument doc, int pageIndex, nint image, int targetDpi, int quality)
    {
        byte[] bgra;
        int width, height, newW, newH;
        uint rawSize;
        lock (Pdfium.Lock)
        {
            uint pw, ph;
            if (FPDFImageObj_GetImagePixelSize(image, &pw, &ph) == 0 || (long)pw * ph < MinPixels) return false;
            rawSize = FPDFImageObj_GetImageDataRaw(image, null, 0);

            // Resolution the image is actually drawn at, from its placement on the page.
            FS_MATRIX m;
            double scale = 1;
            if (FPDFPageObj_GetMatrix(image, &m) != 0)
            {
                double wPts = Math.Sqrt((double)m.A * m.A + (double)m.B * m.B), hPts = Math.Sqrt((double)m.C * m.C + (double)m.D * m.D);
                if (wPts >= 1 && hPts >= 1)
                {
                    double dpi = Math.Max(pw / (wPts / 72), ph / (hPts / 72));
                    if (dpi > targetDpi * 1.15) scale = Math.Max(0.05, targetDpi / dpi);
                }
            }

            var bmp = FPDFImageObj_GetBitmap(image);
            if (bmp == 0) return false;
            try
            {
                if (!ReadBgra(bmp, out bgra, out width, out height)) return false;
            }
            finally { FPDFBitmap_Destroy(bmp); }

            newW = Math.Max(1, (int)Math.Round(width * scale));
            newH = Math.Max(1, (int)Math.Round(height * scale));
        }

        if (newW != width || newH != height) bgra = BoxDownsample(bgra, width, height, newW, newH);
        var jpeg = WicJpeg.Encode(bgra, newW, newH, quality);
        if (rawSize > 0 && jpeg.Length >= rawSize * MustSaveFraction) return false;

        lock (Pdfium.Lock)
        {
            if (HasTransparency(doc, pageIndex, image)) return false;
            fixed (byte* data = jpeg)
            {
                var file = new FPDF_FILEACCESS { FileLen = (uint)jpeg.Length, GetBlock = &ReadBlock, Param = data };
                return FPDFImageObj_LoadJpegFileInline(null, 0, image, &file) != 0;
            }
        }
    }

    /// <summary>Whether the image, drawn with its masks, has any non-opaque pixel. Replacing such an image would lose its mask.</summary>
    private static bool HasTransparency(PdfDocument doc, int pageIndex, nint image)
    {
        var rendered = FPDFImageObj_GetRenderedBitmap(doc.Handle, doc.GetPage(pageIndex), image);
        if (rendered == 0) return true; // can't tell; be safe
        try
        {
            if (FPDFBitmap_GetFormat(rendered) != FPDFBitmap_BGRA) return false;
            int w = FPDFBitmap_GetWidth(rendered), h = FPDFBitmap_GetHeight(rendered), stride = FPDFBitmap_GetStride(rendered);
            var buf = (byte*)FPDFBitmap_GetBuffer(rendered);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (buf[y * stride + x * 4 + 3] != 255) return true;
            return false;
        }
        finally { FPDFBitmap_Destroy(rendered); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadBlock(void* param, uint position, byte* buffer, uint size)
    {
        Buffer.MemoryCopy((byte*)param + position, buffer, size, size);
        return 1;
    }

    /// <summary>Copies a PDFium bitmap into a tight BGRA array. Fails for formats with transparency.</summary>
    private static bool ReadBgra(nint bmp, out byte[] bgra, out int width, out int height)
    {
        int format = FPDFBitmap_GetFormat(bmp), stride = FPDFBitmap_GetStride(bmp);
        width = FPDFBitmap_GetWidth(bmp);
        height = FPDFBitmap_GetHeight(bmp);
        var src = (byte*)FPDFBitmap_GetBuffer(bmp);
        bgra = [];
        if (src == null || width <= 0 || height <= 0) return false;
        bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int d = (y * width + x) * 4;
                switch (format)
                {
                    case FPDFBitmap_Gray:
                        bgra[d] = bgra[d + 1] = bgra[d + 2] = src[y * stride + x];
                        break;
                    case FPDFBitmap_BGR:
                        for (int c = 0; c < 3; c++) bgra[d + c] = src[y * stride + x * 3 + c];
                        break;
                    case FPDFBitmap_BGRx or FPDFBitmap_BGRA:
                        for (int c = 0; c < 3; c++) bgra[d + c] = src[y * stride + x * 4 + c];
                        if (format == FPDFBitmap_BGRA && src[y * stride + x * 4 + 3] != 255) return false;
                        break;
                    default:
                        return false;
                }
                bgra[d + 3] = 255;
            }
        return true;
    }

    /// <summary>Area-average downsampling (box filter), which keeps scanned text readable.</summary>
    internal static byte[] BoxDownsample(byte[] src, int w, int h, int nw, int nh)
    {
        var dst = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
        {
            int y0 = y * h / nh, y1 = Math.Max(y0 + 1, (y + 1) * h / nh);
            for (int x = 0; x < nw; x++)
            {
                int x0 = x * w / nw, x1 = Math.Max(x0 + 1, (x + 1) * w / nw);
                int b = 0, g = 0, r = 0, count = (y1 - y0) * (x1 - x0);
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++)
                    {
                        int s = (yy * w + xx) * 4;
                        b += src[s]; g += src[s + 1]; r += src[s + 2];
                    }
                int d = (y * nw + x) * 4;
                dst[d] = (byte)(b / count); dst[d + 1] = (byte)(g / count); dst[d + 2] = (byte)(r / count); dst[d + 3] = 255;
            }
        }
        return dst;
    }
}
