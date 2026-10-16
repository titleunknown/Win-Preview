using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using WinPreview.Core.Signatures;
using WinPreview.Session;
using WinPreview.Views;

namespace WinPreview;

public sealed partial class MainWindow : Window
{
    private DocumentSession? _session;
    private readonly SignatureStore _signatures = new();
    private bool _closeConfirmed;
    /// <summary>Completes once the window content is loaded; dialogs (e.g. the password prompt) need a XamlRoot.</summary>
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MainWindow()
    {
        InitializeComponent();
        Title = "Win-Preview";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        Root.Loaded += (_, _) =>
        {
            double scale = Root.XamlRoot.RasterizationScale;
            AppWindow.Resize(new SizeInt32((int)(1180 * scale), (int)(860 * scale)));
            UpdateCaptionInset();
            _loaded.TrySetResult();
        };
        Root.SizeChanged += (_, _) => UpdateCaptionInset();
        AppWindow.Closing += OnClosing;

        DocView.CurrentPageChanged += OnCurrentPageChanged;
        DocView.ZoomChanged += UpdateSubtitle;
        DocView.SelectionChanged += OnDocSelectionChanged;
        DocView.ToolChanged += SyncToolButtons;
        DocView.CropRequested += all => Crop(all);
        Info.Closed += (_, _) => Info.Visibility = Visibility.Collapsed; // also when the user clicks its X
        DocView.ContextMenuOpening += OnDocContextMenu;

        BuildMarkupToolbar();
        BuildHighlightMenu();
        BuildMoreMenu();
        UpdateChrome();
    }

    public bool HasDocument => _session != null;

    private void UpdateCaptionInset()
    {
        if (Root.XamlRoot == null) return;
        CaptionColumn.Width = new GridLength(Math.Max(0, AppWindow.TitleBar.RightInset / Root.XamlRoot.RasterizationScale));
    }

    // ---------------- Opening ----------------

    public async Task OpenFilesAsync(IReadOnlyList<string> files)
    {
        try
        {
            await _loaded.Task; // a file given on the command line is opened before the window has loaded
            DocumentSession session;
            if (files.Count > 1 && files.All(ImageCodec.IsImage))
            {
                // Several images open together as one multi-page window, like Preview.
                var frames = new List<ImageFrame>();
                foreach (var f in files) frames.AddRange(await ImageCodec.DecodeAsync(f));
                session = DocumentSession.FromImages(frames, files.Count == 1 ? files[0] : null);
            }
            else
            {
                foreach (var extra in files.Skip(1)) App.Current.OpenWindow([extra]);
                session = await DocumentSession.OpenAsync(files[0], wrong => PromptPasswordAsync(Path.GetFileName(files[0]), wrong));
            }
            SetSession(session);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowError("Couldn't open the file", ex.Message);
        }
    }

    private void SetSession(DocumentSession session)
    {
        if (_session != null)
        {
            _session.DirtyChanged -= UpdateChrome;
            _session.EncryptionChanged -= UpdateChrome;
            _session.StructureChanged -= OnStructureChanged;
            _session.MarkupsChanged -= OnMarkupsChanged;
            _session.Undo.Changed -= UpdateChrome;
            DocView.Detach();
            _session.Dispose();
        }
        _session = session;
        session.DirtyChanged += UpdateChrome;
        session.EncryptionChanged += UpdateChrome;
        session.StructureChanged += OnStructureChanged;
        session.MarkupsChanged += OnMarkupsChanged;
        session.Undo.Changed += UpdateChrome;
        ClearSearch();
        DocView.Tool = Tool.Select;
        // Like Preview, a single image opens without the sidebar; the sidebar button shows it.
        _sidebarVisible = session.IsPdf || session.PageCount > 1;
        DocView.Attach(session);
        RebuildSidebar();
        UpdateChrome();
        DocView.FocusSurface();
        WarmUpLiveText(0);
    }

