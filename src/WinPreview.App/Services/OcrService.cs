using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinPreview.Core;
using WinPreview.Core.Text;
using WinPreview.Session;

namespace WinPreview.Services;

/// <summary>Live Text for images, using the OCR engine built into Windows (no download, runs on-device).</summary>
public static class OcrService
{
    private static OcrEngine? _engine;

    private static OcrEngine? Engine => _engine ??=
        OcrEngine.TryCreateFromUserProfileLanguages()
        ?? (OcrEngine.IsLanguageSupported(new Language("en-US")) ? OcrEngine.TryCreateFromLanguage(new Language("en-US")) : null);

    public static bool IsAvailable => Engine != null;

    /// <returns>The recognized text, or null when Windows has no OCR language installed.</returns>
    public static async Task<OcrTextLayer?> RecognizeAsync(ImageFrame frame)
    {
        var engine = Engine;
        if (engine == null) return null;

        // The engine has a maximum input size; downscale if needed and map boxes back.
        double scale = Math.Min(1, OcrEngine.MaxImageDimension / (double)Math.Max(frame.Width, frame.Height));
        int w = frame.Width, h = frame.Height;
        byte[] px = frame.Pixels;
        if (scale < 1)
        {
            w = Math.Max(1, (int)(frame.Width * scale));
            h = Math.Max(1, (int)(frame.Height * scale));
            px = await ImageResizer.ResizeAsync(frame.Pixels, frame.Width, frame.Height, w, h);
        }
        // OCR reads opaque images best; put transparent areas on white.
        px = ImageCodec.FlattenOnWhite(px);
        using var bmp = SoftwareBitmap.CreateCopyFromBuffer(px.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bmp);

        double inv = 1 / scale;
        var lines = result.Lines.Select(line => new Core.Text.OcrLine(line.Words.Select(word =>
        {
            var r = word.BoundingRect;
            return new Core.Text.OcrWord(word.Text, new RectD(r.X * inv, r.Y * inv, (r.X + r.Width) * inv, (r.Y + r.Height) * inv));
        }).ToList())).Where(l => l.Words.Count > 0).ToList();
        return new OcrTextLayer(lines);
    }
}
