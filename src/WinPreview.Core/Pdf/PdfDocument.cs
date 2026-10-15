using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static WinPreview.Core.Pdf.PdfiumNative;

namespace WinPreview.Core.Pdf;

public static class Pdfium
{
    /// <summary>Global lock: PDFium is single-threaded. Reentrant on the owning thread.</summary>
    public static readonly Lock Lock = new();

    static Pdfium()
    {
        lock (Lock) FPDF_InitLibrary();
    }

    public static void EnsureInitialized() => RuntimeHelpers.RunClassConstructor(typeof(Pdfium).TypeHandle);
}

public sealed class PdfPasswordException(bool wrongPassword)
    : Exception(wrongPassword ? "Incorrect password." : "This PDF is password protected.")
{
    public bool WrongPassword { get; } = wrongPassword;
}

public sealed record PdfPageInfo(double Width, double Height, RectD Crop, int Rotation)
{
    /// <summary>User space → unscaled display space (y-down, rotation applied).</summary>
    public Affine UserToDisplay => PageTransforms.UserToDisplay(Crop, Rotation);
}

public sealed record PdfOutlineItem(string Title, int PageIndex, IReadOnlyList<PdfOutlineItem> Children);

public sealed record PdfSearchHit(int PageIndex, int CharStart, int CharCount, IReadOnlyList<RectD> Rects);

public sealed unsafe class PdfDocument : IDisposable
{
    private static readonly ConcurrentDictionary<nint, PdfDocument> FormOwners = new();

    private nint _doc;
    private void* _data;
    private readonly Dictionary<int, nint> _pages = [];
    private readonly Dictionary<int, nint> _textPages = [];
    private FPDF_FORMFILLINFO* _formInfo;
    private nint _form;

    /// <summary>Raised (on a PDFium caller thread) when an interactive form field needs repainting.</summary>
    public event Action<int>? FormInvalidated;
    /// <summary>Raised when a form field value changes.</summary>
    public event Action? FormChanged;

    public string? FilePath { get; private set; }
    public bool HasForm { get; private set; }
    public bool IsEncrypted { get; private set; }

    private PdfDocument() { }

    public static PdfDocument Open(string path, string? password = null)
    {
        var doc = Open(File.ReadAllBytes(path), password);
        doc.FilePath = path;
        return doc;
    }

    public static PdfDocument Open(ReadOnlySpan<byte> bytes, string? password = null)
    {
        Pdfium.EnsureInitialized();
        var result = new PdfDocument();
        lock (Pdfium.Lock)
        {
            // PDFium reads lazily from the buffer, so it must outlive the document.
            result._data = NativeMemory.Alloc((nuint)Math.Max(1, bytes.Length));
            bytes.CopyTo(new Span<byte>(result._data, bytes.Length));
            result._doc = FPDF_LoadMemDocument64(result._data, (nuint)bytes.Length, password);
            if (result._doc == 0)
            {
                uint err = FPDF_GetLastError();
                NativeMemory.Free(result._data);
                result._data = null;
                if (err == 4) throw new PdfPasswordException(password != null);
                throw new InvalidDataException($"Could not open PDF (PDFium error {err}).");
            }
            result.IsEncrypted = FPDF_GetSecurityHandlerRevision(result._doc) >= 0;
            result.InitForms();
        }
        return result;
    }

    public static PdfDocument CreateEmpty()
    {
        Pdfium.EnsureInitialized();
        var result = new PdfDocument();
        lock (Pdfium.Lock)
        {
            result._doc = FPDF_CreateNewDocument();
            result.InitForms();
        }
        return result;
    }

    internal nint Handle => _doc;

    public int PageCount { get { lock (Pdfium.Lock) return FPDF_GetPageCount(_doc); } }

    internal nint GetPage(int index)
    {
        lock (Pdfium.Lock)
        {
            if (_pages.TryGetValue(index, out var page)) return page;
            page = FPDF_LoadPage(_doc, index);
            if (page == 0) throw new ArgumentOutOfRangeException(nameof(index));
            if (_form != 0) FORM_OnAfterLoadPage(page, _form);
            _pages[index] = page;
            return page;
        }
    }

