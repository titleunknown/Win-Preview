using WinPreview.Core.Pdf;

namespace WinPreview.Core.Tests;

public class CropTests
{
    private static readonly RectD Page = new(0, 0, 400, 300);
    private static readonly RectD Sel = new(100, 100, 200, 160); // 100 x 60

    [Fact]
    public void New_selection_is_clamped_to_the_page_in_any_direction()
    {
        var r = CropMath.NewSelection(new(350, 250), new(900, 900), Page, null);
        Assert.Equal(new RectD(350, 250, 400, 300), r);
        var back = CropMath.NewSelection(new(50, 50), new(-20, -20), Page, null);
        Assert.Equal(new RectD(0, 0, 50, 50), back);
    }

    [Fact]
    public void New_selection_locked_to_a_ratio_never_exceeds_it()
    {
        var r = CropMath.NewSelection(new(10, 10), new(210, 410), Page, ratio: 2); // wants 200x290, clamped to 300 tall
        Assert.Equal(2, r.Width / r.Height, 6);
        Assert.True(r.X2 <= 400 && r.Y2 <= 300 && r.X1 >= 0 && r.Y1 >= 0);
        var square = CropMath.NewSelection(new(100, 100), new(40, 60), Page, ratio: 1); // dragging up-left
        Assert.Equal(1, square.Width / square.Height, 6);
        Assert.Equal(100, square.X2);
        Assert.Equal(100, square.Y2);
    }

    [Theory]
    [InlineData(CropHandle.Right, 260, 100, 100, 100, 260, 160)]   // only the right edge moves
    [InlineData(CropHandle.Top, 150, 70, 100, 70, 200, 160)]
    [InlineData(CropHandle.TopLeft, 80, 90, 80, 90, 200, 160)]
    [InlineData(CropHandle.BottomRight, 300, 220, 100, 100, 300, 220)]
    public void Free_resize_moves_only_the_dragged_sides(CropHandle h, double px, double py, double l, double t, double r, double b)
    {
        var result = CropMath.Resize(Sel, h, new PointD(px, py), Page, null);
        Assert.Equal(new RectD(l, t, r, b), result);
    }

    [Fact]
    public void Resize_cannot_invert_or_vanish()
    {
        var r = CropMath.Resize(Sel, CropHandle.Left, new PointD(500, 130), Page, null); // dragged past the right edge
        Assert.Equal(CropMath.MinSize, r.Width, 6);
        Assert.Equal(200, r.X2);
        var clamped = CropMath.Resize(Sel, CropHandle.BottomRight, new PointD(999, 999), Page, null);
        Assert.Equal(400, clamped.X2);
        Assert.Equal(300, clamped.Y2);
    }

    [Fact]
    public void Corner_resize_with_a_ratio_keeps_the_opposite_corner_and_the_ratio()
    {
        var r = CropMath.Resize(Sel, CropHandle.BottomRight, new PointD(300, 300), Page, ratio: 1);
        Assert.Equal(100, r.X1);
        Assert.Equal(100, r.Y1);
        Assert.Equal(1, r.Width / r.Height, 6);
        // Dragging far to the corner of the page can't push a locked rectangle outside it.
        var big = CropMath.Resize(new RectD(300, 200, 360, 240), CropHandle.BottomRight, new PointD(999, 999), Page, ratio: 1.5);
        Assert.Equal(1.5, big.Width / big.Height, 6);
        Assert.True(big.X2 <= 400.0001 && big.Y2 <= 300.0001);
        Assert.Equal(300, big.X1);
    }

    [Fact]
    public void Edge_resize_with_a_ratio_adjusts_the_other_dimension_around_the_center()
    {
        var r = CropMath.Resize(Sel, CropHandle.Bottom, new PointD(150, 220), Page, ratio: 2); // height becomes 120 → width 240
        Assert.Equal(2, r.Width / r.Height, 6);
        Assert.Equal(100, r.Y1);
        Assert.Equal(150, r.Center.X, 6);
    }

    [Fact]
    public void Move_stays_inside_the_page()
    {
        Assert.Equal(new RectD(0, 100, 100, 160), CropMath.Move(Sel, -500, 0, Page));
        Assert.Equal(new RectD(300, 240, 400, 300), CropMath.Move(Sel, 900, 900, Page));
    }

    [Fact]
    public void Handles_are_found_near_corners_and_edge_midpoints_and_corners_win()
    {
        Assert.Equal(CropHandle.TopLeft, CropMath.HitHandle(Sel, new(103, 98), 8));
        Assert.Equal(CropHandle.Right, CropMath.HitHandle(Sel, new(204, 130), 8));
        Assert.Equal(CropHandle.Top, CropMath.HitHandle(Sel, new(150, 96), 8));
        Assert.Null(CropMath.HitHandle(Sel, new(150, 130), 8)); // the middle of the selection is not a handle
        // On a tiny selection the corner and edge handles overlap: the nearest one is chosen.
        Assert.Equal(CropHandle.TopLeft, CropMath.HitHandle(new RectD(0, 0, 10, 10), new(1, 1), 8));
    }

    [Fact]
    public void Ratio_setting_resolves_against_the_page()
    {
        Assert.Null(CropMath.ResolveRatio(null, 400, 300));
        Assert.Equal(4.0 / 3, CropMath.ResolveRatio(-1, 400, 300)!.Value, 6);
        Assert.Equal(1.0, CropMath.ResolveRatio(1.0, 400, 300));
    }

    [Fact]
    public void Resetting_a_crop_restores_the_full_page()
    {
        using var doc = TestPdfs.TextDocument();
        doc.SetCropBox(0, new RectD(50, 100, 350, 500));
        Assert.Equal(300, doc.GetPageInfo(0).Width, 1);
        Assert.True(doc.ResetCropBox(0));
        var info = doc.GetPageInfo(0);
        Assert.Equal(612, info.Width, 1);
        Assert.Equal(792, info.Height, 1);
        using var reopened = PdfDocument.Open(TestPdfs.Save(doc));
        Assert.Equal(612, reopened.GetPageInfo(0).Width, 1);
    }
}
