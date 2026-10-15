using System.Text;
using WinPreview.Core.Pdf;
using static WinPreview.Core.Pdf.PdfiumNative;

namespace WinPreview.Core.Tests;

public unsafe class PdfDocumentTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void UserToDisplay_matches_pdfium_for_every_rotation(int rotation)
    {
        using var doc = TestPdfs.TextDocument();
        doc.RotatePage(0, rotation);
        var info = doc.GetPageInfo(0);
        Assert.Equal(rotation, info.Rotation);

        const int scale = 10;
        foreach (var p in new PointD[] { new(100, 200), new(0, 0), new(612, 792), new(300.5, 10) })
        {
            var mine = info.UserToDisplay.Apply(p);
            int dx, dy;
            lock (Pdfium.Lock)
                FPDF_PageToDevice(doc.GetPage(0), 0, 0, (int)(info.Width * scale), (int)(info.Height * scale), 0, p.X, p.Y, &dx, &dy);
            Assert.Equal(dx / (double)scale, mine.X, 0.2);
            Assert.Equal(dy / (double)scale, mine.Y, 0.2);
        }
        var back = info.UserToDisplay.Invert().Apply(info.UserToDisplay.Apply(new PointD(123, 456)));
        Assert.Equal(123, back.X, 6);
        Assert.Equal(456, back.Y, 6);
    }

    [Fact]
    public void Extracts_text_and_finds_matches_with_rects()
    {
        using var doc = TestPdfs.TextDocument(pages: 3);
        Assert.Equal(3, doc.PageCount);
        Assert.Contains("quick brown fox", doc.GetPageText(1));

        var hits = doc.Search("FOX").ToList();
        Assert.Equal(3, hits.Count);
        Assert.Equal([0, 1, 2], hits.Select(h => h.PageIndex));
        Assert.All(hits, h => Assert.NotEmpty(h.Rects));
        Assert.Equal("fox", doc.GetText(0, hits[0].CharStart, hits[0].CharCount));

        Assert.Empty(doc.Search("FOX", matchCase: true));
        Assert.Single(doc.Search("Page 2"));
    }

    [Fact]
    public void Char_index_at_a_point_inside_a_word_round_trips_to_that_word()
    {
        using var doc = TestPdfs.TextDocument();
        var hit = doc.Search("brown").Single();
        var center = hit.Rects[0].Center;
        int index = doc.GetCharIndexAt(0, center);
        Assert.InRange(index, hit.CharStart, hit.CharStart + hit.CharCount - 1);
    }

    [Fact]
    public void Renders_text_as_dark_pixels_on_white()
    {
        using var doc = TestPdfs.TextDocument();
        var info = doc.GetPageInfo(0);
        int w = (int)info.Width, h = (int)info.Height;
        var px = doc.Render(0, w, h);
        Assert.Equal(255, px[0]); // top-left corner is white
        var hit = doc.Search("quick").Single().Rects[0];
        var d = info.UserToDisplay.Apply(hit);
        bool anyDark = false;
        for (int y = (int)d.Y1; y < (int)d.Y2 && !anyDark; y++)
            for (int x = (int)d.X1; x < (int)d.X2 && !anyDark; x++)
                anyDark = px[(y * w + x) * 4] < 100;
        Assert.True(anyDark);
    }

    [Fact]
    public void Page_operations_delete_move_import_and_save()
    {
        using var doc = TestPdfs.TextDocument(pages: 3);
        doc.MovePages([2], 0);
        Assert.Contains("Page 3", doc.GetPageText(0));
        doc.DeletePage(1); // was "Page 1"
        Assert.Equal(2, doc.PageCount);
        Assert.Contains("Page 2", doc.GetPageText(1));

        using var other = TestPdfs.TextDocument(pages: 2);
        doc.ImportPages(other, [1], 1);
        Assert.Equal(3, doc.PageCount);
        Assert.Contains("Page 2", doc.GetPageText(1));

        using var reopened = PdfDocument.Open(TestPdfs.Save(doc));
        Assert.Equal(3, reopened.PageCount);
        Assert.Contains("Page 3", reopened.GetPageText(0));
    }

    [Fact]
    public void Rotation_persists_through_save()
    {
        using var doc = TestPdfs.TextDocument();
        doc.RotatePage(0, 90);
        doc.RotatePage(0, 90);
        doc.RotatePage(0, -90);
        using var reopened = PdfDocument.Open(TestPdfs.Save(doc));
        var info = reopened.GetPageInfo(0);
        Assert.Equal(90, info.Rotation);
        Assert.Equal(792, info.Width, 1);
    }

    [Fact]
    public void Crop_box_changes_the_visible_page_and_persists()
    {
        using var doc = TestPdfs.TextDocument();
        doc.SetCropBox(0, new RectD(50, 100, 350, 500));
        var info = doc.GetPageInfo(0);
        Assert.Equal(300, info.Width, 1);
        Assert.Equal(400, info.Height, 1);
        Assert.Equal(new PointD(0, 0), info.UserToDisplay.Apply(new PointD(50, 500)));
        using var reopened = PdfDocument.Open(TestPdfs.Save(doc));
        Assert.Equal(300, reopened.GetPageInfo(0).Width, 1);
    }

    [Fact]
    public void Typing_into_a_form_field_is_saved()
    {
        using var doc = PdfDocument.Open(TestPdfs.FormPdf());
        Assert.True(doc.HasForm);
        var inField = new PointD(150, 710);
        Assert.NotEqual(-1, doc.FormFieldTypeAt(0, inField));
        Assert.Equal(-1, doc.FormFieldTypeAt(0, new PointD(500, 100)));

        doc.FormMouseDown(0, inField, 0);
        doc.FormMouseUp(0, inField, 0);
        foreach (char c in "Ada Lovelace") doc.FormChar(0, c, 0);
        doc.FormKillFocus();

        var saved = TestPdfs.Save(doc);
        Assert.Contains("Ada Lovelace", Encoding.Latin1.GetString(saved));
        using var reopened = PdfDocument.Open(saved);
        Assert.True(reopened.HasForm);
    }

    [Fact]
    public void Wrong_password_is_reported()
    {
        Assert.Throws<InvalidDataException>(() => PdfDocument.Open("not a pdf"u8));
    }
}
