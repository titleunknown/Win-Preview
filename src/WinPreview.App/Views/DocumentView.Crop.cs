using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WinPreview.Core;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace WinPreview.Views;

/// <summary>
/// The crop selection: an adjustable rectangle with handles, a dimmed surround, a thirds grid, a size readout and a
/// floating Crop button. Used by the rectangular selection tool on images and PDF pages.
/// </summary>
public sealed partial class DocumentView
{
    private const double CropHitRadius = 10;
    private CropHandle _cropHandle;
    private RectD _cropStart; // the selection (in DIPs) when a move or resize drag began
    private double? _cropAspect;

    /// <summary>The Crop button, Enter, or "all pages" was chosen. The argument says whether to crop every page.</summary>
    public event Action<bool>? CropRequested;

    /// <summary>Width ÷ height to lock the selection to; null is free, a negative value means "same as the page".</summary>
    public double? CropAspect
    {
        get => _cropAspect;
        set
        {
            _cropAspect = value;
            UpdateCropBar();
            if (_rectSel is { } sel && sel.Page < _pages.Count && CropRatioFor(_pages[sel.Page]) is { } k)
            {
                // Re-fit the existing selection around its center.
                var pv = _pages[sel.Page];
                var r = DipRect(pv, sel.Rect);
                double w = Math.Min(r.Width, r.Height * k), h = w / k;
                SetCrop(pv, new RectD(r.Center.X - w / 2, r.Center.Y - h / 2, r.Center.X + w / 2, r.Center.Y + h / 2));
            }
        }
    }

    /// <summary>The aspect ratios offered for cropping: label and width ÷ height (null = free, negative = same as the page).</summary>
    private static readonly (string Name, double? Value)[] CropAspects =
    [
        ("Free", null), ("Same as page", -1), ("1:1", 1),
        ("4:5", 4.0 / 5), ("5:4", 5.0 / 4), ("2:3", 2.0 / 3), ("3:2", 3.0 / 2),
        ("3:4", 3.0 / 4), ("4:3", 4.0 / 3), ("9:16", 9.0 / 16), ("16:9", 16.0 / 9),
    ];

    private Border? _cropBar;
    private TextBlock? _cropBarLabel;
    private Button? _cropSwap;

    private static string CropAspectName(double? v) =>
        CropAspects.FirstOrDefault(a => a.Value == v || a.Value is { } x && v is { } y && Math.Abs(x - y) < 1e-9).Name ?? "Custom";

