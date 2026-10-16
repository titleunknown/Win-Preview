using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinPreview.Core;
using WinPreview.Core.Markup;
using WinPreview.Core.Pdf;
using WinPreview.Session;

namespace WinPreview.Views;

public enum Tool { Select, RectSelect, Draw, Rectangle, Oval, Line, Arrow, Text, Note, Highlight, InstantAlpha }

public enum FitMode { None, Width, Page }

public sealed class ToolStyle
{
    public RgbaColor Stroke { get; set; } = RgbaColor.Red;
    public RgbaColor? Fill { get; set; }
    public double StrokeWidth { get; set; } = 2;
    public RgbaColor TextColor { get; set; } = RgbaColor.Black;
    public double FontSize { get; set; } = 14;
    public PdfStandardFont Font { get; set; } = PdfStandardFont.Helvetica;
    public TextMarkupStyle MarkupStyle { get; set; } = TextMarkupStyle.Highlight;
    public RgbaColor HighlightColor { get; set; } = RgbaColor.HighlightYellow;
}

/// <summary>
/// The scrolling, zoomable document surface: lays out pages vertically, renders the visible ones,
/// and hosts all pointer/keyboard interaction for selection, markup tools and form filling.
/// </summary>
public sealed partial class DocumentView : Grid
{
    private static readonly double[] ZoomSteps = [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.25, 1.5, 2, 3, 4, 6, 8, 12, 16];
    private const double PageSpacing = 16, PagePadding = 24;

    private readonly ScrollViewer _scroll;
    private readonly Grid _content = new();
    private readonly StackPanel _stack;
    private readonly List<PageView> _pages = [];
    private readonly DispatcherQueue _dispatcher;
    private DocumentSession? _session;
    private bool _renderScheduled, _renderAgain, _rendering;
    private int _currentPage;

