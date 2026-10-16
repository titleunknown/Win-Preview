using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinPreview.Core.Markup;
using WinPreview.Dialogs;
using WinPreview.Views;

namespace WinPreview;

public sealed partial class MainWindow
{
    private static readonly RgbaColor[] Palette =
    [
        RgbaColor.Black, new(0x5A, 0x5A, 0x5E), RgbaColor.Gray, new(0xC7, 0xC7, 0xCC), RgbaColor.White,
        RgbaColor.Red, RgbaColor.Orange, RgbaColor.Yellow, RgbaColor.Green, new(0x00, 0xC7, 0xBE),
        RgbaColor.Blue, new(0x58, 0x56, 0xD6), RgbaColor.Purple, RgbaColor.Pink, new(0xA2, 0x84, 0x5E),
    ];

    private static readonly (string Name, RgbaColor Color)[] HighlightColors =
    [
        ("Yellow", RgbaColor.HighlightYellow), ("Green", new(0x8B, 0xE3, 0x8B)), ("Blue", new(0x8C, 0xC8, 0xFF)),
        ("Pink", new(0xFF, 0x9E, 0xC4)), ("Purple", new(0xCF, 0xA8, 0xFF)),
    ];

    private readonly Dictionary<Tool, ToggleButton> _toolButtons = [];
    private DropDownButton? _shapeButton, _signButton, _widthButton, _strokeButton, _fillButton, _textButton;
    private Button? _cropButton, _sizeButton;
    private TextBlock _aspectLabel = new();
    private ToggleButton? _alphaButton;
    private SplitButton? _removeBgButton;
    private Rectangle? _strokeSwatch, _fillSwatch;
    private TextBlock? _textSwatch;
    private Tool _lastShapeTool = Tool.Rectangle;

    private static FrameworkElement Glyph(string glyph, double size = 16) => new FontIcon { Glyph = glyph, FontSize = size };

