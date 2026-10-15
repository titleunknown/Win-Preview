using WinPreview.Core.Imaging;
using WinPreview.Core.Text;

namespace WinPreview.Core.Tests;

public class TextAndAlphaTests
{
    private static OcrTextLayer Sample() => new(
    [
        new OcrLine([new OcrWord("Hello", new(10, 10, 60, 30)), new OcrWord("world", new(70, 10, 120, 30))]),
        new OcrLine([new OcrWord("Second", new(10, 40, 70, 60))]),
    ]);

    [Fact]
    public void Ocr_layer_joins_words_and_lines()
    {
        var t = Sample();
        Assert.Equal("Hello world\nSecond", t.AllText);
        Assert.Equal("world", t.GetText(6, 5));
    }

    [Fact]
    public void Ocr_layer_maps_points_to_characters()
    {
        var t = Sample();
        Assert.Equal(0, t.GetCharIndexAt(new(11, 20), 0));   // 'H'
        Assert.Equal(4, t.GetCharIndexAt(new(59, 20), 0));   // 'o'
        Assert.Equal(5, t.GetCharIndexAt(new(65, 20), 0));   // the space between words
        Assert.Equal(-1, t.GetCharIndexAt(new(300, 300), 2));
        Assert.Equal(12, t.GetNearestCharIndex(new(0, 50)));  // 'S' at the start of line 2
        Assert.Equal((6, 10), t.WordAt(8));
    }

    [Fact]
    public void Ocr_layer_gives_one_rect_per_line_and_finds_text()
    {
        var t = Sample();
        var rects = t.GetTextRects(6, 9); // "world\nSec"
        Assert.Equal(2, rects.Count);
        Assert.Equal(70, rects[0].X1, 3);
        Assert.Equal(40, rects[1].Y1, 3);
        Assert.Equal([(6, 5)], t.Find("WORLD", matchCase: false).ToList());
        Assert.Empty(t.Find("WORLD", matchCase: true));
    }

    [Fact]
    public void Pdf_text_layer_matches_the_document()
    {
        using var doc = TestPdfs.TextDocument();
        var layer = new PdfTextLayer(doc, 0);
        var (start, count) = layer.Find("brown", false).Single();
        Assert.Equal("brown", layer.GetText(start, count));
        Assert.Single(layer.GetTextRects(start, count));
    }

    private static byte[] Image(int w, int h, Func<int, int, (byte r, byte g, byte b)> color)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = color(x, y);
                int i = (y * w + x) * 4;
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = (b, g, r, 255);
            }
        return px;
    }

    [Fact]
    public void Flood_select_stops_at_color_edges_and_respects_tolerance()
    {
        // White background with a red 4x4 square in the middle, and a slightly off-white strip.
        var px = Image(10, 10, (x, y) => x is >= 3 and < 7 && y is >= 3 and < 7 ? ((byte)255, (byte)0, (byte)0)
            : y == 9 ? ((byte)240, (byte)240, (byte)240) : ((byte)255, (byte)255, (byte)255));
        var tight = AlphaTools.FloodSelect(px, 10, 10, 0, 0, 5);
        Assert.Equal(255, tight[0]);
        Assert.Equal(0, tight[5 * 10 + 5]);   // red square excluded
        Assert.Equal(0, tight[9 * 10 + 0]);   // off-white strip excluded at low tolerance
        var loose = AlphaTools.FloodSelect(px, 10, 10, 0, 0, 40);
        Assert.Equal(255, loose[9 * 10 + 0]);
        Assert.Equal(0, loose[5 * 10 + 5]);

        AlphaTools.ClearSelected(px, 10, 10, tight);
        Assert.Equal(0, px[3]);                         // background cleared
        Assert.Equal(255, px[(5 * 10 + 5) * 4 + 3]);   // square kept
        Assert.True(AlphaTools.HasTransparency(px));
    }

    [Fact]
    public void Mask_is_resampled_onto_the_image()
    {
        var px = Image(4, 4, (_, _) => (10, 20, 30));
        // 2x2 mask: keep the left column, drop the right.
        AlphaTools.ApplyMask(px, 4, 4, [1, 0, 1, 0], 2, 2);
        Assert.Equal(255, px[3]);                 // (0,0)
        Assert.Equal(0, px[(0 * 4 + 3) * 4 + 3]); // (3,0)
    }

    [Fact]
    public void Model_input_is_planar_rgb_normalized()
    {
        var px = Image(2, 2, (_, _) => (255, 0, 0));
        var t = AlphaTools.PrepareSquareInput(px, 2, 2, 2, [0.5f, 0.5f, 0.5f], [0.5f, 0.5f, 0.5f]);
        Assert.Equal(12, t.Length);
        Assert.Equal(1f, t[0], 3);   // R plane: (1 - 0.5) / 0.5
        Assert.Equal(-1f, t[4], 3);  // G plane: (0 - 0.5) / 0.5
        float[] raw = [2, 4, 6];
        AlphaTools.Normalize(raw);
        Assert.Equal([0f, 0.5f, 1f], raw);
    }
}
