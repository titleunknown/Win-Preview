using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WinPreview.Core;
using WinPreview.Core.Markup;
using WinPreview.Core.Pdf;

namespace WinPreview.Views;

public sealed record TextSelection(int Page, int Anchor, int Focus)
{
    public int Start => Math.Min(Anchor, Focus);
    public int Count => Math.Abs(Focus - Anchor) + 1;
}

public sealed partial class DocumentView
{
    private static readonly SolidColorBrush SelectionBrush = new(Windows.UI.Color.FromArgb(80, 0, 120, 215));
    private static readonly SolidColorBrush HitBrush = new(Windows.UI.Color.FromArgb(110, 255, 214, 10));
    private static readonly SolidColorBrush CurrentHitBrush = new(Windows.UI.Color.FromArgb(150, 255, 149, 0));
    private static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(255, 0, 120, 215));

    private (int Page, Guid Id)? _selected;
    private TextSelection? _textSel;
    private (int Page, RectD Rect)? _rectSel;
    private IReadOnlyList<PdfSearchHit> _hits = [];
    private int _currentHit = -1;

    public (int Page, Guid Id)? SelectedMarkup => _selected;
    public TextSelection? TextSelectionRange => _textSel;
    public (int Page, RectD Rect)? RectSelection => _rectSel;

    public MarkupItem? SelectedItem => _selected is { } s ? _session?.FindMarkup(s.Page, s.Id) : null;

    private void RedrawAllOverlays()
    {
        for (int i = 0; i < _pages.Count; i++)
        {
            RedrawMarkups(i);
            RedrawHits(i);
        }
        RedrawTextSelection();
        RedrawAdorners();
    }

    private void RedrawMarkups(int page)
    {
        var pv = _pages[page];
        pv.MarkupLayer.Children.Clear();
        if (_session == null) return;
        var map = pv.Mapping;
        foreach (var item in _session.Pages[page].Markups)
        {
            var visual = MarkupVisuals.Create(item, map);
            if (_editing is { } ed && ed.Page == page && ed.Id == item.Id) visual.Opacity = 0;
            pv.MarkupLayer.Children.Add(visual);
        }
    }

    // ---------------- Markup selection & adorners ----------------

    public void SelectMarkup(int page, Guid id)
    {
        ClearTextSelection();
        _selected = (page, id);
        RedrawAdorners();
        SelectionChanged?.Invoke();
    }

    public void ClearMarkupSelection()
    {
        if (_selected == null) return;
        _selected = null;
        RedrawAdorners();
        SelectionChanged?.Invoke();
    }

    private const double HandleSize = 9;

    /// <summary>Handle positions (DIP) for the selected markup: corners for boxes, endpoints for lines.</summary>
    private List<Point> HandlePoints(PageView pv, MarkupItem item)
    {
        var map = pv.Mapping;
        switch (item)
        {
            case LineMarkup l:
                return [map.ToDip(l.Start), map.ToDip(l.End)];
            case ShapeMarkup or TextBoxMarkup or InkMarkup:
                var r = map.ToDip(item is InkMarkup ink ? RectD.Bounding(ink.Strokes.SelectMany(s => s)) : item.Bounds);
                return [new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom)];
            default:
                return [];
        }
    }

    private void RedrawAdorners()
    {
        foreach (var pv in _pages)
            for (int i = pv.AdornerLayer.Children.Count - 1; i >= 0; i--)
                if (pv.AdornerLayer.Children[i] is FrameworkElement { Tag: "adorner" })
                    pv.AdornerLayer.Children.RemoveAt(i);

        if (_rectSel is { } rs && rs.Page < _pages.Count)
        {
            var pv = _pages[rs.Page];
            var r = pv.Mapping.ToDip(rs.Rect);
            AddAdorner(pv, new Rectangle
            {
                Width = r.Width, Height = r.Height, Stroke = AccentBrush, StrokeThickness = 1.5,
                StrokeDashArray = [4, 3], Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 0, 120, 215)),
            }, r.X, r.Y);
        }

        if (_selected is not { } sel || sel.Page >= _pages.Count || _session == null) return;
        var item = _session.FindMarkup(sel.Page, sel.Id);
        if (item == null || (_editing is { } ed && ed.Id == sel.Id)) return;
        var page = _pages[sel.Page];
        var bounds = page.Mapping.ToDip(item.Bounds);
        if (item is not LineMarkup)
            AddAdorner(page, new Rectangle
            {
                Width = bounds.Width + 6, Height = bounds.Height + 6, Stroke = AccentBrush, StrokeThickness = 1,
                StrokeDashArray = [3, 2],
            }, bounds.X - 3, bounds.Y - 3);
        foreach (var hp in HandlePoints(page, item))
            AddAdorner(page, new Ellipse
            {
                Width = HandleSize, Height = HandleSize, Fill = new SolidColorBrush(Colors.White), Stroke = AccentBrush, StrokeThickness = 1.5,
            }, hp.X - HandleSize / 2, hp.Y - HandleSize / 2);
    }

    private static void AddAdorner(PageView pv, FrameworkElement e, double x, double y)
    {
        e.Tag = "adorner";
        e.IsHitTestVisible = false;
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        pv.AdornerLayer.Children.Add(e);
    }

    /// <summary>Index of the selected markup's handle under a DIP point on <paramref name="page"/>, or -1.</summary>
    private int HitHandle(int page, Point dip)
    {
        if (_selected is not { } sel || sel.Page != page || _session == null) return -1;
        var item = _session.FindMarkup(page, sel.Id);
        if (item == null) return -1;
        var pts = HandlePoints(_pages[page], item);
        for (int i = 0; i < pts.Count; i++)
            if (Math.Abs(pts[i].X - dip.X) <= HandleSize && Math.Abs(pts[i].Y - dip.Y) <= HandleSize) return i;
        return -1;
    }

    // ---------------- Text selection ----------------

    private void SetTextSelection(TextSelection? sel)
    {
        _textSel = sel;
        RedrawTextSelection();
        SelectionChanged?.Invoke();
    }

    public void ClearTextSelection()
    {
        if (_textSel == null) return;
        SetTextSelection(null);
    }

    public void ClearRectSelection()
    {
        if (_rectSel == null) return;
        _rectSel = null;
        RedrawAdorners();
        SelectionChanged?.Invoke();
    }

    private void RedrawTextSelection()
    {
        foreach (var pv in _pages) pv.SelectionLayer.Children.Clear();
        if (_textSel is not { } t || t.Page >= _pages.Count || _session?.GetTextLayer(t.Page) is not { } layer) return;
        _pages[t.Page].ShowRects(_pages[t.Page].SelectionLayer, layer.GetTextRects(t.Start, t.Count), SelectionBrush);
    }

    public string SelectedText =>
        _textSel is { } t && _session?.GetTextLayer(t.Page) is { } layer ? layer.GetText(t.Start, t.Count) : "";

    public void SelectAllText()
    {
        if (_pages.Count == 0 || _session?.GetTextLayer(_currentPage) is not { } layer) return;
        int n = layer.CharCount;
        if (n > 0) SetTextSelection(new TextSelection(_currentPage, 0, n - 1));
    }

    /// <summary>Turns the current text selection into a highlight/underline/strikeout markup (PDFs only, as in Preview).</summary>
    public void MarkUpSelectedText(TextMarkupStyle style)
    {
        if (_textSel is not { } t || _session?.Pdf is not { } pdf) return;
        var rects = pdf.GetTextRects(t.Page, t.Start, t.Count);
        if (rects.Count == 0) return;
        // Like Preview: highlights take the chosen highlight color; underline and strikethrough are red.
        var color = style == TextMarkupStyle.Highlight ? CurrentStyle.HighlightColor : RgbaColor.Red;
        var tm = new TextMarkup { Style = style, Rects = rects, Text = pdf.GetText(t.Page, t.Start, t.Count), Color = color };
        _session.AddMarkup(t.Page, tm, style switch
        {
            TextMarkupStyle.Underline => "Underline",
            TextMarkupStyle.Strikeout => "Strikethrough",
            _ => "Highlight",
        });
        ClearTextSelection();
    }

    // ---------------- Search hits ----------------

    public void SetSearchHits(IReadOnlyList<PdfSearchHit> hits, int current)
    {
        _hits = hits;
        _currentHit = current;
        for (int i = 0; i < _pages.Count; i++) RedrawHits(i);
        if (current >= 0 && current < hits.Count) RevealRect(hits[current].PageIndex, hits[current].Rects.FirstOrDefault());
    }

    private void RedrawHits(int page)
    {
        var pv = _pages[page];
        pv.HitLayer.Children.Clear();
        for (int i = 0; i < _hits.Count; i++)
            if (_hits[i].PageIndex == page)
                pv.ShowRects(pv.HitLayer, _hits[i].Rects.Select(r => r.Inflate(1)), i == _currentHit ? CurrentHitBrush : HitBrush);
    }

    // ---------------- Inline text box editor ----------------

    private sealed record EditState(int Page, Guid Id, TextBox Box, TextBoxMarkup Draft, bool IsNew);
    private EditState? _editing;

    public bool IsEditingText => _editing != null;

    /// <summary>Starts editing a text box markup in place; <paramref name="isNew"/> means it isn't in the document yet.</summary>
    private void BeginTextEdit(int page, TextBoxMarkup draft, bool isNew)
    {
        CommitInlineEditor();
        var pv = _pages[page];
        var map = pv.Mapping;
        var r = map.ToDip(draft.Rect);
        var box = new TextBox
        {
            Text = draft.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = MarkupVisuals.FontFor(draft.Font), FontSize = Math.Max(4, draft.FontSize * map.Scale),
            Foreground = draft.Color.Brush(),
            // Clear unless the user chose a fill, so the page shows through while typing (as it will once saved).
            Background = draft.Fill is { } f ? f.Brush() : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = AccentBrush, BorderThickness = new Thickness(1),
            Padding = new Thickness(2 * map.Scale), MinWidth = 20, MinHeight = 0, Width = Math.Max(30, r.Width),
            CornerRadius = new CornerRadius(0),
        };
        // WinUI paints its own fills on hover/focus; keep every state matching the box.
        foreach (var state in new[] { "", "PointerOver", "Focused", "Disabled" })
        {
            box.Resources[$"TextControlBackground{state}"] = box.Background;
            box.Resources[$"TextControlForeground{state}"] = box.Foreground;
            box.Resources[$"TextControlBorderBrush{state}"] = AccentBrush;
        }
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(1);

        // Like Preview, the box widens as you type until it reaches the page edge, then wraps.
        double minWidth = box.Width, maxWidth = Math.Max(minWidth, pv.Width - r.X - 4);
        var measurer = new TextBlock { FontFamily = box.FontFamily, FontSize = box.FontSize };
        void Fit()
        {
            double widest = 0;
            foreach (var line in box.Text.Split('\r', '\n'))
            {
                measurer.Text = line.Length == 0 ? " " : line;
                measurer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                widest = Math.Max(widest, measurer.DesiredSize.Width);
            }
            box.Width = Math.Clamp(widest + box.Padding.Left + box.Padding.Right + 14, minWidth, maxWidth);
        }
        box.TextChanged += (_, _) => Fit();
        Fit();
        Canvas.SetLeft(box, r.X);
        Canvas.SetTop(box, r.Y);
        pv.AdornerLayer.Children.Add(box);
        _editing = new EditState(page, draft.Id, box, draft, isNew);
        RedrawMarkups(page);
        RedrawAdorners();

        box.LostFocus += (_, _) => CommitInlineEditor();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                CommitInlineEditor();
                FocusSurface();
            }
        };
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectionStart = box.Text.Length;
        };
    }

    /// <summary>Finishes any in-place text edit, writing the result to the document.</summary>
    public void CommitInlineEditor()
    {
        if (_editing is not { } ed || _session == null) return;
        _editing = null;
        var pv = ed.Page < _pages.Count ? _pages[ed.Page] : null;
        string text = ed.Box.Text.Replace("\r", "\n");
        double heightDip = ed.Box.ActualHeight, widthDip = ed.Box.ActualWidth;
        pv?.AdornerLayer.Children.Remove(ed.Box);

        // Text boxes grow downward (in display space) to fit their text.
        var rect = ed.Draft.Rect;
        if (pv != null)
        {
            var map = pv.Mapping;
            var r = map.ToDip(rect);
            if (heightDip > r.Height || widthDip > r.Width)
                rect = map.ToUser(new Rect(r.X, r.Y, Math.Max(r.Width, widthDip), Math.Max(r.Height, heightDip)));
        }

        if (ed.IsNew)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                ed.Draft.Text = text;
                ed.Draft.Rect = rect;
                _session.AddMarkup(ed.Page, ed.Draft, "Add Text");
                _selected = (ed.Page, ed.Draft.Id);
            }
        }
        else if (string.IsNullOrWhiteSpace(text))
        {
            _session.RemoveMarkup(ed.Page, ed.Id);
        }
        else if (_session.FindMarkup(ed.Page, ed.Id) is TextBoxMarkup current && (current.Text != text || current.Rect != rect))
        {
            _session.EditMarkup(ed.Page, ed.Id, m => { ((TextBoxMarkup)m).Text = text; ((TextBoxMarkup)m).Rect = rect; }, "Edit Text");
        }
        if (ed.Page < _pages.Count) RedrawMarkups(ed.Page);
        RedrawAdorners();
        SelectionChanged?.Invoke();
    }

    // ---------------- Note editor ----------------

    private void OpenNoteEditor(int page, NoteMarkup note)
    {
        if (_session == null) return;
        var pv = _pages[page];
        var anchor = pv.MarkupLayer.Children.OfType<FrameworkElement>().FirstOrDefault(e => e.Tag is Guid g && g == note.Id);
        var box = new TextBox
        {
            Text = note.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Width = 240, Height = 140,
            PlaceholderText = "Type a note", Background = note.Color.Brush(0.25),
        };
        var flyout = new Flyout { Content = box, Placement = FlyoutPlacementMode.RightEdgeAlignedTop };
        flyout.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        flyout.Closed += (_, _) =>
        {
            string text = box.Text.Replace("\r", "\n");
            if (_session?.FindMarkup(page, note.Id) is NoteMarkup n && n.Text != text)
                _session.EditMarkup(page, note.Id, m => ((NoteMarkup)m).Text = text, "Edit Note");
        };
        if (anchor != null) flyout.ShowAt(anchor);
        else flyout.ShowAt(pv, new FlyoutShowOptions { Position = pv.Mapping.ToDip(note.Rect.Center) });
    }
}