    public DocumentView()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _stack = new StackPanel
        {
            Spacing = PageSpacing, Padding = new Thickness(PagePadding),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        _content.Children.Add(_stack);
        _scroll = new ScrollViewer
        {
            Content = _content, ZoomMode = ZoomMode.Disabled, IsTabStop = true, UseSystemFocusVisuals = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto, VerticalScrollMode = ScrollMode.Auto,
            Background = (Brush)Application.Current.Resources["CanvasBackgroundBrush"],
        };
        Children.Add(_scroll);

        _scroll.ViewChanged += (_, _) => { UpdateCurrentPage(); ScheduleRender(); };
        _scroll.SizeChanged += (_, _) => OnViewportSizeChanged();
        _scroll.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheel), true);
        PreviewKeyDown += OnPreviewKeyDown;
        CharacterReceived += OnCharacterReceived;
    }

    public DocumentSession? Session => _session;
    public double Zoom { get; private set; } = 1;
    public FitMode Fit { get; private set; } = FitMode.Width;
    public int CurrentPage => _currentPage;
    public IReadOnlyList<PageView> PageViews => _pages;
    public ToolStyle CurrentStyle { get; } = new();

    public event Action<int>? CurrentPageChanged;
    public event Action? ZoomChanged;
    /// <summary>Selection of a markup, text, or region changed.</summary>
    public event Action? SelectionChanged;
    /// <summary>The active tool changed from inside the view (e.g. back to Select after placing a shape).</summary>
    public event Action? ToolChanged;

    private Tool _tool;
    public Tool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            CommitInlineEditor();
            _tool = value;
            if (value != Tool.Select && value != Tool.Highlight) ClearTextSelection();
            if (value != Tool.RectSelect) ClearRectSelection();
            ToolChanged?.Invoke();
        }
    }

    private double RasterScale => XamlRoot?.RasterizationScale ?? 1;

    /// <summary>
    /// Style sizes (line widths, font sizes) are in points. On large images a 2 pt line would be a hairline,
    /// so they're scaled to the picture: 1 for PDFs and small images, larger for big photos.
    /// </summary>
    public double StyleScale(int page)
    {
        if (_session is not { IsPdf: false } || page < 0 || page >= _session.PageCount) return 1;
        var g = _session.GetGeometry(page);
        return Math.Max(1, Math.Max(g.Width, g.Height) / 1000.0);
    }

    public double SelectedStyleScale => _selected is { } s ? StyleScale(s.Page) : 1;

    // ---------------- Session ----------------

    public void Attach(DocumentSession session)
    {
        Detach();
        _session = session;
        session.StructureChanged += OnStructureChanged;
        session.PageContentChanged += OnPageContentChanged;
        session.MarkupsChanged += OnMarkupsChanged;
        Fit = session.IsPdf ? FitMode.Width : FitMode.Page;
        RebuildPages();
    }

    public void Detach()
    {
        if (_session == null) return;
        _session.StructureChanged -= OnStructureChanged;
        _session.PageContentChanged -= OnPageContentChanged;
        _session.MarkupsChanged -= OnMarkupsChanged;
        _session = null;
        _pages.Clear();
        _stack.Children.Clear();
    }

    private void OnStructureChanged() => _dispatcher.TryEnqueue(() =>
    {
        _selected = null;
        _textSel = null;
        _rectSel = null;
        _formPage = -1;
        RebuildPages();
        SelectionChanged?.Invoke();
    });

    private void OnPageContentChanged(int page) => _dispatcher.TryEnqueue(() =>
    {
        if (page < _pages.Count)
        {
            _pages[page].Invalidate();
            ScheduleRender();
        }
    });

    private void OnMarkupsChanged(int page) => _dispatcher.TryEnqueue(() =>
    {
        if (page >= _pages.Count) return;
        RedrawMarkups(page);
        if (_selected is { } s && _session!.FindMarkup(s.Page, s.Id) == null) _selected = null;
        RedrawAdorners();
        SelectionChanged?.Invoke();
    });

    private void RebuildPages()
    {
        _stack.Children.Clear();
        _pages.Clear();
        if (_session == null) return;
        for (int i = 0; i < _session.PageCount; i++)
        {
            var pv = new PageView(i, _session.GetGeometry(i));
            pv.SetGeometry(pv.Geometry, Zoom);
            HookPage(pv);
            _pages.Add(pv);
            _stack.Children.Add(pv);
        }
        _currentPage = Math.Clamp(_currentPage, 0, Math.Max(0, _pages.Count - 1));
        if (Fit != FitMode.None) ApplyFit();
        RedrawAllOverlays();
        ScheduleRender();
    }

    // ---------------- Zoom ----------------

    private void OnViewportSizeChanged()
    {
        _content.MinWidth = _scroll.ViewportWidth;
        _content.MinHeight = _scroll.ViewportHeight;
        if (Fit != FitMode.None) ApplyFit();
        ScheduleRender();
    }

    private void ApplyFit()
    {
        if (_pages.Count == 0 || _scroll.ViewportWidth <= 0) return;
        double availW = _scroll.ViewportWidth - 2 * PagePadding - 2;
        double availH = _scroll.ViewportHeight - 2 * PagePadding - 2;
        double z;
        if (Fit == FitMode.Width)
        {
            double maxW = _pages.Max(p => p.Geometry.Width * p.Geometry.UnitToDip);
            z = availW / maxW;
        }
        else
        {
            var g = _pages[Math.Clamp(_currentPage, 0, _pages.Count - 1)].Geometry;
            z = Math.Min(availW / (g.Width * g.UnitToDip), availH / (g.Height * g.UnitToDip));
            if (_session is { IsPdf: false }) z = Math.Min(z, 1); // small images show at actual size
        }
        SetZoomCore(Math.Clamp(z, ZoomSteps[0], ZoomSteps[^1]), null);
    }

    public void SetFit(FitMode mode)
    {
        Fit = mode;
        ApplyFit();
        ZoomChanged?.Invoke();
    }

    public void ZoomIn() => SetZoom(ZoomSteps.FirstOrDefault(z => z > Zoom * 1.01, ZoomSteps[^1]));
    public void ZoomOut() => SetZoom(ZoomSteps.LastOrDefault(z => z < Zoom * 0.99, ZoomSteps[0]));
    public void ZoomToActualSize() => SetZoom(1);

    /// <param name="anchor">Viewport point to keep fixed; defaults to the viewport center.</param>
    public void SetZoom(double zoom, Point? anchor = null)
    {
        Fit = FitMode.None;
        SetZoomCore(Math.Clamp(zoom, ZoomSteps[0], ZoomSteps[^1]), anchor);
    }

    private void SetZoomCore(double zoom, Point? anchor)
    {
        if (Math.Abs(zoom - Zoom) < 1e-4 && _pages.All(p => Math.Abs(p.Zoom - zoom) < 1e-6)) return;
        var a = anchor ?? new Point(_scroll.ViewportWidth / 2, _scroll.ViewportHeight / 2);
        double contentX = _scroll.HorizontalOffset + a.X - PagePadding, contentY = _scroll.VerticalOffset + a.Y - PagePadding;
        double ratio = zoom / Zoom;
        Zoom = zoom;
        foreach (var p in _pages) p.SetGeometry(p.Geometry, zoom);
        _content.UpdateLayout();
        _scroll.ChangeView(Math.Max(0, contentX * ratio + PagePadding - a.X), Math.Max(0, contentY * ratio + PagePadding - a.Y), null, true);
        RedrawAllOverlays();
        ScheduleRender();
        ZoomChanged?.Invoke();
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        var mods = e.KeyModifiers;
        if ((mods & Windows.System.VirtualKeyModifiers.Control) == 0) return;
        var pt = e.GetCurrentPoint(_scroll);
        double factor = Math.Pow(1.0015, pt.Properties.MouseWheelDelta);
        SetZoom(Zoom * factor, pt.Position);
        e.Handled = true;
    }

    // ---------------- Rendering ----------------

    public void ScheduleRender()
    {
        if (_rendering) { _renderAgain = true; return; }
        if (_renderScheduled) return;
        _renderScheduled = true;
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            _renderScheduled = false;
            await RenderVisibleAsync();
        });
    }

    private (double Top, double Bottom) PageSpan(PageView pv)
    {
        double top = pv.ActualOffset.Y + _stack.ActualOffset.Y;
        return (top, top + pv.Height);
    }

    private async Task RenderVisibleAsync()
    {
        if (_session == null || _pages.Count == 0) return;
        _rendering = true;
        try
        {
            do
            {
                _renderAgain = false;
                double vTop = _scroll.VerticalOffset, vh = Math.Max(1, _scroll.ViewportHeight), vBottom = vTop + vh;
                double center = (vTop + vBottom) / 2;
                var order = _pages
                    .Select(p => (p, span: PageSpan(p)))
                    .OrderBy(t => Math.Max(0, Math.Max(t.span.Top - center, center - t.span.Bottom)))
                    .ToList();
                foreach (var (p, span) in order)
                {
                    bool near = span.Bottom >= vTop - vh && span.Top <= vBottom + vh;
                    bool far = span.Bottom < vTop - 4 * vh || span.Top > vBottom + 4 * vh;
                    if (far) { p.ReleaseBitmap(); continue; }
                    if (near && p.NeedsRender(RasterScale))
                    {
                        if (_session == null) return;
                        await p.RenderAsync(_session, RasterScale);
                    }
                }
            } while (_renderAgain);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Render failed: {ex}");
        }
        finally { _rendering = false; }
    }

    // ---------------- Navigation ----------------

    private void UpdateCurrentPage()
    {
        if (_pages.Count == 0) return;
        double center = _scroll.VerticalOffset + _scroll.ViewportHeight / 3;
        int best = 0;
        for (int i = 0; i < _pages.Count; i++)
        {
            var (top, _) = PageSpan(_pages[i]);
            if (top <= center) best = i; else break;
        }
        if (best == _currentPage) return;
        _currentPage = best;
        CurrentPageChanged?.Invoke(best);
    }

    public void GoToPage(int index)
    {
        if (index < 0 || index >= _pages.Count) return;
        var (top, _) = PageSpan(_pages[index]);
        _scroll.ChangeView(null, Math.Max(0, top - PageSpacing), null, false);
        _currentPage = index;
        CurrentPageChanged?.Invoke(index);
    }

    public void NextPage() => GoToPage(Math.Min(_pages.Count - 1, _currentPage + 1));
    public void PreviousPage() => GoToPage(Math.Max(0, _currentPage - 1));

    /// <summary>Scrolls so a user-space rect on a page is visible, roughly a third of the way down.</summary>
    public void RevealRect(int page, RectD userRect)
    {
        if (page < 0 || page >= _pages.Count) return;
        var pv = _pages[page];
        var r = pv.Mapping.ToDip(userRect);
        var (top, _) = PageSpan(pv);
        double left = pv.ActualOffset.X + _stack.ActualOffset.X;
        double y = top + r.Y - _scroll.ViewportHeight / 3;
        double? x = r.Right + left > _scroll.HorizontalOffset + _scroll.ViewportWidth || r.X + left < _scroll.HorizontalOffset
            ? Math.Max(0, left + r.X - 40) : null;
        _scroll.ChangeView(x, Math.Max(0, y), null, false);
    }

    /// <summary>The visible part of a page, in display units, for placing new content.</summary>
    public RectD VisibleDisplayRect(int page)
    {
        var pv = _pages[page];
        var (top, _) = PageSpan(pv);
        double scale = pv.Mapping.Scale;
        double y1 = Math.Max(0, _scroll.VerticalOffset - top), y2 = Math.Min(pv.Height, _scroll.VerticalOffset + _scroll.ViewportHeight - top);
        if (y2 <= y1) { y1 = 0; y2 = pv.Height; }
        return new RectD(0, y1 / scale, pv.Width / scale, y2 / scale);
    }

    public void FocusSurface() => _scroll.Focus(FocusState.Programmatic);

    /// <summary>Zoom and scroll position, kept per tab so switching back restores where you were.</summary>
    public sealed record ViewState(double Zoom, FitMode Fit, double HorizontalOffset, double VerticalOffset);

    public ViewState CaptureState() => new(Zoom, Fit, _scroll.HorizontalOffset, _scroll.VerticalOffset);

    /// <summary>Call after <see cref="Attach"/> while the view is visible.</summary>
    public void RestoreState(ViewState state)
    {
        Fit = state.Fit;
        if (state.Fit == FitMode.None) SetZoomCore(Math.Clamp(state.Zoom, ZoomSteps[0], ZoomSteps[^1]), null);
        else ApplyFit();
        ZoomChanged?.Invoke();
        // Scroll once the new layout has been measured.
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _content.UpdateLayout();
            _scroll.ChangeView(state.HorizontalOffset, state.VerticalOffset, null, true);
            ScheduleRender();
        });
    }
}
