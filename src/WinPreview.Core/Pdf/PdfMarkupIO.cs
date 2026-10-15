using System.Globalization;
using System.Text;
using System.Text.Json;
using WinPreview.Core.Markup;
using static WinPreview.Core.Pdf.PdfiumNative;

namespace WinPreview.Core.Pdf;

/// <summary>Custom annotation dictionary keys used to round-trip markups PDF can't describe natively.</summary>
internal static class MarkupKeys
{
    public const string Line = "WPLine";          // "x1 y1 x2 y2 arrow(0|1)"
    public const string TextBox = "WPTextBox";    // JSON TextBoxData
    public const string Signature = "WPSignature";
}

internal sealed record TextBoxData(string Text, double FontSize, PdfStandardFont Font, string Color, string? Fill, double StrokeWidth);

internal static unsafe class PdfMarkupWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Appends annotations for <paramref name="items"/> to the page; returns how many were added.</summary>
    public static int Write(PdfDocument doc, int pageIndex, IReadOnlyList<MarkupItem> items)
    {
        var page = doc.GetPage(pageIndex);
        int before = FPDFPage_GetAnnotCount(page);
        foreach (var item in items)
        {
            switch (item)
            {
                case InkMarkup ink: WriteInk(page, ink); break;
                case ShapeMarkup shape: WriteShape(page, shape); break;
                case LineMarkup line: WriteLine(page, line); break;
                case TextBoxMarkup box: WriteTextBox(doc, page, box); break;
                case NoteMarkup note: WriteNote(page, note); break;
                case TextMarkup tm: WriteTextMarkup(page, tm); break;
            }
        }
        return FPDFPage_GetAnnotCount(page) - before;
    }

    public static void RemoveLast(PdfDocument doc, int pageIndex, int count)
    {
        var page = doc.GetPage(pageIndex);
        for (int i = 0; i < count; i++)
            FPDFPage_RemoveAnnot(page, FPDFPage_GetAnnotCount(page) - 1);
    }

    private static nint Create(nint page, int subtype, RectD rect, RgbaColor color)
    {
        var annot = FPDFPage_CreateAnnot(page, subtype);
        if (annot == 0) throw new InvalidOperationException($"Could not create annotation subtype {subtype}.");
        var r = ToFs(rect);
        FPDFAnnot_SetRect(annot, &r);
        FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_Color, color.R, color.G, color.B, color.A);
        FPDFAnnot_SetFlags(annot, FPDF_ANNOT_FLAG_PRINT);
        return annot;
    }

    private static void WriteInk(nint page, InkMarkup ink)
    {
        var annot = Create(page, FPDF_ANNOT_INK, ink.Bounds, ink.Color);
        try
        {
            FPDFAnnot_SetBorder(annot, 0, 0, (float)ink.StrokeWidth);
            foreach (var stroke in ink.Strokes.Where(s => s.Count > 0))
            {
                // A single tap should still leave a dot.
                var pts = stroke.Count == 1 ? [stroke[0], stroke[0] with { X = stroke[0].X + 0.01 }] : stroke;
                var buf = pts.Select(p => new FS_POINTF { X = (float)p.X, Y = (float)p.Y }).ToArray();
                fixed (FS_POINTF* b = buf) FPDFAnnot_AddInkStroke(annot, b, (nuint)buf.Length);
            }
            if (ink.IsSignature) FPDFAnnot_SetStringValue(annot, MarkupKeys.Signature, "1");
        }
        finally { FPDFPage_CloseAnnot(annot); }
    }

    private static void WriteShape(nint page, ShapeMarkup shape)
    {
        int subtype = shape.Shape == ShapeKind.Oval ? FPDF_ANNOT_CIRCLE : FPDF_ANNOT_SQUARE;
        // PDF's Rect includes the border; the markup rect is the stroke's centerline.
        var annot = Create(page, subtype, shape.Rect.Inflate(shape.StrokeWidth / 2), shape.Color);
        try
        {
            FPDFAnnot_SetBorder(annot, 0, 0, (float)shape.StrokeWidth);
            if (shape.Fill is { } f) FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_InteriorColor, f.R, f.G, f.B, f.A);
        }
        finally { FPDFPage_CloseAnnot(annot); }
    }

    /// <summary>
    /// PDFium can't create /Line annotations, so lines and arrows are stamps whose appearance is
    /// vector paths; the endpoints are kept in a private key for editing.
    /// </summary>
    private static void WriteLine(nint page, LineMarkup line)
    {
        var annot = Create(page, FPDF_ANNOT_STAMP, line.Bounds, line.Color);
        try
        {
            var c = line.Color;
            var end = line.End;
            if (line.ArrowAtEnd)
            {
                // Stop the shaft inside the arrowhead so the round cap doesn't poke through the tip.
                var d = line.End - line.Start;
                double len = d.Length, back = Math.Min(len, line.ArrowLength * 0.8);
                if (len > 0) end = new(line.End.X - d.X / len * back, line.End.Y - d.Y / len * back);
            }
            var shaft = FPDFPageObj_CreateNewPath((float)line.Start.X, (float)line.Start.Y);
            FPDFPath_LineTo(shaft, (float)end.X, (float)end.Y);
            FPDFPageObj_SetStrokeColor(shaft, c.R, c.G, c.B, c.A);
            FPDFPageObj_SetStrokeWidth(shaft, (float)line.StrokeWidth);
            FPDFPageObj_SetLineCap(shaft, 1);
            FPDFPath_SetDrawMode(shaft, 0, 1);
            FPDFAnnot_AppendObject(annot, shaft);
            if (line.ArrowAtEnd)
            {
                var (l, r) = line.ArrowHead();
                var head = FPDFPageObj_CreateNewPath((float)line.End.X, (float)line.End.Y);
                FPDFPath_LineTo(head, (float)l.X, (float)l.Y);
                FPDFPath_LineTo(head, (float)r.X, (float)r.Y);
                FPDFPath_Close(head);
                FPDFPageObj_SetFillColor(head, c.R, c.G, c.B, c.A);
                FPDFPath_SetDrawMode(head, 1, 0);
                FPDFAnnot_AppendObject(annot, head);
            }
            FPDFAnnot_SetStringValue(annot, MarkupKeys.Line,
                string.Create(Inv, $"{line.Start.X} {line.Start.Y} {line.End.X} {line.End.Y} {(line.ArrowAtEnd ? 1 : 0)} {line.StrokeWidth}"));
        }
        finally { FPDFPage_CloseAnnot(annot); }
    }

    internal static string FontName(PdfStandardFont f) => f switch
    {
        PdfStandardFont.TimesRoman => "Times-Roman",
        PdfStandardFont.Courier => "Courier",
        _ => "Helvetica",
    };

    /// <summary>
    /// Text boxes are written as stamp annotations whose appearance holds real text objects, which every
    /// viewer renders identically. The editable source is kept in a private key so we can reopen it.
    /// </summary>
    private static void WriteTextBox(PdfDocument doc, nint page, TextBoxMarkup box)
    {
        var annot = Create(page, FPDF_ANNOT_STAMP, box.Rect, box.Color);
        try
        {
            if (box.Fill is { } fill)
            {
                var bg = FPDFPageObj_CreateNewRect((float)box.Rect.X1, (float)box.Rect.Y1, (float)box.Rect.Width, (float)box.Rect.Height);
                FPDFPageObj_SetFillColor(bg, fill.R, fill.G, fill.B, fill.A);
                FPDFPath_SetDrawMode(bg, 1, 0);
                FPDFAnnot_AppendObject(annot, bg);
            }

            const double pad = 2;
            string font = FontName(box.Font);
            double lineHeight = box.FontSize * 1.2;
            double y = box.Rect.Y2 - pad - box.FontSize * 0.9; // first baseline
            foreach (var line in TextLayout.Wrap(box.Text, box.Rect.Width - 2 * pad, s => MeasureText(doc, font, box.FontSize, s)))
            {
                if (line.Length > 0)
                {
                    var t = FPDFPageObj_NewTextObj(doc.Handle, font, (float)box.FontSize);
                    FPDFText_SetText(t, line);
                    FPDFPageObj_SetFillColor(t, box.Color.R, box.Color.G, box.Color.B, box.Color.A);
                    FPDFPageObj_Transform(t, 1, 0, 0, 1, box.Rect.X1 + pad, y);
                    FPDFAnnot_AppendObject(annot, t);
                }
                y -= lineHeight;
            }

            FPDFAnnot_SetStringValue(annot, "Contents", box.Text);
            FPDFAnnot_SetStringValue(annot, MarkupKeys.TextBox, JsonSerializer.Serialize(new TextBoxData(
                box.Text, box.FontSize, box.Font, box.Color.ToHex(), box.Fill?.ToHex(), box.StrokeWidth)));
        }
        finally { FPDFPage_CloseAnnot(annot); }
    }

    internal static double MeasureText(PdfDocument doc, string font, double size, string s)
    {
        if (s.Length == 0) return 0;
        var t = FPDFPageObj_NewTextObj(doc.Handle, font, (float)size);
        try
        {
            FPDFText_SetText(t, s);
            float l, b, r, top;
            return FPDFPageObj_GetBounds(t, &l, &b, &r, &top) != 0 ? r - l : s.Length * size * 0.5;
        }
        finally { FPDFPageObj_Destroy(t); }
    }

    private static void WriteNote(nint page, NoteMarkup note)
    {
        var annot = Create(page, FPDF_ANNOT_TEXT, note.Rect, note.Color);
        try { FPDFAnnot_SetStringValue(annot, "Contents", note.Text); }
        finally { FPDFPage_CloseAnnot(annot); }
    }

    private static void WriteTextMarkup(nint page, TextMarkup tm)
    {
        int subtype = tm.Style switch
        {
            TextMarkupStyle.Underline => FPDF_ANNOT_UNDERLINE,
            TextMarkupStyle.Strikeout => FPDF_ANNOT_STRIKEOUT,
            _ => FPDF_ANNOT_HIGHLIGHT,
        };
        var annot = Create(page, subtype, tm.Bounds, tm.Color);
        try
        {
            foreach (var r in tm.Rects)
            {
                // Acrobat ordering: top-left, top-right, bottom-left, bottom-right.
                var q = new FS_QUADPOINTSF
                {
                    X1 = (float)r.X1, Y1 = (float)r.Y2, X2 = (float)r.X2, Y2 = (float)r.Y2,
                    X3 = (float)r.X1, Y3 = (float)r.Y1, X4 = (float)r.X2, Y4 = (float)r.Y1,
                };
                FPDFAnnot_AppendAttachmentPoints(annot, &q);
            }
            if (tm.Text.Length > 0) FPDFAnnot_SetStringValue(annot, "Contents", tm.Text);
        }
        finally { FPDFPage_CloseAnnot(annot); }
    }

    internal static FS_RECTF ToFs(RectD r) => new() { Left = (float)r.X1, Bottom = (float)r.Y1, Right = (float)r.X2, Top = (float)r.Y2 };
}

