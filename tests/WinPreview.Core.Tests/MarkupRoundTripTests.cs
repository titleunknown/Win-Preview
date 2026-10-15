using WinPreview.Core.Markup;
using WinPreview.Core.Pdf;
using WinPreview.Core.Signatures;

namespace WinPreview.Core.Tests;

public class MarkupRoundTripTests
{
    private static List<MarkupItem> AllKinds() =>
    [
        new InkMarkup { Color = RgbaColor.Blue, StrokeWidth = 3, Strokes = [[new(100, 100), new(150, 140), new(200, 100)]] },
        new InkMarkup { Color = RgbaColor.Black, StrokeWidth = 1.5, IsSignature = true, Strokes = [[new(300, 100), new(340, 120)]] },
        new ShapeMarkup { Shape = ShapeKind.Rectangle, Rect = new(100, 300, 200, 360), Color = RgbaColor.Red, StrokeWidth = 4, Fill = RgbaColor.Green },
        new ShapeMarkup { Shape = ShapeKind.Oval, Rect = new(250, 300, 350, 360), Color = RgbaColor.Purple, StrokeWidth = 2 },
        new LineMarkup { Start = new(100, 450), End = new(300, 500), ArrowAtEnd = true, Color = RgbaColor.Red, StrokeWidth = 3 },
        new TextBoxMarkup { Rect = new(350, 400, 550, 460), Text = "Hello from the text box tool", FontSize = 16, Color = RgbaColor.Blue, Fill = RgbaColor.Yellow },
        new NoteMarkup { Rect = new(500, 600, 520, 620), Text = "A sticky note" },
        new TextMarkup { Style = TextMarkupStyle.Highlight, Rects = [new(72, 690, 200, 704)], Text = "hi" },
        new TextMarkup { Style = TextMarkupStyle.Underline, Rects = [new(72, 670, 200, 684)], Color = RgbaColor.Red },
        new TextMarkup { Style = TextMarkupStyle.Strikeout, Rects = [new(72, 650, 200, 664)], Color = RgbaColor.Red },
    ];

