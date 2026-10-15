using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using Windows.Graphics.Printing;
using WinPreview.Core.Markup;
using WinPreview.Core.Pdf;
using WinPreview.Session;

namespace WinPreview;

/// <summary>
/// Prints through the standard Windows print dialog. Pages are rendered (with markups burned in)
/// to bitmaps sized for the printer's printable area.
/// </summary>
public sealed class Printing
{
    private const double PrintDpi = 150;

    private readonly nint _hwnd;
    private readonly DocumentSession _session;
    private PrintDocument? _doc;
    private IPrintDocumentSource? _source;
    private PdfDocument? _flattened;
    private readonly List<UIElement> _pages = [];

    private Printing(nint hwnd, DocumentSession session)
    {
        _hwnd = hwnd;
        _session = session;
    }

    public static async Task PrintAsync(nint hwnd, DocumentSession session)
    {
        var p = new Printing(hwnd, session);
        await p.ShowAsync();
    }

    private async Task ShowAsync()
    {
        if (_session.Pdf is { } pdf)
        {
            // A flattened copy renders markups exactly as they will appear in the saved file.
            using var ms = new MemoryStream();
            var map = _session.Pages.Select((p, i) => (p, i)).Where(t => t.p.Markups.Count > 0)
                .ToDictionary(t => t.i, t => (IReadOnlyList<MarkupItem>)t.p.Markups.Select(m => m.Clone()).ToList());
            await Task.Run(() => pdf.Save(ms, map));
            _flattened = PdfDocument.Open(ms.ToArray());
        }

        _doc = new PrintDocument();
        _source = _doc.DocumentSource;
        _doc.Paginate += OnPaginate;
        _doc.GetPreviewPage += (_, e) => _doc.SetPreviewPage(e.PageNumber, _pages[e.PageNumber - 1]);
        _doc.AddPages += (_, _) =>
        {
            foreach (var p in _pages) _doc.AddPage(p);
            _doc.AddPagesComplete();
        };

        var manager = PrintManagerInterop.GetForWindow(_hwnd);
        void OnRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
        {
            var task = args.Request.CreatePrintTask(_session.DisplayName, r => r.SetSource(_source));
            task.Completed += (_, _) => _flattened?.Dispose();
            sender.PrintTaskRequested -= OnRequested;
        }
        manager.PrintTaskRequested += OnRequested;
        try
        {
            bool shown = await PrintManagerInterop.ShowPrintUIForWindowAsync(_hwnd);
            Log.Write($"print UI shown={shown}");
        }
        catch
        {
            manager.PrintTaskRequested -= OnRequested;
            throw;
        }
    }

    private void OnPaginate(object sender, PaginateEventArgs e)
    {
        _pages.Clear();
        var desc = e.PrintTaskOptions.GetPageDescription(0);
        var imageable = desc.ImageableRect;
        for (int i = 0; i < _session.PageCount; i++)
        {
            var g = _session.GetGeometry(i);
            // Fit the page into the printable area, preserving aspect ratio.
            double pageW = g.Width * g.UnitToDip, pageH = g.Height * g.UnitToDip;
            double fit = Math.Min(imageable.Width / pageW, imageable.Height / pageH);
            if (_session.IsPdf) fit = Math.Min(fit, 1); // don't upscale PDFs past actual size
            double w = pageW * fit, h = pageH * fit;
            int pw = Math.Max(1, (int)(w / 96 * PrintDpi)), ph = Math.Max(1, (int)(h / 96 * PrintDpi));

            var image = new Image { Width = w, Height = h, Stretch = Stretch.Fill, Source = RenderPage(i, pw, ph) };
            var canvas = new Canvas { Width = desc.PageSize.Width, Height = desc.PageSize.Height };
            Canvas.SetLeft(image, imageable.X + (imageable.Width - w) / 2);
            Canvas.SetTop(image, imageable.Y + (imageable.Height - h) / 2);
            canvas.Children.Add(image);
            _pages.Add(canvas);
        }
        ((PrintDocument)sender).SetPreviewPageCount(_pages.Count, PreviewPageCountType.Final);
    }

    private WriteableBitmap RenderPage(int index, int w, int h)
    {
        byte[] px;
        int pw = w, ph = h;
        if (_flattened != null) px = RenderFlattened(index, w, h);
        else
        {
            var page = _session.Pages[index];
            var f = page.Frame!.Clone();
            if (page.Markups.Count > 0) MarkupRasterizer.Composite(f.Pixels, MarkupRasterizer.Render(page.Markups, f.Width, f.Height));
            px = ImageCodec.Premultiply(f.Pixels);
            (pw, ph) = (f.Width, f.Height);
        }
        var wb = new WriteableBitmap(pw, ph);
        using (var s = wb.PixelBuffer.AsStream()) s.Write(px, 0, pw * ph * 4);
        wb.Invalidate();
        return wb;
    }

    private byte[] RenderFlattened(int index, int w, int h)
    {
        var buf = new byte[w * h * 4];
        _flattened!.Render(index, w, h, 0, 0, w, h, buf, printing: true);
        return buf;
    }
}
