using WinPreview.Core.Pdf;

namespace WinPreview.Core.Text;

/// <summary>
/// Selectable text on a page, addressed by character index. Implemented by PDF text (PDFium)
/// and by OCR results on images, so selection, copy and search work the same for both.
/// Coordinates are page user space.
/// </summary>
public abstract class TextLayer
{
    public abstract int CharCount { get; }
    public abstract string GetText(int start, int count);
    /// <summary>One rectangle per line covering the character range.</summary>
    public abstract List<RectD> GetTextRects(int start, int count);
    /// <summary>Character under a point (within tolerance), or -1.</summary>
    public abstract int GetCharIndexAt(PointD p, double tolerance);
    /// <summary>The closest character to a point, or -1 if the page has no text.</summary>
    public abstract int GetNearestCharIndex(PointD p);

    public string AllText => GetText(0, CharCount);

    /// <summary>Finds all occurrences of a query. Returns (start, count) character ranges.</summary>
    public virtual IEnumerable<(int Start, int Count)> Find(string query, bool matchCase)
    {
        if (string.IsNullOrEmpty(query)) yield break;
        string text = AllText;
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (int i = text.IndexOf(query, cmp); i >= 0; i = text.IndexOf(query, i + query.Length, cmp))
            yield return (i, query.Length);
    }

    /// <summary>Expands an index to the surrounding word (letters and digits).</summary>
    public (int Start, int End)? WordAt(int index)
    {
        string text = AllText;
        if (index < 0 || index >= text.Length || !char.IsLetterOrDigit(text[index])) return null;
        int s = index, e = index;
        while (s > 0 && char.IsLetterOrDigit(text[s - 1])) s--;
        while (e < text.Length - 1 && char.IsLetterOrDigit(text[e + 1])) e++;
        return (s, e);
    }
}

public sealed class PdfTextLayer(PdfDocument doc, int page) : TextLayer
{
    public override int CharCount => doc.CountChars(page);
    public override string GetText(int start, int count) => doc.GetText(page, start, count);
    public override List<RectD> GetTextRects(int start, int count) => doc.GetTextRects(page, start, count);
    public override int GetCharIndexAt(PointD p, double tolerance) => doc.GetCharIndexAt(page, p, tolerance);
    public override int GetNearestCharIndex(PointD p) => doc.GetNearestCharIndex(page, p);

    public override IEnumerable<(int Start, int Count)> Find(string query, bool matchCase) =>
        doc.SearchPage(page, query, matchCase).Select(h => (h.CharStart, h.CharCount));
}

public sealed record OcrWord(string Text, RectD Rect);

public sealed record OcrLine(IReadOnlyList<OcrWord> Words);

/// <summary>
/// Text recognized in an image. Words are joined with spaces and lines with newlines; each character
/// gets an evenly divided slice of its word's box, which is what OCR engines report at word granularity.
/// </summary>
public sealed class OcrTextLayer : TextLayer
{
    private readonly string _text;
    private readonly RectD[] _boxes;   // per character; empty for newlines
    private readonly int[] _lineOf;    // per character

    public IReadOnlyList<OcrLine> Lines { get; }

    public OcrTextLayer(IReadOnlyList<OcrLine> lines)
    {
        Lines = lines;
        var sb = new System.Text.StringBuilder();
        var boxes = new List<RectD>();
        var lineOf = new List<int>();
        for (int li = 0; li < lines.Count; li++)
        {
            var words = lines[li].Words;
            for (int wi = 0; wi < words.Count; wi++)
            {
                var w = words[wi];
                if (wi > 0)
                {
                    // The space spans the gap between this word and the previous one.
                    var prev = words[wi - 1].Rect;
                    sb.Append(' ');
                    boxes.Add(new RectD(prev.X2, Math.Min(prev.Y1, w.Rect.Y1), Math.Max(prev.X2, w.Rect.X1), Math.Max(prev.Y2, w.Rect.Y2)));
                    lineOf.Add(li);
                }
                double cw = w.Rect.Width / Math.Max(1, w.Text.Length);
                for (int c = 0; c < w.Text.Length; c++)
                {
                    sb.Append(w.Text[c]);
                    boxes.Add(new RectD(w.Rect.X1 + c * cw, w.Rect.Y1, w.Rect.X1 + (c + 1) * cw, w.Rect.Y2));
                    lineOf.Add(li);
                }
            }
            if (li < lines.Count - 1)
            {
                sb.Append('\n');
                boxes.Add(default);
                lineOf.Add(li);
            }
        }
        _text = sb.ToString();
        _boxes = boxes.ToArray();
        _lineOf = lineOf.ToArray();
    }

    public override int CharCount => _text.Length;

    public override string GetText(int start, int count)
    {
        start = Math.Clamp(start, 0, _text.Length);
        return _text.Substring(start, Math.Clamp(count, 0, _text.Length - start));
    }

    public override List<RectD> GetTextRects(int start, int count)
    {
        var rects = new List<RectD>();
        int end = Math.Min(_text.Length, start + count);
        int line = -1;
        RectD acc = default;
        for (int i = Math.Max(0, start); i < end; i++)
        {
            if (_boxes[i].IsEmpty) continue;
            if (_lineOf[i] != line)
            {
                if (line >= 0) rects.Add(acc);
                line = _lineOf[i];
                acc = _boxes[i];
            }
            else acc = acc.Union(_boxes[i]);
        }
        if (line >= 0) rects.Add(acc);
        return rects;
    }

    public override int GetCharIndexAt(PointD p, double tolerance)
    {
        for (int i = 0; i < _boxes.Length; i++)
            if (!_boxes[i].IsEmpty && _boxes[i].Inflate(tolerance).Contains(p)) return i;
        return -1;
    }

    public override int GetNearestCharIndex(PointD p)
    {
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < _boxes.Length; i++)
        {
            var b = _boxes[i];
            if (b.IsEmpty) continue;
            double dx = Math.Max(0, Math.Max(b.X1 - p.X, p.X - b.X2)), dy = Math.Max(0, Math.Max(b.Y1 - p.Y, p.Y - b.Y2));
            // Vertical distance dominates so dragging across a line picks characters on that line.
            double d = dx + dy * 4;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }
}
