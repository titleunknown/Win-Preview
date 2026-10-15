using WinPreview.Core.Markup;

namespace WinPreview.Core.Tests;

public class RasterTests
{
    [Fact]
    public void Rasterized_markups_are_in_y_down_pixel_space_with_transparency()
    {
        var px = MarkupRasterizer.Render(
            [new ShapeMarkup { Rect = new(10, 10, 40, 20), Color = RgbaColor.Red, StrokeWidth = 2, Fill = RgbaColor.Blue }],
            100, 50);
        int At(int x, int y) => (y * 100 + x) * 4;

        Assert.Equal(0, px[At(80, 40) + 3]); // untouched pixels stay transparent
        int fill = At(25, 15);              // filled interior near the TOP of the image (y-down)
        Assert.Equal(255, px[fill + 3]);
        Assert.True(px[fill] > 200 && px[fill + 2] < 60, "interior should be blue");
        Assert.Equal(0, px[At(25, 35) + 3]); // the same x near the bottom is empty
    }

    [Fact]
    public void Composite_blends_straight_alpha()
    {
        byte[] img = [0, 0, 255, 255];          // opaque red
        MarkupRasterizer.Composite(img, [255, 0, 0, 128]); // half-transparent blue
        Assert.Equal(255, img[3]);
        Assert.InRange(img[0], 126, 130);
        Assert.InRange(img[2], 125, 129);
    }

    [Fact]
    public void Rotating_pixels_and_markups_agree()
    {
        // 3x2 image with a marker pixel at (2, 0).
        var src = new byte[3 * 2 * 4];
        src[(0 * 3 + 2) * 4] = 9;
        var cw = MarkupRasterizer.Rotate90(src, 3, 2, clockwise: true); // now 2x3
        var p = MarkupRasterizer.RotationTransform(3, 2, clockwise: true).Apply(new PointD(2.5, 0.5));
        Assert.Equal(9, cw[((int)p.Y * 2 + (int)p.X) * 4]);

        var ccw = MarkupRasterizer.Rotate90(src, 3, 2, clockwise: false);
        var q = MarkupRasterizer.RotationTransform(3, 2, clockwise: false).Apply(new PointD(2.5, 0.5));
        Assert.Equal(9, ccw[((int)q.Y * 2 + (int)q.X) * 4]);
    }

    [Fact]
    public void Transform_maps_every_markup_kind()
    {
        var flip = new Affine(1, 0, 0, -1, 0, 100);
        var line = new LineMarkup { Start = new(0, 10), End = new(5, 20) };
        line.Transform(flip);
        Assert.Equal(new PointD(0, 90), line.Start);
        var box = new TextBoxMarkup { Rect = new(0, 10, 50, 30), FontSize = 12 };
        box.Transform(flip);
        Assert.Equal(new RectD(0, 70, 50, 90), box.Rect);
        Assert.Equal(12, box.FontSize);
        var ink = new InkMarkup { StrokeWidth = 2, Strokes = [[new(1, 1)]] };
        ink.Transform(new Affine(2, 0, 0, 2, 0, 0));
        Assert.Equal(4, ink.StrokeWidth);
    }
}
