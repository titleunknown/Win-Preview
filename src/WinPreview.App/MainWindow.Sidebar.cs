using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinPreview.Core.Markup;
using WinPreview.Core.Pdf;
using WinPreview.Session;
using WinPreview.Views;

namespace WinPreview;

public sealed partial class MainWindow
{
    private const double ThumbWidth = 120;
    private readonly ObservableCollection<ThumbItem> _thumbs = [];
    private readonly ObservableCollection<NoteItem> _notes = [];
    private readonly ObservableCollection<ResultItem> _results = [];
    private CancellationTokenSource? _thumbCts, _searchCts;
    private readonly HashSet<int> _thumbRefresh = [];
    private bool _thumbRefreshQueued, _syncingThumbSelection, _sidebarVisible = true;
    private List<PdfSearchHit> _hits = [];
    private int _hitIndex = -1;
    private string _lastQuery = "";

    // ---------------- Visibility & tabs ----------------

    private void SidebarButton_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void ToggleSidebar(bool? show = null)
    {
        _sidebarVisible = show ?? !_sidebarVisible;
        ApplySidebarVisibility();
    }

    private void ApplySidebarVisibility()
    {
        bool show = _sidebarVisible && _session != null && (_session.PageCount > 1 || _session.IsPdf || ResultsPanel.Visibility == Visibility.Visible);
        Sidebar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(show ? 210 : 0);
    }

    private void ShowSidebarTab(SelectorBarItem tab)
    {
        // Show the panel first: a collapsed SelectorBar doesn't apply selection changes.
        _sidebarVisible = true;
        SyncSidebarPanels(tab);
        ApplySidebarVisibility();
        SidebarSelector.SelectedItem = tab;
    }

    private void SidebarSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        SyncSidebarPanels(sender.SelectedItem);

