using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WinPreview.Core;
using WinPreview.Session;

namespace WinPreview.Views;

/// <summary>
/// One page: the rendered bitmap plus overlay layers (search hits, text selection, markups, adorners),
/// all in DIPs relative to the page's top-left corner.
/// </summary>
public sealed partial class PageView : Grid
{
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private int _renderGeneration;

    public Canvas HitLayer { get; } = new() { IsHitTestVisible = false };
    public Canvas SelectionLayer { get; } = new() { IsHitTestVisible = false };
    public Canvas MarkupLayer { get; } = new() { IsHitTestVisible = false };
    /// <summary>Handles, previews, and inline editors. Hit-testable so text boxes inside it work.</summary>
    public Canvas AdornerLayer { get; } = new();

    public int Index { get; set; }
    public PageGeometry Geometry { get; private set; }
    public double Zoom { get; private set; } = 1;
    public PageMapping Mapping => new(Geometry.UserToDisplay, Geometry.UnitToDip * Zoom);

    /// <summary>The pixel scale (pixels per display unit) the current bitmap was rendered at; 0 = none.</summary>
    public double RenderedScale { get; private set; }
    public bool IsRendering { get; private set; }

    public PageView(int index, PageGeometry geometry)
    {
        Index = index;
        Geometry = geometry;
        Background = new SolidColorBrush(Microsoft.UI.Colors.White);
        BorderBrush = (Brush)Application.Current.Resources["PageBorderBrush"];
        BorderThickness = new Thickness(0.5);
        Children.Add(_image);
        Children.Add(HitLayer);
        Children.Add(SelectionLayer);
        Children.Add(MarkupLayer);
        Children.Add(AdornerLayer);
        // Canvases clip nothing by default; keep adorners free to overhang the page edge.
        HorizontalAlignment = HorizontalAlignment.Center;
        Translation = new System.Numerics.Vector3(0, 0, 8);
        Shadow = SharedShadow;
    }

    private static ThemeShadow? _shadow;
    private static ThemeShadow SharedShadow => _shadow ??= new ThemeShadow();

    public void SetCursor(InputSystemCursorShape shape) => ProtectedCursor = InputSystemCursor.Create(shape);

    public void SetCursor(InputCursor cursor) => ProtectedCursor = cursor;

    public void SetGeometry(PageGeometry geometry, double zoom)
    {
        Geometry = geometry;
        Zoom = zoom;
        Width = Math.Max(1, geometry.Width * geometry.UnitToDip * zoom);
        Height = Math.Max(1, geometry.Height * geometry.UnitToDip * zoom);
    }

    /// <summary>Pixels per display unit wanted for crisp output at the given raster scale, capped by size.</summary>
    public double DesiredPixelScale(double rasterScale)
    {
        const double maxPixels = 32_000_000;
        double s = Geometry.UnitToDip * Zoom * rasterScale;
        double pixels = Geometry.Width * s * Geometry.Height * s;
        if (pixels > maxPixels) s *= Math.Sqrt(maxPixels / pixels);
        return s;
    }

    public bool NeedsRender(double rasterScale)
    {
        if (IsRendering) return false;
        double want = DesiredPixelScale(rasterScale);
        return RenderedScale <= 0 || Math.Abs(want - RenderedScale) / want > 0.04;
    }

    public async Task RenderAsync(DocumentSession session, double rasterScale)
    {
        int gen = ++_renderGeneration;
        double s = DesiredPixelScale(rasterScale);
        int w = Math.Max(1, (int)Math.Round(Geometry.Width * s)), h = Math.Max(1, (int)Math.Round(Geometry.Height * s));
        IsRendering = true;
        try
        {
            var (px, pw, ph) = await session.RenderAsync(Index, w, h);
            if (gen != _renderGeneration) return;
            var wb = _image.Source as WriteableBitmap;
            if (wb == null || wb.PixelWidth != pw || wb.PixelHeight != ph) wb = new WriteableBitmap(pw, ph);
            using (var stream = wb.PixelBuffer.AsStream())
                await stream.WriteAsync(px.AsMemory(0, Math.Min(px.Length, pw * ph * 4)));
            wb.Invalidate();
            _image.Source = wb;
            RenderedScale = s;
        }
        finally
        {
            if (gen == _renderGeneration) IsRendering = false;
        }
    }

    /// <summary>Forces a re-render on the next pass (content changed).</summary>
    public void Invalidate()
    {
        RenderedScale = 0;
        _renderGeneration++;
        IsRendering = false;
    }

    public void ReleaseBitmap()
    {
        _image.Source = null;
        Invalidate();
    }

    public void ShowRects(Canvas layer, IEnumerable<RectD> userRects, Brush fill)
    {
        var map = Mapping;
        foreach (var ur in userRects)
        {
            var r = map.ToDip(ur);
            var rect = new Rectangle { Width = r.Width, Height = r.Height, Fill = fill, RadiusX = 2, RadiusY = 2 };
            Canvas.SetLeft(rect, r.X);
            Canvas.SetTop(rect, r.Y);
            layer.Children.Add(rect);
        }
    }

    public Point Clamp(Point p) => new(Math.Clamp(p.X, 0, Width), Math.Clamp(p.Y, 0, Height));
}
