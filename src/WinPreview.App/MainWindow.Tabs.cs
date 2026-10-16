using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinPreview.Session;
using WinPreview.Views;

namespace WinPreview;

/// <summary>
/// Documents are always held as tabs. A window that opens one file at a time simply has a single tab and no strip;
/// with "Open Files in Tabs" on, the strip shows and new files join the window instead of opening a new one.
/// The active tab drives the single viewer and sidebar; the others keep their session in memory.
/// </summary>
public sealed partial class MainWindow
{
    private sealed class DocTab
    {
        public required DocumentSession Session { get; init; }
        public required TabViewItem Item { get; init; }
        public DocumentView.ViewState? View { get; set; }
        public bool SidebarVisible { get; set; }
        public Action? DirtyHandler { get; set; }
    }

    private readonly List<DocTab> _tabs = [];
    private DocTab? _current;

    /// <summary>Tabs in the order shown in the strip (the user can reorder them).</summary>
    private List<DocTab> TabsInOrder() => Tabs.TabItems.OfType<TabViewItem>().Select(i => (DocTab)i.Tag).ToList();

    // ---------------- Adding and switching ----------------

    /// <summary>Opens a session in a new tab of this window and shows it.</summary>
    private void AddDocument(DocumentSession session)
    {
        var item = new TabViewItem { IconSource = new FontIconSource { Glyph = "" }, IsClosable = true };
        var tab = new DocTab { Session = session, Item = item, SidebarVisible = session.IsPdf || session.PageCount > 1 };
        item.Tag = tab;
        tab.DirtyHandler = () => UpdateTabHeader(tab);
        session.DirtyChanged += tab.DirtyHandler;
        UpdateTabHeader(tab);
        item.ContextFlyout = BuildTabMenu(tab);

        _tabs.Add(tab);
        Tabs.TabItems.Add(item);
        ActivateTab(tab);
        Tabs.SelectedItem = item;
        UpdateTabStrip();
    }

    /// <summary>Takes over a session that was moved from another window.</summary>
    public async Task AdoptSessionAsync(DocumentSession session)
    {
        await _loaded.Task;
        AddDocument(session);
    }

    private void UpdateTabHeader(DocTab tab)
    {
        tab.Item.Header = tab.Session.DisplayName + (tab.Session.IsDirty ? " •" : "");
        ToolTipService.SetToolTip(tab.Item, tab.Session.FilePath ?? tab.Session.DisplayName);
    }

    private void UpdateTabStrip() =>
        Tabs.Visibility = _tabs.Count > 1 || (AppSettings.Current.OpenInTabs && _tabs.Count >= 1) ? Visibility.Visible : Visibility.Collapsed;

    private void HookSession(DocumentSession s, bool on)
    {
        if (on)
        {
            s.DirtyChanged += UpdateChrome;
            s.EncryptionChanged += UpdateChrome;
            s.StructureChanged += OnStructureChanged;
            s.MarkupsChanged += OnMarkupsChanged;
            s.Undo.Changed += UpdateChrome;
        }
        else
        {
            s.DirtyChanged -= UpdateChrome;
            s.EncryptionChanged -= UpdateChrome;
            s.StructureChanged -= OnStructureChanged;
            s.MarkupsChanged -= OnMarkupsChanged;
            s.Undo.Changed -= UpdateChrome;
        }
    }

    /// <summary>Lets go of the active tab (remembering its view) without disposing its session.</summary>
    private void DetachCurrent()
    {
        if (_current == null) return;
        DocView.CommitInlineEditor();
        DocView.KillFormFocus();
        _current.View = DocView.CaptureState();
        _current.SidebarVisible = _sidebarVisible;
        HookSession(_current.Session, false);
        _thumbCts?.Cancel();
        _searchCts?.Cancel();
        DocView.Detach();
        _current = null;
        _session = null;
    }