    private static byte[] SaveWith(PdfDocument doc, List<MarkupItem> items)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, new Dictionary<int, IReadOnlyList<MarkupItem>> { [0] = items });
        return ms.ToArray();
    }

    [Fact]
    public void Saving_does_not_leave_annotations_in_the_live_document()
    {
        using var doc = TestPdfs.TextDocument();
        SaveWith(doc, AllKinds());
        Assert.Empty(PdfMarkupReader.ExtractAll(doc));
    }

    [Fact]
    public void Every_markup_kind_round_trips_through_a_saved_pdf()
    {
        using var doc = TestPdfs.TextDocument();
        var original = AllKinds();
        using var reopened = PdfDocument.Open(SaveWith(doc, original));
        var read = PdfMarkupReader.ExtractAll(reopened)[0];

        Assert.Equal(original.Count, read.Count);
        Assert.Equal(original.Select(m => m.GetType()), read.Select(m => m.GetType()));

        var ink = (InkMarkup)read[0];
        Assert.Equal(RgbaColor.Blue, ink.Color);
        Assert.Equal(3, ink.StrokeWidth, 3);
        Assert.Equal(3, ink.Strokes[0].Count);
        Assert.Equal(150, ink.Strokes[0][1].X, 1);
        Assert.True(((InkMarkup)read[1]).IsSignature);

        var rect = (ShapeMarkup)read[2];
        Assert.Equal(ShapeKind.Rectangle, rect.Shape);
        Assert.Equal(100, rect.Rect.X1, 1);
        Assert.Equal(360, rect.Rect.Y2, 1);
        Assert.Equal(RgbaColor.Green, rect.Fill);
        Assert.Equal(ShapeKind.Oval, ((ShapeMarkup)read[3]).Shape);
        Assert.Null(((ShapeMarkup)read[3]).Fill);

        var line = (LineMarkup)read[4];
        Assert.True(line.ArrowAtEnd);
        Assert.Equal(300, line.End.X, 1);

        var box = (TextBoxMarkup)read[5];
        Assert.Equal("Hello from the text box tool", box.Text);
        Assert.Equal(16, box.FontSize);
        Assert.Equal(RgbaColor.Yellow, box.Fill);

        Assert.Equal("A sticky note", ((NoteMarkup)read[6]).Text);

        var hl = (TextMarkup)read[7];
        Assert.Equal(TextMarkupStyle.Highlight, hl.Style);
        Assert.Equal(72, hl.Rects[0].X1, 1);
        Assert.Equal(704, hl.Rects[0].Y2, 1);
        Assert.Equal(TextMarkupStyle.Underline, ((TextMarkup)read[8]).Style);
        Assert.Equal(TextMarkupStyle.Strikeout, ((TextMarkup)read[9]).Style);

        // Extraction removes them from the live page, so a second pass finds nothing.
        Assert.Empty(PdfMarkupReader.ExtractAll(reopened));
    }

    /// <summary>
    /// Other PDF viewers only see appearance streams, so every annotation must render
    /// from the saved file without any help from our overlay.
    /// </summary>
    [Fact]
    public void Saved_markups_render_in_a_plain_viewer()
    {
        using var doc = TestPdfs.TextDocument();
        using var reopened = PdfDocument.Open(SaveWith(doc, AllKinds()));
        const int w = 612, h = 792;
        var px = reopened.Render(0, w, h);

        (byte b, byte g, byte r) At(double ux, double uy)
        {
            int x = (int)ux, y = h - (int)uy, i = (y * w + x) * 4;
            return (px[i], px[i + 1], px[i + 2]);
        }
        bool NotWhite((byte b, byte g, byte r) c) => c.b < 240 || c.g < 240 || c.r < 240;
        bool AnyNotWhite(RectD r)
        {
            for (int y = (int)r.Y1; y < (int)r.Y2; y++)
                for (int x = (int)r.X1; x < (int)r.X2; x++)
                    if (NotWhite(At(x, y))) return true;
            return false;
        }

        Assert.True(AnyNotWhite(new(145, 135, 155, 145)), "ink");
        Assert.True(NotWhite(At(100, 330)), "rectangle border");
        var fill = At(150, 330);
        Assert.True(fill.g > fill.r && fill.g > fill.b, $"rectangle fill should be green, was {fill}");
        Assert.True(AnyNotWhite(new(247, 327, 253, 333)), "oval border");
        Assert.True(AnyNotWhite(new(195, 470, 205, 480)), "line");
        Assert.True(AnyNotWhite(new(355, 435, 545, 458)), "text box text");
        Assert.True(AnyNotWhite(new(502, 602, 518, 618)), "note icon");
        var hl = At(180, 697);
        Assert.True(hl.r > 200 && hl.b < 200, $"highlight should be yellow, was {hl}");
        Assert.True(AnyNotWhite(new(150, 669, 160, 673)), "underline");
        Assert.True(AnyNotWhite(new(150, 655, 160, 660)), "strikeout");
    }

    [Fact]
    public void Text_wraps_at_word_boundaries_and_breaks_long_words()
    {
        double Measure(string s) => s.Length * 10;
        Assert.Equal(["hello", "world"], TextLayout.Wrap("hello world", 60, Measure));
        Assert.Equal(["ab cd", "ef"], TextLayout.Wrap("ab cd ef", 50, Measure));
        Assert.Equal(["abc", "def", "g"], TextLayout.Wrap("abcdefg", 30, Measure));
        Assert.Equal(["a", "", "b"], TextLayout.Wrap("a\n\nb", 100, Measure));
    }

    [Fact]
    public void Signatures_are_normalized_and_persisted()
    {
        var sig = Signature.FromStrokes([[new(10, 20), new(110, 70)]]);
        Assert.Equal(2, sig.AspectRatio, 6);
        Assert.Equal(new PointD(0, 0), sig.Strokes[0][0]);
        Assert.Equal(1, sig.Strokes[0][1].Y, 6);

        string path = Path.Combine(Path.GetTempPath(), $"wp-sig-{Guid.NewGuid()}.json");
        try
        {
            var store = new SignatureStore(path);
            store.Add(sig);
            var loaded = Assert.Single(store.Load());
            Assert.Equal(sig.Id, loaded.Id);
            store.Remove(sig.Id);
            Assert.Empty(store.Load());
        }
        finally { File.Delete(path); }
    }
}