    /// <summary>Builds the floating "Aspect ratio" bar shown while cropping (created on first use).</summary>
    private void EnsureCropBar()
    {
        if (_cropBar != null) return;
        _cropBarLabel = new TextBlock { FontSize = 12 };
        var picker = new DropDownButton
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { new TextBlock { Text = "Aspect ratio", FontSize = 12, Opacity = 0.7 }, _cropBarLabel },
            },
            Padding = new Thickness(10, 4, 8, 4),
        };
        ToolTipService.SetToolTip(picker, "Lock the crop to a shape, or crop freely");
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            foreach (var (name, value) in CropAspects)
            {
                if (name == "4:5") menu.Items.Add(new MenuFlyoutSeparator());
                var item = new RadioMenuFlyoutItem
                {
                    Text = name == "Same as page" ? "Same as page" : name, GroupName = "crop-aspect",
                    IsChecked = CropAspectName(_cropAspect) == name,
                };
                item.Click += (_, _) => { CropAspect = value; FocusSurface(); };
                menu.Items.Add(item);
            }
        };
        picker.Flyout = menu;

        _cropSwap = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(8, 5, 8, 5) };
        ToolTipService.SetToolTip(_cropSwap, "Swap portrait and landscape");
        _cropSwap.Click += (_, _) => { SwapCropOrientation(); FocusSurface(); };

        _cropBar = new Border
        {
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { picker, _cropSwap } },
            Padding = new Thickness(6), CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0),
            Background = (Brush)Application.Current.Resources["AcrylicBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["SurfaceStrokeColorDefaultBrush"], BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
        };
        Children.Add(_cropBar);
    }

    /// <summary>Shows the aspect bar while the crop tool is active or a crop selection exists, and keeps it in sync.</summary>
    private void UpdateCropBar()
    {
        bool show = _session != null && (Tool == Tool.RectSelect || _rectSel != null);
        if (!show && _cropBar == null) return;
        EnsureCropBar();
        _cropBar!.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        _cropBarLabel!.Text = CropAspectName(_cropAspect);
        _cropSwap!.IsEnabled = _cropAspect is { } k && Math.Abs(k) > 0 && (k < 0 || Math.Abs(k - 1) > 1e-9);
    }

    /// <summary>Turns a landscape lock into portrait and vice versa (4:5 ⇄ 5:4).</summary>
    private void SwapCropOrientation()
    {
        if (_cropAspect is not { } k) return;
        if (k < 0)
        {
            var sel = _rectSel is { } s && s.Page < _pages.Count ? _pages[s.Page] : _pages.FirstOrDefault();
            if (sel == null) return;
            k = sel.Width / Math.Max(1e-9, sel.Height);
        }
        CropAspect = 1 / k;
    }

    /// <summary>True while keystrokes belong to the document: a PDF form field or a text box being edited.</summary>
    public bool IsTypingIntoDocument => _formPage >= 0 || _editing != null;

    private double? CropRatioFor(PageView pv) => CropMath.ResolveRatio(_cropAspect, pv.Width, pv.Height);

    private static RectD PageBoundsDip(PageView pv) => new(0, 0, pv.Width, pv.Height);

    private static RectD ToRectD(Rect r) => new(r.X, r.Y, r.Right, r.Bottom);

    private static Rect ToRect(RectD r) => new(r.X1, r.Y1, r.Width, r.Height);

    private static RectD DipRect(PageView pv, RectD user) => ToRectD(pv.Mapping.ToDip(user));

    private void SetCrop(PageView pv, RectD dipRect)
    {
        _rectSel = (pv.Index, pv.Mapping.ToUser(ToRect(dipRect)));
        RedrawAdorners();
        SelectionChanged?.Invoke();
    }

    /// <summary>Starts moving or resizing the existing selection when the press lands on it.</summary>
    private bool TryBeginCropEdit(PageView pv, PointerRoutedEventArgs e, Point dip)
    {
        if (_rectSel is not { } sel || sel.Page != pv.Index) return false;
        var r = DipRect(pv, sel.Rect);
        var p = new PointD(dip.X, dip.Y);
        if (CropMath.HitHandle(r, p, CropHitRadius) is { } handle)
        {
            _cropHandle = handle;
            _cropStart = r;
            BeginDrag(pv, e, DragKind.CropResize);
            return true;
        }
        if (r.Contains(p))
        {
            _cropStart = r;
            BeginDrag(pv, e, DragKind.CropMove);
            return true;
        }
        return false;
    }

    private bool CropEditingActive(PageView pv) =>
        _rectSel is { } s && s.Page == pv.Index && (Tool == Tool.RectSelect || _session is { IsPdf: false } && Tool == Tool.Select);

    /// <summary>Sets the hover cursor over the crop selection; returns false when the pointer isn't over it.</summary>
    private bool UpdateCropCursor(PageView pv, Point dip)
    {
        if (!CropEditingActive(pv)) return false;
        var r = DipRect(pv, _rectSel!.Value.Rect);
        var p = new PointD(dip.X, dip.Y);
        if (CropMath.HitHandle(r, p, CropHitRadius) is { } h)
        {
            pv.SetCursor(h switch
            {
                CropHandle.TopLeft or CropHandle.BottomRight => InputSystemCursorShape.SizeNorthwestSoutheast,
                CropHandle.TopRight or CropHandle.BottomLeft => InputSystemCursorShape.SizeNortheastSouthwest,
                CropHandle.Top or CropHandle.Bottom => InputSystemCursorShape.SizeNorthSouth,
                _ => InputSystemCursorShape.SizeWestEast,
            });
            return true;
        }
        if (!r.Contains(p)) return false;
        pv.SetCursor(InputSystemCursorShape.SizeAll);
        return true;
    }

    private static bool IsInsideButton(object? source)
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ButtonBase) return true;
            if (d is PageView) return false;
        }
        return false;
    }

    // ---------------- Drawing ----------------

    private void DrawCropAdorners()
    {
        if (_rectSel is not { } sel || sel.Page >= _pages.Count || _session == null) return;
        var pv = _pages[sel.Page];
        var r = DipRect(pv, sel.Rect);
        if (r.Width < 1 || r.Height < 1) return;

        // Dim everything outside the selection so it's clear what stays.
        var dim = new Path
        {
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 0, 0, 0)),
            Data = new GeometryGroup
            {
                FillRule = FillRule.EvenOdd,
                Children =
                {
                    new RectangleGeometry { Rect = new Rect(0, 0, pv.Width, pv.Height) },
                    new RectangleGeometry { Rect = ToRect(r) },
                },
            },
        };
        AddAdorner(pv, dim, 0, 0);

        // Outline: dark under white so it reads on both light and dark pictures.
        AddAdorner(pv, new Rectangle { Width = r.Width + 2, Height = r.Height + 2, Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(150, 0, 0, 0)), StrokeThickness = 1 }, r.X1 - 1, r.Y1 - 1);
        AddAdorner(pv, new Rectangle { Width = r.Width, Height = r.Height, Stroke = new SolidColorBrush(Microsoft.UI.Colors.White), StrokeThickness = 1.5 }, r.X1, r.Y1);

        if (r.Width > 60 && r.Height > 60)
        {
            var grid = new SolidColorBrush(Windows.UI.Color.FromArgb(110, 255, 255, 255));
            for (int i = 1; i <= 2; i++)
            {
                AddAdorner(pv, new Line { X1 = 0, X2 = 0, Y1 = 0, Y2 = r.Height, Stroke = grid, StrokeThickness = 1 }, r.X1 + r.Width * i / 3, r.Y1);
                AddAdorner(pv, new Line { X1 = 0, X2 = r.Width, Y1 = 0, Y2 = 0, Stroke = grid, StrokeThickness = 1 }, r.X1, r.Y1 + r.Height * i / 3);
            }
        }

        foreach (var (hx, hy) in new[]
        {
            (r.X1, r.Y1), (r.X2, r.Y1), (r.X2, r.Y2), (r.X1, r.Y2),
            (r.Center.X, r.Y1), (r.X2, r.Center.Y), (r.Center.X, r.Y2), (r.X1, r.Center.Y),
        })
            AddAdorner(pv, new Ellipse { Width = 11, Height = 11, Fill = new SolidColorBrush(Microsoft.UI.Colors.White), Stroke = AccentBrush, StrokeThickness = 1.5 }, hx - 5.5, hy - 5.5);

        // Size readout: pixels for images, inches for PDFs.
        var shown = _session.GetGeometry(sel.Page).UserToDisplay.Apply(sel.Rect);
        string size = _session.IsPdf
            ? $"{shown.Width / 72:0.##} × {shown.Height / 72:0.##} in"
            : $"{Math.Round(shown.Width)} × {Math.Round(shown.Height)} px";
        var label = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 20, 20, 20)), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2),
            Child = new TextBlock { Text = size, FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double ly = r.Y1 - label.DesiredSize.Height - 6;
        if (ly < 2) ly = r.Y1 + 6;
        AddAdorner(pv, label, Math.Clamp(r.X1, 2, Math.Max(2, pv.Width - label.DesiredSize.Width - 2)), ly);

        AddCropPill(pv, r);
    }

    private void AddCropPill(PageView pv, RectD r)
    {
        Button Make(string text, bool accent)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), MinWidth = 0 };
            if (accent) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            return b;
        }
        var crop = Make("Crop", accent: true);
        crop.Click += (_, _) => CropRequested?.Invoke(false);
        ToolTipService.SetToolTip(crop, "Crop to the selection (Enter)");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { crop } };
        if (_session is { IsPdf: true, PageCount: > 1 })
        {
            var all = Make("All pages", accent: false);
            all.Click += (_, _) => CropRequested?.Invoke(true);
            ToolTipService.SetToolTip(all, "Crop every page to this area");
            row.Children.Add(all);
        }
        var cancel = Make("✕", accent: false);
        cancel.Padding = new Thickness(8, 4, 8, 4);
        cancel.Click += (_, _) => ClearRectSelection();
        ToolTipService.SetToolTip(cancel, "Cancel (Esc)");
        row.Children.Add(cancel);

        var pill = new Border
        {
            Child = row, Padding = new Thickness(6), CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["AcrylicBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["SurfaceStrokeColorDefaultBrush"], BorderThickness = new Thickness(1),
        };
        pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = pill.DesiredSize.Width, h = pill.DesiredSize.Height;
        double y = r.Y2 + 10;
        if (y + h > pv.Height - 2) y = r.Y1 - h - 10;          // no room below: above the selection
        if (y < 2) y = Math.Max(2, r.Y2 - h - 10);             // no room above either: inside the bottom edge
        double x = Math.Clamp(r.Center.X - w / 2, 2, Math.Max(2, pv.Width - w - 2));
        AddAdorner(pv, pill, x, y, hitTest: true);
    }
}
