// Generates sample documents for manual testing: a multi-page PDF (text + a fillable form page) and a PNG.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using WinPreview.Core.Pdf;

if (args is ["compress", var inPdf, var outPdf, var levelArg])
{
    // Runs the same compressor the app uses and reports the result.
    var bytes = File.ReadAllBytes(inPdf);
    var res = await PdfCompressor.CompressAsync(bytes, Enum.Parse<CompressionLevel>(levelArg, ignoreCase: true));
    File.WriteAllBytes(outPdf, res.Pdf);
    Console.WriteLine($"{res.BeforeBytes:N0} -> {res.AfterBytes:N0} bytes ({res.Saved:P0} smaller), images recompressed: {res.ImagesRecompressed}, fell back: {res.FellBackToLossless}");
    return;
}

if (args is ["photo-pdf", var photoOut, var transparentArg])
{
    // A one-page PDF holding a large photo-like image, optionally with a transparent half (soft mask).
    int w = 1800, h = 2200;
    var px = new byte[w * h * 4];
    var rng = new Random(42);
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            px[i] = (byte)Math.Clamp(128 + 90 * Math.Sin(x / (double)w * 9) + rng.Next(-7, 8), 0, 255);
            px[i + 1] = (byte)Math.Clamp(128 + 90 * Math.Sin(y / (double)h * 7) + rng.Next(-7, 8), 0, 255);
            px[i + 2] = (byte)Math.Clamp(128 + 90 * Math.Cos((x - y) / (double)w * 5) + rng.Next(-7, 8), 0, 255);
            px[i + 3] = bool.Parse(transparentArg) && x < w / 2 ? (byte)0 : (byte)255;
        }
    using var d = PdfDocument.CreateEmpty();
    d.AddImagePage(px, w, h, 612, 792);
    using var fs = File.Create(photoOut);
    d.Save(fs);
    Console.WriteLine($"Wrote {photoOut}");
    return;
}

if (args is ["render", var pdfPath, var pageArg, var pngPath])
{
    // Renders a page with plain PDFium (annotations via their appearance streams), as any other viewer would.
    using var doc = PdfDocument.Open(pdfPath);
    int pg = int.Parse(pageArg);
    var info = doc.GetPageInfo(pg);
    int w = (int)(info.Width * 1.5), h = (int)(info.Height * 1.5);
    var px = doc.Render(pg, w, h);
    using var img = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    var data = img.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, img.PixelFormat);
    System.Runtime.InteropServices.Marshal.Copy(px, 0, data.Scan0, px.Length);
    img.UnlockBits(data);
    img.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
    Console.WriteLine($"Rendered page {pg} ({w}x{h})");
    return;
}

string outDir = args.Length > 0 ? args[0] : "samples";
Directory.CreateDirectory(outDir);

string[] para =
[
    "Preview for Windows sample document",
    "",
    "This page exists to exercise text selection, search, and markup.",
    "The quick brown fox jumps over the lazy dog. Pack my box with five dozen",
    "liquor jugs. How vexingly quick daft zebras jump! Sphinx of black quartz,",
    "judge my vow. The five boxing wizards jump quickly.",
    "",
    "Try highlighting a sentence, drawing a shape, adding a note, or signing.",
];

using var form = PdfDocument.Open(FormPdf());
using var text = PdfDocument.CreateEmpty();
for (int i = 0; i < 5; i++)
    text.AddTextPage(para.Select(l => l.Length > 0 && i > 0 && l.StartsWith("Preview") ? $"Chapter {i + 1}" : l).Append($"Page {i + 1}"), fontSize: 13);
form.ImportPages(text, null, 0);
using (var fs = File.Create(Path.Combine(outDir, "sample.pdf"))) form.Save(fs);

using var bmp = new Bitmap(1600, 1000);
using (var g = Graphics.FromImage(bmp))
{
    g.SmoothingMode = SmoothingMode.AntiAlias;
    using var sky = new LinearGradientBrush(new Rectangle(0, 0, 1600, 650), Color.FromArgb(70, 140, 230), Color.FromArgb(190, 225, 255), 90f);
    g.FillRectangle(sky, 0, 0, 1600, 650);
    g.FillEllipse(new SolidBrush(Color.FromArgb(255, 220, 60)), 1250, 120, 160, 160);
    using var hills = new GraphicsPath();
    hills.AddBezier(0, 600, 400, 420, 800, 760, 1600, 520);
    hills.AddLine(1600, 520, 1600, 1000); hills.AddLine(1600, 1000, 0, 1000); hills.CloseFigure();
    g.FillPath(new SolidBrush(Color.FromArgb(60, 160, 80)), hills);
    g.DrawString("Sample photo", new Font("Segoe UI", 48, FontStyle.Bold), Brushes.White, 60, 60);
}
bmp.Save(Path.Combine(outDir, "photo.png"), System.Drawing.Imaging.ImageFormat.Png);
// A "photo of a document" for Live Text.
using (var note = new Bitmap(1400, 700))
using (var g = Graphics.FromImage(note))
{
    g.Clear(Color.FromArgb(250, 248, 240));
    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
    using var title = new Font("Segoe UI", 40, FontStyle.Bold);
    using var body = new Font("Segoe UI", 26);
    g.DrawString("Grocery Receipt", title, Brushes.Black, 80, 60);
    string[] rows = ["Apples 3.49", "Fresh bread 4.25", "Oat milk 2.99", "Total 10.73", "Thank you for shopping!"];
    for (int i = 0; i < rows.Length; i++) g.DrawString(rows[i], body, Brushes.DimGray, 80, 190 + i * 85);
    note.Save(Path.Combine(outDir, "receipt.png"), System.Drawing.Imaging.ImageFormat.Png);
}

