using System.Text;
using WinPreview.Core.Markup;
using WinPreview.Core.Pdf;

namespace WinPreview.Core.Tests;

public class SystemFontTests
{
    private static byte[] SaveWith(PdfDocument doc, params MarkupItem[] items)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, new Dictionary<int, IReadOnlyList<MarkupItem>> { [0] = items });
        return ms.ToArray();
    }

    private static int Count(byte[] pdf, string token)
    {
        var s = Encoding.Latin1.GetString(pdf);
        int n = 0;
        for (int i = s.IndexOf(token, StringComparison.Ordinal); i >= 0; i = s.IndexOf(token, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void Lists_installed_windows_fonts_by_family()
    {
        var families = SystemFonts.Families;
        Assert.Contains("Arial", families);
        Assert.Contains("Times New Roman", families);
        var arial = SystemFonts.Find("arial")!; // case-insensitive
        Assert.False(arial.Italic);
        Assert.Equal(400, arial.Weight);
        Assert.False(arial.Symbol);
        if (SystemFonts.Find("Wingdings") is { } wingdings) Assert.True(wingdings.Symbol);
    }

    [Fact]
    public void A_face_inside_a_collection_is_copied_out_as_a_standalone_font()
    {
        var face = SystemFonts.Families.Select(SystemFonts.Find)
            .FirstOrDefault(f => f!.Path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) && f.Index > 0)
            ?? SystemFonts.Families.Select(SystemFonts.Find).First(f => f!.Path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase));
        var data = SystemFonts.LoadData(face!);
        string tmp = Path.Combine(Path.GetTempPath(), $"wp-face-{Guid.NewGuid()}.ttf");
        try
        {
            File.WriteAllBytes(tmp, data);
            var read = Assert.Single(SystemFonts.ReadFaces(tmp));
            Assert.Equal(face!.Family, read.Family);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Installed_font_is_embedded_once_and_round_trips()
    {
        using var doc = TestPdfs.TextDocument();
        var a = new TextBoxMarkup { Rect = new(100, 600, 400, 640), Text = "Embedded Arial", FontSize = 16, Font = "Arial" };
        var b = new TextBoxMarkup { Rect = new(100, 500, 400, 540), Text = "Second box", FontSize = 16, Font = "Arial" };
        var saved = SaveWith(doc, a, b);

        Assert.Equal(1, Count(saved, "/FontFile2"));
        using var reopened = PdfDocument.Open(saved);
        var read = PdfMarkupReader.ExtractAll(reopened)[0].Cast<TextBoxMarkup>().ToList();
        Assert.All(read, t => Assert.Equal("Arial", t.Font));

        // A plain viewer draws the text from the embedded font.
        using var plain = PdfDocument.Open(saved);
        var px = plain.Render(0, 612, 792);
        bool ink = false;
        for (int y = 792 - 640; y < 792 - 600 && !ink; y++)
            for (int x = 100; x < 400 && !ink; x++)
                ink = px[(y * 612 + x) * 4] < 128;
        Assert.True(ink, "text box text should render");
    }

    [Fact]
    public void Saving_again_does_not_embed_the_font_again()
    {
        using var doc = TestPdfs.TextDocument();
        var box = new TextBoxMarkup { Rect = new(100, 600, 400, 640), Text = "Hello", FontSize = 16, Font = "Arial" };
        var first = SaveWith(doc, box);
        var second = SaveWith(doc, box);
        Assert.Equal(1, Count(second, "/FontFile2"));
        Assert.InRange(second.Length, first.Length - 1024, first.Length + 1024);
    }

    [Fact]
    public void Removing_the_text_box_drops_its_font_from_the_next_save()
    {
        using var doc = TestPdfs.TextDocument();
        SaveWith(doc, new TextBoxMarkup { Rect = new(100, 600, 400, 640), Text = "Hello", FontSize = 16, Font = "Arial" });
        Assert.Equal(0, Count(SaveWith(doc), "/FontFile2"));
    }

    [Fact]
    public void Standard_fonts_embed_nothing_and_keep_the_old_format()
    {
        using var doc = TestPdfs.TextDocument();
        var saved = SaveWith(doc, new TextBoxMarkup { Rect = new(100, 600, 400, 640), Text = "Times", FontSize = 16, Font = TextFonts.Times });
        Assert.Equal(0, Count(saved, "/FontFile2"));
        Assert.Equal(0, Count(saved, "Family"));
        using var reopened = PdfDocument.Open(saved);
        Assert.Equal(TextFonts.Times, ((TextBoxMarkup)PdfMarkupReader.ExtractAll(reopened)[0][0]).Font);
    }

    [Fact]
    public void A_font_missing_on_this_pc_falls_back_to_helvetica_but_keeps_its_name()
    {
        using var doc = TestPdfs.TextDocument();
        var saved = SaveWith(doc, new TextBoxMarkup { Rect = new(100, 600, 400, 640), Text = "Fallback", FontSize = 16, Font = "No Such Font 123" });
        Assert.Equal(0, Count(saved, "/FontFile2"));
        using var reopened = PdfDocument.Open(saved);
        Assert.Equal("No Such Font 123", ((TextBoxMarkup)PdfMarkupReader.ExtractAll(reopened)[0][0]).Font);
    }
}
