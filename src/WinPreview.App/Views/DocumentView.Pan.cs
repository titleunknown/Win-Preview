using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace WinPreview.Views;

/// <summary>The Zoom tool (Z): pan by dragging when zoomed in, double-click to jump between actual size and fit.</summary>
public sealed partial class DocumentView
{
    private Point _panStartPoint;
    private Point _panStartOffset;
    private PageView? _hoverPage;
    private Point _hoverDip;

    /// <summary>Remembers where the pointer is so the cursor can be re-chosen when something changes without the mouse moving.</summary>
    private void RememberHover(PageView pv, Point dip)
    {
        _hoverPage = pv;
        _hoverDip = dip;
    }

    /// <summary>Re-applies the right cursor after a zoom or tool change (the mouse hasn't moved, so no pointer event will).</summary>
    private void RefreshHoverCursor()
    {
        if (_hoverPage is { } pv && _drag == DragKind.None && _pages.Contains(pv)) UpdateHoverCursor(pv, _hoverDip);
    }

    /// <summary>True when zoomed to exactly 100% (one picture pixel per DIP, or a PDF at its nominal size).</summary>
    private bool IsActualSize => Fit == FitMode.None && Math.Abs(Zoom - 1) < 0.01;

    /// <summary>Whether this page is bigger than the window in either direction, so there is something to drag around.</summary>
    private bool CanPan(PageView pv) =>
        pv.Width > _scroll.ViewportWidth - 2 * PagePadding + 2 || pv.Height > _scroll.ViewportHeight - 2 * PagePadding + 2;

    private void BeginPan(PageView pv, PointerRoutedEventArgs e)
    {
        _panStartPoint = e.GetCurrentPoint(_scroll).Position;
        _panStartOffset = new Point(_scroll.HorizontalOffset, _scroll.VerticalOffset);
        BeginDrag(pv, e, DragKind.Pan);
        pv.SetCursor(AppCursors.HandClosed);
    }

    /// <summary>Double-click in the Zoom tool: actual size around the clicked point, or back to fit if already there.</summary>
    /// <param name="anchor">The clicked point in viewport coordinates, kept under the pointer while zooming in.</param>
    private void ToggleActualSize(Point anchor)
    {
        if (_session == null) return;
        if (IsActualSize) SetFit(_session.IsPdf ? FitMode.Width : FitMode.Page);
        else SetZoom(1, anchor);
    }
}
