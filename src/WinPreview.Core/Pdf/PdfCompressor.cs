namespace WinPreview.Core.Pdf;

public enum CompressionLevel
{
    /// <summary>Repacks the file structure only. Nothing visible changes.</summary>
    Lossless,
    /// <summary>Images above 150 dpi are downsampled and re-encoded as JPEG (quality 75).</summary>
    Balanced,
    /// <summary>Images above 96 dpi are downsampled and re-encoded as JPEG (quality 55).</summary>
    Smallest,
}

/// <param name="ImagesRecompressed">How many images were replaced by smaller versions.</param>
/// <param name="FellBackToLossless">Image compression was tried but rejected, so only lossless optimization was applied.</param>
public sealed record CompressionResult(byte[] Pdf, long BeforeBytes, long AfterBytes, int ImagesRecompressed, bool FellBackToLossless)
{
    public double Saved => BeforeBytes == 0 ? 0 : 1 - (double)AfterBytes / BeforeBytes;
}

public static class PdfCompressor
{
    /// <summary>Largest allowed average per-pixel difference (0–255) between original and compressed pages.</summary>
    private const double MaxPageDifference = 14;
    private const double CheckScale = 0.5; // render the comparison at half size (36 dpi) for speed

    public static (int Dpi, int Quality) Settings(CompressionLevel level) => level switch
    {
        CompressionLevel.Balanced => (150, 75),
        CompressionLevel.Smallest => (96, 55),
        _ => (0, 0),
    };

    /// <summary>
    /// Compresses an (unencrypted) PDF. The result is never larger than the input: the smallest of the
    /// original, the losslessly repacked file and the image-compressed file is returned.
    /// </summary>
    public static async Task<CompressionResult> CompressAsync(
        byte[] pdf, CompressionLevel level, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var lossless = await Qpdf.OptimizeAsync(pdf, ct);
        byte[] best = lossless.Length < pdf.Length ? lossless : pdf;
        int replaced = 0;
        bool rejected = false;

        if (level != CompressionLevel.Lossless)
        {
            var (dpi, quality) = Settings(level);
            var candidate = await Task.Run(async () =>
            {
                using var doc = PdfDocument.Open(pdf);
                int n = ImageRecompressor.Process(doc, dpi, quality, progress, ct);
                if (n == 0) return (Bytes: (byte[]?)null, Count: 0);
                using var ms = new MemoryStream();
                doc.Save(ms, removeSecurity: true);
                return (Bytes: (byte[]?)await Qpdf.OptimizeAsync(ms.ToArray(), ct), Count: n);
            }, ct);

            if (candidate.Bytes != null && candidate.Bytes.Length < best.Length)
            {
                if (await Task.Run(() => LooksTheSame(pdf, candidate.Bytes, ct), ct))
                {
                    best = candidate.Bytes;
                    replaced = candidate.Count;
                }
                else rejected = true;
            }
        }

        progress?.Report(1);
        return new CompressionResult(best, pdf.Length, best.Length, replaced, rejected);
    }

    /// <summary>Renders every page of both files small and compares them.</summary>
    internal static bool LooksTheSame(byte[] original, byte[] compressed, CancellationToken ct)
    {
        try
        {
            using var a = PdfDocument.Open(original);
            using var b = PdfDocument.Open(compressed);
            if (a.PageCount != b.PageCount) return false;
            for (int i = 0; i < a.PageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var info = a.GetPageInfo(i);
                int w = Math.Max(1, (int)(info.Width * CheckScale)), h = Math.Max(1, (int)(info.Height * CheckScale));
                var pa = a.Render(i, w, h);
                var pb = b.Render(i, w, h);
                long sum = 0;
                for (int k = 0; k < pa.Length; k += 4)
                    sum += Math.Abs(pa[k] - pb[k]) + Math.Abs(pa[k + 1] - pb[k + 1]) + Math.Abs(pa[k + 2] - pb[k + 2]);
                if (sum / (pa.Length / 4.0 * 3) > MaxPageDifference) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false; // a file we can't read back is not acceptable
        }
    }
}
