using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using WinPreview.Core.Markup;
using WinPreview.Session;
using WinPreview.Views;
using WinRT.Interop;

namespace WinPreview;

public sealed partial class MainWindow
{
    private nint Hwnd => WindowNative.GetWindowHandle(this);

    // ---------------- More menu ----------------

    private void BuildMoreMenu()
    {
        MoreMenu.Opening += (_, _) => FillMoreMenu();
    }

    private void FillMoreMenu()
    {
        var m = MoreMenu.Items;
        m.Clear();
        MenuFlyoutItem Add(string text, string glyph, Action action, string? keys = null, bool enabled = true)
        {
            var item = DocumentView.Item(text, glyph, action);
            item.IsEnabled = enabled;
            if (keys != null) item.KeyboardAcceleratorTextOverride = keys;
            m.Add(item);
            return item;
        }
        bool has = _session != null, pdf = _session?.IsPdf == true;

        Add("Open…", "", () => _ = OpenAsync(), "Ctrl+O");
        Add("New from Clipboard", "", () => _ = NewFromClipboardAsync(), "Ctrl+N");
        Add("Save", "", () => _ = SaveAsync(), "Ctrl+S", has);
        Add("Save As…", "", () => _ = SaveAsAsync(), "Ctrl+Shift+S", has);
        Add("Export…", "", () => _ = ExportAsync(), null, has);
        Add("Print…", "", () => _ = PrintAsync(), "Ctrl+P", has);
        if (pdf)
        {
            Add(_session!.Encryption == null ? "Password Protect…" : "Change or Remove Password…", "", () => _ = PasswordProtectAsync());
            Add("Reduce File Size…", "", () => _ = ReduceFileSizeAsync());
        }
        m.Add(new MenuFlyoutSeparator());
        Add(_session?.Undo.UndoName is { } u ? $"Undo {u}" : "Undo", "", Undo, "Ctrl+Z", _session?.Undo.CanUndo == true);
        Add(_session?.Undo.RedoName is { } r ? $"Redo {r}" : "Redo", "", Redo, "Ctrl+Y", _session?.Undo.CanRedo == true);
        m.Add(new MenuFlyoutSeparator());
        if (has)
        {
            var pages = new MenuFlyoutSubItem { Text = pdf ? "Pages" : "Images", Icon = new FontIcon { Glyph = "" } };
            var tmp = new MenuFlyout();
            AddPageCommands(tmp, SelectedPages());
            foreach (var item in tmp.Items.ToList()) { tmp.Items.Remove(item); pages.Items.Add(item); }
            m.Add(pages);
        }
        Add("Crop to Selection", "", Crop, "Ctrl+K", DocView.RectSelection != null);
        if (_session is { IsPdf: false })
        {
            Add("Adjust Size…", "", () => _ = AdjustSizeAsync());
            Add("Remove Background", "", () => _ = RemoveBackgroundAsync(null));
        }
        m.Add(new MenuFlyoutSeparator());
        Add("Actual Size", "", DocView.ZoomToActualSize, "Ctrl+0", has);
        Add("Zoom to Fit", "", () => DocView.SetFit(FitMode.Page), "Ctrl+9", has);
        Add("Zoom to Width", "", () => DocView.SetFit(FitMode.Width), null, has);
        Add("Slideshow", "", StartSlideshow, "F5", has);
        m.Add(new MenuFlyoutSeparator());
        Add("Document Info", "", () => _ = ShowInspectorAsync(), "Ctrl+I", has);
        Add("Keyboard Shortcuts", "", () => _ = ShowShortcutsAsync());
    }

    public void AddPageCommands(MenuFlyout menu, List<int> pages)
    {
        bool pdf = _session?.IsPdf == true;
        string noun = pdf ? (pages.Count > 1 ? "Pages" : "Page") : (pages.Count > 1 ? "Images" : "Image");
        menu.Items.Add(DocumentView.Item("Rotate Left", "", () => _session?.RotatePages(pages, clockwise: false)));
        menu.Items.Add(DocumentView.Item("Rotate Right", "", () => _session?.RotatePages(pages, clockwise: true)));
        if (pdf) menu.Items.Add(DocumentView.Item("Insert Blank Page", "", () => _session?.InsertBlankPage(pages.Max() + 1)));
        menu.Items.Add(DocumentView.Item($"Insert from File…", "", () => _ = InsertFromFileAsync(pages.Max() + 1)));
        var del = DocumentView.Item($"Delete {noun}", "", DeletePages);
        del.IsEnabled = _session != null && pages.Count < _session.PageCount;
        menu.Items.Add(del);
    }