    internal nint GetTextPage(int index)
    {
        lock (Pdfium.Lock)
        {
            if (_textPages.TryGetValue(index, out var tp)) return tp;
            tp = FPDFText_LoadPage(GetPage(index));
            _textPages[index] = tp;
            return tp;
        }
    }

    /// <summary>Drop cached page handles; required after structural page changes.</summary>
    private void ClosePages()
    {
        foreach (var tp in _textPages.Values) FPDFText_ClosePage(tp);
        _textPages.Clear();
        foreach (var p in _pages.Values)
        {
            if (_form != 0) FORM_OnBeforeClosePage(p, _form);
            FPDF_ClosePage(p);
        }
        _pages.Clear();
    }

    /// <summary>Text pages cache the page's text layout; invalidate after content edits.</summary>
    internal void InvalidateText(int index)
    {
        lock (Pdfium.Lock)
        {
            if (_textPages.Remove(index, out var tp)) FPDFText_ClosePage(tp);
        }
    }

    public PdfPageInfo GetPageInfo(int index)
    {
        lock (Pdfium.Lock)
        {
            var page = GetPage(index);
            FS_RECTF box;
            FPDF_GetPageBoundingBox(page, &box);
            var crop = new RectD(box.Left, box.Bottom, box.Right, box.Top);
            return new PdfPageInfo(FPDF_GetPageWidthF(page), FPDF_GetPageHeightF(page), crop, FPDFPage_GetRotation(page) * 90);
        }
    }

    /// <summary>
    /// Renders a region of the page into a BGRA buffer (stride = width*4). The full page is scaled to
    /// <paramref name="pageWidthPx"/> x <paramref name="pageHeightPx"/>, and the region starting at
    /// (<paramref name="x"/>, <paramref name="y"/>) of size <paramref name="width"/> x <paramref name="height"/> is drawn.
    /// </summary>
    public void Render(int index, int pageWidthPx, int pageHeightPx, int x, int y, int width, int height, Span<byte> bgra, bool printing = false)
    {
        if (bgra.Length < width * height * 4) throw new ArgumentException("Buffer too small.", nameof(bgra));
        lock (Pdfium.Lock)
        {
            var page = GetPage(index);
            fixed (byte* buf = bgra)
            {
                var bmp = FPDFBitmap_CreateEx(width, height, FPDFBitmap_BGRA, buf, width * 4);
                try
                {
                    FPDFBitmap_FillRect(bmp, 0, 0, width, height, 0xFFFFFFFF);
                    int flags = FPDF_ANNOT | (printing ? FPDF_PRINTING : 0);
                    FPDF_RenderPageBitmap(bmp, page, -x, -y, pageWidthPx, pageHeightPx, 0, flags);
                    if (_form != 0) FPDF_FFLDraw(_form, bmp, page, -x, -y, pageWidthPx, pageHeightPx, 0, flags);
                }
                finally { FPDFBitmap_Destroy(bmp); }
            }
        }
    }

    public byte[] Render(int index, int widthPx, int heightPx)
    {
        var buf = new byte[widthPx * heightPx * 4];
        Render(index, widthPx, heightPx, 0, 0, widthPx, heightPx, buf);
        return buf;
    }

    // ---------------- Text ----------------

    public int CountChars(int index)
    {
        lock (Pdfium.Lock) return FPDFText_CountChars(GetTextPage(index));
    }

    /// <summary>Character index at a user-space point, or -1.</summary>
    public int GetCharIndexAt(int index, PointD user, double tolerance = 4)
    {
        lock (Pdfium.Lock) return FPDFText_GetCharIndexAtPos(GetTextPage(index), user.X, user.Y, tolerance, tolerance);
    }

    /// <summary>Nearest character to a point (searching outward), for starting selections in whitespace.</summary>
    public int GetNearestCharIndex(int index, PointD user)
    {
        lock (Pdfium.Lock)
        {
            var tp = GetTextPage(index);
            foreach (double tol in (double[])[2, 8, 20, 60])
            {
                int i = FPDFText_GetCharIndexAtPos(tp, user.X, user.Y, tol, tol);
                if (i >= 0) return i;
            }
            return -1;
        }
    }