    private static FrameworkElement Symbol(string text, double size = 16) => new TextBlock
    {
        Text = text, FontSize = size, FontFamily = new FontFamily("Segoe UI Symbol"),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0),
    };

    private void BuildMarkupToolbar()
    {
        var bar = MarkupTools.Children;

        ToggleButton ToolButton(Tool tool, FrameworkElement icon, string tip)
        {
            var b = new ToggleButton { Content = icon, Style = (Style)Application.Current.Resources["ToolToggleStyle"] };
            ToolTipService.SetToolTip(b, tip);
            b.Click += (_, _) =>
            {
                DocView.Tool = b.IsChecked == true || DocView.Tool != tool ? tool : Tool.Select;
                SyncToolButtons();
            };
            _toolButtons[tool] = b;
            return b;
        }

        bar.Add(ToolButton(Tool.Select, Glyph(""), "Text selection — select text, markups, and form fields"));
        bar.Add(ToolButton(Tool.RectSelect, Symbol("⬚", 18), "Rectangular selection — select an area to crop"));
        bar.Add(ToolButton(Tool.Draw, Glyph(""), "Draw — freehand ink"));

        _shapeButton = new DropDownButton { Content = Symbol("▭", 18), Background = new SolidColorBrush(Colors.Transparent), BorderBrush = new SolidColorBrush(Colors.Transparent), Padding = new Thickness(8, 4, 6, 4) };
        ToolTipService.SetToolTip(_shapeButton, "Shapes");
        var shapes = new MenuFlyout();
        foreach (var (tool, sym, name) in new[] { (Tool.Rectangle, "▭", "Rectangle"), (Tool.Oval, "◯", "Oval"), (Tool.Line, "╱", "Line"), (Tool.Arrow, "↗", "Arrow") })
        {
            var item = new MenuFlyoutItem { Text = name, Icon = new FontIcon { Glyph = sym, FontFamily = new FontFamily("Segoe UI Symbol") } };
            item.Click += (_, _) => { _lastShapeTool = tool; _shapeButton.Content = Symbol(sym, 18); DocView.Tool = tool; };
            shapes.Items.Add(item);
        }
        _shapeButton.Flyout = shapes;
        bar.Add(_shapeButton);

        bar.Add(ToolButton(Tool.Text, Glyph(""), "Text box — click to add text"));
        bar.Add(ToolButton(Tool.Note, Glyph(""), "Note — click to add a sticky note"));

        _signButton = new DropDownButton { Content = Glyph(""), Background = new SolidColorBrush(Colors.Transparent), BorderBrush = new SolidColorBrush(Colors.Transparent), Padding = new Thickness(8, 4, 6, 4) };
        ToolTipService.SetToolTip(_signButton, "Sign");
        _signButton.Flyout = new Flyout();
        _signButton.Flyout.Opening += (_, _) => BuildSignatureFlyout((Flyout)_signButton.Flyout);
        bar.Add(_signButton);

        bar.Add(Separator());

        _cropButton = new Button { Content = Glyph(""), Style = (Style)Application.Current.Resources["ToolButtonStyle"] };
        ToolTipService.SetToolTip(_cropButton, "Crop — drag over the area to keep, then press Enter (shortcut: C)");
        _cropButton.Click += (_, _) => CropOrStartSelection();
        bar.Add(_cropButton);

        _aspectLabel = new TextBlock { Text = "Free", FontSize = 12 };
        var aspectButton = StyleDropDown(_aspectLabel, "Crop shape — free, or lock to an aspect ratio");
        var aspects = new MenuFlyout();
        foreach (var (name, value) in new (string, double?)[]
        {
            ("Free", null), ("Same as page", -1), ("Square (1:1)", 1), ("4:3", 4.0 / 3), ("3:2", 3.0 / 2), ("16:9", 16.0 / 9), ("5:4", 5.0 / 4),
        })
        {
            var item = new RadioMenuFlyoutItem { Text = name, GroupName = "crop-aspect", IsChecked = value == null };
            item.Click += (_, _) =>
            {
                _aspectLabel.Text = value == null ? "Free" : name.Split(' ')[0] is "Square" ? "1:1" : name == "Same as page" ? "Page" : name;
                DocView.CropAspect = value;
                DocView.FocusSurface();
            };
            aspects.Items.Add(item);
        }
        aspectButton.Flyout = aspects;
        bar.Add(aspectButton);

        _sizeButton = new Button { Content = Glyph(""), Style = (Style)Application.Current.Resources["ToolButtonStyle"] };
        ToolTipService.SetToolTip(_sizeButton, "Adjust size…");
        _sizeButton.Click += async (_, _) => await AdjustSizeAsync();
        bar.Add(_sizeButton);

        _alphaButton = ToolButton(Tool.InstantAlpha, new TextBlock
        {
            Text = "α", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -4, 0, 0),
        }, "Instant Alpha — press on a color and drag to remove similar connected colors");
        bar.Add(_alphaButton);

        _removeBgButton = new SplitButton
        {
            Content = Glyph(""), Padding = new Thickness(8, 4, 6, 4),
            Background = new SolidColorBrush(Colors.Transparent), BorderBrush = new SolidColorBrush(Colors.Transparent),
        };
        ToolTipService.SetToolTip(_removeBgButton, "Remove Background");
        _removeBgButton.Click += (_, _) => _ = RemoveBackgroundAsync(null);
        var bgMenu = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };
        bgMenu.Opening += (_, _) => BuildRemoveBackgroundMenu(bgMenu);
        _removeBgButton.Flyout = bgMenu;
        bar.Add(_removeBgButton);

        bar.Add(Separator());

        _widthButton = StyleDropDown(Symbol("≡", 18), "Shape style — line width");
        _widthButton.Flyout = BuildWidthFlyout();
        bar.Add(_widthButton);

        _strokeSwatch = new Rectangle { Width = 16, Height = 16, StrokeThickness = 3, RadiusX = 3, RadiusY = 3 };
        _strokeButton = StyleDropDown(_strokeSwatch, "Border color");
        _strokeButton.Flyout = BuildColorFlyout(c => SetStrokeColor(c), allowNone: false);
        bar.Add(_strokeButton);

        _fillSwatch = new Rectangle { Width = 16, Height = 16, RadiusX = 3, RadiusY = 3, StrokeThickness = 1 };
        _fillButton = StyleDropDown(_fillSwatch, "Fill color");
        _fillButton.Flyout = BuildColorFlyout(c => SetFillColor(c), allowNone: true);
        bar.Add(_fillButton);

        _textSwatch = new TextBlock { Text = "Aa", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _textButton = StyleDropDown(_textSwatch, "Text style");
        _textButton.Flyout = BuildTextFlyout();
        bar.Add(_textButton);

        SyncToolButtons();
        SyncStyleSwatches();
    }

    private static DropDownButton StyleDropDown(FrameworkElement content, string tip)
    {
        var b = new DropDownButton
        {
            Content = content, Padding = new Thickness(8, 4, 6, 4),
            Background = new SolidColorBrush(Colors.Transparent), BorderBrush = new SolidColorBrush(Colors.Transparent),
        };
        ToolTipService.SetToolTip(b, tip);
        return b;
    }

    private static Border Separator() => new()
    {
        Width = 1, Margin = new Thickness(6, 6, 6, 6),
        Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
    };

    private void SyncToolButtons()
    {
        foreach (var (tool, b) in _toolButtons) b.IsChecked = DocView.Tool == tool;
        ZoomToolToggle.IsChecked = DocView.Tool == Tool.Zoom;
        bool shapeActive = DocView.Tool is Tool.Rectangle or Tool.Oval or Tool.Line or Tool.Arrow;
        if (_shapeButton != null)
            _shapeButton.Background = shapeActive ? (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"] : new SolidColorBrush(Colors.Transparent);
        HighlightButton.Background = DocView.Tool == Tool.Highlight
            ? (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"] : new SolidColorBrush(Colors.Transparent);
    }

    private void UpdateToolAvailability()
    {
        bool pdf = _session?.IsPdf == true;
        if (_toolButtons.TryGetValue(Tool.Note, out var note)) note.Visibility = pdf ? Visibility.Visible : Visibility.Collapsed;
        var imageOnly = _session is { IsPdf: false } ? Visibility.Visible : Visibility.Collapsed;
        if (_sizeButton != null) _sizeButton.Visibility = imageOnly;
        if (_alphaButton != null) _alphaButton.Visibility = imageOnly;
        if (_removeBgButton != null) _removeBgButton.Visibility = imageOnly;
        if (_toolButtons.TryGetValue(Tool.Select, out var sel))
            ToolTipService.SetToolTip(sel, pdf ? "Text selection — select text, markups, and form fields" : "Selection — select markups or an area");
    }

    // ---------------- Style ----------------

    private void SyncStyleSwatches()
    {
        var s = DocView.CurrentStyle;
        if (_strokeSwatch != null) _strokeSwatch.Stroke = s.Stroke.Brush();
        if (_fillSwatch != null)
        {
            _fillSwatch.Fill = s.Fill is { } f ? f.Brush() : null;
            _fillSwatch.Stroke = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            _fillSwatch.StrokeDashArray = s.Fill == null ? [2, 2] : null;
        }
        if (_textSwatch != null)
        {
            _textSwatch.Foreground = s.TextColor == RgbaColor.White ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"] : s.TextColor.Brush();
            _textSwatch.FontFamily = MarkupVisuals.FontFor(s.Font);
        }
    }

    /// <summary>When a markup is selected, the style buttons show (and edit) its style, as in Preview.</summary>
    private void OnDocSelectionChanged()
    {
        if (DocView.SelectedItem is { } item)
        {
            var s = DocView.CurrentStyle;
            double k = DocView.SelectedStyleScale;
            switch (item)
            {
                case ShapeMarkup sh: s.Stroke = sh.Color; s.Fill = sh.Fill; s.StrokeWidth = sh.StrokeWidth / k; break;
                case LineMarkup l: s.Stroke = l.Color; s.StrokeWidth = l.StrokeWidth / k; break;
                case InkMarkup { IsSignature: false } ink: s.Stroke = ink.Color; s.StrokeWidth = ink.StrokeWidth / k; break;
                case TextBoxMarkup tb: s.TextColor = tb.Color; s.FontSize = Math.Round(tb.FontSize / k, 1); s.Font = tb.Font; s.Fill = tb.Fill; break;
            }
            SyncStyleSwatches();
        }
        UpdateToolAvailability();
    }

    private void SetStrokeColor(RgbaColor? c)
    {
        if (c is not { } color) return;
        DocView.CurrentStyle.Stroke = color;
        DocView.ApplyToSelection(m =>
        {
            if (m is TextBoxMarkup) return;
            m.Color = color;
        }, "Change Color");
        SyncStyleSwatches();
    }

    private void SetFillColor(RgbaColor? c)
    {
        DocView.CurrentStyle.Fill = c;
        DocView.ApplyToSelection(m =>
        {
            if (m is ShapeMarkup s) s.Fill = c;
            else if (m is TextBoxMarkup t) t.Fill = c;
        }, "Change Fill");
        SyncStyleSwatches();
    }

    private void SetStrokeWidth(double w)
    {
        DocView.CurrentStyle.StrokeWidth = w;
        double k = DocView.SelectedStyleScale;
        DocView.ApplyToSelection(m => { if (m is not (TextBoxMarkup or NoteMarkup or TextMarkup)) m.StrokeWidth = w * k; }, "Change Line Width");
    }

    private Flyout BuildWidthFlyout()
    {
        var panel = new StackPanel { Spacing = 2, Width = 140 };
        foreach (double w in new[] { 0.5, 1, 2, 3, 4, 6, 8, 12 })
        {
            var b = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent), BorderBrush = new SolidColorBrush(Colors.Transparent),
                Content = new Grid
                {
                    Height = 16,
                    Children =
                    {
                        new Line
                        {
                            X1 = 0, X2 = 90, Y1 = 8, Y2 = 8, StrokeThickness = w, StrokeEndLineCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round,
                            Stroke = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                        },
                        new TextBlock { Text = $"{w} pt", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0.7 },
                    },
                },
            };
            b.Click += (_, _) => { SetStrokeWidth(w); _widthButton?.Flyout.Hide(); };
            panel.Children.Add(b);
        }
        return new Flyout { Content = panel };
    }

    private Flyout BuildColorFlyout(Action<RgbaColor?> pick, bool allowNone)
    {
        var flyout = new Flyout();
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 5, ItemWidth = 34, ItemHeight = 34 };
        foreach (var c in Palette)
        {
            var b = new Button
            {
                Width = 28, Height = 28, Padding = new Thickness(0), CornerRadius = new CornerRadius(14),
                Background = c.Brush(), BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"], BorderThickness = new Thickness(1),
            };
            ToolTipService.SetToolTip(b, c.ToHex()[..7]);
            b.Click += (_, _) => { pick(c); flyout.Hide(); };
            grid.Children.Add(b);
        }
        var panel = new StackPanel { Spacing = 8, Children = { grid } };
        if (allowNone)
        {
            var none = new Button { Content = "No Fill", HorizontalAlignment = HorizontalAlignment.Stretch };
            none.Click += (_, _) => { pick(null); flyout.Hide(); };
            panel.Children.Add(none);
        }
        var more = new Button { Content = "More Colors…", HorizontalAlignment = HorizontalAlignment.Stretch };
        more.Click += (_, _) =>
        {
            var picker = new ColorPicker { IsAlphaEnabled = true, IsMoreButtonVisible = false, Color = DocView.CurrentStyle.Stroke.ToUi() };
            var ok = new Button { Content = "Use Color", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
            var pickerFlyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { picker, ok } } };
            ok.Click += (_, _) => { pick(picker.Color.ToRgba()); pickerFlyout.Hide(); };
            flyout.Hide();
            pickerFlyout.ShowAt(MarkupBar);
        };
        panel.Children.Add(more);
        flyout.Content = panel;
        return flyout;
    }

    private Flyout BuildTextFlyout()
    {
        var fonts = new ComboBox { Header = "Font", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { "Helvetica", "Times", "Courier" } };
        var size = new NumberBox { Header = "Size", Minimum = 4, Maximum = 288, SmallChange = 1, LargeChange = 4, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var colors = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 5, ItemWidth = 30, ItemHeight = 30 };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 10, Width = 190, Children = { fonts, size, new TextBlock { Text = "Color" }, colors } } };
        foreach (var c in Palette)
        {
            var b = new Button
            {
                Width = 24, Height = 24, Padding = new Thickness(0), CornerRadius = new CornerRadius(12),
                Background = c.Brush(), BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"], BorderThickness = new Thickness(1),
            };
            b.Click += (_, _) =>
            {
                DocView.CurrentStyle.TextColor = c;
                DocView.ApplyToSelection(m => { if (m is TextBoxMarkup t) t.Color = c; }, "Change Text Color");
                SyncStyleSwatches();
            };
            colors.Children.Add(b);
        }
        bool syncing = false;
        flyout.Opening += (_, _) =>
        {
            syncing = true;
            fonts.SelectedIndex = (int)DocView.CurrentStyle.Font;
            size.Value = DocView.CurrentStyle.FontSize;
            syncing = false;
        };
        fonts.SelectionChanged += (_, _) =>
        {
            if (syncing || fonts.SelectedIndex < 0) return;
            var f = (PdfStandardFont)fonts.SelectedIndex;
            DocView.CurrentStyle.Font = f;
            DocView.ApplyToSelection(m => { if (m is TextBoxMarkup t) t.Font = f; }, "Change Font");
            SyncStyleSwatches();
        };
        size.ValueChanged += (_, e) =>
        {
            if (syncing || double.IsNaN(e.NewValue)) return;
            DocView.CurrentStyle.FontSize = e.NewValue;
            double k = DocView.SelectedStyleScale;
            DocView.ApplyToSelection(m => { if (m is TextBoxMarkup t) t.FontSize = e.NewValue * k; }, "Change Font Size");
        };
        return flyout;
    }

    // ---------------- Highlight ----------------

    private void BuildHighlightMenu()
    {
        HighlightMenu.Items.Clear();
        foreach (var (name, color) in HighlightColors)
        {
            var item = new MenuFlyoutItem { Text = name, Icon = new FontIcon { Glyph = "", Foreground = color.Brush() } };
            item.Click += (_, _) => { DocView.CurrentStyle.MarkupStyle = TextMarkupStyle.Highlight; DocView.CurrentStyle.HighlightColor = color; ApplyHighlightChoice(); };
            HighlightMenu.Items.Add(item);
        }
        HighlightMenu.Items.Add(new MenuFlyoutSeparator());
        var underline = new MenuFlyoutItem { Text = "Underline", Icon = new FontIcon { Glyph = "" } };
        underline.Click += (_, _) => { DocView.CurrentStyle.MarkupStyle = TextMarkupStyle.Underline; ApplyHighlightChoice(); };
        var strike = new MenuFlyoutItem { Text = "Strikethrough", Icon = new FontIcon { Glyph = "" } };
        strike.Click += (_, _) => { DocView.CurrentStyle.MarkupStyle = TextMarkupStyle.Strikeout; ApplyHighlightChoice(); };
        HighlightMenu.Items.Add(underline);
        HighlightMenu.Items.Add(strike);
    }

    private void ApplyHighlightChoice()
    {
        var s = DocView.CurrentStyle;
        HighlightIcon.Glyph = s.MarkupStyle switch { TextMarkupStyle.Underline => "", TextMarkupStyle.Strikeout => "", _ => "" };
        HighlightIcon.Foreground = s.MarkupStyle == TextMarkupStyle.Highlight ? s.HighlightColor.Brush() : null;
        if (DocView.TextSelectionRange != null) DocView.MarkUpSelectedText(s.MarkupStyle);
        else DocView.Tool = Tool.Highlight;
    }

    private void HighlightButton_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (DocView.TextSelectionRange != null) DocView.MarkUpSelectedText(DocView.CurrentStyle.MarkupStyle);
        else DocView.Tool = DocView.Tool == Tool.Highlight ? Tool.Select : Tool.Highlight;
    }

    // ---------------- Signatures ----------------

    private void BuildSignatureFlyout(Flyout flyout)
    {
        var panel = new StackPanel { Spacing = 6, MinWidth = 240 };
        var sigs = _signatures.Load();
        if (sigs.Count == 0)
            panel.Children.Add(new TextBlock { Text = "No saved signatures yet.", Opacity = 0.7, Margin = new Thickness(4) });
        foreach (var sig in sigs)
        {
            var row = new Grid { ColumnSpacing = 4 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var content = new StackPanel { Spacing = 2, Children = { SignatureDialog.Preview(sig) } };
            if (!string.IsNullOrEmpty(sig.Description)) content.Children.Add(new TextBlock { Text = sig.Description, FontSize = 11, Opacity = 0.7 });
            var use = new Button
            {
                Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Colors.Transparent), Padding = new Thickness(8),
            };
            ToolTipService.SetToolTip(use, "Click to add this signature to the page");
            use.Click += (_, _) => { flyout.Hide(); DocView.InsertSignature(sig); };
            var del = new Button { Content = Glyph("", 12), Background = new SolidColorBrush(Colors.Transparent), BorderBrush = new SolidColorBrush(Colors.Transparent), VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(del, "Delete this signature");
            del.Click += (_, _) => { _signatures.Remove(sig.Id); BuildSignatureFlyout(flyout); };
            Grid.SetColumn(del, 1);
            row.Children.Add(use);
            row.Children.Add(del);
            panel.Children.Add(row);
        }
        panel.Children.Add(new MenuFlyoutSeparator());
        var create = new Button { Content = "Create Signature…", HorizontalAlignment = HorizontalAlignment.Stretch };
        create.Click += async (_, _) =>
        {
            flyout.Hide();
            var sig = await SignatureDialog.ShowAsync(Root.XamlRoot);
            if (sig == null) return;
            _signatures.Add(sig);
            DocView.InsertSignature(sig);
        };
        panel.Children.Add(create);
        flyout.Content = panel;
    }
}
