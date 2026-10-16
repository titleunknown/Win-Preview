using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using WinPreview.Core;
using WinPreview.Core.Markup;
using WinPreview.Core.Pdf;

namespace WinPreview.Views;

public sealed partial class DocumentView
{
    private enum DragKind { None, TextSelect, Move, Resize, Ink, Shape, RectSelect, Form, Alpha, CropMove, CropResize }

    private DragKind _drag;
    private int _dragPage, _dragHandle, _textAnchor = -1, _formPage = -1;
    private Point _dragStartDip;
    private PointD _dragLastUser;
    private MarkupItem? _dragBefore;
    private bool _dragMoved;
    private List<PointD>? _liveStroke;
    private FrameworkElement? _livePreview;

    /// <summary>Raised when the user asks for the context menu; the window fills in page-level commands.</summary>
    public event Action<MenuFlyout, int>? ContextMenuOpening;

    private void HookPage(PageView pv)
    {
        pv.PointerPressed += OnPagePointerPressed;
        pv.PointerMoved += OnPagePointerMoved;
        pv.PointerReleased += OnPagePointerReleased;
        pv.PointerCaptureLost += (_, _) => { if (_drag != DragKind.None) EndDrag(null); };
        pv.DoubleTapped += OnPageDoubleTapped;
        pv.RightTapped += OnPageRightTapped;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static int PdfModifiers()
    {
        int m = 0;
        if (IsDown(VirtualKey.Shift)) m |= 1;
        if (IsDown(VirtualKey.Control)) m |= 2;
        if (IsDown(VirtualKey.Menu)) m |= 4;
        return m;
    }

    private bool IsInsideEditor(object? source)
    {
        if (_editing == null || source is not DependencyObject d) return false;
        for (; d != null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, _editing.Box)) return true;
        return false;
    }

    // ---------------- Pointer ----------------

