using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinPreview.Core.Markup;

namespace WinPreview.Views;

/// <summary>
/// Font chooser: a button showing the current font, which opens a search field over a list of fonts. Typing filters
/// the list (names that start with the text first); each name is written in its own font, except symbol fonts
/// (Wingdings and the like), whose names would be unreadable. Enter picks the top match, Down moves into the list.
/// </summary>
public sealed partial class FontPicker : StackPanel
{
    /// <summary>
    /// Every row has this height, whatever its font's line height: tall enough for fonts with big line heights
    /// (Segoe UI Emoji, Comic Sans), and uniform so scrolling doesn't re-measure rows as fonts load.
    /// </summary>
    private const double RowHeight = 40;

    private readonly TextBlock _label = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _button;
    private readonly TextBox _search = new() { PlaceholderText = "Search fonts" };
    private readonly ListView _list = new() { Height = 360, SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
    private readonly Flyout _flyout;
    private IReadOnlyList<string> _fonts = [];
    private string _current = "";

    /// <summary>Raised when the user chooses a font.</summary>
    public event Action<string>? FontPicked;

    public FontPicker()
    {
        Spacing = 4;
        _button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
                Children = { _label, Chevron() },
            },
        };
        ToolTipService.SetToolTip(_button, "Helvetica, Times and Courier are built into every PDF viewer. Other fonts are embedded in the saved file, which makes it larger.");
        Children.Add(new TextBlock { Text = "Font" });
        Children.Add(_button);

        _list.ContainerContentChanging += (_, e) =>
        {
            if (e.ItemContainer is not { } row || e.Item is not string name) return;
            row.FontFamily = PreviewFont(name);
            row.Height = row.MinHeight = row.MaxHeight = RowHeight;
            row.Padding = new Thickness(row.Padding.Left, 0, row.Padding.Right, 0);
            row.VerticalContentAlignment = VerticalAlignment.Center;
        };
        _list.ItemClick += (_, e) => { if (e.ClickedItem is string name) Pick(name); };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && _list.SelectedItem is string name) { Pick(name); e.Handled = true; }
        };

        _search.TextChanged += (_, _) => Filter();
        _search.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case VirtualKey.Enter when _list.Items.Count > 0:
                    Pick((string)_list.Items[0]);
                    e.Handled = true;
                    break;
                case VirtualKey.Down when _list.Items.Count > 0:
                    _list.SelectedIndex = 0;
                    (_list.ContainerFromIndex(0) as Control)?.Focus(FocusState.Keyboard);
                    e.Handled = true;
                    break;
            }
        };

        _flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            Content = new StackPanel { Spacing = 6, Width = 280, Children = { _search, _list } },
        };
        _flyout.Opened += (_, _) =>
        {
            _search.Text = "";
            Filter();
            _search.Focus(FocusState.Programmatic);
        };
        _button.Flyout = _flyout;
    }

    /// <summary>The fonts to offer, in order, and the current one.</summary>
    public void SetFonts(IReadOnlyList<string> fonts, string current)
    {
        _fonts = fonts;
        _current = current;
        _label.Text = current;
        _label.FontFamily = PreviewFont(current);
    }

    private void Filter()
    {
        string q = _search.Text.Trim();
        var shown = q.Length == 0 ? _fonts.ToList()
            : _fonts.Where(f => f.StartsWith(q, StringComparison.CurrentCultureIgnoreCase))
                .Concat(_fonts.Where(f => !f.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) && f.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
                .ToList();
        _list.ItemsSource = shown;
        int i = shown.IndexOf(_current);
        _list.SelectedIndex = i;
        if (i >= 0) _list.ScrollIntoView(shown[i], ScrollIntoViewAlignment.Leading);
    }

    private void Pick(string name)
    {
        _flyout.Hide();
        SetFonts(_fonts, name);
        FontPicked?.Invoke(name);
    }

    private static FontIcon Chevron()
    {
        var icon = new FontIcon { Glyph = "", FontSize = 10, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(icon, 1);
        return icon;
    }

    private static FontFamily UiFont => (FontFamily)Application.Current.Resources["ContentControlThemeFontFamily"];

    private static FontFamily PreviewFont(string name) =>
        SystemFonts.Find(name) is { Symbol: true } ? UiFont : MarkupVisuals.FontFor(name);
}
