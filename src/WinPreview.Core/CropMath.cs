namespace WinPreview.Core;

public enum CropHandle { TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

/// <summary>
/// Pure geometry for the crop selection: creating, moving and resizing a rectangle inside page bounds,
/// optionally locked to an aspect ratio. Works in any consistent y-down space (the app uses on-screen DIPs).
/// </summary>
public static class CropMath
{
    public const double MinSize = 8;

    private static bool IsLeft(CropHandle h) => h is CropHandle.TopLeft or CropHandle.Left or CropHandle.BottomLeft;
    private static bool IsRight(CropHandle h) => h is CropHandle.TopRight or CropHandle.Right or CropHandle.BottomRight;
    private static bool IsTop(CropHandle h) => h is CropHandle.TopLeft or CropHandle.Top or CropHandle.TopRight;
    private static bool IsBottom(CropHandle h) => h is CropHandle.BottomLeft or CropHandle.Bottom or CropHandle.BottomRight;
    private static bool IsCorner(CropHandle h) => (IsLeft(h) || IsRight(h)) && (IsTop(h) || IsBottom(h));

    private static PointD Clamp(PointD p, RectD b) => new(Math.Clamp(p.X, b.X1, b.X2), Math.Clamp(p.Y, b.Y1, b.Y2));

    /// <summary>A new selection dragged from <paramref name="anchor"/> to <paramref name="p"/>.</summary>
    /// <param name="ratio">Width ÷ height to lock to, or null for a free rectangle.</param>
    public static RectD NewSelection(PointD anchor, PointD p, RectD bounds, double? ratio)
    {
        p = Clamp(p, bounds);
        anchor = Clamp(anchor, bounds);
        double w = Math.Abs(p.X - anchor.X), h = Math.Abs(p.Y - anchor.Y);
        if (ratio is { } k and > 0)
        {
            w = Math.Min(w, h * k);
            h = w / k;
        }
        double sx = p.X >= anchor.X ? 1 : -1, sy = p.Y >= anchor.Y ? 1 : -1;
        return RectD.FromPoints(anchor, new PointD(anchor.X + sx * w, anchor.Y + sy * h));
    }

    /// <summary>Resizes by dragging a handle to <paramref name="p"/>; the opposite side (or corner) stays put.</summary>
    public static RectD Resize(RectD start, CropHandle handle, PointD p, RectD bounds, double? ratio)
    {
        p = Clamp(p, bounds);
        double l = start.X1, t = start.Y1, r = start.X2, b = start.Y2;
        if (IsLeft(handle)) l = Math.Min(p.X, r - MinSize);
        if (IsRight(handle)) r = Math.Max(p.X, l + MinSize);
        if (IsTop(handle)) t = Math.Min(p.Y, b - MinSize);
        if (IsBottom(handle)) b = Math.Max(p.Y, t + MinSize);
        if (ratio is not { } k || k <= 0) return new RectD(l, t, r, b);

        if (IsCorner(handle))
        {
            double w = Math.Min(r - l, (b - t) * k);
            w = Math.Max(w, MinSize);
            double h = w / k;
            if (IsLeft(handle)) l = r - w; else r = l + w;
            if (IsTop(handle)) t = b - h; else b = t + h;
            return FitInside(new RectD(l, t, r, b), handle, bounds);
        }

        // Edge handle: the dragged side sets one dimension; the other follows the ratio around the center.
        double cx = (start.X1 + start.X2) / 2, cy = (start.Y1 + start.Y2) / 2;
        if (handle is CropHandle.Top or CropHandle.Bottom)
        {
            double h = b - t, w = h * k;
            double maxW = 2 * Math.Min(cx - bounds.X1, bounds.X2 - cx);
            if (w > maxW) { w = maxW; h = w / k; }
            if (handle == CropHandle.Top) t = b - h; else b = t + h;
            return new RectD(cx - w / 2, t, cx + w / 2, b);
        }
        else
        {
            double w = r - l, h = w / k;
            double maxH = 2 * Math.Min(cy - bounds.Y1, bounds.Y2 - cy);
            if (h > maxH) { h = maxH; w = h * k; }
            if (handle == CropHandle.Left) l = r - w; else r = l + w;
            return new RectD(l, cy - h / 2, r, cy + h / 2);
        }
    }

    /// <summary>Shrinks a ratio-locked rectangle that poked outside the bounds, keeping the anchored corner fixed.</summary>
    private static RectD FitInside(RectD rect, CropHandle corner, RectD bounds)
    {
        if (rect.X1 >= bounds.X1 && rect.Y1 >= bounds.Y1 && rect.X2 <= bounds.X2 && rect.Y2 <= bounds.Y2) return rect;
        double ax = IsLeft(corner) ? rect.X2 : rect.X1, ay = IsTop(corner) ? rect.Y2 : rect.Y1;
        double availW = IsLeft(corner) ? ax - bounds.X1 : bounds.X2 - ax;
        double availH = IsTop(corner) ? ay - bounds.Y1 : bounds.Y2 - ay;
        double scale = Math.Min(1, Math.Min(availW / rect.Width, availH / rect.Height));
        double w = rect.Width * scale, h = rect.Height * scale;
        double l = IsLeft(corner) ? ax - w : ax, t = IsTop(corner) ? ay - h : ay;
        return new RectD(l, t, l + w, t + h);
    }

    /// <summary>Moves a rectangle, keeping it inside the bounds.</summary>
    public static RectD Move(RectD start, double dx, double dy, RectD bounds)
    {
        dx = Math.Clamp(dx, bounds.X1 - start.X1, bounds.X2 - start.X2);
        dy = Math.Clamp(dy, bounds.Y1 - start.Y1, bounds.Y2 - start.Y2);
        return start.Offset(dx, dy);
    }

    /// <summary>The handle under a point (within <paramref name="radius"/>), preferring corners; null if none.</summary>
    public static CropHandle? HitHandle(RectD r, PointD p, double radius)
    {
        var (cx, cy) = (r.Center.X, r.Center.Y);
        (CropHandle H, double X, double Y)[] spots =
        [
            (CropHandle.TopLeft, r.X1, r.Y1), (CropHandle.TopRight, r.X2, r.Y1),
            (CropHandle.BottomRight, r.X2, r.Y2), (CropHandle.BottomLeft, r.X1, r.Y2),
            (CropHandle.Top, cx, r.Y1), (CropHandle.Right, r.X2, cy), (CropHandle.Bottom, cx, r.Y2), (CropHandle.Left, r.X1, cy),
        ];
        CropHandle? best = null;
        double bestDist = double.MaxValue;
        foreach (var (h, x, y) in spots)
        {
            double d = Math.Max(Math.Abs(p.X - x), Math.Abs(p.Y - y));
            if (d <= radius && d < bestDist) { best = h; bestDist = d; }
        }
        return best;
    }

    /// <summary>The ratio (width ÷ height) a selection should lock to; negative means "same as the page".</summary>
    public static double? ResolveRatio(double? setting, double pageWidth, double pageHeight) =>
        setting is null ? null : setting < 0 ? pageWidth / Math.Max(1e-9, pageHeight) : setting;
}