    private void OnPagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session == null || IsInsideEditor(e.OriginalSource) || IsInsideButton(e.OriginalSource)) return;
        var pv = (PageView)sender;
        var pt = e.GetCurrentPoint(pv);
        if (!pt.Properties.IsLeftButtonPressed) return;
        CommitInlineEditor();

        int page = pv.Index;
        var map = pv.Mapping;
        var dip = pt.Position;
        var user = map.ToUser(dip);
        var pdf = _session.Pdf;
        Log.Write($"press page={page} dip={dip} user={user} tool={Tool}");
        _dragPage = page;
        _dragStartDip = dip;
        _dragLastUser = user;
        _dragMoved = false;
        e.Handled = true;

        switch (Tool)
        {
            case Tool.Select:
            {
                int h = HitHandle(page, dip);
                if (h >= 0 && SelectedItem is { } selectedItem)
                {
                    BeginDrag(pv, e, DragKind.Resize, selectedItem.Clone(), h);
                    return;
                }
                var hit = MarkupHitTest.Find(_session.Pages[page].Markups, user, map.UserLength(5));
                if (hit != null)
                {
                    KillFormFocus();
                    SelectMarkup(page, hit.Id);
                    BeginDrag(pv, e, DragKind.Move, hit.Clone());
                    return;
                }
                ClearMarkupSelection();
                if (pdf != null && pdf.FormFieldTypeAt(page, user) >= 0)
                {
                    ClearTextSelection();
                    _formPage = page;
                    FocusSurface();
                    pdf.FormMouseDown(page, user, PdfModifiers());
                    BeginDrag(pv, e, DragKind.Form);
                    return;
                }
                KillFormFocus();
                FocusSurface();
                // On images, pressing on recognized text selects text (Live Text); elsewhere it draws a selection rectangle.
                if (pdf != null || _session.GetTextLayer(page)?.GetCharIndexAt(user, map.UserLength(2)) >= 0)
                    BeginTextSelect(pv, e, user);
                else
                {
                    ClearTextSelection();
                    if (!TryBeginCropEdit(pv, e, dip)) { ClearRectSelection(); BeginDrag(pv, e, DragKind.RectSelect); }
                }
                return;
            }
            case Tool.Highlight:
                FocusSurface();
                if (pdf != null) BeginTextSelect(pv, e, user);
                return;
            case Tool.InstantAlpha when !_session.IsPdf:
                FocusSurface();
                ClearMarkupSelection();
                BeginInstantAlpha(pv, dip);
                BeginDrag(pv, e, DragKind.Alpha);
                return;
            case Tool.RectSelect:
                FocusSurface();
                if (!TryBeginCropEdit(pv, e, dip)) { ClearRectSelection(); BeginDrag(pv, e, DragKind.RectSelect); }
                return;
            case Tool.Draw:
            {
                ClearMarkupSelection();
                _liveStroke = [user];
                var preview = new Polyline
                {
                    Stroke = CurrentStyle.Stroke.Brush(), StrokeThickness = CurrentStyle.StrokeWidth * StyleScale(page) * map.Scale,
                    StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    IsHitTestVisible = false, Points = { dip },
                };
                SetLivePreview(pv, preview);
                BeginDrag(pv, e, DragKind.Ink);
                return;
            }
            case Tool.Rectangle or Tool.Oval or Tool.Line or Tool.Arrow:
                ClearMarkupSelection();
                BeginDrag(pv, e, DragKind.Shape);
                return;
            case Tool.Text:
            {
                ClearMarkupSelection();
                double fontSize = CurrentStyle.FontSize * StyleScale(page);
                var draftRectDip = new Rect(dip.X, dip.Y - fontSize * map.Scale * 0.7, 60, (fontSize * 1.2 + 4) * map.Scale);
                var draft = new TextBoxMarkup
                {
                    Rect = map.ToUser(draftRectDip), FontSize = fontSize, Font = CurrentStyle.Font, Color = CurrentStyle.TextColor, Fill = CurrentStyle.Fill,
                };
                Tool = Tool.Select;
                BeginTextEdit(page, draft, isNew: true);
                return;
            }
            case Tool.Note:
            {
                double half = NoteMarkup.IconSize / 2;
                var note = new NoteMarkup { Rect = map.ToUser(new Rect(dip.X - half, dip.Y - half, 2 * half, 2 * half)) };
                _session.AddMarkup(page, note, "Add Note");
                Tool = Tool.Select;
                SelectMarkup(page, note.Id);
                DispatcherQueue.TryEnqueue(() => OpenNoteEditor(page, note));
                return;
            }
        }
    }

    private void BeginTextSelect(PageView pv, PointerRoutedEventArgs e, PointD user)
    {
        ClearTextSelection();
        _textAnchor = _session!.GetTextLayer(pv.Index)?.GetCharIndexAt(user, pv.Mapping.UserLength(3)) ?? -1;
        BeginDrag(pv, e, DragKind.TextSelect);
    }

    private void BeginDrag(PageView pv, PointerRoutedEventArgs e, DragKind kind, MarkupItem? before = null, int handle = -1)
    {
        _drag = kind;
        _dragBefore = before;
        _dragHandle = handle;
        pv.CapturePointer(e.Pointer);
    }

    private void SetLivePreview(PageView pv, FrameworkElement? element)
    {
        if (_livePreview != null) foreach (var p in _pages) p.AdornerLayer.Children.Remove(_livePreview);
        _livePreview = element;
        if (element != null) pv.AdornerLayer.Children.Add(element);
    }

    private void OnPagePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_session == null) return;
        var pv = (PageView)sender;
        if (_drag == DragKind.None)
        {
            UpdateHoverCursor(pv, e.GetCurrentPoint(pv).Position);
            return;
        }
        if (pv.Index != _dragPage) return;
        var map = pv.Mapping;
        var raw = e.GetCurrentPoint(pv).Position;
        var dip = _drag is DragKind.Move ? raw : pv.Clamp(raw);
        var user = map.ToUser(dip);
        if (!_dragMoved && Math.Abs(raw.X - _dragStartDip.X) + Math.Abs(raw.Y - _dragStartDip.Y) < 3) return;
        _dragMoved = true;
        var pdf = _session.Pdf;
        Log.Write($"move drag={_drag} dip={dip} anchor={_textAnchor}");

        switch (_drag)
        {
            case DragKind.TextSelect when _session.GetTextLayer(_dragPage) is { } layer:
            {
                int focus = layer.GetNearestCharIndex(user);
                if (_textAnchor < 0) _textAnchor = layer.GetNearestCharIndex(map.ToUser(_dragStartDip));
                if (focus >= 0 && _textAnchor >= 0) SetTextSelection(new TextSelection(_dragPage, _textAnchor, focus));
                break;
            }
            case DragKind.Move when SelectedItem is { } item:
                item.Offset(user.X - _dragLastUser.X, user.Y - _dragLastUser.Y);
                _dragLastUser = user;
                RedrawMarkups(_dragPage);
                RedrawAdorners();
                break;
            case DragKind.Resize when SelectedItem is { } item && _dragBefore != null:
                ApplyResize(pv, item, _dragBefore, _dragHandle, dip, IsDown(VirtualKey.Shift));
                RedrawMarkups(_dragPage);
                RedrawAdorners();
                break;
            case DragKind.Ink when _liveStroke != null && _livePreview is Polyline poly:
                _liveStroke.Add(user);
                poly.Points.Add(dip);
                break;
            case DragKind.Shape:
                SetLivePreview(pv, MarkupVisuals.Create(BuildShape(map, _dragStartDip, dip, IsDown(VirtualKey.Shift)), map));
                _livePreview!.IsHitTestVisible = false;
                break;
            case DragKind.RectSelect:
                SetCrop(pv, CropMath.NewSelection(new PointD(_dragStartDip.X, _dragStartDip.Y), new PointD(dip.X, dip.Y), PageBoundsDip(pv), CropRatioFor(pv)));
                break;
            case DragKind.CropMove:
                SetCrop(pv, CropMath.Move(_cropStart, dip.X - _dragStartDip.X, dip.Y - _dragStartDip.Y, PageBoundsDip(pv)));
                break;
            case DragKind.CropResize:
                SetCrop(pv, CropMath.Resize(_cropStart, _cropHandle, new PointD(dip.X, dip.Y), PageBoundsDip(pv), CropRatioFor(pv)));
                break;
            case DragKind.Form when pdf != null:
                pdf.FormMouseMove(_dragPage, user, PdfModifiers());
                break;
            case DragKind.Alpha:
                DragInstantAlpha(raw);
                break;
        }
    }

    private void OnPagePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == DragKind.None) return;
        var pv = (PageView)sender;
        EndDrag(pv.Index == _dragPage ? e.GetCurrentPoint(pv).Position : null);
        pv.ReleasePointerCaptures();
        e.Handled = true;
    }

    private void EndDrag(Point? releaseDip)
    {
        var kind = _drag;
        _drag = DragKind.None;
        Log.Write($"end drag={kind} moved={_dragMoved} release={releaseDip}");
        if (_session == null || _dragPage >= _pages.Count) return;
        var pv = _pages[_dragPage];
        var map = pv.Mapping;
        var dip = releaseDip.HasValue ? pv.Clamp(releaseDip.Value) : _dragStartDip;

        switch (kind)
        {
            case DragKind.TextSelect:
                if (Tool == Tool.Highlight && _textSel != null) MarkUpSelectedText(CurrentStyle.MarkupStyle);
                else if (!_dragMoved) ClearTextSelection();
                break;
            case DragKind.Move or DragKind.Resize when _dragBefore != null:
                if (_dragMoved) _session.CommitEdit(_dragPage, _dragBefore, kind == DragKind.Move ? "Move" : "Resize");
                else if (SelectedItem is NoteMarkup note) OpenNoteEditor(_dragPage, note);
                break;
            case DragKind.Ink when _liveStroke is { Count: > 0 }:
                _session.AddMarkup(_dragPage, new InkMarkup
                {
                    Color = CurrentStyle.Stroke, StrokeWidth = CurrentStyle.StrokeWidth * StyleScale(_dragPage), Strokes = [Simplify(_liveStroke, map.UserLength(0.4))],
                }, "Draw");
                break;
            case DragKind.Shape:
            {
                var shape = BuildShape(map, _dragStartDip, dip, IsDown(VirtualKey.Shift));
                _session.AddMarkup(_dragPage, shape, "Add Shape");
                Tool = Tool.Select;
                SelectMarkup(_dragPage, shape.Id);
                break;
            }
            case DragKind.RectSelect:
                if (!_dragMoved) ClearRectSelection();
                else SelectionChanged?.Invoke();
                break;
            case DragKind.Form when _session.Pdf is { } pdf:
                pdf.FormMouseUp(_dragPage, map.ToUser(dip), PdfModifiers());
                break;
            case DragKind.Alpha:
                EndInstantAlpha(apply: releaseDip.HasValue);
                break;
        }
        _liveStroke = null;
        _dragBefore = null;
        SetLivePreview(pv, null);
    }

    /// <summary>Drops points closer than <paramref name="minDist"/> to the previous kept point.</summary>
    private static List<PointD> Simplify(List<PointD> pts, double minDist)
    {
        var o = new List<PointD> { pts[0] };
        foreach (var p in pts.Skip(1))
            if ((p - o[^1]).Length >= minDist) o.Add(p);
        if (pts.Count > 1 && o[^1] != pts[^1]) o.Add(pts[^1]);
        return o;
    }

    private MarkupItem BuildShape(PageMapping map, Point a, Point b, bool constrain)
    {
        bool tiny = Math.Abs(a.X - b.X) < 4 && Math.Abs(a.Y - b.Y) < 4;
        double width = CurrentStyle.StrokeWidth * StyleScale(_dragPage);
        if (Tool is Tool.Line or Tool.Arrow)
        {
            if (tiny) b = new Point(a.X + 120, a.Y);
            else if (constrain)
            {
                // Snap to 45° increments.
                double ang = Math.Round(Math.Atan2(b.Y - a.Y, b.X - a.X) / (Math.PI / 4)) * (Math.PI / 4);
                double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                b = new Point(a.X + Math.Cos(ang) * len, a.Y + Math.Sin(ang) * len);
            }
            return new LineMarkup
            {
                Start = map.ToUser(a), End = map.ToUser(b), ArrowAtEnd = Tool == Tool.Arrow, Color = CurrentStyle.Stroke, StrokeWidth = width,
            };
        }
        if (tiny) b = new Point(a.X + 120, a.Y + 80);
        else if (constrain)
        {
            double side = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
            b = new Point(a.X + Math.Sign(b.X - a.X) * side, a.Y + Math.Sign(b.Y - a.Y) * side);
        }
        return new ShapeMarkup
        {
            Shape = Tool == Tool.Oval ? ShapeKind.Oval : ShapeKind.Rectangle,
            Rect = map.ToUser(new Rect(a, b)), Color = CurrentStyle.Stroke, StrokeWidth = width, Fill = CurrentStyle.Fill,
        };
    }

    private void ApplyResize(PageView pv, MarkupItem item, MarkupItem before, int handle, Point dip, bool constrain)
    {
        var map = pv.Mapping;
        switch (item)
        {
            case LineMarkup line:
                if (handle == 0) line.Start = map.ToUser(dip); else line.End = map.ToUser(dip);
                return;
            case ShapeMarkup or TextBoxMarkup:
            {
                var opposite = HandlePoints(pv, before)[(handle + 2) % 4];
                if (constrain && item is ShapeMarkup)
                {
                    double side = Math.Max(Math.Abs(dip.X - opposite.X), Math.Abs(dip.Y - opposite.Y));
                    dip = new Point(opposite.X + Math.Sign(dip.X - opposite.X) * side, opposite.Y + Math.Sign(dip.Y - opposite.Y) * side);
                }
                var r = map.ToUser(new Rect(opposite, dip));
                if (item is ShapeMarkup s) s.Rect = r; else ((TextBoxMarkup)item).Rect = r;
                return;
            }
            case InkMarkup ink when before is InkMarkup b:
            {
                var opposite = HandlePoints(pv, b)[(handle + 2) % 4];
                var oldDip = map.ToDip(RectD.Bounding(b.Strokes.SelectMany(s => s)));
                double sx = Math.Abs(dip.X - opposite.X) / Math.Max(1, oldDip.Width);
                double sy = Math.Abs(dip.Y - opposite.Y) / Math.Max(1, oldDip.Height);
                if (b.IsSignature || constrain || oldDip.Width < 2 || oldDip.Height < 2) sx = sy = Math.Max(sx, sy);
                sx = Math.Max(0.05, sx);
                sy = Math.Max(0.05, sy);
                ink.Strokes = b.Strokes.Select(s => s.Select(p =>
                {
                    var d = map.ToDip(p);
                    return map.ToUser(new Point(opposite.X + (d.X - opposite.X) * sx, opposite.Y + (d.Y - opposite.Y) * sy));
                }).ToList()).ToList();
                ink.StrokeWidth = b.StrokeWidth * Math.Sqrt(sx * sy);
                return;
            }
        }
    }

    private void UpdateHoverCursor(PageView pv, Point dip)
    {
        if (_session == null) return;
        if (UpdateCropCursor(pv, dip)) return;
        var shape = InputSystemCursorShape.Arrow;
        switch (Tool)
        {
            case Tool.Select or Tool.Highlight:
            {
                var user = pv.Mapping.ToUser(dip);
                var pdf = _session.Pdf;
                if (HitHandle(pv.Index, dip) >= 0) shape = InputSystemCursorShape.SizeNorthwestSoutheast;
                else if (Tool == Tool.Select && MarkupHitTest.Find(_session.Pages[pv.Index].Markups, user, pv.Mapping.UserLength(5)) != null)
                    shape = InputSystemCursorShape.SizeAll;
                else if (pdf != null && Tool == Tool.Select && pdf.FormFieldTypeAt(pv.Index, user) is int ft and >= 0)
                {
                    shape = ft == 6 ? InputSystemCursorShape.IBeam : InputSystemCursorShape.Hand; // 6 = text field
                    pdf.FormMouseMove(pv.Index, user, 0);
                }
                else if (_session.GetTextLayer(pv.Index)?.GetCharIndexAt(user, pv.Mapping.UserLength(2)) >= 0) shape = InputSystemCursorShape.IBeam;
                else if (pdf == null) shape = InputSystemCursorShape.Cross;
                break;
            }
            case Tool.Text: shape = InputSystemCursorShape.IBeam; break;
            case Tool.InstantAlpha: shape = InputSystemCursorShape.Cross; break;
            default: shape = InputSystemCursorShape.Cross; break;
        }
        pv.SetCursor(shape);
    }

    private void OnPageDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_session == null || Tool != Tool.Select || IsInsideEditor(e.OriginalSource)) return;
        var pv = (PageView)sender;
        var dip = e.GetPosition(pv);
        var user = pv.Mapping.ToUser(dip);
        var hit = MarkupHitTest.Find(_session.Pages[pv.Index].Markups, user, pv.Mapping.UserLength(5));
        if (hit is TextBoxMarkup tb)
        {
            BeginTextEdit(pv.Index, (TextBoxMarkup)tb.Clone(), isNew: false);
            e.Handled = true;
        }
        else if (hit is NoteMarkup note)
        {
            OpenNoteEditor(pv.Index, note);
            e.Handled = true;
        }
        else if (hit == null && (_session.Pdf?.FormFieldTypeAt(pv.Index, user) ?? -1) < 0)
        {
            SelectWordAt(pv.Index, user, pv.Mapping.UserLength(3));
            e.Handled = true;
        }
    }

    private void SelectWordAt(int page, PointD user, double tol)
    {
        if (_session?.GetTextLayer(page) is not { } layer) return;
        if (layer.WordAt(layer.GetCharIndexAt(user, tol)) is { } word)
            SetTextSelection(new TextSelection(page, word.Start, word.End));
    }

    private void OnPageRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_session == null || IsInsideEditor(e.OriginalSource)) return;
        var pv = (PageView)sender;
        var dip = e.GetPosition(pv);
        var user = pv.Mapping.ToUser(dip);
        var menu = new MenuFlyout();
        var hit = MarkupHitTest.Find(_session.Pages[pv.Index].Markups, user, pv.Mapping.UserLength(5));
        if (hit != null)
        {
            SelectMarkup(pv.Index, hit.Id);
            if (hit is TextBoxMarkup tb)
                menu.Items.Add(Item("Edit Text", "", () => BeginTextEdit(pv.Index, (TextBoxMarkup)tb.Clone(), false)));
            if (hit is NoteMarkup nm)
                menu.Items.Add(Item("Edit Note", "", () => OpenNoteEditor(pv.Index, nm)));
            if (hit is TextMarkup tm && tm.Text.Length > 0)
                menu.Items.Add(Item("Copy Text", "", () => CopyText(tm.Text)));
            menu.Items.Add(Item("Delete", "", DeleteSelection));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        else if (_textSel != null)
        {
            menu.Items.Add(Item("Copy", "", CopySelection));
            if (_session.IsPdf)
            {
                menu.Items.Add(Item("Highlight Text", "", () => MarkUpSelectedText(TextMarkupStyle.Highlight)));
                menu.Items.Add(Item("Underline Text", "", () => MarkUpSelectedText(TextMarkupStyle.Underline)));
                menu.Items.Add(Item("Strikethrough Text", "", () => MarkUpSelectedText(TextMarkupStyle.Strikeout)));
            }
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        ContextMenuOpening?.Invoke(menu, pv.Index);
        if (menu.Items.Count > 0 && menu.Items[^1] is MenuFlyoutSeparator) menu.Items.RemoveAt(menu.Items.Count - 1);
        if (menu.Items.Count > 0) menu.ShowAt(pv, dip);
        e.Handled = true;
    }

    public static MenuFlyoutItem Item(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    // ---------------- Commands ----------------

    public bool HasSelection => _selected != null || _textSel != null;

    public void DeleteSelection()
    {
        if (_session == null || _selected is not { } s) return;
        _selected = null;
        _session.RemoveMarkup(s.Page, s.Id);
    }

    public void CopySelection()
    {
        if (_formPage >= 0 && _session?.Pdf is { } pdf)
        {
            CopyText(pdf.FormSelectedText(_formPage));
            return;
        }
        if (_textSel != null) CopyText(SelectedText);
        else if (SelectedItem is TextBoxMarkup tb) CopyText(tb.Text);
        else if (SelectedItem is NoteMarkup n) CopyText(n.Text);
    }

    private static void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var dp = new DataPackage();
        dp.SetText(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        Clipboard.SetContent(dp);
    }

    /// <summary>Applies a style change to the selected markup (with undo). Returns false when nothing is selected.</summary>
    public bool ApplyToSelection(Action<MarkupItem> change, string name)
    {
        if (_session == null || _selected is not { } s) return false;
        _session.EditMarkup(s.Page, s.Id, change, name);
        return true;
    }

    public void KillFormFocus()
    {
        if (_formPage < 0) return;
        _session?.Pdf?.FormKillFocus();
        _formPage = -1;
    }

    /// <summary>Places a saved signature on the current page, centered in the visible area.</summary>
    public void InsertSignature(Core.Signatures.Signature sig)
    {
        if (_session == null || _pages.Count == 0) return;
        CommitInlineEditor();
        int page = _currentPage;
        var pv = _pages[page];
        var g = pv.Geometry;
        var visible = VisibleDisplayRect(page);
        double h = _session.IsPdf ? 40 : Math.Max(20, g.Height * 0.08);
        double w = h * sig.AspectRatio;
        if (w > g.Width * 0.8) { w = g.Width * 0.8; h = w / sig.AspectRatio; }
        var c = visible.Center;
        var ink = new InkMarkup
        {
            IsSignature = true, Color = RgbaColor.Black, StrokeWidth = Math.Max(0.6, h * 0.035),
            Strokes = sig.Strokes.Select(s => s.Select(p =>
                g.DisplayToUser.Apply(new PointD(c.X - w / 2 + p.X * h, c.Y - h / 2 + p.Y * h))).ToList()).ToList(),
        };
        _session.AddMarkup(page, ink, "Add Signature");
        Tool = Tool.Select;
        SelectMarkup(page, ink.Id);
    }

    // ---------------- Keyboard ----------------

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_session == null || _editing != null) return;
        if (e.OriginalSource is TextBox or PasswordBox or AutoSuggestBox) return;
        bool ctrl = IsDown(VirtualKey.Control);

        if (_formPage >= 0 && _session.Pdf is { } pdf)
        {
            // Ctrl+Shift combinations are window commands (e.g. Ctrl+Shift+A for the markup toolbar).
            if (ctrl && IsDown(VirtualKey.Shift)) return;
            switch (e.Key)
            {
                case VirtualKey.Escape:
                    KillFormFocus();
                    e.Handled = true;
                    return;
                case VirtualKey.C when ctrl:
                    CopySelection();
                    e.Handled = true;
                    return;
                case VirtualKey.X when ctrl:
                    CopySelection();
                    pdf.FormReplaceSelection(_formPage, "");
                    e.Handled = true;
                    return;
                case VirtualKey.V when ctrl:
                    _ = PasteIntoFormAsync(pdf, _formPage);
                    e.Handled = true;
                    return;
                case VirtualKey.A when ctrl:
                    pdf.FormSelectAll(_formPage);
                    e.Handled = true;
                    return;
                // Leave window-level shortcuts alone.
                case VirtualKey.S or VirtualKey.O or VirtualKey.P or VirtualKey.W or VirtualKey.F or VirtualKey.N when ctrl:
                    return;
                // Space must be swallowed so the scroller doesn't page down, and swallowing the key also
                // suppresses its character, so type it here.
                case VirtualKey.Space when !ctrl:
                    pdf.FormChar(_formPage, ' ', PdfModifiers());
                    e.Handled = true;
                    return;
            }
            pdf.FormKeyDown(_formPage, (int)e.Key, PdfModifiers());
            // Typing keys arrive via CharacterReceived; swallow navigation keys so the scroller doesn't move.
            e.Handled = e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End
                or VirtualKey.Back or VirtualKey.Delete or VirtualKey.Tab or VirtualKey.Enter or VirtualKey.PageUp or VirtualKey.PageDown
                || ctrl && e.Key is VirtualKey.Z or VirtualKey.Y;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Enter when _rectSel != null && !ctrl:
                CropRequested?.Invoke(false);
                e.Handled = true;
                break;
            case VirtualKey.Delete or VirtualKey.Back when _selected != null:
                DeleteSelection();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                ClearMarkupSelection();
                ClearTextSelection();
                ClearRectSelection();
                Tool = Tool.Select;
                e.Handled = true;
                break;
            case VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down when _selected is { } s && !ctrl:
            {
                double step = IsDown(VirtualKey.Shift) ? 10 : 1; // display units (points or pixels)
                var d = e.Key switch
                {
                    VirtualKey.Left => new PointD(-step, 0), VirtualKey.Right => new PointD(step, 0),
                    VirtualKey.Up => new PointD(0, -step), _ => new PointD(0, step),
                };
                // Nudge in display space so arrows match what's on screen regardless of rotation.
                var g = _pages[s.Page].Geometry;
                var u0 = g.DisplayToUser.Apply(new PointD(0, 0));
                var u1 = g.DisplayToUser.Apply(d);
                _session.EditMarkup(s.Page, s.Id, m => m.Offset(u1.X - u0.X, u1.Y - u0.Y), "Move");
                e.Handled = true;
                break;
            }
            case VirtualKey.Left or VirtualKey.Up when !_session.IsPdf && !ctrl:
                PreviousPage();
                e.Handled = true;
                break;
            case VirtualKey.Right or VirtualKey.Down when !_session.IsPdf && !ctrl:
                NextPage();
                e.Handled = true;
                break;
        }
    }

    private static async Task PasteIntoFormAsync(PdfDocument pdf, int page)
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text)) return;
        string text = await content.GetTextAsync();
        pdf.FormReplaceSelection(page, text);
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (_formPage < 0 || _editing != null || _session?.Pdf is not { } pdf) return;
        if (e.Character <= 0x20) return; // control characters and space are handled as key presses
        pdf.FormChar(_formPage, e.Character, PdfModifiers());
        e.Handled = true;
    }
}
