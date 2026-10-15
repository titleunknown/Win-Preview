using WinPreview.Core.Markup;

namespace WinPreview.Core.Tests;

public class HitTestTests
{
    [Fact]
    public void Unfilled_shapes_are_hit_on_their_outline_only()
    {
        var rect = new ShapeMarkup { Shape = ShapeKind.Rectangle, Rect = new(0, 0, 100, 100), StrokeWidth = 2 };
        Assert.True(MarkupHitTest.Hits(rect, new(0, 50), 3));
        Assert.False(MarkupHitTest.Hits(rect, new(50, 50), 3));
        rect.Fill = RgbaColor.Blue;
        Assert.True(MarkupHitTest.Hits(rect, new(50, 50), 3));

        var oval = new ShapeMarkup { Shape = ShapeKind.Oval, Rect = new(0, 0, 100, 50), StrokeWidth = 2 };
        Assert.True(MarkupHitTest.Hits(oval, new(50, 0.5), 3));
        Assert.False(MarkupHitTest.Hits(oval, new(50, 25), 3));
        Assert.False(MarkupHitTest.Hits(oval, new(2, 2), 3)); // corner of the bounding box is outside the ellipse
    }

    [Fact]
    public void Lines_and_ink_are_hit_near_their_path()
    {
        var line = new LineMarkup { Start = new(0, 0), End = new(100, 0), StrokeWidth = 2 };
        Assert.True(MarkupHitTest.Hits(line, new(50, 3), 3));
        Assert.False(MarkupHitTest.Hits(line, new(50, 10), 3));

        var ink = new InkMarkup { StrokeWidth = 2, Strokes = [[new(0, 0), new(50, 50)], [new(100, 0)]] };
        Assert.True(MarkupHitTest.Hits(ink, new(25, 26), 3));
        Assert.True(MarkupHitTest.Hits(ink, new(101, 1), 3));
        Assert.False(MarkupHitTest.Hits(ink, new(50, 10), 3));
    }

    [Fact]
    public void Find_returns_the_topmost_markup()
    {
        var below = new ShapeMarkup { Rect = new(0, 0, 100, 100), Fill = RgbaColor.Red };
        var above = new NoteMarkup { Rect = new(40, 40, 60, 60) };
        Assert.Same(above, MarkupHitTest.Find([below, above], new(50, 50), 1));
        Assert.Same(below, MarkupHitTest.Find([below, above], new(10, 10), 1));
        Assert.Null(MarkupHitTest.Find([below, above], new(200, 200), 1));
    }
}
