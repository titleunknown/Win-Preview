using WinPreview.Core;
using WinPreview.Core.Markup;
using WinPreview.Core.Pdf;
using WinPreview.Core.Text;

namespace WinPreview.Session;

public enum DocumentKind { Pdf, Image }

/// <summary>Page size and coordinate mapping. Display units are points (PDF) or pixels (images).</summary>
public sealed record PageGeometry(double Width, double Height, Affine UserToDisplay, double UnitToDip)
{
    public Affine DisplayToUser { get; } = UserToDisplay.Invert();
}

public sealed class PageState
{
    public List<MarkupItem> Markups { get; set; } = [];
    /// <summary>Pixels for image documents; null for PDF pages.</summary>
    public ImageFrame? Frame { get; set; }

    public PageState Clone() => new() { Markups = Markups.Select(m => m.Clone()).ToList(), Frame = Frame?.Clone() };
}

/// <summary>
/// An open document: the PDF or image pixels, the editable markup layer on each page, undo history and
/// dirty state. Everything the window shows is derived from this.
/// </summary>
public sealed class DocumentSession : IDisposable
{
    private readonly List<PageGeometry?> _geometry = [];
    private readonly Dictionary<int, byte[]> _premultiplied = [];

    public DocumentKind Kind { get; }
    public string? FilePath { get; private set; }
    public string DisplayName => FilePath != null ? Path.GetFileName(FilePath) : "Untitled";
    public PdfDocument? Pdf { get; private set; }
    public List<PageState> Pages { get; private set; }
    public UndoManager Undo { get; } = new();
    public bool IsDirty { get; private set; }
    public bool IsPdf => Kind == DocumentKind.Pdf;
    public int PageCount => Pages.Count;

    public event Action? DirtyChanged;
    /// <summary>Pages were added, removed, reordered or resized; the whole layout must be rebuilt.</summary>
    public event Action? StructureChanged;
    /// <summary>A page's rendered content changed (form edits, image edits).</summary>
    public event Action<int>? PageContentChanged;
    public event Action<int>? MarkupsChanged;

    private DocumentSession(DocumentKind kind, string? path, PdfDocument? pdf, List<PageState> pages)
    {
        Kind = kind;
        FilePath = path;
        Pages = pages;
        AttachPdf(pdf);
        ResetGeometry();
    }

    // ---------------- Opening ----------------

