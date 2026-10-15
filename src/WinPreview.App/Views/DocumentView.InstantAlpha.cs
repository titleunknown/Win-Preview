using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using WinPreview.Core.Imaging;

namespace WinPreview.Views;

/// <summary>
/// Instant Alpha, as in Preview: press on a color and drag. The farther you drag, the more similar colors
/// are included; the selection previews in red and becomes transparent when you let go.
/// </summary>
public sealed partial class DocumentView
{
    private const int AlphaPreviewMax = 640;
    private const double DefaultAlphaTolerance = 24;

    private sealed class AlphaDrag
    {
        public required byte[] Small { get; init; }
        public required int SmallW { get; init; }
        public required int SmallH { get; init; }
        public required int SeedX { get; init; }   // full-resolution pixel
        public required int SeedY { get; init; }
        public required Image Overlay { get; init; }
        public double Tolerance { get; set; } = DefaultAlphaTolerance;
    }

    private AlphaDrag? _alpha;

    private void BeginInstantAlpha(PageView pv, Point dip)
    {
        var frame = _session?.Pages[pv.Index].Frame;
        if (frame == null) return;
        var user = pv.Mapping.ToUser(dip);
        int sx = Math.Clamp((int)user.X, 0, frame.Width - 1), sy = Math.Clamp((int)user.Y, 0, frame.Height - 1);

        // Preview on a nearest-neighbor downscale so dragging stays responsive on large photos.
        double k = Math.Min(1, AlphaPreviewMax / (double)Math.Max(frame.Width, frame.Height));
        int w = Math.Max(1, (int)(frame.Width * k)), h = Math.Max(1, (int)(frame.Height * k));
        var small = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Buffer.BlockCopy(frame.Pixels, ((int)(y / k) * frame.Width + (int)(x / k)) * 4, small, (y * w + x) * 4, 4);

        var overlay = new Image { Width = pv.Width, Height = pv.Height, Stretch = Stretch.Fill, IsHitTestVisible = false, Tag = "alpha-preview" };
        pv.AdornerLayer.Children.Add(overlay);
        _alpha = new AlphaDrag { Small = small, SmallW = w, SmallH = h, SeedX = sx, SeedY = sy, Overlay = overlay };
        UpdateAlphaPreview();
    }

    private void DragInstantAlpha(Point dip)
    {
        if (_alpha == null) return;
        double dist = Math.Sqrt(Math.Pow(dip.X - _dragStartDip.X, 2) + Math.Pow(dip.Y - _dragStartDip.Y, 2));
        _alpha.Tolerance = Math.Clamp(DefaultAlphaTolerance + dist * 0.8, 0, 250);
        UpdateAlphaPreview();
    }

    private void UpdateAlphaPreview()
    {
        if (_alpha is not { } a) return;
        int sx = Math.Clamp((int)(a.SeedX * (double)a.SmallW / _session!.Pages[_dragPage].Frame!.Width), 0, a.SmallW - 1);
        int sy = Math.Clamp((int)(a.SeedY * (double)a.SmallH / _session.Pages[_dragPage].Frame!.Height), 0, a.SmallH - 1);
        var mask = AlphaTools.FloodSelect(a.Small, a.SmallW, a.SmallH, sx, sy, a.Tolerance);
        var px = new byte[a.SmallW * a.SmallH * 4];
        for (int i = 0; i < mask.Length; i++)
            if (mask[i] != 0)
            {
                // Premultiplied translucent red.
                px[i * 4 + 2] = 150;
                px[i * 4 + 3] = 150;
            }
        var wb = a.Overlay.Source as WriteableBitmap ?? new WriteableBitmap(a.SmallW, a.SmallH);
        using (var s = wb.PixelBuffer.AsStream()) s.Write(px, 0, px.Length);
        wb.Invalidate();
        a.Overlay.Source = wb;
    }

    private void EndInstantAlpha(bool apply)
    {
        if (_alpha is not { } a) return;
        _alpha = null;
        foreach (var pv in _pages) pv.AdornerLayer.Children.Remove(a.Overlay);
        if (apply) _session?.InstantAlpha(_dragPage, a.SeedX, a.SeedY, a.Tolerance);
    }
}