public static unsafe class PdfMarkupReader
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Converts every annotation the markup tools can edit into a <see cref="MarkupItem"/> and removes it
    /// from the in-memory page, so the app draws it as an editable overlay instead of PDFium baking it in.
    /// Annotations we can't represent faithfully are left alone and keep rendering natively.
    /// </summary>
    public static Dictionary<int, List<MarkupItem>> ExtractAll(PdfDocument doc)
    {
        var result = new Dictionary<int, List<MarkupItem>>();
        lock (Pdfium.Lock)
            for (int i = 0, n = doc.PageCount; i < n; i++)
            {
                var items = Extract(doc, i);
                if (items.Count > 0) result[i] = items;
            }
        return result;
    }

    public static List<MarkupItem> Extract(PdfDocument doc, int pageIndex)
    {
        var items = new List<MarkupItem>();
        lock (Pdfium.Lock)
        {
            var page = doc.GetPage(pageIndex);
            var toRemove = new SortedSet<int>();
            int count = FPDFPage_GetAnnotCount(page);
            for (int i = 0; i < count; i++)
            {
                var annot = FPDFPage_GetAnnot(page, i);
                if (annot == 0) continue;
                try
                {
                    // Hidden (bit 2) or NoView (bit 6) annotations aren't ours to surface.
                    if ((FPDFAnnot_GetFlags(annot) & ((1 << 1) | (1 << 5))) != 0) continue;
                    var item = Read(annot);
                    if (item == null) continue;
                    items.Add(item);
                    toRemove.Add(i);
                    var popup = FPDFAnnot_GetLinkedAnnot(annot, "Popup");
                    if (popup != 0)
                    {
                        int pi = FPDFPage_GetAnnotIndex(page, popup);
                        if (pi >= 0) toRemove.Add(pi);
                        FPDFPage_CloseAnnot(popup);
                    }
                }
                finally { FPDFPage_CloseAnnot(annot); }
            }
            foreach (int i in toRemove.Reverse()) FPDFPage_RemoveAnnot(page, i);
        }
        return items;
    }

    private static MarkupItem? Read(nint annot)
    {
        int subtype = FPDFAnnot_GetSubtype(annot);
        if (subtype is not (FPDF_ANNOT_INK or FPDF_ANNOT_SQUARE or FPDF_ANNOT_CIRCLE or FPDF_ANNOT_LINE or FPDF_ANNOT_TEXT
            or FPDF_ANNOT_FREETEXT or FPDF_ANNOT_HIGHLIGHT or FPDF_ANNOT_UNDERLINE or FPDF_ANNOT_STRIKEOUT or FPDF_ANNOT_STAMP))
            return null;

        string? textBoxJson = GetString(annot, MarkupKeys.TextBox);
        string? lineSpec = GetString(annot, MarkupKeys.Line);
        if (subtype == FPDF_ANNOT_STAMP && textBoxJson == null && lineSpec == null) return null; // a real stamp; leave it native

        FS_RECTF fr;
        FPDFAnnot_GetRect(annot, &fr);
        var rect = new RectD(Math.Min(fr.Left, fr.Right), Math.Min(fr.Bottom, fr.Top), Math.Max(fr.Left, fr.Right), Math.Max(fr.Bottom, fr.Top));
        float h, v, w = 1;
        if (FPDFAnnot_GetBorder(annot, &h, &v, &w) == 0 || w <= 0) w = 1;

        // GetColor refuses to work while an appearance stream exists; we're replacing it anyway.
        FPDFAnnot_SetAP(annot, FPDF_ANNOT_APPEARANCEMODE_NORMAL, null);
        var color = GetColor(annot, FPDFANNOT_COLORTYPE_Color);
        string contents = GetString(annot, "Contents") ?? "";

        switch (subtype)
        {
            case FPDF_ANNOT_INK:
            {
                var ink = new InkMarkup { Color = color ?? RgbaColor.Black, StrokeWidth = w, IsSignature = GetString(annot, MarkupKeys.Signature) == "1" };
                uint paths = (uint)FPDFAnnot_GetInkListCount(annot);
                for (uint p = 0; p < paths; p++)
                {
                    uint n = FPDFAnnot_GetInkListPath(annot, p, null, 0);
                    var buf = new FS_POINTF[n];
                    fixed (FS_POINTF* b = buf) FPDFAnnot_GetInkListPath(annot, p, b, n);
                    ink.Strokes.Add(buf.Select(pt => new PointD(pt.X, pt.Y)).ToList());
                }
                return ink.Strokes.Count > 0 ? ink : null;
            }
            case FPDF_ANNOT_SQUARE or FPDF_ANNOT_CIRCLE:
                return new ShapeMarkup
                {
                    Shape = subtype == FPDF_ANNOT_CIRCLE ? ShapeKind.Oval : ShapeKind.Rectangle,
                    Rect = rect.Inflate(-w / 2),
                    Color = color ?? RgbaColor.Black,
                    StrokeWidth = w,
                    // GetColor reports black for a missing IC; absent means unfilled.
                    Fill = FPDFAnnot_HasKey(annot, "IC") != 0 ? GetColor(annot, FPDFANNOT_COLORTYPE_InteriorColor) : null,
                };
            case FPDF_ANNOT_LINE:
            case FPDF_ANNOT_STAMP when lineSpec != null:
            {
                var line = new LineMarkup { Color = color ?? RgbaColor.Black, StrokeWidth = w };
                var parts = lineSpec?.Split(' ');
                if (parts is { Length: >= 5 })
                {
                    double P(int i) => double.Parse(parts[i], Inv);
                    line.Start = new(P(0), P(1));
                    line.End = new(P(2), P(3));
                    line.ArrowAtEnd = parts[4] == "1";
                    if (parts.Length >= 6) line.StrokeWidth = P(5);
                }
                else
                {
                    FS_POINTF s, e;
                    if (FPDFAnnot_GetLine(annot, &s, &e) == 0) return null;
                    line.Start = new(s.X, s.Y);
                    line.End = new(e.X, e.Y);
                }
                return line;
            }
            case FPDF_ANNOT_TEXT:
                return new NoteMarkup { Rect = rect, Text = contents, Color = color ?? RgbaColor.Yellow };
            case FPDF_ANNOT_FREETEXT:
                return new TextBoxMarkup { Rect = rect, Text = contents, FontSize = 12, Color = RgbaColor.Black };
            case FPDF_ANNOT_STAMP:
            {
                var d = JsonSerializer.Deserialize<TextBoxData>(textBoxJson!)!;
                return new TextBoxMarkup
                {
                    Rect = rect, Text = d.Text, FontSize = d.FontSize, Font = d.Font, StrokeWidth = d.StrokeWidth,
                    Color = RgbaColor.FromHex(d.Color), Fill = d.Fill is null ? null : RgbaColor.FromHex(d.Fill),
                };
            }
            default: // text markup
            {
                var tm = new TextMarkup
                {
                    Style = subtype switch
                    {
                        FPDF_ANNOT_UNDERLINE => TextMarkupStyle.Underline,
                        FPDF_ANNOT_STRIKEOUT => TextMarkupStyle.Strikeout,
                        _ => TextMarkupStyle.Highlight,
                    },
                    Text = contents,
                };
                tm.Color = color ?? (tm.Style == TextMarkupStyle.Highlight ? RgbaColor.HighlightYellow : RgbaColor.Red);
                for (nuint q = 0, n = FPDFAnnot_CountAttachmentPoints(annot); q < n; q++)
                {
                    FS_QUADPOINTSF qp;
                    if (FPDFAnnot_GetAttachmentPoints(annot, q, &qp) == 0) continue;
                    tm.Rects.Add(RectD.Bounding([new(qp.X1, qp.Y1), new(qp.X2, qp.Y2), new(qp.X3, qp.Y3), new(qp.X4, qp.Y4)]));
                }
                if (tm.Rects.Count == 0) tm.Rects.Add(rect);
                return tm;
            }
        }
    }

    private static RgbaColor? GetColor(nint annot, int type)
    {
        uint r, g, b, a;
        if (FPDFAnnot_GetColor(annot, type, &r, &g, &b, &a) == 0) return null;
        return new RgbaColor((byte)r, (byte)g, (byte)b, (byte)a);
    }

    private static string? GetString(nint annot, string key)
    {
        if (FPDFAnnot_HasKey(annot, key) == 0) return null;
        uint len = FPDFAnnot_GetStringValue(annot, key, null, 0);
        if (len <= 2) return "";
        var buf = new char[len / 2];
        fixed (char* p = buf) FPDFAnnot_GetStringValue(annot, key, p, len);
        return new string(buf).TrimEnd('\0');
    }
}