    private void OnStructureChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        ClearSearch();
        RebuildSidebar();
        UpdateChrome();
    });

    private void OnMarkupsChanged(int page) => DispatcherQueue.TryEnqueue(() =>
    {
        RebuildNotes();
        QueueThumbRefresh(page);
    });

    private async Task<string?> PromptPasswordAsync(string name, bool wrong)
    {
        var box = new PasswordBox { PlaceholderText = "Password" };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = wrong ? "That password is incorrect. Try again." : $"“{name}” is password protected.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(box);
        var dlg = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "Enter Password", Content = content,
            PrimaryButtonText = "Open", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        return await dlg.ShowAsync() == ContentDialogResult.Primary ? box.Password : null;
    }

    // ---------------- Chrome ----------------

    private void UpdateChrome()
    {
        bool has = _session != null;
        EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        DocView.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        MainToolbar.Opacity = has ? 1 : 0.4;
        MainToolbar.IsHitTestVisible = has;
        if (!has)
        {
            Sidebar.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);
        }
        string name = _session?.DisplayName ?? "Win-Preview";
        TitleText.Text = _session?.IsDirty == true ? $"{name} — Edited" : name;
        Title = _session != null ? $"{name} - Win-Preview" : "Win-Preview";
        SearchBox.IsEnabled = _session != null;
        SearchBox.PlaceholderText = _session is { IsPdf: false } ? "Search text in image" : "Search";
        HighlightButton.IsEnabled = _session?.IsPdf == true;
        UpdateSubtitle();
        UpdateToolAvailability();
    }

    private void UpdateSubtitle()
    {
        if (_session == null) { SubtitleText.Text = ""; return; }
        string zoom = $"{Math.Round(DocView.Zoom * 100)}%";
        if (_session.IsPdf)
            SubtitleText.Text = $"Page {DocView.CurrentPage + 1} of {_session.PageCount} · {zoom}" + (_session.Encryption != null ? " · 🔒 Password protected" : "");
        else
        {
            var f = _session.Pages[Math.Clamp(DocView.CurrentPage, 0, _session.PageCount - 1)].Frame!;
            string count = _session.PageCount > 1 ? $"{DocView.CurrentPage + 1} of {_session.PageCount} · " : "";
            SubtitleText.Text = $"{count}{f.Width} × {f.Height} px · {zoom}";
        }
    }

    private void OnCurrentPageChanged(int page)
    {
        UpdateSubtitle();
        SelectThumb(page);
        WarmUpLiveText(page);
    }

    /// <summary>Starts OCR on an image page in the background so its text is selectable when the pointer gets there.</summary>
    private void WarmUpLiveText(int page)
    {
        if (_session is { IsPdf: false } s && page < s.PageCount) _ = s.GetTextLayerAsync(page);
    }

    public void ShowError(string title, string message) => ShowInfo(title, message, InfoBarSeverity.Error);

    public void ShowInfo(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        _infoTimer?.Stop(); // an older message's timer must not close this one early
        Info.Title = title;
        Info.Message = message;
        Info.Severity = severity;
        Info.Visibility = Visibility.Visible;
        Info.IsOpen = true;
        var timer = _infoTimer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(severity == InfoBarSeverity.Error ? 8 : 4);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => HideInfo();
        timer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _infoTimer;

    /// <summary>Closes the message bar and collapses it, so no empty shell is left over the page.</summary>
    private void HideInfo()
    {
        Info.IsOpen = false;
        Info.Visibility = Visibility.Collapsed;
    }

    // ---------------- Closing ----------------

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || _session is not { IsDirty: true }) { Cleanup(); return; }
        args.Cancel = true;
        await RequestCloseAsync();
    }

    /// <summary>Closes the window, first offering to save unsaved changes. (Window.Close() skips AppWindow.Closing.)</summary>
    public async Task RequestCloseAsync()
    {
        DocView.CommitInlineEditor();
        if (_session is not { IsDirty: true })
        {
            Cleanup();
            _closeConfirmed = true;
            Close();
            return;
        }
        var dlg = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = $"Do you want to save changes to “{_session.DisplayName}”?",
            Content = "Your changes will be lost if you don't save them.",
            PrimaryButtonText = "Save", SecondaryButtonText = "Don't Save", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.None) return;
        if (result == ContentDialogResult.Primary && !await SaveAsync()) return;
        Cleanup();
        _closeConfirmed = true;
        Close();
    }

    private void Cleanup()
    {
        _thumbCts?.Cancel();
        DocView.Detach();
        _session?.Dispose();
        _session = null;
    }

    // ---------------- Drag & drop ----------------

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Open";
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var files = items.OfType<StorageFile>().Select(f => f.Path).Where(p => DocumentSession.LooksLikePdf(p) || ImageCodec.IsImage(p)).ToList();
        if (files.Count == 0) return;
        await App.Current.OpenFilesAsync(files, this);
    }
}