    private void SyncSidebarPanels(SelectorBarItem? sel)
    {
        ThumbList.Visibility = sel == ThumbsTab ? Visibility.Visible : Visibility.Collapsed;
        TocTree.Visibility = sel == TocTab ? Visibility.Visible : Visibility.Collapsed;
        NotesList.Visibility = sel == NotesTab ? Visibility.Visible : Visibility.Collapsed;
        ResultsPanel.Visibility = sel == ResultsTab ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RebuildSidebar()
    {
        RebuildThumbnails();
        RebuildToc();
        RebuildNotes();
        bool pdf = _session?.IsPdf == true;
        TocTab.Visibility = pdf ? Visibility.Visible : Visibility.Collapsed;
        NotesTab.Visibility = pdf ? Visibility.Visible : Visibility.Collapsed;
        if (!pdf && !ThumbsTab.IsSelected) SidebarSelector.SelectedItem = ThumbsTab;
        ApplySidebarVisibility();
    }

    // ---------------- Thumbnails ----------------

    private void RebuildThumbnails()
    {
        _thumbCts?.Cancel();
        _thumbs.Clear();
        ThumbList.ItemsSource = _thumbs;
        if (_session == null) return;
        for (int i = 0; i < _session.PageCount; i++)
        {
            var g = _session.GetGeometry(i);
            double h = ThumbWidth * g.Height / g.Width;
            if (h > ThumbWidth * 1.5) h = ThumbWidth * 1.5;
            _thumbs.Add(new ThumbItem { Index = i, Label = (i + 1).ToString(), DisplayWidth = h * g.Width / g.Height, DisplayHeight = h });
        }
        _thumbCts = new CancellationTokenSource();
        _ = RenderThumbsAsync(Enumerable.Range(0, _thumbs.Count).ToList(), _thumbCts.Token);
        SelectThumb(DocView.CurrentPage);
    }

    private async Task RenderThumbsAsync(IReadOnlyList<int> pages, CancellationToken ct)
    {
        var session = _session;
        if (session == null) return;
        double raster = Root.XamlRoot?.RasterizationScale ?? 1;
        foreach (int i in pages)
        {
            if (ct.IsCancellationRequested || session != _session || i >= _thumbs.Count) return;
            var item = _thumbs[i];
            int w = Math.Max(1, (int)(item.DisplayWidth * raster)), h = Math.Max(1, (int)(item.DisplayHeight * raster));
            try
            {
                byte[] px;
                int pw, ph;
                if (session.IsPdf) (px, pw, ph) = await session.RenderAsync(i, w, h);
                else
                {
                    // Image thumbnails include markups, downscaled from the composited frame.
                    var page = session.Pages[i];
                    var frame = page.Frame!;
                    var flat = await Task.Run(() =>
                    {
                        var f = frame.Clone();
                        if (page.Markups.Count > 0) MarkupRasterizer.Composite(f.Pixels, MarkupRasterizer.Render(page.Markups.Select(m => m.Clone()).ToList(), f.Width, f.Height));
                        return f;
                    }, ct);
                    var small = await ImageResizer.ResizeAsync(flat.Pixels, flat.Width, flat.Height, w, h);
                    (px, pw, ph) = (ImageCodec.Premultiply(small), w, h);
                }
                if (ct.IsCancellationRequested || session != _session) return;
                var wb = new WriteableBitmap(pw, ph);
                using (var s = wb.PixelBuffer.AsStream()) await s.WriteAsync(px.AsMemory(0, pw * ph * 4), ct);
                wb.Invalidate();
                item.Image = wb;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Thumbnail {i} failed: {ex.Message}"); }
        }
    }

    private void QueueThumbRefresh(int page)
    {
        if (_session is not { IsPdf: false }) return; // PDF thumbnails show page content, not the markup overlay
        _thumbRefresh.Add(page);
        if (_thumbRefreshQueued) return;
        _thumbRefreshQueued = true;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            _thumbRefreshQueued = false;
            var pages = _thumbRefresh.ToList();
            _thumbRefresh.Clear();
            _ = RenderThumbsAsync(pages, _thumbCts?.Token ?? default);
        };
        timer.Start();
    }

    private void SelectThumb(int page)
    {
        if (page < 0 || page >= _thumbs.Count) return;
        if (ThumbList.SelectedItems.Count == 1 && ThumbList.SelectedIndex == page) return;
        _syncingThumbSelection = true;
        ThumbList.SelectedIndex = page;
        ThumbList.ScrollIntoView(_thumbs[page]);
        _syncingThumbSelection = false;
    }

    private void ThumbList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ThumbItem t) DocView.GoToPage(t.Index);
    }

    private void ThumbList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingThumbSelection) return;
        if (ThumbList.SelectedItems.Count == 1 && ThumbList.SelectedItem is ThumbItem t && t.Index != DocView.CurrentPage) DocView.GoToPage(t.Index);
    }

    private List<int> SelectedPages()
    {
        var pages = ThumbList.SelectedItems.OfType<ThumbItem>().Select(t => t.Index).OrderBy(i => i).ToList();
        return pages.Count > 0 ? pages : [DocView.CurrentPage];
    }

    private void ThumbList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (_session == null || args.DropResult != DataPackageOperation.Move) return;
        var order = _thumbs.Select(t => t.Index).ToList();
        if (order.SequenceEqual(Enumerable.Range(0, order.Count))) return;
        _session.ApplyOrder(order);
    }

    private void ThumbList_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Insert pages";
            e.Handled = true;
        }
    }

    private async void ThumbList_Drop(object sender, DragEventArgs e)
    {
        if (_session == null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;
        var items = await e.DataView.GetStorageItemsAsync();
        int at = DropIndex(e);
        foreach (var f in items.OfType<StorageFile>().Reverse())
        {
            if (!DocumentSession.LooksLikePdf(f.Path) && !ImageCodec.IsImage(f.Path)) continue;
            try { await _session.InsertFileAsync(f.Path, at); }
            catch (Exception ex) { ShowError($"Couldn't insert “{f.Name}”", ex.Message); }
        }
    }

    /// <summary>Which gap between thumbnails the pointer is over.</summary>
    private int DropIndex(DragEventArgs e)
    {
        for (int i = 0; i < _thumbs.Count; i++)
            if (ThumbList.ContainerFromIndex(i) is FrameworkElement c)
            {
                var p = e.GetPosition(c);
                if (p.Y < c.ActualHeight / 2) return i;
            }
        return _thumbs.Count;
    }

    private void ThumbList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Delete or Windows.System.VirtualKey.Back)
        {
            DeletePages();
            e.Handled = true;
        }
    }

    private void ThumbList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_session == null) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ThumbItem t && !ThumbList.SelectedItems.Contains(t))
            ThumbList.SelectedItem = t;
        var menu = new MenuFlyout();
        AddPageCommands(menu, SelectedPages());
        menu.ShowAt(ThumbList, e.GetPosition(ThumbList));
        e.Handled = true;
    }

    // ---------------- Table of contents ----------------

    private void RebuildToc()
    {
        TocTree.RootNodes.Clear();
        if (_session?.Pdf is not { } pdf) return;
        void Add(IList<TreeViewNode> into, IReadOnlyList<PdfOutlineItem> items)
        {
            foreach (var item in items)
            {
                var node = new TreeViewNode { Content = new TocEntry(item.Title, item.PageIndex), IsExpanded = false };
                Add(node.Children, item.Children);
                into.Add(node);
            }
        }
        var outline = pdf.GetOutline();
        if (outline.Count == 0)
            TocTree.RootNodes.Add(new TreeViewNode { Content = new TocEntry("This document has no table of contents.", -1) });
        Add(TocTree.RootNodes, outline);
    }

    private void TocTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode { Content: TocEntry { Page: >= 0 } entry }) DocView.GoToPage(entry.Page);
    }

    // ---------------- Highlights & notes ----------------

    private void RebuildNotes()
    {
        _notes.Clear();
        NotesList.ItemsSource = _notes;
        if (_session == null) return;
        for (int p = 0; p < _session.PageCount; p++)
            foreach (var m in _session.Pages[p].Markups)
            {
                (string kind, string text) = m switch
                {
                    TextMarkup tm => (tm.Style switch { TextMarkupStyle.Underline => "Underline", TextMarkupStyle.Strikeout => "Strikethrough", _ => "Highlight" }, tm.Text),
                    NoteMarkup n => ("Note", n.Text),
                    TextBoxMarkup tb => ("Text", tb.Text),
                    _ => ("", ""),
                };
                if (kind.Length == 0) continue;
                _notes.Add(new NoteItem(p, m.Id, $"Page {p + 1} · {kind}", string.IsNullOrWhiteSpace(text) ? "(empty)" : text.Trim(), m.Color.Brush()));
            }
    }

    private void NotesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not NoteItem n || _session?.FindMarkup(n.Page, n.Id) is not { } m) return;
        DocView.RevealRect(n.Page, m.Bounds);
        DocView.SelectMarkup(n.Page, n.Id);
    }

    // ---------------- Search ----------------

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && sender.Text.Length == 0) ClearSearch();
    }

    private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        string q = args.QueryText.Trim();
        if (q.Length == 0) { ClearSearch(); return; }
        if (q == _lastQuery && _hits.Count > 0)
        {
            bool back = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            StepHit(back ? -1 : 1);
            return;
        }
        await RunSearchAsync(q);
    }

    private async Task RunSearchAsync(string query)
    {
        if (_session is not { } session) return;
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        _lastQuery = query;
        _results.Clear();
        ResultsList.ItemsSource = _results;
        ResultsHeader.Text = session.IsPdf ? "Searching…" : "Reading text in images…";
        ShowSidebarTab(ResultsTab);
        _hits = [];
        try
        {
            var found = new List<(PdfSearchHit Hit, string Context)>();
            if (session.Pdf is { } pdf)
            {
                await Task.Run(() =>
                {
                    foreach (var hit in pdf.Search(query, ct: cts.Token))
                        found.Add((hit, Snippet(new Core.Text.PdfTextLayer(pdf, hit.PageIndex), hit)));
                }, cts.Token);
            }
            else
            {
                // Images: search the OCR text (recognized on demand).
                for (int p = 0; p < session.PageCount && !cts.IsCancellationRequested; p++)
                {
                    if (await session.GetTextLayerAsync(p) is not { } layer) continue;
                    foreach (var (start, count) in layer.Find(query, matchCase: false))
                    {
                        var hit = new PdfSearchHit(p, start, count, layer.GetTextRects(start, count));
                        found.Add((hit, Snippet(layer, hit)));
                    }
                }
            }
            if (cts.IsCancellationRequested) return;
            _hits = found.Select(f => f.Hit).ToList();
            for (int i = 0; i < found.Count; i++)
                _results.Add(new ResultItem(i, found[i].Hit, $"Page {found[i].Hit.PageIndex + 1}", found[i].Context));
            ResultsHeader.Text = found.Count switch { 0 => $"No matches for “{query}”.", 1 => "1 match", _ => $"{found.Count} matches" };
            // Start from the first hit at or after the current page.
            _hitIndex = -1;
            if (_hits.Count > 0)
            {
                int first = _hits.FindIndex(h => h.PageIndex >= DocView.CurrentPage);
                SetHit(first < 0 ? 0 : first);
            }
            else DocView.SetSearchHits([], -1);
        }
        catch (OperationCanceledException) { }
    }

    private static string Snippet(Core.Text.TextLayer layer, PdfSearchHit hit)
    {
        int n = layer.CharCount;
        int start = Math.Max(0, hit.CharStart - 40), end = Math.Min(n, hit.CharStart + hit.CharCount + 60);
        string s = layer.GetText(start, end - start).Replace("\r", " ").Replace("\n", " ");
        return (start > 0 ? "…" : "") + s.Trim() + (end < n ? "…" : "");
    }

    private void SetHit(int index)
    {
        if (_hits.Count == 0) return;
        _hitIndex = (index % _hits.Count + _hits.Count) % _hits.Count;
        DocView.SetSearchHits(_hits, _hitIndex);
        ResultsList.SelectedIndex = _hitIndex;
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
        ResultsHeader.Text = $"{_hitIndex + 1} of {_hits.Count} matches";
    }

    private void StepHit(int delta) => SetHit(_hitIndex + delta);

    private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ResultItem r) SetHit(r.Index);
    }

    private void ClearSearch()
    {
        _searchCts?.Cancel();
        _hits = [];
        _hitIndex = -1;
        _lastQuery = "";
        _results.Clear();
        DocView.SetSearchHits([], -1);
        if (ResultsTab.IsSelected) SidebarSelector.SelectedItem = ThumbsTab;
    }
}
