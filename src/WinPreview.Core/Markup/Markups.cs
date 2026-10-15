namespace WinPreview.Core.Markup;

public readonly record struct RgbaColor(byte R, byte G, byte B, byte A = 255)
{
    public static readonly RgbaColor Black = new(0, 0, 0);
    public static readonly RgbaColor Red = new(0xFF, 0x3B, 0x30);
    public static readonly RgbaColor Yellow = new(0xFF, 0xD6, 0x0A);
    public static readonly RgbaColor Green = new(0x34, 0xC7, 0x59);
    public static readonly RgbaColor Blue = new(0x00, 0x7A, 0xFF);
    public static readonly RgbaColor Pink = new(0xFF, 0x2D, 0x55);
    public static readonly RgbaColor Purple = new(0xAF, 0x52, 0xDE);
    public static readonly RgbaColor HighlightYellow = new(0xFF, 0xE0, 0x66);

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    public static RgbaColor FromHex(string s)
    {
        s = s.TrimStart('#');
        byte P(int i) => Convert.ToByte(s.Substring(i, 2), 16);
        return new(P(0), P(2), P(4), s.Length >= 8 ? P(6) : (byte)255);
    }
}

/// <summary>
/// A user-editable mark on a page. All coordinates are in page user space
/// (PDF points y-up for PDFs, pixels y-down for images); see <see cref="PageTransforms"/>.
/// </summary>
public abstract class MarkupItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public RgbaColor Color { get; set; } = RgbaColor.Red;
    public double StrokeWidth { get; set; } = 2;

    public abstract RectD Bounds { get; }
    public abstract void Offset(double dx, double dy);
    public MarkupItem Clone() => CloneCore();
    protected abstract MarkupItem CloneCore();
}

public sealed class InkMarkup : MarkupItem
{
    public List<List<PointD>> Strokes { get; set; } = [];
    /// <summary>True when this ink was placed from the signature library.</summary>
    public bool IsSignature { get; set; }

    public override RectD Bounds => RectD.Bounding(Strokes.SelectMany(s => s)).Inflate(StrokeWidth / 2);

    public override void Offset(double dx, double dy) =>
        Strokes = Strokes.Select(s => s.Select(p => new PointD(p.X + dx, p.Y + dy)).ToList()).ToList();

    protected override MarkupItem CloneCore() => new InkMarkup
    {
        Color = Color, StrokeWidth = StrokeWidth, IsSignature = IsSignature,
        Strokes = Strokes.Select(s => s.ToList()).ToList(),
    };
}

public enum ShapeKind { Rectangle, Oval }

public sealed class ShapeMarkup : MarkupItem
{
    public ShapeKind Shape { get; set; }
    public RectD Rect { get; set; }
    public RgbaColor? Fill { get; set; }

    public override RectD Bounds => Rect;
    public override void Offset(double dx, double dy) => Rect = Rect.Offset(dx, dy);
    protected override MarkupItem CloneCore() => new ShapeMarkup
    {
        Color = Color, StrokeWidth = StrokeWidth, Shape = Shape, Rect = Rect, Fill = Fill,
    };
}

public sealed class LineMarkup : MarkupItem
{
    public PointD Start { get; set; }
    public PointD End { get; set; }
    public bool ArrowAtEnd { get; set; }

    /// <summary>Arrowhead length, scaled with the stroke so thick arrows stay proportional.</summary>
    public double ArrowLength => Math.Max(8, StrokeWidth * 4);

    public override RectD Bounds => RectD.FromPoints(Start, End).Inflate(ArrowAtEnd ? ArrowLength : StrokeWidth);
    public override void Offset(double dx, double dy)
    {
        Start = new(Start.X + dx, Start.Y + dy);
        End = new(End.X + dx, End.Y + dy);
    }
    protected override MarkupItem CloneCore() => new LineMarkup
    {
        Color = Color, StrokeWidth = StrokeWidth, Start = Start, End = End, ArrowAtEnd = ArrowAtEnd,
    };

    /// <summary>The two far corners of the arrowhead, given the direction of the line.</summary>
    public (PointD Left, PointD Right) ArrowHead()
    {
        var d = End - Start;
        double len = d.Length;
        if (len < 1e-6) return (End, End);
        double ux = d.X / len, uy = d.Y / len, l = ArrowLength, w = l * 0.5;
        var back = new PointD(End.X - ux * l, End.Y - uy * l);
        return (new(back.X - uy * w, back.Y + ux * w), new(back.X + uy * w, back.Y - ux * w));
    }
}

public enum PdfStandardFont { Helvetica, TimesRoman, Courier }

public sealed class TextBoxMarkup : MarkupItem
{
    public RectD Rect { get; set; }
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 14;
    public PdfStandardFont Font { get; set; } = PdfStandardFont.Helvetica;
    public RgbaColor? Fill { get; set; }

    public override RectD Bounds => Rect;
    public override void Offset(double dx, double dy) => Rect = Rect.Offset(dx, dy);
    protected override MarkupItem CloneCore() => new TextBoxMarkup
    {
        Color = Color, StrokeWidth = StrokeWidth, Rect = Rect, Text = Text, FontSize = FontSize, Font = Font, Fill = Fill,
    };
}

public sealed class NoteMarkup : MarkupItem
{
    public const double IconSize = 20;

    /// <summary>Where the note icon sits, in user space.</summary>
    public RectD Rect { get; set; }
    public string Text { get; set; } = "";

    public NoteMarkup() => Color = RgbaColor.Yellow;

    public override RectD Bounds => Rect;
    public override void Offset(double dx, double dy) => Rect = Rect.Offset(dx, dy);
    protected override MarkupItem CloneCore() => new NoteMarkup { Color = Color, Rect = Rect, Text = Text };
}

public enum TextMarkupStyle { Highlight, Underline, Strikeout }

/// <summary>Highlight / underline / strike-through over runs of text, one rect per line.</summary>
public sealed class TextMarkup : MarkupItem
{
    public TextMarkupStyle Style { get; set; }
    public List<RectD> Rects { get; set; } = [];
    /// <summary>The marked-up text, kept for copy and accessibility; may be empty.</summary>
    public string Text { get; set; } = "";

    public TextMarkup() => Color = RgbaColor.HighlightYellow;

    public override RectD Bounds => Rects.Aggregate(default(RectD), (a, r) => a.Union(r));
    public override void Offset(double dx, double dy) => Rects = Rects.Select(r => r.Offset(dx, dy)).ToList();
    protected override MarkupItem CloneCore() => new TextMarkup
    {
        Color = Color, StrokeWidth = StrokeWidth, Style = Style, Rects = Rects.ToList(), Text = Text,
    };
}