    public RectD GetCharBox(int index, int charIndex)
    {
        lock (Pdfium.Lock)
        {
            double l, r, b, t;
            FPDFText_GetCharBox(GetTextPage(index), charIndex, &l, &r, &b, &t);
            return new RectD(l, b, r, t);
        }
    }

    /// <summary>Line-merged rectangles covering a character range, in user space.</summary>
    public List<RectD> GetTextRects(int index, int start, int count)
    {
        var rects = new List<RectD>();
        if (count <= 0) return rects;
        lock (Pdfium.Lock)
        {
            var tp = GetTextPage(index);
            int n = FPDFText_CountRects(tp, start, count);
            for (int i = 0; i < n; i++)
            {
                double l, t, r, b;
                if (FPDFText_GetRect(tp, i, &l, &t, &r, &b) != 0)
                    rects.Add(new RectD(Math.Min(l, r), Math.Min(b, t), Math.Max(l, r), Math.Max(b, t)));
            }
        }
        return rects;
    }

    public string GetText(int index, int start, int count)
    {
        if (count <= 0) return "";
        lock (Pdfium.Lock)
        {
            var buf = new char[count + 1];
            fixed (char* p = buf)
            {
                int written = FPDFText_GetText(GetTextPage(index), start, count, p);
                return new string(buf, 0, Math.Max(0, written - 1));
            }
        }
    }

    public string GetPageText(int index) => GetText(index, 0, CountChars(index));