// A single subject on a plain backdrop, saved as JPEG, for Remove Background / Instant Alpha.
using (var obj = new Bitmap(1200, 900))
using (var g = Graphics.FromImage(obj))
{
    g.SmoothingMode = SmoothingMode.AntiAlias;
    using var bg = new LinearGradientBrush(new Rectangle(0, 0, 1200, 900), Color.FromArgb(205, 215, 225), Color.FromArgb(160, 175, 190), 90f);
    g.FillRectangle(bg, 0, 0, 1200, 900);
    g.FillEllipse(new SolidBrush(Color.FromArgb(70, 0, 0, 0)), 380, 700, 460, 70);           // shadow
    g.FillRectangle(new SolidBrush(Color.FromArgb(230, 110, 40)), 420, 260, 340, 460);        // mug body
    g.FillEllipse(new SolidBrush(Color.FromArgb(200, 90, 30)), 420, 690, 340, 60);
    g.FillEllipse(new SolidBrush(Color.FromArgb(120, 50, 20)), 420, 230, 340, 60);            // rim
    using var handle = new Pen(Color.FromArgb(230, 110, 40), 42);
    g.DrawArc(handle, 700, 340, 200, 230, -90, 180);
    var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
    using var ps = new System.Drawing.Imaging.EncoderParameters(1);
    ps.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
    obj.Save(Path.Combine(outDir, "mug.jpg"), codec, ps);
}
Console.WriteLine($"Wrote samples to {Path.GetFullPath(outDir)}");

static byte[] FormPdf()
{
    // Page with a text field, a checkbox, and a signature line.
    string content = "BT /Helv 20 Tf 72 720 Td (Registration Form) Tj ET " +
                     "BT /Helv 12 Tf 72 676 Td (Full name:) Tj ET " +
                     "BT /Helv 12 Tf 72 636 Td (Email:) Tj ET " +
                     "BT /Helv 12 Tf 100 596 Td (I agree to the terms) Tj ET " +
                     "0.5 w 72 500 m 300 500 l S BT /Helv 10 Tf 72 486 Td (Signature) Tj ET";
    string checkOn = "q 0 0 0 rg BT /ZaDb 12 Tf 2 3 Td (4) Tj ET Q";
    string[] objects =
    [
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 7 0 R 8 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 5 0 R /ZaDb 11 0 R >> >> >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /Helv 5 0 R >> >> /Annots [4 0 R 7 0 R 8 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [150 670 450 690] /F 4 /DA (/Helv 12 Tf 0 g) /MK << /BC [0.6 0.6 0.6] >> /P 3 0 R >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (email) /Rect [150 630 450 650] /F 4 /DA (/Helv 12 Tf 0 g) /MK << /BC [0.6 0.6 0.6] >> /P 3 0 R >>",
        "<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /Rect [72 592 88 608] /F 4 /V /Off /AS /Off /MK << /BC [0 0 0] /CA (4) >> /AP << /N << /Yes 9 0 R /Off 10 0 R >> >> /P 3 0 R >>",
        $"<< /Type /XObject /Subtype /Form /BBox [0 0 16 16] /Resources << /Font << /ZaDb 11 0 R >> >> /Length {checkOn.Length} >>\nstream\n{checkOn}\nendstream",
        "<< /Type /XObject /Subtype /Form /BBox [0 0 16 16] /Length 0 >>\nstream\n\nendstream",
        "<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >>",
    ];
    var sb = new StringBuilder("%PDF-1.7\n");
    var offsets = new List<int>();
    for (int i = 0; i < objects.Length; i++)
    {
        offsets.Add(sb.Length);
        sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
    }
    int xref = sb.Length;
    sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
    foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
    sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    return Encoding.ASCII.GetBytes(sb.ToString());
}
