namespace WinPreview.Core.Markup;

public static class MarkupHitTest
{
    /// <summary>The topmost markup under a user-space point, given a tolerance in user units.</summary>
    public static MarkupItem? Find(IReadOnlyList<MarkupItem> items, PointD p, double tolerance)
    {
        for (int i = items.Count - 1; i >= 0; i--)
            if (Hits(items[i], p, tolerance)) return items[i];
        return null;
    }

    public static bool Hits(MarkupItem item, PointD p, double tol)
    {
        if (!item.Bounds.Inflate(tol).Contains(p)) return false;
        switch (item)
        {
            case InkMarkup ink:
                // Signatures are small and dense; grabbing anywhere in the box is friendlier.
                if (ink.IsSignature) return true;
                double reach = tol + ink.StrokeWidth / 2;
                return ink.Strokes.Any(s => s.Count == 1 ? (s[0] - p).Length <= reach
                    : s.Zip(s.Skip(1)).Any(seg => SegmentDistance(p, seg.First, seg.Second) <= reach));
            case LineMarkup line:
                return SegmentDistance(p, line.Start, line.End) <= tol + line.StrokeWidth / 2
                    || line.ArrowAtEnd && (line.End - p).Length <= line.ArrowLength;
            case ShapeMarkup shape when shape.Fill == null:
                return NearShapeEdge(shape, p, tol + shape.StrokeWidth / 2);
            case TextMarkup tm:
                return tm.Rects.Any(r => r.Inflate(tol / 2).Contains(p));
            default:
                return true; // filled shapes, text boxes, notes: anywhere inside
        }
    }

    public static double SegmentDistance(PointD p, PointD a, PointD b)
    {
        var ab = b - a;
        double len2 = ab.X * ab.X + ab.Y * ab.Y;
        if (len2 < 1e-12) return (p - a).Length;
        double t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
        return (p - new PointD(a.X + ab.X * t, a.Y + ab.Y * t)).Length;
    }

    private static bool NearShapeEdge(ShapeMarkup s, PointD p, double tol)
    {
        var r = s.Rect;
        if (s.Shape == ShapeKind.Rectangle)
        {
            bool inOuter = r.Inflate(tol).Contains(p);
            bool inInner = r.Width > 2 * tol && r.Height > 2 * tol && r.Inflate(-tol).Contains(p);
            return inOuter && !inInner;
        }
        double rx = r.Width / 2, ry = r.Height / 2;
        if (rx < tol || ry < tol) return true;
        var c = r.Center;
        double nx = (p.X - c.X) / rx, ny = (p.Y - c.Y) / ry;
        double radial = Math.Sqrt(nx * nx + ny * ny);
        return Math.Abs(radial - 1) * Math.Min(rx, ry) <= tol;
    }
}
