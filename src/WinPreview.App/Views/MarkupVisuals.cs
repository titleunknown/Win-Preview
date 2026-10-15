using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WinPreview.Core;
using WinPreview.Core.Markup;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace WinPreview.Views;

public static class ColorExtensions
{
    public static Windows.UI.Color ToUi(this RgbaColor c) => Windows.UI.Color.FromArgb(c.A, c.R, c.G, c.B);
    public static RgbaColor ToRgba(this Windows.UI.Color c) => new(c.R, c.G, c.B, c.A);
    public static SolidColorBrush Brush(this RgbaColor c, double opacity = 1) => new(c.ToUi()) { Opacity = opacity };
}

/// <summary>Maps page user space to DIPs inside a page view.</summary>
public readonly record struct PageMapping(Affine UserToDisplay, double Scale)
{
    public Point ToDip(PointD user)
    {
        var d = UserToDisplay.Apply(user);
        return new Point(d.X * Scale, d.Y * Scale);
    }

    public Rect ToDip(RectD user)
    {
        var d = UserToDisplay.Apply(user);
        return new Rect(d.X1 * Scale, d.Y1 * Scale, d.Width * Scale, d.Height * Scale);
    }

    public PointD ToUser(Point dip) => UserToDisplay.Invert().Apply(new PointD(dip.X / Scale, dip.Y / Scale));

    public RectD ToUser(Rect dip) => UserToDisplay.Invert().Apply(
        new RectD(dip.X / Scale, dip.Y / Scale, (dip.X + dip.Width) / Scale, (dip.Y + dip.Height) / Scale));

    /// <summary>User-space length for a distance in DIPs.</summary>
    public double UserLength(double dip) => dip / Scale;
}

/// <summary>Builds XAML visuals for markups. Each visual is tagged with the markup's Id.</summary>
public static class MarkupVisuals
{
    public static FontFamily FontFor(PdfStandardFont f) => f switch
    {
        // Metric-compatible stand-ins for the PDF base-14 fonts, so wrapping matches the saved file.
        PdfStandardFont.TimesRoman => new FontFamily("Times New Roman"),
        PdfStandardFont.Courier => new FontFamily("Courier New"),
        _ => new FontFamily("Arial"),
    };

    public static FrameworkElement Create(MarkupItem item, PageMapping map)
    {
        FrameworkElement e = item switch
        {
            InkMarkup ink => Ink(ink, map),
            ShapeMarkup s => Shape(s, map),
            LineMarkup l => Line(l, map),
            TextBoxMarkup t => TextBox(t, map),
            NoteMarkup n => Note(n, map),
            TextMarkup tm => TextMarkupVisual(tm, map),
            _ => new Canvas(),
        };
        e.Tag = item.Id;
        return e;
    }