    private void ActivateTab(DocTab tab)
    {
        if (_current == tab) return;
        DetachCurrent();
        _current = tab;
        _session = tab.Session;
        HookSession(tab.Session, true);
        ClearSearch();
        DocView.Tool = Tool.Select;
        _sidebarVisible = tab.SidebarVisible;
        DocView.Attach(tab.Session);
        RebuildSidebar();
        UpdateChrome();
        if (tab.View is { } view) DocView.RestoreState(view);
        DocView.FocusSurface();
        WarmUpLiveText(0);
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is TabViewItem { Tag: DocTab tab }) ActivateTab(tab);
    }

    private void Tabs_AddTabButtonClick(TabView sender, object args) => _ = OpenAsync(asTabs: true);

    private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab.Tag is DocTab tab) _ = CloseTabAsync(tab);
    }

    // ---------------- Closing ----------------

    /// <summary>Closes a tab after offering to save it. Closing the last tab closes the window.</summary>
    private async Task CloseTabAsync(DocTab tab)
    {
        if (_tabs.Count <= 1)
        {
            await RequestCloseAsync();
            return;
        }
        if (!await ConfirmCloseAsync(tab)) return;
        RemoveTab(tab, dispose: true);
    }

    private Task CloseCurrentTabAsync() => _current != null ? CloseTabAsync(_current) : RequestCloseAsync();

    private async Task CloseOtherTabsAsync(DocTab keep)
    {
        foreach (var other in TabsInOrder().Where(t => t != keep))
        {
            if (!await ConfirmCloseAsync(other)) return;
            RemoveTab(other, dispose: true);
        }
        Tabs.SelectedItem = keep.Item;
    }

    /// <summary>Asks whether to save a document with unsaved changes. False means the user cancelled.</summary>
    private async Task<bool> ConfirmCloseAsync(DocTab tab)
    {
        if (tab == _current) DocView.CommitInlineEditor();
        if (!tab.Session.IsDirty) return true;
        // Show the tab being asked about, and make it the target of Save.
        ActivateTab(tab);
        Tabs.SelectedItem = tab.Item;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = $"Do you want to save changes to “{tab.Session.DisplayName}”?",
            Content = "Your changes will be lost if you don't save them.",
            PrimaryButtonText = "Save", SecondaryButtonText = "Don't Save", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return false;
        return result != ContentDialogResult.Primary || await SaveAsync();
    }

    private void RemoveTab(DocTab tab, bool dispose)
    {
        var ordered = TabsInOrder();
        int index = ordered.IndexOf(tab);
        var next = index + 1 < ordered.Count ? ordered[index + 1] : ordered.ElementAtOrDefault(index - 1);

        if (tab == _current) DetachCurrent();
        tab.Session.DirtyChanged -= tab.DirtyHandler;
        _tabs.Remove(tab);
        Tabs.TabItems.Remove(tab.Item);
        if (dispose) tab.Session.Dispose();

        if (next != null && next != tab) Tabs.SelectedItem = next.Item;
        else
        {
            UpdateChrome();
            RebuildSidebar();
        }
        UpdateTabStrip();
    }

    private void MoveToNewWindow(DocTab tab)
    {
        if (_tabs.Count < 2) return;
        DocView.CommitInlineEditor();
        var session = tab.Session;
        RemoveTab(tab, dispose: false);
        App.Current.OpenWindowWithSession(session);
    }

    private MenuFlyout BuildTabMenu(DocTab tab)
    {
        var menu = new MenuFlyout();
        var close = DocumentView.Item("Close Tab", "", () => _ = CloseTabAsync(tab));
        var others = DocumentView.Item("Close Other Tabs", "", () => _ = CloseOtherTabsAsync(tab));
        var move = DocumentView.Item("Move to New Window", "", () => MoveToNewWindow(tab));
        menu.Items.Add(close);
        menu.Items.Add(others);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(move);
        menu.Opening += (_, _) => others.IsEnabled = move.IsEnabled = _tabs.Count > 1;
        return menu;
    }

    /// <summary>Releases everything this window holds. Safe to call twice.</summary>
    private void Cleanup()
    {
        _thumbCts?.Cancel();
        DetachCurrent();
        foreach (var tab in _tabs)
        {
            tab.Session.DirtyChanged -= tab.DirtyHandler;
            tab.Session.Dispose();
        }
        _tabs.Clear();
    }
}