    public IEnumerable<PdfSearchHit> Search(string query, bool matchCase = false, bool wholeWord = false, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(query)) yield break;
        uint flags = (matchCase ? FPDF_MATCHCASE : 0) | (wholeWord ? FPDF_MATCHWHOLEWORD : 0);
        int pages = PageCount;
        for (int p = 0; p < pages; p++)
        {
            ct.ThrowIfCancellationRequested();
            var hits = new List<PdfSearchHit>();
            lock (Pdfium.Lock)
            {
                var handle = FPDFText_FindStart(GetTextPage(p), query, flags, 0);
                try
                {
                    while (FPDFText_FindNext(handle) != 0)
                    {
                        int start = FPDFText_GetSchResultIndex(handle), count = FPDFText_GetSchCount(handle);
                        hits.Add(new PdfSearchHit(p, start, count, GetTextRects(p, start, count)));
                    }
                }
                finally { FPDFText_FindClose(handle); }
            }
            foreach (var h in hits) yield return h;
        }
    }

    // ---------------- Outline & metadata ----------------

    public IReadOnlyList<PdfOutlineItem> GetOutline()
    {
        lock (Pdfium.Lock)
        {
            var seen = new HashSet<nint>();
            return ReadOutline(0, seen, 0);
        }
    }

    private List<PdfOutlineItem> ReadOutline(nint parent, HashSet<nint> seen, int depth)
    {
        var items = new List<PdfOutlineItem>();
        if (depth > 32) return items;
        for (var bm = FPDFBookmark_GetFirstChild(_doc, parent); bm != 0 && seen.Add(bm); bm = FPDFBookmark_GetNextSibling(_doc, bm))
        {
            uint len = FPDFBookmark_GetTitle(bm, null, 0);
            var buf = new char[Math.Max(1, len / 2)];
            fixed (char* p = buf) FPDFBookmark_GetTitle(bm, p, len);
            string title = new string(buf).TrimEnd('\0');

            var dest = FPDFBookmark_GetDest(_doc, bm);
            if (dest == 0)
            {
                var action = FPDFBookmark_GetAction(bm);
                if (action != 0) dest = FPDFAction_GetDest(_doc, action);
            }
            int page = dest != 0 ? FPDFDest_GetDestPageIndex(_doc, dest) : -1;
            items.Add(new PdfOutlineItem(title, page, ReadOutline(bm, seen, depth + 1)));
        }
        return items;
    }

    public string GetMetadata(string tag)
    {
        lock (Pdfium.Lock)
        {
            uint len = FPDF_GetMetaText(_doc, tag, null, 0);
            if (len <= 2) return "";
            var buf = new char[len / 2];
            fixed (char* p = buf) FPDF_GetMetaText(_doc, tag, p, len);
            return new string(buf).TrimEnd('\0');
        }
    }

    // ---------------- Page operations ----------------

    /// <summary>Rotates a page by a multiple of 90° (positive = clockwise).</summary>
    public void RotatePage(int index, int degrees)
    {
        lock (Pdfium.Lock)
        {
            var page = GetPage(index);
            int quarter = ((FPDFPage_GetRotation(page) + degrees / 90) % 4 + 4) % 4;
            FPDFPage_SetRotation(page, quarter);
        }
    }

    public void DeletePage(int index)
    {
        lock (Pdfium.Lock)
        {
            ClosePages();
            FPDFPage_Delete(_doc, index);
        }
    }

    /// <summary>Moves the given pages (in order) so the first lands at <paramref name="destIndex"/> in the resulting document.</summary>
    public void MovePages(int[] indices, int destIndex)
    {
        lock (Pdfium.Lock)
        {
            ClosePages();
            fixed (int* p = indices)
                if (FPDF_MovePages(_doc, p, (nuint)indices.Length, destIndex) == 0)
                    throw new InvalidOperationException("Could not move pages.");
        }
    }

    /// <summary>Copies pages from another document, inserting them at <paramref name="insertAt"/>.</summary>
    public void ImportPages(PdfDocument source, int[]? indices, int insertAt)
    {
        lock (Pdfium.Lock)
        {
            ClosePages();
            indices ??= Enumerable.Range(0, source.PageCount).ToArray();
            fixed (int* p = indices)
                if (FPDF_ImportPagesByIndex(_doc, source._doc, p, (nuint)indices.Length, insertAt) == 0)
                    throw new InvalidOperationException("Could not import pages.");
        }
    }

    public void InsertBlankPage(int index, double width = 612, double height = 792)
    {
        lock (Pdfium.Lock)
        {
            ClosePages();
            FPDF_ClosePage(FPDFPage_New(_doc, index, width, height));
        }
    }

    /// <summary>Adds a page with lines of Helvetica text (top-left aligned with a 72pt margin).</summary>
    public void AddTextPage(IEnumerable<string> lines, double width = 612, double height = 792, double fontSize = 12)
    {
        lock (Pdfium.Lock)
        {
            ClosePages();
            var page = FPDFPage_New(_doc, FPDF_GetPageCount(_doc), width, height);
            double y = height - 72 - fontSize;
            foreach (var line in lines)
            {
                var t = FPDFPageObj_NewTextObj(_doc, "Helvetica", (float)fontSize);
                FPDFText_SetText(t, line);
                FPDFPageObj_Transform(t, 1, 0, 0, 1, 72, y);
                FPDFPage_InsertObject(page, t);
                y -= fontSize * 1.4;
            }
            FPDFPage_GenerateContent(page);
            FPDF_ClosePage(page);
        }
    }

    /// <summary>Adds a page showing a BGRA image (used for images → PDF and New from Clipboard).</summary>
    public void AddImagePage(ReadOnlySpan<byte> bgra, int pixelWidth, int pixelHeight, double pageWidth, double pageHeight)
    {
        lock (Pdfium.Lock)
        {
            ClosePages();
            var page = FPDFPage_New(_doc, FPDF_GetPageCount(_doc), pageWidth, pageHeight);
            var img = FPDFPageObj_NewImageObj(_doc);
            fixed (byte* p = bgra)
            {
                var bmp = FPDFBitmap_CreateEx(pixelWidth, pixelHeight, FPDFBitmap_BGRA, p, pixelWidth * 4);
                FPDFImageObj_SetBitmap(&page, 1, img, bmp);
                FPDFBitmap_Destroy(bmp);
            }
            FPDFImageObj_SetMatrix(img, pageWidth, 0, 0, pageHeight, 0, 0);
            FPDFPage_InsertObject(page, img);
            FPDFPage_GenerateContent(page);
            FPDF_ClosePage(page);
        }
    }

    // ---------------- Save ----------------

    private static readonly ConcurrentDictionary<nint, Stream> SaveTargets = new();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteBlock(FPDF_FILEWRITE* self, void* data, uint size)
    {
        try
        {
            SaveTargets[(nint)self].Write(new ReadOnlySpan<byte>(data, (int)size));
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Writes the document. <paramref name="markups"/> are added as real PDF annotations in the saved
    /// file but removed again from the in-memory document, which keeps rendering them as an overlay.
    /// </summary>
    public void Save(Stream output, IReadOnlyDictionary<int, IReadOnlyList<Markup.MarkupItem>>? markups = null, bool removeSecurity = false)
    {
        lock (Pdfium.Lock)
        {
            if (_form != 0) FORM_ForceToKillFocus(_form);
            var added = new Dictionary<int, int>();
            try
            {
                if (markups != null)
                    foreach (var (page, items) in markups)
                        if (items.Count > 0) added[page] = PdfMarkupWriter.Write(this, page, items);

                var writer = (FPDF_FILEWRITE*)NativeMemory.AllocZeroed((nuint)sizeof(FPDF_FILEWRITE));
                try
                {
                    writer->Version = 1;
                    writer->WriteBlock = &WriteBlock;
                    SaveTargets[(nint)writer] = output;
                    if (FPDF_SaveAsCopy(_doc, writer, removeSecurity ? FPDF_REMOVE_SECURITY : FPDF_NO_INCREMENTAL) == 0)
                        throw new IOException("PDFium failed to save the document.");
                }
                finally
                {
                    SaveTargets.TryRemove((nint)writer, out _);
                    NativeMemory.Free(writer);
                }
            }
            finally
            {
                foreach (var (page, count) in added) PdfMarkupWriter.RemoveLast(this, page, count);
            }
        }
    }

    /// <summary>Saves via a temp file then atomically replaces <paramref name="path"/>.</summary>
    public void SaveToFile(string path, IReadOnlyDictionary<int, IReadOnlyList<Markup.MarkupItem>>? markups = null)
    {
        string tmp = path + ".wptmp";
        using (var fs = File.Create(tmp)) Save(fs, markups);
        File.Move(tmp, path, overwrite: true);
        FilePath = path;
    }

    // ---------------- Interactive forms ----------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnInvalidate(FPDF_FORMFILLINFO* self, nint page, double l, double t, double r, double b)
    {
        if (!FormOwners.TryGetValue((nint)self, out var doc)) return;
        foreach (var (index, handle) in doc._pages)
            if (handle == page) { doc.FormInvalidated?.Invoke(index); break; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSetCursor(FPDF_FORMFILLINFO* self, int cursor) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnChange(FPDF_FORMFILLINFO* self)
    {
        if (FormOwners.TryGetValue((nint)self, out var doc)) doc.FormChanged?.Invoke();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnGetPage(FPDF_FORMFILLINFO* self, nint document, int index)
    {
        if (!FormOwners.TryGetValue((nint)self, out var doc)) return 0;
        try { return doc.GetPage(index); } catch { return 0; }
    }

    private void InitForms()
    {
        _formInfo = (FPDF_FORMFILLINFO*)NativeMemory.AllocZeroed((nuint)sizeof(FPDF_FORMFILLINFO));
        _formInfo->Version = 1;
        _formInfo->FFI_Invalidate = &OnInvalidate;
        _formInfo->FFI_SetCursor = &OnSetCursor;
        _formInfo->FFI_OnChange = &OnChange;
        _formInfo->FFI_GetPage = &OnGetPage;
        FormOwners[(nint)_formInfo] = this;
        _form = FPDFDOC_InitFormFillEnvironment(_doc, _formInfo);
        if (_form == 0) return;
        // Light blue tint over fields, like Preview's form highlighting.
        FPDF_SetFormFieldHighlightColor(_form, FPDF_FORMFIELD_UNKNOWN, 0xFFE4DD);
        FPDF_SetFormFieldHighlightAlpha(_form, 90);
        FORM_DoDocumentOpenAction(_form);
        for (int i = 0, n = FPDF_GetPageCount(_doc); i < n && !HasForm; i++)
        {
            var page = GetPage(i);
            for (int a = 0, c = FPDFPage_GetAnnotCount(page); a < c && !HasForm; a++)
            {
                var annot = FPDFPage_GetAnnot(page, a);
                HasForm = FPDFAnnot_GetSubtype(annot) == FPDF_ANNOT_WIDGET;
                FPDFPage_CloseAnnot(annot);
            }
        }
    }

    /// <summary>The form field type under a point, or -1 when there is none.</summary>
    public int FormFieldTypeAt(int index, PointD user)
    {
        if (_form == 0) return -1;
        lock (Pdfium.Lock) return FPDFPage_HasFormFieldAtPoint(_form, GetPage(index), user.X, user.Y);
    }

    public bool FormMouseDown(int index, PointD user, int modifiers)
    {
        if (_form == 0) return false;
        lock (Pdfium.Lock) return FORM_OnLButtonDown(_form, GetPage(index), modifiers, user.X, user.Y) != 0;
    }

    public bool FormMouseUp(int index, PointD user, int modifiers)
    {
        if (_form == 0) return false;
        lock (Pdfium.Lock) return FORM_OnLButtonUp(_form, GetPage(index), modifiers, user.X, user.Y) != 0;
    }

    public bool FormMouseMove(int index, PointD user, int modifiers)
    {
        if (_form == 0) return false;
        lock (Pdfium.Lock) return FORM_OnMouseMove(_form, GetPage(index), modifiers, user.X, user.Y) != 0;
    }

    /// <param name="virtualKey">Windows virtual-key code (PDFium uses the same values).</param>
    public bool FormKeyDown(int index, int virtualKey, int modifiers)
    {
        if (_form == 0) return false;
        lock (Pdfium.Lock) return FORM_OnKeyDown(_form, GetPage(index), virtualKey, modifiers) != 0;
    }

    public bool FormChar(int index, char ch, int modifiers)
    {
        if (_form == 0) return false;
        lock (Pdfium.Lock) return FORM_OnChar(_form, GetPage(index), ch, modifiers) != 0;
    }

    public void FormKillFocus()
    {
        if (_form == 0) return;
        lock (Pdfium.Lock) FORM_ForceToKillFocus(_form);
    }

    public string FormSelectedText(int index)
    {
        if (_form == 0) return "";
        lock (Pdfium.Lock)
        {
            var page = GetPage(index);
            uint len = FORM_GetSelectedText(_form, page, null, 0);
            if (len <= 2) return "";
            var buf = new char[len / 2];
            fixed (char* p = buf) FORM_GetSelectedText(_form, page, p, len);
            return new string(buf).TrimEnd('\0');
        }
    }

    public void FormReplaceSelection(int index, string text)
    {
        if (_form == 0) return;
        lock (Pdfium.Lock) FORM_ReplaceSelection(_form, GetPage(index), text);
    }

    public void FormSelectAll(int index)
    {
        if (_form == 0) return;
        lock (Pdfium.Lock) FORM_SelectAllText(_form, GetPage(index));
    }

    // ---------------- Lifetime ----------------

    public void Dispose()
    {
        lock (Pdfium.Lock)
        {
            if (_doc == 0) return;
            ClosePages();
            if (_form != 0) FPDFDOC_ExitFormFillEnvironment(_form);
            _form = 0;
            if (_formInfo != null)
            {
                FormOwners.TryRemove((nint)_formInfo, out _);
                NativeMemory.Free(_formInfo);
                _formInfo = null;
            }
            FPDF_CloseDocument(_doc);
            _doc = 0;
            if (_data != null) NativeMemory.Free(_data);
            _data = null;
        }
    }
}