    public static bool LooksLikePdf(string path)
    {
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[5];
            return fs.Read(head) == 5 && head.SequenceEqual("%PDF-"u8);
        }
        catch (IOException) { return false; }
    }

    /// <param name="passwordPrompt">Called with true when a previous attempt was wrong; return null to cancel.</param>
    public static async Task<DocumentSession> OpenAsync(string path, Func<bool, Task<string?>> passwordPrompt)
    {
        if (LooksLikePdf(path))
        {
            string? password = null;
            while (true)
            {
                try
                {
                    var pdf = await Task.Run(() => PdfDocument.Open(path, password));
                    var session = FromPdf(pdf, path);
                    // A PDF opened with a password stays protected with that password when saved, like Preview.
                    if (password != null) session.Encryption = new PdfEncryption(password);
                    return session;
                }
                catch (PdfPasswordException e)
                {
                    password = await passwordPrompt(e.WrongPassword);
                    if (password == null) throw new OperationCanceledException();
                }
            }
        }
        var frames = await ImageCodec.DecodeAsync(path);
        return FromImages(frames, path);
    }

    public static DocumentSession FromPdf(PdfDocument pdf, string? path)
    {
        var markups = PdfMarkupReader.ExtractAll(pdf);
        var pages = Enumerable.Range(0, pdf.PageCount)
            .Select(i => new PageState { Markups = markups.GetValueOrDefault(i) ?? [] }).ToList();
        return new DocumentSession(DocumentKind.Pdf, path, pdf, pages);
    }

    public static DocumentSession FromImages(List<ImageFrame> frames, string? path) =>
        new(DocumentKind.Image, path, null, frames.Select(f => new PageState { Frame = f }).ToList());

    private void AttachPdf(PdfDocument? pdf)
    {
        if (Pdf != null)
        {
            Pdf.FormInvalidated -= OnFormInvalidated;
            Pdf.FormChanged -= OnFormChanged;
        }
        Pdf = pdf;
        if (pdf != null)
        {
            pdf.FormInvalidated += OnFormInvalidated;
            pdf.FormChanged += OnFormChanged;
        }
    }

    private void OnFormInvalidated(int page) => PageContentChanged?.Invoke(page);
    private void OnFormChanged() => MarkDirty();

    // ---------------- Geometry & rendering ----------------

    private void ResetGeometry()
    {
        _geometry.Clear();
        _geometry.AddRange(Enumerable.Repeat<PageGeometry?>(null, Pages.Count));
        _premultiplied.Clear();
        _textLayers.Clear();
        _ocrTasks.Clear();
        _contentGeneration++;
    }

    // ---------------- Text (PDF text or OCR'd image text) ----------------

    private readonly Dictionary<int, TextLayer?> _textLayers = [];
    private readonly Dictionary<int, Task<TextLayer?>> _ocrTasks = [];
    private int _contentGeneration;

    /// <summary>Raised when OCR finishes for an image page, making its text selectable.</summary>
    public event Action<int>? TextLayerReady;

    /// <summary>The page's text, or null while OCR is still running (or when there is none).</summary>
    public TextLayer? GetTextLayer(int page)
    {
        if (page < 0 || page >= Pages.Count) return null;
        if (_textLayers.TryGetValue(page, out var layer)) return layer;
        if (Pdf != null) return _textLayers[page] = new PdfTextLayer(Pdf, page);
        _ = GetTextLayerAsync(page);
        return null;
    }

    public Task<TextLayer?> GetTextLayerAsync(int page)
    {
        if (_textLayers.TryGetValue(page, out var layer)) return Task.FromResult(layer);
        if (Pdf != null) return Task.FromResult(GetTextLayer(page));
        if (_ocrTasks.TryGetValue(page, out var running)) return running;
        int gen = _contentGeneration;
        var frame = Pages[page].Frame!.Clone();
        var task = RunOcrAsync(page, frame, gen);
        _ocrTasks[page] = task;
        return task;
    }

    private async Task<TextLayer?> RunOcrAsync(int page, ImageFrame frame, int gen)
    {
        TextLayer? layer;
        try { layer = await Services.OcrService.RecognizeAsync(frame); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OCR failed: {ex.Message}");
            layer = null;
        }
        if (gen != _contentGeneration) return null; // pixels changed while recognizing
        _textLayers[page] = layer;
        TextLayerReady?.Invoke(page);
        return layer;
    }

    public PageGeometry GetGeometry(int index)
    {
        if (_geometry[index] is { } g) return g;
        if (Pdf != null)
        {
            var info = Pdf.GetPageInfo(index);
            g = new PageGeometry(info.Width, info.Height, info.UserToDisplay, 96.0 / 72);
        }
        else
        {
            var f = Pages[index].Frame!;
            g = new PageGeometry(f.Width, f.Height, Affine.Identity, 1);
        }
        return (_geometry[index] = g)!;
    }

    /// <summary>Premultiplied BGRA pixels of the page (images ignore the requested size).</summary>
    public Task<(byte[] Pixels, int Width, int Height)> RenderAsync(int index, int widthPx, int heightPx)
    {
        if (Pdf is { } pdf)
            return Task.Run(() => (pdf.Render(index, widthPx, heightPx), widthPx, heightPx));

        var f = Pages[index].Frame!;
        if (!_premultiplied.TryGetValue(index, out var px))
            _premultiplied[index] = px = Core.Imaging.AlphaTools.HasTransparency(f.Pixels)
                ? ImageCodec.OnCheckerboard(f.Pixels, f.Width, f.Height)
                : ImageCodec.Premultiply(f.Pixels);
        return Task.FromResult((px, f.Width, f.Height));
    }

    // ---------------- Dirty tracking ----------------

    private void MarkDirty()
    {
        if (IsDirty) return;
        IsDirty = true;
        DirtyChanged?.Invoke();
    }

    private void MarkClean()
    {
        if (!IsDirty) return;
        IsDirty = false;
        DirtyChanged?.Invoke();
    }

    // ---------------- Markup editing ----------------

    public MarkupItem? FindMarkup(int page, Guid id) => Pages[page].Markups.FirstOrDefault(m => m.Id == id);

    public void AddMarkup(int page, MarkupItem item, string name = "Add Markup")
    {
        var copy = item.Clone();
        Pages[page].Markups.Add(item);
        Changed(page);
        Undo.Push(name,
            () => { Pages[page].Markups.RemoveAll(m => m.Id == copy.Id); Changed(page); },
            () => { Pages[page].Markups.Add(copy.Clone()); Changed(page); });
    }

    public void RemoveMarkup(int page, Guid id)
    {
        int index = Pages[page].Markups.FindIndex(m => m.Id == id);
        if (index < 0) return;
        var removed = Pages[page].Markups[index];
        Pages[page].Markups.RemoveAt(index);
        Changed(page);
        Undo.Push("Delete Markup",
            () => { Pages[page].Markups.Insert(Math.Min(index, Pages[page].Markups.Count), removed.Clone()); Changed(page); },
            () => { Pages[page].Markups.RemoveAll(m => m.Id == id); Changed(page); });
    }

    /// <summary>Records an in-place edit: <paramref name="before"/> is a clone taken before the item was changed.</summary>
    public void CommitEdit(int page, MarkupItem before, string name = "Edit Markup")
    {
        var after = FindMarkup(page, before.Id)?.Clone();
        if (after == null) return;
        Changed(page);
        Undo.Push(name, () => Replace(page, before.Clone()), () => Replace(page, after.Clone()));
    }

    /// <summary>Mutates a markup and records the change for undo.</summary>
    public void EditMarkup(int page, Guid id, Action<MarkupItem> mutate, string name = "Edit Markup")
    {
        var item = FindMarkup(page, id);
        if (item == null) return;
        var before = item.Clone();
        mutate(item);
        CommitEdit(page, before, name);
    }

    private void Replace(int page, MarkupItem item)
    {
        int i = Pages[page].Markups.FindIndex(m => m.Id == item.Id);
        if (i >= 0) Pages[page].Markups[i] = item;
        Changed(page);
    }

    private void Changed(int page)
    {
        MarkDirty();
        MarkupsChanged?.Invoke(page);
    }

    // ---------------- Page operations ----------------

    private sealed record Snapshot(byte[]? PdfBytes, List<PageState> Pages);

    private Snapshot TakeSnapshot()
    {
        byte[]? bytes = null;
        if (Pdf != null)
        {
            using var ms = new MemoryStream();
            Pdf.Save(ms);
            bytes = ms.ToArray();
        }
        return new Snapshot(bytes, Pages.Select(p => p.Clone()).ToList());
    }

    private void Restore(Snapshot s)
    {
        if (s.PdfBytes != null)
        {
            var old = Pdf;
            AttachPdf(PdfDocument.Open(s.PdfBytes));
            old?.Dispose();
        }
        Pages = s.Pages.Select(p => p.Clone()).ToList();
        ResetGeometry();
        MarkDirty();
        StructureChanged?.Invoke();
    }

    /// <summary>Runs a structural edit with whole-document snapshot undo.</summary>
    private void Structural(string name, Action edit)
    {
        try
        {
            var before = TakeSnapshot();
            edit();
            ResetGeometry();
            var after = TakeSnapshot();
            MarkDirty();
            StructureChanged?.Invoke();
            Undo.Push(name, () => Restore(before), () => Restore(after));
            Log.Write($"structural '{name}' ok, pages={Pages.Count}");
        }
        catch (Exception ex)
        {
            Log.Write($"structural '{name}' FAILED: {ex}");
            throw;
        }
    }

    public void RotatePages(IReadOnlyList<int> pages, bool clockwise)
    {
        if (pages.Count == 0) return;
        Structural(clockwise ? "Rotate Right" : "Rotate Left", () =>
        {
            foreach (int p in pages)
            {
                if (Pdf != null)
                {
                    Pdf.RotatePage(p, clockwise ? 90 : -90);
                    continue;
                }
                var f = Pages[p].Frame!;
                var m = MarkupRasterizer.RotationTransform(f.Width, f.Height, clockwise);
                f.Pixels = MarkupRasterizer.Rotate90(f.Pixels, f.Width, f.Height, clockwise);
                (f.Width, f.Height, f.DpiX, f.DpiY) = (f.Height, f.Width, f.DpiY, f.DpiX);
                foreach (var item in Pages[p].Markups) item.Transform(m);
            }
        });
    }

    public void DeletePages(IReadOnlyList<int> pages)
    {
        var sorted = pages.Distinct().OrderByDescending(p => p).ToList();
        if (sorted.Count == 0 || sorted.Count >= Pages.Count) return; // never delete every page
        Structural(sorted.Count == 1 ? "Delete Page" : "Delete Pages", () =>
        {
            foreach (int p in sorted)
            {
                Pdf?.DeletePage(p);
                Pages.RemoveAt(p);
            }
        });
    }

    /// <summary>Reorders pages so that position k holds the page previously at <paramref name="newOrder"/>[k].</summary>
    public void ApplyOrder(IReadOnlyList<int> newOrder)
    {
        if (newOrder.SequenceEqual(Enumerable.Range(0, Pages.Count))) return;
        Structural("Move Pages", () =>
        {
            var current = Enumerable.Range(0, Pages.Count).ToList(); // current[pos] = original index
            for (int k = 0; k < newOrder.Count; k++)
            {
                int pos = current.IndexOf(newOrder[k]);
                if (pos == k) continue;
                Pdf?.MovePages([pos], k);
                current.RemoveAt(pos);
                current.Insert(k, newOrder[k]);
            }
            var old = Pages;
            Pages = newOrder.Select(i => old[i]).ToList();
        });
    }

    public void InsertBlankPage(int at)
    {
        if (Pdf == null) return;
        var size = at > 0 ? GetGeometry(at - 1) : GetGeometry(0);
        Structural("Insert Page", () =>
        {
            Pdf.InsertBlankPage(at, size.Width, size.Height);
            Pages.Insert(at, new PageState());
        });
    }

    /// <summary>Inserts every page of a PDF or image file at <paramref name="at"/>.</summary>
    public async Task InsertFileAsync(string path, int at)
    {
        if (Pdf != null)
        {
            using var source = await LoadAsPdfAsync(path);
            var markups = PdfMarkupReader.ExtractAll(source);
            Structural("Insert Pages", () =>
            {
                Pdf.ImportPages(source, null, at);
                for (int i = 0; i < source.PageCount; i++)
                    Pages.Insert(at + i, new PageState { Markups = markups.GetValueOrDefault(i) ?? [] });
            });
        }
        else
        {
            var frames = await ImageCodec.DecodeAsync(path);
            Structural("Insert Images", () => Pages.InsertRange(at, frames.Select(f => new PageState { Frame = f })));
        }
    }

    private static async Task<PdfDocument> LoadAsPdfAsync(string path)
    {
        if (LooksLikePdf(path)) return await Task.Run(() => PdfDocument.Open(path));
        var frames = await ImageCodec.DecodeAsync(path);
        var pdf = PdfDocument.CreateEmpty();
        foreach (var f in frames) pdf.AddImagePage(f.Pixels, f.Width, f.Height, f.Width * 72 / f.DpiX, f.Height * 72 / f.DpiY);
        return pdf;
    }

    /// <summary>Crops an image page to a rectangle in pixel space.</summary>
    public void CropImage(int page, RectD rect)
    {
        var f = Pages[page].Frame;
        if (f == null) return;
        int x = (int)Math.Clamp(Math.Floor(rect.X1), 0, f.Width - 1), y = (int)Math.Clamp(Math.Floor(rect.Y1), 0, f.Height - 1);
        int w = (int)Math.Clamp(Math.Ceiling(rect.X2) - x, 1, f.Width - x), h = (int)Math.Clamp(Math.Ceiling(rect.Y2) - y, 1, f.Height - y);
        Structural("Crop", () =>
        {
            f.Pixels = MarkupRasterizer.Crop(f.Pixels, f.Width, x, y, w, h);
            (f.Width, f.Height) = (w, h);
            foreach (var item in Pages[page].Markups) item.Offset(-x, -y);
        });
    }

    /// <summary>Crops a PDF page's visible area (CropBox) to a rectangle in user space.</summary>
    public void CropPdfPage(int page, RectD userRect)
    {
        if (Pdf == null) return;
        Structural("Crop", () => Pdf.SetCropBox(page, userRect));
    }

    public async Task ResizeImageAsync(int page, int width, int height, double dpi)
    {
        var f = Pages[page].Frame;
        if (f == null) return;
        var resized = await ImageResizer.ResizeAsync(f.Pixels, f.Width, f.Height, width, height);
        double sx = (double)width / f.Width, sy = (double)height / f.Height;
        Structural("Adjust Size", () =>
        {
            foreach (var item in Pages[page].Markups) item.Transform(new Affine(sx, 0, 0, sy, 0, 0));
            f.Pixels = resized;
            (f.Width, f.Height, f.DpiX, f.DpiY) = (width, height, dpi, dpi);
        });
    }

    /// <summary>Instant Alpha: clears the region connected to a pixel whose colors are within tolerance.</summary>
    public void InstantAlpha(int page, int x, int y, double tolerance)
    {
        var f = Pages[page].Frame;
        if (f == null) return;
        Structural("Instant Alpha", () =>
        {
            var sel = Core.Imaging.AlphaTools.FloodSelect(f.Pixels, f.Width, f.Height, x, y, tolerance);
            Core.Imaging.AlphaTools.ClearSelected(f.Pixels, f.Width, f.Height, sel);
        });
    }

    /// <summary>Multiplies a page's alpha by a foreground mask (Remove Background).</summary>
    public void ApplyAlphaMask(int page, float[] mask, int maskSize)
    {
        var f = Pages[page].Frame;
        if (f == null) return;
        Structural("Remove Background", () => Core.Imaging.AlphaTools.ApplyMask(f.Pixels, f.Width, f.Height, mask, maskSize, maskSize));
    }

    public bool HasTransparency => Pages.Any(p => p.Frame != null && Core.Imaging.AlphaTools.HasTransparency(p.Frame.Pixels));

    // ---------------- Password protection ----------------

    /// <summary>The password protection applied whenever this PDF is saved, or null for none.</summary>
    public PdfEncryption? Encryption { get; private set; }

    public event Action? EncryptionChanged;

    /// <summary>Sets or removes password protection. It takes effect on the next save or export.</summary>
    public void SetEncryption(PdfEncryption? encryption)
    {
        if (!IsPdf) return;
        var before = Encryption;
        Apply(encryption);
        Undo.Push(encryption == null ? "Remove Password" : "Password Protect", () => Apply(before), () => Apply(encryption));

        void Apply(PdfEncryption? value)
        {
            Encryption = value;
            MarkDirty();
            EncryptionChanged?.Invoke();
        }
    }

    // ---------------- Saving & exporting ----------------

    /// <summary>The PDF as it would be saved, markups included, without password protection.</summary>
    public async Task<byte[]> BuildPdfBytesAsync()
    {
        if (Pdf is { } pdf)
        {
            var map = MarkupMap();
            return await Task.Run(() =>
            {
                using var ms = new MemoryStream();
                pdf.Save(ms, map, removeSecurity: true); // protection is re-applied by qpdf below
                return ms.ToArray();
            });
        }
        var frames = await Task.Run(FlattenedFrames);
        return await Task.Run(() =>
        {
            using var pdf = PdfDocument.CreateEmpty();
            foreach (var f in frames)
                pdf.AddImagePage(f.Pixels, f.Width, f.Height, f.Width * 72 / f.DpiX, f.Height * 72 / f.DpiY);
            using var ms = new MemoryStream();
            pdf.Save(ms);
            return ms.ToArray();
        });
    }

    /// <summary>Runs the compressor on the current document; nothing on disk or in the session changes.</summary>
    public async Task<CompressionResult> CompressAsync(CompressionLevel level, IProgress<double>? progress, CancellationToken ct) =>
        await PdfCompressor.CompressAsync(await BuildPdfBytesAsync(), level, progress, ct);

    /// <summary>Writes already-built PDF bytes to a file, applying this document's password protection. Atomic.</summary>
    public async Task WritePdfAsync(byte[] pdf, string path)
    {
        if (Encryption is { } enc) pdf = await Qpdf.EncryptAsync(pdf, enc);
        string tmp = path + ".wptmp";
        try
        {
            await File.WriteAllBytesAsync(tmp, pdf);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private Dictionary<int, IReadOnlyList<MarkupItem>> MarkupMap() =>
        Pages.Select((p, i) => (p, i)).Where(t => t.p.Markups.Count > 0)
            .ToDictionary(t => t.i, t => (IReadOnlyList<MarkupItem>)t.p.Markups);

    /// <summary>Image frames with their markups burned in.</summary>
    public List<ImageFrame> FlattenedFrames() => Pages.Select(p =>
    {
        var f = p.Frame!.Clone();
        if (p.Markups.Count > 0)
            MarkupRasterizer.Composite(f.Pixels, MarkupRasterizer.Render(p.Markups, f.Width, f.Height));
        return f;
    }).ToList();

    public bool CanSaveInPlace =>
        FilePath != null && (IsPdf ? LooksLikePdf(FilePath) : ImageCodec.FormatForExtension(Path.GetExtension(FilePath)) != null);

    public async Task SaveAsync() => await SaveAsAsync(FilePath ?? throw new InvalidOperationException("No file path."));

    public async Task SaveAsAsync(string path)
    {
        await ExportAsync(path);
        FilePath = path;
        MarkClean();
    }

    /// <summary>Writes a copy in the format implied by the extension; the session keeps editing its own file.</summary>
    public async Task ExportAsync(string path, double quality = 0.9, double rasterDpi = 150)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pdf")
        {
            await WritePdfAsync(await BuildPdfBytesAsync(), path);
            return;
        }

        string tmp = path + ".wptmp";
        try
        {
            await using (var fs = File.Create(tmp))
            {
                var format = ImageCodec.FormatForExtension(ext) ?? throw new NotSupportedException($"Can't save {ext} files.");
                var frames = Pdf != null ? await RasterizePdfAsync(rasterDpi) : await Task.Run(FlattenedFrames);
                await ImageCodec.EncodeAsync(fs, format, frames, quality);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>Renders each PDF page (with markups) to an image frame.</summary>
    private async Task<List<ImageFrame>> RasterizePdfAsync(double dpi)
    {
        using var ms = new MemoryStream();
        var map = MarkupMap();
        await Task.Run(() => Pdf!.Save(ms, map));
        return await Task.Run(() =>
        {
            using var copy = PdfDocument.Open(ms.ToArray());
            var frames = new List<ImageFrame>();
            for (int i = 0; i < copy.PageCount; i++)
            {
                var info = copy.GetPageInfo(i);
                int w = (int)Math.Round(info.Width * dpi / 72), h = (int)Math.Round(info.Height * dpi / 72);
                frames.Add(new ImageFrame { Pixels = copy.Render(i, w, h), Width = w, Height = h, DpiX = dpi, DpiY = dpi });
            }
            return frames;
        });
    }

    public void Dispose()
    {
        var pdf = Pdf;
        AttachPdf(null);
        pdf?.Dispose();
    }
}
