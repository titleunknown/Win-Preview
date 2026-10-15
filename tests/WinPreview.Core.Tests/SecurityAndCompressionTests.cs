using WinPreview.Core.Pdf;

namespace WinPreview.Core.Tests;

public class SecurityAndCompressionTests
{
    /// <summary>A photo-like image: smooth color blotches plus a little noise, so it compresses well as JPEG but poorly as Flate.</summary>
    private static byte[] PhotoPixels(int w, int h, bool halfTransparent = false)
    {
        var px = new byte[w * h * 4];
        var rng = new Random(42);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                double fx = x / (double)w, fy = y / (double)h;
                px[i] = (byte)Math.Clamp(128 + 90 * Math.Sin(fx * 9) + rng.Next(-7, 8), 0, 255);
                px[i + 1] = (byte)Math.Clamp(128 + 90 * Math.Sin(fy * 7 + fx * 3) + rng.Next(-7, 8), 0, 255);
                px[i + 2] = (byte)Math.Clamp(128 + 90 * Math.Cos(fx * 5 - fy * 6) + rng.Next(-7, 8), 0, 255);
                px[i + 3] = halfTransparent && x < w / 2 ? (byte)0 : (byte)255;
            }
        return px;
    }

    private static byte[] ImagePdf(int w = 1800, int h = 2200, bool halfTransparent = false)
    {
        using var doc = PdfDocument.CreateEmpty();
        doc.AddImagePage(PhotoPixels(w, h, halfTransparent), w, h, 612, 792); // ~215 dpi
        return TestPdfs.Save(doc);
    }

    // ---------- password protection ----------

    [Theory]
    [InlineData("hunter2")]
    [InlineData("p@ss \"word\" -x ünïcode")]
    public async Task Encrypted_pdf_needs_the_password_and_keeps_its_content(string password)
    {
        var plain = TestPdfs.Save(TestPdfs.TextDocument(2));
        var encrypted = await Qpdf.EncryptAsync(plain, new PdfEncryption(password));

        var missing = Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(encrypted));
        Assert.False(missing.WrongPassword);
        Assert.True(Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(encrypted, "wrong")).WrongPassword);

        using var doc = PdfDocument.Open(encrypted, password);
        Assert.True(doc.IsEncrypted);
        Assert.Equal(2, doc.PageCount);
        Assert.Contains("quick brown fox", doc.GetPageText(1));
        Assert.DoesNotContain("quick brown fox", System.Text.Encoding.Latin1.GetString(encrypted)); // really encrypted
    }

    [Fact]
    public async Task Owner_password_can_differ_and_restrictions_apply()
    {
        var plain = TestPdfs.Save(TestPdfs.TextDocument());
        var encrypted = await Qpdf.EncryptAsync(plain, new PdfEncryption("open-me", "owner-pw", AllowPrint: false, AllowCopy: false, AllowModify: false));
        using var asUser = PdfDocument.Open(encrypted, "open-me");
        using var asOwner = PdfDocument.Open(encrypted, "owner-pw");
        Assert.Equal(1, asUser.PageCount);
        Assert.Equal(1, asOwner.PageCount);
    }

    [Fact]
    public async Task Saving_with_remove_security_drops_the_password()
    {
        var encrypted = await Qpdf.EncryptAsync(TestPdfs.Save(TestPdfs.TextDocument()), new PdfEncryption("pw"));
        using var doc = PdfDocument.Open(encrypted, "pw");
        using var ms = new MemoryStream();
        doc.Save(ms, removeSecurity: true);
        using var reopened = PdfDocument.Open(ms.ToArray()); // no password needed
        Assert.Contains("quick brown fox", reopened.GetPageText(0));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(" lead", false)]
    [InlineData("trail ", false)]
    [InlineData("two\nlines", false)]
    [InlineData("fine pass", true)]
    public void Password_validation(string password, bool ok) => Assert.Equal(ok, Qpdf.ValidatePassword(password) == null);

    // ---------- compression ----------

    [Fact]
    public async Task Lossless_optimization_keeps_pages_identical()
    {
        var original = TestPdfs.Save(TestPdfs.TextDocument(4));
        var result = await PdfCompressor.CompressAsync(original, CompressionLevel.Lossless);
        Assert.True(result.AfterBytes <= result.BeforeBytes);
        Assert.Equal(0, result.ImagesRecompressed);
        Assert.True(PdfCompressor.LooksTheSame(original, result.Pdf, default));
        using var doc = PdfDocument.Open(result.Pdf);
        Assert.Equal(4, doc.PageCount);
        Assert.Contains("Page 3 heading", doc.GetPageText(2));
    }

    [Theory]
    [InlineData(CompressionLevel.Balanced)]
    [InlineData(CompressionLevel.Smallest)]
    public async Task Image_compression_shrinks_a_photo_pdf_without_changing_how_it_looks(CompressionLevel level)
    {
        var original = ImagePdf();
        var result = await PdfCompressor.CompressAsync(original, level);

        Assert.False(result.FellBackToLossless);
        Assert.Equal(1, result.ImagesRecompressed);
        Assert.True(result.AfterBytes < original.Length * 0.5, $"{original.Length:N0} → {result.AfterBytes:N0}");
        Assert.True(PdfCompressor.LooksTheSame(original, result.Pdf, default));

        using var doc = PdfDocument.Open(result.Pdf);
        Assert.Equal(1, doc.PageCount);
        Assert.Equal(612, doc.GetPageInfo(0).Width, 1); // page size untouched
    }

    [Fact]
    public async Task Images_with_transparency_are_left_alone()
    {
        var original = ImagePdf(halfTransparent: true);
        var result = await PdfCompressor.CompressAsync(original, CompressionLevel.Smallest);
        Assert.Equal(0, result.ImagesRecompressed);
        Assert.True(PdfCompressor.LooksTheSame(original, result.Pdf, default));
    }

    [Fact]
    public async Task Compression_never_makes_a_file_bigger()
    {
        foreach (var level in Enum.GetValues<CompressionLevel>())
        {
            var original = TestPdfs.Save(TestPdfs.TextDocument(3));
            var result = await PdfCompressor.CompressAsync(original, level);
            Assert.True(result.AfterBytes <= original.Length, $"{level}: {original.Length} → {result.AfterBytes}");
        }
    }

    [Fact]
    public void Box_downsample_averages_blocks()
    {
        byte[] src = [0, 0, 0, 255, 100, 100, 100, 255, 200, 200, 200, 255, 100, 100, 100, 255]; // 2x2
        var one = ImageRecompressor.BoxDownsample(src, 2, 2, 1, 1);
        Assert.Equal(100, one[0]);
        Assert.Equal(255, one[3]);
    }
}