    public static Path Ink(InkMarkup ink, PageMapping map)
    {
        var geo = new PathGeometry();
        foreach (var stroke in ink.Strokes.Where(s => s.Count > 0))
        {
            var fig = new PathFigure { StartPoint = map.ToDip(stroke[0]), IsClosed = false, IsFilled = false };
            if (stroke.Count == 1) fig.Segments.Add(new LineSegment { Point = map.ToDip(stroke[0]) });
            var poly = new PolyLineSegment();
            foreach (var p in stroke.Skip(1)) poly.Points.Add(map.ToDip(p));
            fig.Segments.Add(poly);
            geo.Figures.Add(fig);
        }
        return new Path
        {
            Data = geo, Stroke = ink.Color.Brush(), StrokeThickness = Math.Max(0.5, ink.StrokeWidth * map.Scale),
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
    }

    private static FrameworkElement Shape(ShapeMarkup s, PageMapping map)
    {
        var r = map.ToDip(s.Rect);
        Shape shape = s.Shape == ShapeKind.Oval ? new Ellipse() : new Rectangle();
        shape.Width = Math.Max(1, r.Width);
        shape.Height = Math.Max(1, r.Height);
        shape.Stroke = s.Color.Brush();
        shape.StrokeThickness = s.StrokeWidth * map.Scale;
        shape.Fill = s.Fill is { } f ? f.Brush() : null;
        Canvas.SetLeft(shape, r.X);
        Canvas.SetTop(shape, r.Y);
        // The PDF stroke is centered on the rect edge; XAML strokes sit inside, so grow by half a stroke.
        double half = shape.StrokeThickness / 2;
        shape.Width += 2 * half; shape.Height += 2 * half;
        Canvas.SetLeft(shape, r.X - half);
        Canvas.SetTop(shape, r.Y - half);
        return shape;
    }

    private static FrameworkElement Line(LineMarkup l, PageMapping map)
    {
        var canvas = new Canvas();
        var start = map.ToDip(l.Start);
        var end = map.ToDip(l.End);
        var brush = l.Color.Brush();
        double width = l.StrokeWidth * map.Scale;
        var shaftEnd = end;
        if (l.ArrowAtEnd)
        {
            double dx = end.X - start.X, dy = end.Y - start.Y, len = Math.Sqrt(dx * dx + dy * dy);
            double back = Math.Min(len, l.ArrowLength * 0.8 * map.Scale);
            if (len > 0) shaftEnd = new Point(end.X - dx / len * back, end.Y - dy / len * back);
            var (a, b) = l.ArrowHead();
            canvas.Children.Add(new Polygon
            {
                Points = { end, map.ToDip(a), map.ToDip(b) },
                Fill = brush,
            });
        }
        canvas.Children.Insert(0, new Microsoft.UI.Xaml.Shapes.Line
        {
            X1 = start.X, Y1 = start.Y, X2 = shaftEnd.X, Y2 = shaftEnd.Y, Stroke = brush, StrokeThickness = width,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        return canvas;
    }

    private static FrameworkElement TextBox(TextBoxMarkup t, PageMapping map)
    {
        var r = map.ToDip(t.Rect);
        var border = new Border
        {
            Width = r.Width, Height = r.Height,
            Background = t.Fill is { } f ? f.Brush() : null,
            Padding = new Thickness(2 * map.Scale),
            Child = new TextBlock
            {
                Text = t.Text, TextWrapping = TextWrapping.Wrap, FontFamily = FontFor(t.Font),
                FontSize = Math.Max(1, t.FontSize * map.Scale), Foreground = t.Color.Brush(),
                LineHeight = t.FontSize * 1.2 * map.Scale, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextLineBounds = TextLineBounds.Full,
            },
        };
        Canvas.SetLeft(border, r.X);
        Canvas.SetTop(border, r.Y);
        return border;
    }

    private static FrameworkElement Note(NoteMarkup n, PageMapping map)
    {
        var r = map.ToDip(n.Rect);
        // A note icon stays a readable size regardless of zoom.
        double size = Math.Clamp(r.Width, 16, 28);
        var icon = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(3),
            Background = n.Color.Brush(), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 0, 0, 0)), BorderThickness = new Thickness(1),
            Child = new FontIcon { Glyph = "", FontSize = size * 0.6, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 0, 0, 0)) },
        };
        ToolTipService.SetToolTip(icon, string.IsNullOrWhiteSpace(n.Text) ? "Note" : n.Text);
        Canvas.SetLeft(icon, r.X);
        Canvas.SetTop(icon, r.Y);
        return icon;
    }

    private static FrameworkElement TextMarkupVisual(TextMarkup tm, PageMapping map)
    {
        var canvas = new Canvas();
        foreach (var rect in tm.Rects)
        {
            var r = map.ToDip(rect);
            if (tm.Style == TextMarkupStyle.Highlight)
            {
                var hl = new Rectangle { Width = r.Width, Height = r.Height, Fill = tm.Color.Brush(0.45) };
                Canvas.SetLeft(hl, r.X);
                Canvas.SetTop(hl, r.Y);
                canvas.Children.Add(hl);
            }
            else
            {
                double y = tm.Style == TextMarkupStyle.Underline ? r.Bottom - Math.Max(1, r.Height * 0.08) : r.Y + r.Height * 0.55;
                canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
                {
                    X1 = r.X, X2 = r.Right, Y1 = y, Y2 = y, Stroke = tm.Color.Brush(),
                    StrokeThickness = Math.Max(1, r.Height * 0.07),
                });
            }
        }
        return canvas;
    }
}