    private void OnDocContextMenu(MenuFlyout menu, int page)
    {
        AddPageCommands(menu, [page]);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(DocumentView.Item("Zoom In", "", DocView.ZoomIn));
        menu.Items.Add(DocumentView.Item("Zoom Out", "", DocView.ZoomOut));
        menu.Items.Add(DocumentView.Item("Actual Size", "", DocView.ZoomToActualSize));
    }

    // ---------------- Toolbar buttons ----------------

    private void Open_Click(object sender, RoutedEventArgs e) => _ = OpenAsync();
    private void Inspector_Click(object sender, RoutedEventArgs e) => _ = ShowInspectorAsync();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => DocView.ZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => DocView.ZoomOut();
    private void RotateLeft_Click(object sender, RoutedEventArgs e) => RotateCurrent(clockwise: false);
    private void Share_Click(object sender, RoutedEventArgs e) => _ = ShareAsync();

    private void MarkupToggle_Click(object sender, RoutedEventArgs e) => SetMarkupBar(MarkupToggle.IsChecked == true);

    private void SetMarkupBar(bool show)
    {
        MarkupToggle.IsChecked = show;
        MarkupBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) DocView.Tool = Tool.Select;
    }

    private void RotateCurrent(bool clockwise)
    {
        if (_session == null) return;
        var pages = ThumbList.SelectedItems.Count > 1 ? SelectedPages() : [DocView.CurrentPage];
        _session.RotatePages(pages, clockwise);
    }

    private void DeletePages()
    {
        if (_session == null) return;
        var pages = SelectedPages();
        if (pages.Count >= _session.PageCount)
        {
            ShowInfo("Can't delete every page", "A document needs at least one page.");
            return;
        }
        _session.DeletePages(pages);
    }

    private void Undo()
    {
        DocView.CommitInlineEditor();
        _session?.Undo.Undo();
    }

    private void Redo() => _session?.Undo.Redo();

    private void Crop()
    {
        if (_session == null || DocView.RectSelection is not { } sel) return;
        if (_session.IsPdf) _session.CropPdfPage(sel.Page, sel.Rect);
        else _session.CropImage(sel.Page, sel.Rect);
        DocView.Tool = Tool.Select;
    }

    // ---------------- Keyboard ----------------

    private static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Down(VirtualKey.Control), shift = Down(VirtualKey.Shift), alt = Down(VirtualKey.Menu);
        bool inText = e.OriginalSource is TextBox or PasswordBox or NumberBox;
        e.Handled = true;
        switch (e.Key)
        {
            case VirtualKey.O when ctrl: _ = OpenAsync(); break;
            case VirtualKey.N when ctrl: _ = NewFromClipboardAsync(); break;
            case VirtualKey.S when ctrl && alt: ToggleSidebar(); break;
            case VirtualKey.S when ctrl && shift: _ = SaveAsAsync(); break;
            case VirtualKey.S when ctrl: _ = SaveAsync(); break;
            case VirtualKey.W when ctrl: _ = RequestCloseAsync(); break;
            case VirtualKey.P when ctrl: _ = PrintAsync(); break;
            case VirtualKey.Z when ctrl && shift && !inText: Redo(); break;
            case VirtualKey.Z when ctrl && !inText: Undo(); break;
            case VirtualKey.Y when ctrl && !inText: Redo(); break;
            case VirtualKey.C when ctrl && !inText: DocView.CopySelection(); break;
            case VirtualKey.A when ctrl && shift: SetMarkupBar(MarkupBar.Visibility != Visibility.Visible); break;
            case VirtualKey.A when ctrl && !inText: DocView.SelectAllText(); break;
            case VirtualKey.F when ctrl: SearchBox.Focus(FocusState.Keyboard); break;
            case VirtualKey.G when ctrl: StepHit(shift ? -1 : 1); break;
            case (VirtualKey)187 or VirtualKey.Add when ctrl: DocView.ZoomIn(); break;       // '=' / '+'
            case (VirtualKey)189 or VirtualKey.Subtract when ctrl: DocView.ZoomOut(); break; // '-'
            case VirtualKey.Number0 or VirtualKey.NumberPad0 when ctrl: DocView.ZoomToActualSize(); break;
            case VirtualKey.Number9 when ctrl: DocView.SetFit(FitMode.Page); break;
            case VirtualKey.L when ctrl: RotateCurrent(false); break;
            case VirtualKey.R when ctrl: RotateCurrent(true); break;
            case VirtualKey.K when ctrl: Crop(); break;
            case VirtualKey.I when ctrl: _ = ShowInspectorAsync(); break;
            case VirtualKey.Number1 when ctrl && alt: ToggleSidebar(false); break;
            case VirtualKey.Number2 when ctrl && alt: ShowSidebarTab(ThumbsTab); break;
            case VirtualKey.Number3 when ctrl && alt && _session?.IsPdf == true: ShowSidebarTab(TocTab); break;
            case VirtualKey.Number4 when ctrl && alt && _session?.IsPdf == true: ShowSidebarTab(NotesTab); break;
            case VirtualKey.F5: StartSlideshow(); break;
            case VirtualKey.Escape when e.OriginalSource is AutoSuggestBox or TextBox && SearchBox.FocusState != FocusState.Unfocused:
                DocView.FocusSurface();
                break;
            default: e.Handled = false; break;
        }
    }

    // ---------------- Files ----------------

    private void InitPicker(object picker) => InitializeWithWindow.Initialize(picker, Hwnd);

    private async Task OpenAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.Thumbnail };
        picker.FileTypeFilter.Add(".pdf");
        foreach (var ext in ImageCodec.OpenExtensions) picker.FileTypeFilter.Add(ext);
        InitPicker(picker);
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;
        await App.Current.OpenFilesAsync(files.Select(f => f.Path).ToList(), this);
    }

    private async Task NewFromClipboardAsync()
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Bitmap))
        {
            ShowInfo("Nothing to paste", "The clipboard doesn't contain an image.");
            return;
        }
        var reference = await content.GetBitmapAsync();
        using var stream = await reference.OpenReadAsync();
        var frames = await ImageCodec.DecodeAsync(stream);
        var target = HasDocument ? App.Current.OpenWindow(null) : this;
        target.SetSession(DocumentSession.FromImages(frames, null));
    }

    private async Task InsertFromFileAsync(int at)
    {
        if (_session == null) return;
        var picker = new FileOpenPicker();
        if (_session.IsPdf) picker.FileTypeFilter.Add(".pdf");
        foreach (var ext in ImageCodec.OpenExtensions) picker.FileTypeFilter.Add(ext);
        InitPicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;
        try { await _session.InsertFileAsync(file.Path, Math.Min(at, _session.PageCount)); }
        catch (Exception ex) { ShowError("Couldn't insert the file", ex.Message); }
    }

    /// <returns>True when the document was saved.</returns>
    private async Task<bool> SaveAsync()
    {
        if (_session == null) return false;
        DocView.CommitInlineEditor();
        DocView.KillFormFocus();
        if (!_session.CanSaveInPlace) return await SaveAsAsync();

        // Like Preview: offer PNG when transparency would be lost (e.g. after removing a JPEG's background).
        if (!_session.IsPdf && ImageCodec.FormatForExtension(Path.GetExtension(_session.FilePath!)) is { } fmt
            && !ImageCodec.SupportsAlpha(fmt) && _session.HasTransparency)
        {
            var dlg = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Save as PNG?",
                Content = $"This image now has transparent areas, which {fmt.Name} can't store; they would turn white. Save a PNG copy instead?",
                PrimaryButtonText = "Save as PNG", SecondaryButtonText = $"Save as {fmt.Name}", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            var choice = await dlg.ShowAsync();
            if (choice == ContentDialogResult.None) return false;
            if (choice == ContentDialogResult.Primary)
            {
                string png = Path.ChangeExtension(_session.FilePath!, ".png");
                if (File.Exists(png) && !await ConfirmReplaceAsync(png)) return await SaveAsAsync();
                try
                {
                    await _session.SaveAsAsync(png);
                    UpdateChrome();
                    return true;
                }
                catch (Exception ex)
                {
                    ShowError("Couldn't save", ex.Message);
                    return false;
                }
            }
        }

        try
        {
            await _session.SaveAsync();
            UpdateChrome();
            return true;
        }
        catch (Exception ex)
        {
            ShowError("Couldn't save", ex.Message);
            return false;
        }
    }

    private async Task<bool> ConfirmReplaceAsync(string path) =>
        await new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = $"“{Path.GetFileName(path)}” already exists",
            Content = "Do you want to replace it?", PrimaryButtonText = "Replace", CloseButtonText = "Choose Another Name",
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync() == ContentDialogResult.Primary;

    private async Task<bool> SaveAsAsync()
    {
        if (_session == null) return false;
        DocView.CommitInlineEditor();
        var path = await PickSavePathAsync(_session.IsPdf ? "PDF" : (_session.FilePath is { } p && ImageCodec.FormatForExtension(Path.GetExtension(p)) is { } f ? f.Name : "PNG"));
        if (path == null) return false;
        try
        {
            await _session.SaveAsAsync(path);
            UpdateChrome();
            return true;
        }
        catch (Exception ex)
        {
            ShowError("Couldn't save", ex.Message);
            return false;
        }
    }

    private async Task<string?> PickSavePathAsync(string preferredFormat)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(_session?.FilePath ?? "Untitled"),
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        var formats = new List<(string Name, string[] Ext)> { ("PDF", [".pdf"]) };
        formats.AddRange(ImageCodec.SaveFormats.Select(f => (f.Name, f.Extensions)));
        foreach (var (name, ext) in formats.OrderBy(f => f.Name == preferredFormat ? 0 : 1))
            picker.FileTypeChoices.Add(name, ext.ToList());
        InitPicker(picker);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private async Task ExportAsync()
    {
        if (_session == null) return;
        DocView.CommitInlineEditor();
        var formats = new List<string> { "PDF" };
        formats.AddRange(ImageCodec.SaveFormats.Select(f => f.Name));
        var formatBox = new ComboBox { Header = "Format", ItemsSource = formats, SelectedIndex = _session.IsPdf ? Math.Max(0, formats.IndexOf("PNG")) : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var quality = new Slider { Header = "Quality", Minimum = 10, Maximum = 100, Value = 85, StepFrequency = 5 };
        var dpi = new NumberBox { Header = "Resolution (pixels/inch)", Value = 150, Minimum = 36, Maximum = 1200, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var panel = new StackPanel { Spacing = 12, MinWidth = 300, Children = { formatBox, quality, dpi } };
        void Sync()
        {
            string f = formats[Math.Max(0, formatBox.SelectedIndex)];
            quality.Visibility = ImageCodec.SaveFormats.FirstOrDefault(x => x.Name == f)?.SupportsQuality == true ? Visibility.Visible : Visibility.Collapsed;
            dpi.Visibility = _session!.IsPdf && f != "PDF" ? Visibility.Visible : Visibility.Collapsed;
        }
        formatBox.SelectionChanged += (_, _) => Sync();
        Sync();
        var dlg = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "Export", Content = panel,
            PrimaryButtonText = "Choose Location…", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        string format = formats[formatBox.SelectedIndex];
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(_session.FilePath ?? "Untitled") };
        var exts = format == "PDF" ? [".pdf"] : ImageCodec.SaveFormats.First(f => f.Name == format).Extensions;
        picker.FileTypeChoices.Add(format, exts.ToList());
        InitPicker(picker);
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        try
        {
            await _session.ExportAsync(file.Path, quality.Value / 100, dpi.Value);
            if (_session.IsPdf && format != "PDF" && _session.PageCount > 1 && !ImageCodec.SaveFormats.First(f => f.Name == format).MultiFrame)
                ShowInfo("Exported the first page", $"{format} files hold one image. Use TIFF or PDF to keep every page.");
            else ShowInfo("Exported", file.Name, InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowError("Couldn't export", ex.Message); }
    }

    private async Task AdjustSizeAsync()
    {
        if (_session is not { IsPdf: false }) return;
        int page = DocView.CurrentPage;
        var f = _session.Pages[page].Frame!;
        var w = new NumberBox { Header = "Width (px)", Value = f.Width, Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var h = new NumberBox { Header = "Height (px)", Value = f.Height, Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var res = new NumberBox { Header = "Resolution (pixels/inch)", Value = Math.Round(f.DpiX), Minimum = 1, Maximum = 4800 };
        var keep = new CheckBox { Content = "Scale proportionally", IsChecked = true };
        var resample = new CheckBox { Content = "Resample image", IsChecked = true };
        var info = new TextBlock { Opacity = 0.7, FontSize = 12 };
        bool syncing = false;
        void Update()
        {
            info.Text = $"Print size: {w.Value / res.Value:0.##} × {h.Value / res.Value:0.##} in";
        }
        w.ValueChanged += (_, e) =>
        {
            if (syncing || double.IsNaN(e.NewValue)) return;
            syncing = true;
            if (keep.IsChecked == true) h.Value = Math.Max(1, Math.Round(e.NewValue * f.Height / f.Width));
            syncing = false;
            Update();
        };
        h.ValueChanged += (_, e) =>
        {
            if (syncing || double.IsNaN(e.NewValue)) return;
            syncing = true;
            if (keep.IsChecked == true) w.Value = Math.Max(1, Math.Round(e.NewValue * f.Width / f.Height));
            syncing = false;
            Update();
        };
        res.ValueChanged += (_, _) => Update();
        resample.Checked += (_, _) => { w.IsEnabled = h.IsEnabled = true; };
        resample.Unchecked += (_, _) => { w.Value = f.Width; h.Value = f.Height; w.IsEnabled = h.IsEnabled = false; };
        Update();
        var dlg = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "Adjust Size",
            Content = new StackPanel { Spacing = 10, MinWidth = 300, Children = { w, h, res, keep, resample, info } },
            PrimaryButtonText = "OK", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        await _session.ResizeImageAsync(page, (int)w.Value, (int)h.Value, res.Value);
    }

    // ---------------- Inspector ----------------

    private async Task ShowInspectorAsync()
    {
        if (_session == null) return;
        var rows = new List<(string, string)>();
        if (_session.FilePath is { } path && File.Exists(path))
        {
            var fi = new FileInfo(path);
            rows.Add(("File", fi.Name));
            rows.Add(("Location", fi.DirectoryName ?? ""));
            rows.Add(("File size", FormatBytes(fi.Length)));
            rows.Add(("Modified", fi.LastWriteTime.ToString("g")));
        }
        if (_session.Pdf is { } pdf)
        {
            foreach (var tag in new[] { "Title", "Author", "Subject", "Keywords", "Creator", "Producer" })
                if (pdf.GetMetadata(tag) is { Length: > 0 } v) rows.Add((tag, v));
            rows.Add(("Pages", _session.PageCount.ToString()));
            var g = _session.GetGeometry(DocView.CurrentPage);
            rows.Add(("Page size", $"{g.Width / 72:0.##} × {g.Height / 72:0.##} in ({g.Width / 72 * 25.4:0} × {g.Height / 72 * 25.4:0} mm)"));
            rows.Add(("Encrypted", pdf.IsEncrypted ? "Yes" : "No"));
            rows.Add(("Fillable form", pdf.HasForm ? "Yes" : "No"));
        }
        else
        {
            var f = _session.Pages[DocView.CurrentPage].Frame!;
            rows.Add(("Dimensions", $"{f.Width} × {f.Height} pixels"));
            rows.Add(("Resolution", $"{f.DpiX:0} × {f.DpiY:0} pixels/inch"));
            rows.Add(("Print size", $"{f.Width / f.DpiX:0.##} × {f.Height / f.DpiY:0.##} in"));
            if (_session.PageCount > 1) rows.Add(("Frames", _session.PageCount.ToString()));
        }
        int markups = _session.Pages.Sum(p => p.Markups.Count);
        rows.Add(("Markups", markups.ToString()));

        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = rows[i].Item1, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right };
            var v = new TextBlock { Text = rows[i].Item2, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 340 };
            Grid.SetRow(k, i);
            Grid.SetRow(v, i);
            Grid.SetColumn(v, 1);
            grid.Children.Add(k);
            grid.Children.Add(v);
        }
        await new ContentDialog { XamlRoot = Root.XamlRoot, Title = _session.DisplayName, Content = grid, CloseButtonText = "Close" }.ShowAsync();
    }

    private static string FormatBytes(long b) => b switch
    {
        < 1024 => $"{b} bytes",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        _ => $"{b / 1024.0 / 1024.0:0.##} MB",
    };

    private async Task ShowShortcutsAsync()
    {
        const string text = """
            Ctrl+O	Open                      Ctrl+S	Save
            Ctrl+Shift+S	Save As               Ctrl+N	New from Clipboard
            Ctrl+Z / Ctrl+Y	Undo / Redo        Ctrl+C	Copy
            Ctrl+F	Search                     Ctrl+G	Next match (Shift: previous)
            Ctrl+= / Ctrl+−	Zoom in / out      Ctrl+0	Actual size
            Ctrl+9	Zoom to fit                Ctrl+L / Ctrl+R	Rotate left / right
            Ctrl+K	Crop to selection          Ctrl+I	Document info
            Ctrl+Shift+A	Markup toolbar       Ctrl+Alt+S	Sidebar
            Ctrl+Alt+2/3/4	Thumbnails / Contents / Highlights & Notes
            F5	Slideshow                     Delete	Delete selected markup or pages
            Shift (while drawing)	Constrain shapes and lines
            """;
        await new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "Keyboard Shortcuts", CloseButtonText = "Close",
            Content = new TextBlock { Text = text, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12, IsTextSelectionEnabled = true },
        }.ShowAsync();
    }

    // ---------------- Print ----------------

    private async Task PrintAsync()
    {
        if (_session == null) return;
        DocView.CommitInlineEditor();
        DocView.KillFormFocus();
        Log.Write("print requested");
        try { await Printing.PrintAsync(Hwnd, _session); }
        catch (Exception ex)
        {
            Log.Write($"print failed: {ex}");
            ShowError("Couldn't print", ex.Message);
        }
    }

    // ---------------- Slideshow ----------------

    private void StartSlideshow()
    {
        if (_session == null) return;
        DocView.CommitInlineEditor();
        new SlideshowWindow(_session, DocView.CurrentPage).Activate();
    }

    // ---------------- Share ----------------

    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        nint GetForWindow(nint appWindow, in Guid riid);
        void ShowShareUIForWindow(nint appWindow);
    }

    private async Task ShareAsync()
    {
        if (_session == null) return;
        DocView.CommitInlineEditor();
        try
        {
            // Share what's on screen, including unsaved markups.
            string name = Path.GetFileNameWithoutExtension(_session.FilePath ?? "Untitled");
            string ext = _session.IsPdf ? ".pdf" : (_session.FilePath is { } p && ImageCodec.FormatForExtension(Path.GetExtension(p)) != null ? Path.GetExtension(p) : ".png");
            string dir = Path.Combine(Path.GetTempPath(), "WinPreview", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(dir, name + ext);
            await _session.ExportAsync(tmp);
            var file = await StorageFile.GetFileFromPathAsync(tmp);

            var factory = WinRT.ActivationFactory.Get("Windows.ApplicationModel.DataTransfer.DataTransferManager");
            var interop = (IDataTransferManagerInterop)Marshal.GetObjectForIUnknown(factory.ThisPtr);
            var iid = new Guid("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C"); // IDataTransferManager
            var dtm = DataTransferManager.FromAbi(interop.GetForWindow(Hwnd, iid));
            void OnRequested(DataTransferManager s, DataRequestedEventArgs a)
            {
                a.Request.Data.Properties.Title = Path.GetFileName(tmp);
                a.Request.Data.SetStorageItems([file]);
                s.DataRequested -= OnRequested;
            }
            dtm.DataRequested += OnRequested;
            interop.ShowShareUIForWindow(Hwnd);
        }
        catch (Exception ex) { ShowError("Couldn't share", ex.Message); }
    }
}
