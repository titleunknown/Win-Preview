using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinPreview.Core.Markup;
using WinPreview.Session;

namespace WinPreview;

/// <summary>Full-screen, one page at a time. Arrows/Space/click advance, Esc exits.</summary>
public sealed class SlideshowWindow : Window
{
    private readonly DocumentSession _session;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _counter = new() { Foreground = new SolidColorBrush(Colors.Gray), Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Grid _root;
    private int _page, _generation;

    public SlideshowWindow(DocumentSession session, int startPage)
    {
        _session = session;
        _page = startPage;
        _root = new Grid { Background = new SolidColorBrush(Colors.Black), Children = { _image, _counter } };
        // A Grid can't take keyboard focus; a UserControl host can.
        var host = new UserControl { Content = _root, IsTabStop = true };
        Content = host;
        Title = "Slideshow";
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        host.KeyDown += OnKeyDown;
        _root.Tapped += (_, _) => Go(1);
        _root.RightTapped += (_, _) => Go(-1);
        _root.Loaded += (_, _) => { host.Focus(FocusState.Programmatic); _ = ShowAsync(); };
        _root.SizeChanged += (_, _) => _ = ShowAsync();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape: Close(); break;
            case Windows.System.VirtualKey.Right or Windows.System.VirtualKey.Down or Windows.System.VirtualKey.Space or Windows.System.VirtualKey.PageDown: Go(1); break;
            case Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Up or Windows.System.VirtualKey.PageUp: Go(-1); break;
            case Windows.System.VirtualKey.Home: _page = 0; _ = ShowAsync(); break;
            case Windows.System.VirtualKey.End: _page = _session.PageCount - 1; _ = ShowAsync(); break;
        }
        e.Handled = true;
    }

    private void Go(int delta)
    {
        int next = Math.Clamp(_page + delta, 0, _session.PageCount - 1);
        if (next == _page) return;
        _page = next;
        _ = ShowAsync();
    }

    private async Task ShowAsync()
    {
        if (_root.ActualWidth <= 0 || _page >= _session.PageCount) return;
        int gen = ++_generation;
        double raster = _root.XamlRoot?.RasterizationScale ?? 1;
        var g = _session.GetGeometry(_page);
        double fit = Math.Min(_root.ActualWidth / g.Width, _root.ActualHeight / g.Height) * raster;
        int w = Math.Max(1, (int)(g.Width * fit)), h = Math.Max(1, (int)(g.Height * fit));
        _counter.Text = $"{_page + 1} / {_session.PageCount}";

        byte[] px;
        int pw, ph;
        var page = _session.Pages[_page];
        if (_session.Pdf is { } pdf)
        {
            // Burn markups in by rendering a saved copy of just this page.
            (px, pw, ph) = await Task.Run(() =>
            {
                using var ms = new MemoryStream();
                pdf.Save(ms, new Dictionary<int, IReadOnlyList<MarkupItem>> { [_page] = page.Markups.Select(m => m.Clone()).ToList() });
                using var copy = Core.Pdf.PdfDocument.Open(ms.ToArray());
                return (copy.Render(_page, w, h), w, h);
            });
        }
        else
        {
            var f = page.Frame!.Clone();
            if (page.Markups.Count > 0) MarkupRasterizer.Composite(f.Pixels, MarkupRasterizer.Render(page.Markups, f.Width, f.Height));
            (px, pw, ph) = (ImageCodec.Premultiply(f.Pixels), f.Width, f.Height);
        }
        if (gen != _generation) return;
        var wb = new WriteableBitmap(pw, ph);
        using (var s = wb.PixelBuffer.AsStream()) await s.WriteAsync(px.AsMemory(0, pw * ph * 4));
        wb.Invalidate();
        _image.Source = wb;
    }
}
