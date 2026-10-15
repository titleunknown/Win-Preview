namespace WinPreview.Core;

public readonly record struct PointD(double X, double Y)
{
    public static PointD operator +(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);
    public static PointD operator -(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);
    public double Length => Math.Sqrt(X * X + Y * Y);
}

/// <summary>An axis-aligned rectangle stored as its min and max corners.</summary>
public readonly record struct RectD(double X1, double Y1, double X2, double Y2)
{
    public double Width => X2 - X1;
    public double Height => Y2 - Y1;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static RectD FromPoints(PointD a, PointD b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    public static RectD Bounding(IEnumerable<PointD> points)
    {
        double x1 = double.MaxValue, y1 = double.MaxValue, x2 = double.MinValue, y2 = double.MinValue;
        foreach (var p in points)
        {
            x1 = Math.Min(x1, p.X); y1 = Math.Min(y1, p.Y);
            x2 = Math.Max(x2, p.X); y2 = Math.Max(y2, p.Y);
        }
        return x1 > x2 ? default : new(x1, y1, x2, y2);
    }

    public RectD Union(RectD o) => IsEmpty ? o : o.IsEmpty ? this
        : new(Math.Min(X1, o.X1), Math.Min(Y1, o.Y1), Math.Max(X2, o.X2), Math.Max(Y2, o.Y2));

    public RectD Inflate(double d) => new(X1 - d, Y1 - d, X2 + d, Y2 + d);
    public RectD Offset(double dx, double dy) => new(X1 + dx, Y1 + dy, X2 + dx, Y2 + dy);
    public bool Contains(PointD p) => p.X >= X1 && p.X <= X2 && p.Y >= Y1 && p.Y <= Y2;
    public bool Intersects(RectD o) => X1 <= o.X2 && o.X1 <= X2 && Y1 <= o.Y2 && o.Y1 <= Y2;
    public PointD Center => new((X1 + X2) / 2, (Y1 + Y2) / 2);
}

/// <summary>2D affine transform: x' = M11*x + M21*y + DX, y' = M12*x + M22*y + DY.</summary>
public readonly record struct Affine(double M11, double M12, double M21, double M22, double DX, double DY)
{
    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

    public PointD Apply(PointD p) => new(M11 * p.X + M21 * p.Y + DX, M12 * p.X + M22 * p.Y + DY);
    public RectD Apply(RectD r) => RectD.FromPoints(Apply(new PointD(r.X1, r.Y1)), Apply(new PointD(r.X2, r.Y2)));

    public Affine Invert()
    {
        double det = M11 * M22 - M12 * M21;
        return new(M22 / det, -M12 / det, -M21 / det, M11 / det,
            (M21 * DY - M22 * DX) / det, (M12 * DX - M11 * DY) / det);
    }
}

/// <summary>
/// Maps a page's "user space" (PDF points, y-up, origin at the MediaBox origin; or image pixels)
/// to "display space" (unscaled points/pixels, y-down, origin at the top-left of the visible page
/// after /Rotate is applied).
/// </summary>
public static class PageTransforms
{
    /// <param name="crop">The visible page box in user space.</param>
    /// <param name="rotation">Clockwise page rotation in degrees (0, 90, 180, 270).</param>
    public static Affine UserToDisplay(RectD crop, int rotation) => (((rotation % 360) + 360) % 360) switch
    {
        // dx = x - l, dy = t - y
        0 => new(1, 0, 0, -1, -crop.X1, crop.Y2),
        // dx = y - b, dy = x - l
        90 => new(0, 1, 1, 0, -crop.Y1, -crop.X1),
        // dx = r - x, dy = y - b
        180 => new(-1, 0, 0, 1, crop.X2, -crop.Y1),
        // dx = t - y, dy = r - x
        270 => new(0, -1, -1, 0, crop.Y2, crop.X2),
        _ => throw new ArgumentOutOfRangeException(nameof(rotation)),
    };
}
