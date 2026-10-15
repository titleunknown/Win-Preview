using System.Runtime.InteropServices;

namespace WinPreview.Core.Pdf;

/// <summary>Raw P/Invoke declarations for the subset of PDFium the app uses. Handles are nint.</summary>
/// <remarks>PDFium is not thread-safe; every call must happen inside <see cref="Pdfium.Lock"/>.</remarks>
internal static unsafe partial class PdfiumNative
{
    private const string Lib = "pdfium";

    // ---- fpdfview.h ----
    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;
    public const int FPDF_PRINTING = 0x800;
    public const int FPDFBitmap_BGRA = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_RECTF { public float Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_POINTF { public float X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_QUADPOINTSF { public float X1, Y1, X2, Y2, X3, Y3, X4, Y4; }

    [LibraryImport(Lib)] public static partial void FPDF_InitLibrary();
    [LibraryImport(Lib)] public static partial uint FPDF_GetLastError();

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint FPDF_LoadMemDocument64(void* data, nuint size, string? password);

    [LibraryImport(Lib)] public static partial void FPDF_CloseDocument(nint doc);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageCount(nint doc);
    [LibraryImport(Lib)] public static partial nint FPDF_LoadPage(nint doc, int index);
    [LibraryImport(Lib)] public static partial void FPDF_ClosePage(nint page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageWidthF(nint page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageHeightF(nint page);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageBoundingBox(nint page, FS_RECTF* rect);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageSizeByIndexF(nint doc, int index, float* size);

    [LibraryImport(Lib)] public static partial nint FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);
    [LibraryImport(Lib)] public static partial void FPDFBitmap_Destroy(nint bitmap);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);
    [LibraryImport(Lib)] public static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [LibraryImport(Lib)]
    public static partial int FPDF_PageToDevice(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, int* deviceX, int* deviceY);

    [LibraryImport(Lib)] public static partial uint FPDF_GetDocPermissions(nint doc);
    [LibraryImport(Lib)] public static partial int FPDF_GetSecurityHandlerRevision(nint doc);

    // ---- fpdf_edit.h ----
    [LibraryImport(Lib)] public static partial nint FPDF_CreateNewDocument();
    [LibraryImport(Lib)] public static partial void FPDFPage_Delete(nint doc, int index);
    [LibraryImport(Lib)] public static partial int FPDFPage_GetRotation(nint page);
    [LibraryImport(Lib)] public static partial void FPDFPage_SetRotation(nint page, int rotate);
    [LibraryImport(Lib)] public static partial int FPDFPage_GenerateContent(nint page);
    [LibraryImport(Lib)] public static partial int FPDF_MovePages(nint doc, int* pageIndices, nuint count, int destIndex);
    [LibraryImport(Lib)] public static partial nint FPDFPage_New(nint doc, int index, double width, double height);
    [LibraryImport(Lib)] public static partial void FPDFPage_InsertObject(nint page, nint obj);
    [LibraryImport(Lib)] public static partial nint FPDFPageObj_NewImageObj(nint doc);
    [LibraryImport(Lib)] public static partial int FPDFImageObj_SetBitmap(nint* pages, int count, nint imageObj, nint bitmap);
    [LibraryImport(Lib)] public static partial int FPDFImageObj_SetMatrix(nint imageObj, double a, double b, double c, double d, double e, double f);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint FPDFPageObj_NewTextObj(nint doc, string font, float fontSize);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int FPDFText_SetText(nint textObj, string text);

    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetFillColor(nint obj, uint r, uint g, uint b, uint a);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetStrokeColor(nint obj, uint r, uint g, uint b, uint a);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetStrokeWidth(nint obj, float width);
    [LibraryImport(Lib)] public static partial void FPDFPageObj_Transform(nint obj, double a, double b, double c, double d, double e, double f);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetBounds(nint obj, float* left, float* bottom, float* right, float* top);
    [LibraryImport(Lib)] public static partial nint FPDFPageObj_CreateNewRect(float x, float y, float w, float h);
    [LibraryImport(Lib)] public static partial int FPDFPath_SetDrawMode(nint path, int fillMode, int stroke);
    [LibraryImport(Lib)] public static partial nint FPDFPageObj_CreateNewPath(float x, float y);
    [LibraryImport(Lib)] public static partial int FPDFPath_LineTo(nint path, float x, float y);
    [LibraryImport(Lib)] public static partial int FPDFPath_MoveTo(nint path, float x, float y);
    [LibraryImport(Lib)] public static partial int FPDFPath_Close(nint path);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetLineCap(nint obj, int cap);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetLineJoin(nint obj, int join);

    // ---- fpdf_ppo.h ----
    [LibraryImport(Lib)] public static partial int FPDF_ImportPagesByIndex(nint dest, nint src, int* indices, nuint length, int index);

    // ---- fpdf_save.h ----
    public const uint FPDF_NO_INCREMENTAL = 2;
    public const uint FPDF_REMOVE_SECURITY = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FILEWRITE
    {
        public int Version;
        public delegate* unmanaged[Cdecl]<FPDF_FILEWRITE*, void*, uint, int> WriteBlock;
    }

    [LibraryImport(Lib)] public static partial int FPDF_SaveAsCopy(nint doc, FPDF_FILEWRITE* writer, uint flags);

    // ---- fpdf_text.h ----
    public const uint FPDF_MATCHCASE = 0x1;
    public const uint FPDF_MATCHWHOLEWORD = 0x2;

    [LibraryImport(Lib)] public static partial nint FPDFText_LoadPage(nint page);
    [LibraryImport(Lib)] public static partial void FPDFText_ClosePage(nint textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_CountChars(nint textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_GetText(nint textPage, int start, int count, char* result);
    [LibraryImport(Lib)] public static partial int FPDFText_GetCharIndexAtPos(nint textPage, double x, double y, double xTol, double yTol);
    [LibraryImport(Lib)] public static partial int FPDFText_CountRects(nint textPage, int start, int count);
    [LibraryImport(Lib)] public static partial int FPDFText_GetRect(nint textPage, int index, double* left, double* top, double* right, double* bottom);
    [LibraryImport(Lib)] public static partial int FPDFText_GetCharBox(nint textPage, int index, double* left, double* right, double* bottom, double* top);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FPDFText_FindStart(nint textPage, string findWhat, uint flags, int startIndex);

    [LibraryImport(Lib)] public static partial int FPDFText_FindNext(nint handle);
    [LibraryImport(Lib)] public static partial int FPDFText_GetSchResultIndex(nint handle);
    [LibraryImport(Lib)] public static partial int FPDFText_GetSchCount(nint handle);
    [LibraryImport(Lib)] public static partial void FPDFText_FindClose(nint handle);

    // ---- fpdf_doc.h ----
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetFirstChild(nint doc, nint bookmark);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetNextSibling(nint doc, nint bookmark);
    [LibraryImport(Lib)] public static partial uint FPDFBookmark_GetTitle(nint bookmark, char* buffer, uint bufLen);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetDest(nint doc, nint bookmark);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetAction(nint bookmark);
    [LibraryImport(Lib)] public static partial nint FPDFAction_GetDest(nint doc, nint action);
    [LibraryImport(Lib)] public static partial int FPDFDest_GetDestPageIndex(nint doc, nint dest);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial uint FPDF_GetMetaText(nint doc, string tag, char* buffer, uint bufLen);

    // ---- fpdf_annot.h ----
    public const int FPDF_ANNOT_TEXT = 1, FPDF_ANNOT_FREETEXT = 3, FPDF_ANNOT_LINE = 4, FPDF_ANNOT_SQUARE = 5,
        FPDF_ANNOT_CIRCLE = 6, FPDF_ANNOT_HIGHLIGHT = 9, FPDF_ANNOT_UNDERLINE = 10, FPDF_ANNOT_STRIKEOUT = 12,
        FPDF_ANNOT_STAMP = 13, FPDF_ANNOT_INK = 15, FPDF_ANNOT_POPUP = 16, FPDF_ANNOT_WIDGET = 20;
    public const int FPDF_ANNOT_FLAG_PRINT = 1 << 2;
    public const int FPDFANNOT_COLORTYPE_Color = 0, FPDFANNOT_COLORTYPE_InteriorColor = 1;
    public const int FPDF_ANNOT_APPEARANCEMODE_NORMAL = 0;

    [LibraryImport(Lib)] public static partial int FPDFPage_GetAnnotCount(nint page);
    [LibraryImport(Lib)] public static partial nint FPDFPage_GetAnnot(nint page, int index);
    [LibraryImport(Lib)] public static partial nint FPDFPage_CreateAnnot(nint page, int subtype);
    [LibraryImport(Lib)] public static partial int FPDFPage_RemoveAnnot(nint page, int index);
    [LibraryImport(Lib)] public static partial void FPDFPage_CloseAnnot(nint annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetSubtype(nint annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetColor(nint annot, int type, uint r, uint g, uint b, uint a);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetColor(nint annot, int type, uint* r, uint* g, uint* b, uint* a);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetRect(nint annot, FS_RECTF* rect);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetRect(nint annot, FS_RECTF* rect);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetBorder(nint annot, float hRadius, float vRadius, float width);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetBorder(nint annot, float* hRadius, float* vRadius, float* width);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetFlags(nint annot, int flags);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_AddInkStroke(nint annot, FS_POINTF* points, nuint count);
    [LibraryImport(Lib)] public static partial nuint FPDFAnnot_GetInkListCount(nint annot);
    [LibraryImport(Lib)] public static partial uint FPDFAnnot_GetInkListPath(nint annot, uint pathIndex, FS_POINTF* buffer, uint length);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_AppendAttachmentPoints(nint annot, FS_QUADPOINTSF* quad);
    [LibraryImport(Lib)] public static partial nuint FPDFAnnot_CountAttachmentPoints(nint annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetAttachmentPoints(nint annot, nuint index, FS_QUADPOINTSF* quad);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetLine(nint annot, FS_POINTF* start, FS_POINTF* end);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_AppendObject(nint annot, nint obj);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetFlags(nint annot);
    [LibraryImport(Lib)] public static partial int FPDFPage_GetAnnotIndex(nint page, nint annot);
    [LibraryImport(Lib)] public static partial void FPDFPageObj_Destroy(nint obj);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint FPDFAnnot_GetLinkedAnnot(nint annot, string key);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int FPDFAnnot_GetNumberValue(nint annot, string key, float* value);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int FPDFAnnot_HasKey(nint annot, string key);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial uint FPDFAnnot_GetStringValue(nint annot, string key, char* buffer, uint bufLen);

    [LibraryImport(Lib)]
    public static partial int FPDFAnnot_SetStringValue(nint annot, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPWStr)] string value);

    [LibraryImport(Lib)]
    public static partial int FPDFAnnot_SetAP(nint annot, int mode, [MarshalAs(UnmanagedType.LPWStr)] string? value);

    [LibraryImport(Lib)] public static partial uint FPDFAnnot_GetAP(nint annot, int mode, char* buffer, uint bufLen);

    // ---- fpdf_formfill.h ----
    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FORMFILLINFO
    {
        public int Version;
        public nint Release;
        public delegate* unmanaged[Cdecl]<FPDF_FORMFILLINFO*, nint, double, double, double, double, void> FFI_Invalidate;
        public nint FFI_OutputSelectedRect;
        public delegate* unmanaged[Cdecl]<FPDF_FORMFILLINFO*, int, void> FFI_SetCursor;
        public nint FFI_SetTimer;
        public nint FFI_KillTimer;
        public nint FFI_GetLocalTime;
        public delegate* unmanaged[Cdecl]<FPDF_FORMFILLINFO*, void> FFI_OnChange;
        public delegate* unmanaged[Cdecl]<FPDF_FORMFILLINFO*, nint, int, nint> FFI_GetPage;
        public nint FFI_GetCurrentPage;
        public nint FFI_GetRotation;
        public nint FFI_ExecuteNamedAction;
        public nint FFI_SetTextFieldFocus;
        public nint FFI_DoURIAction;
        public nint FFI_DoGoToAction;
        public nint JsPlatform;
        // Version 2 (XFA) fields; zeroed and unused, but allocated so PDFium never reads past the struct.
        public int XfaDisabled;
        public fixed long Reserved[24];
    }

    public const int FPDF_FORMFIELD_UNKNOWN = 0;
    public const int FWL_EVENTFLAG_ShiftKey = 1 << 0, FWL_EVENTFLAG_ControlKey = 1 << 1, FWL_EVENTFLAG_AltKey = 1 << 2;

    [LibraryImport(Lib)] public static partial nint FPDFDOC_InitFormFillEnvironment(nint doc, FPDF_FORMFILLINFO* info);
    [LibraryImport(Lib)] public static partial void FPDFDOC_ExitFormFillEnvironment(nint form);
    [LibraryImport(Lib)] public static partial void FORM_OnAfterLoadPage(nint page, nint form);
    [LibraryImport(Lib)] public static partial void FORM_OnBeforeClosePage(nint page, nint form);
    [LibraryImport(Lib)] public static partial void FORM_DoDocumentOpenAction(nint form);
    [LibraryImport(Lib)] public static partial void FPDF_SetFormFieldHighlightColor(nint form, int fieldType, uint color);
    [LibraryImport(Lib)] public static partial void FPDF_SetFormFieldHighlightAlpha(nint form, byte alpha);
    [LibraryImport(Lib)] public static partial void FPDF_FFLDraw(nint form, nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [LibraryImport(Lib)] public static partial int FPDFPage_HasFormFieldAtPoint(nint form, nint page, double x, double y);
    [LibraryImport(Lib)] public static partial int FORM_OnMouseMove(nint form, nint page, int modifier, double x, double y);
    [LibraryImport(Lib)] public static partial int FORM_OnLButtonDown(nint form, nint page, int modifier, double x, double y);
    [LibraryImport(Lib)] public static partial int FORM_OnLButtonUp(nint form, nint page, int modifier, double x, double y);
    [LibraryImport(Lib)] public static partial int FORM_OnKeyDown(nint form, nint page, int keyCode, int modifier);
    [LibraryImport(Lib)] public static partial int FORM_OnKeyUp(nint form, nint page, int keyCode, int modifier);
    [LibraryImport(Lib)] public static partial int FORM_OnChar(nint form, nint page, int ch, int modifier);
    [LibraryImport(Lib)] public static partial int FORM_ForceToKillFocus(nint form);
    [LibraryImport(Lib)] public static partial int FORM_Undo(nint form, nint page);
    [LibraryImport(Lib)] public static partial int FORM_Redo(nint form, nint page);
    [LibraryImport(Lib)] public static partial int FORM_SelectAllText(nint form, nint page);
    [LibraryImport(Lib)] public static partial uint FORM_GetSelectedText(nint form, nint page, char* buffer, uint bufLen);

    [LibraryImport(Lib)]
    public static partial void FORM_ReplaceSelection(nint form, nint page, [MarshalAs(UnmanagedType.LPWStr)] string text);
}
