namespace WinPreview.Core.Markup;

public static class TextLayout
{
    /// <summary>Greedy word wrap. Honors explicit newlines and breaks words longer than a line.</summary>
    public static List<string> Wrap(string text, double maxWidth, Func<string, double> measure)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            string current = "";
            foreach (var word in paragraph.Split(' '))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (measure(candidate) <= maxWidth || current.Length == 0 && measure(word) <= maxWidth)
                {
                    current = candidate;
                    continue;
                }
                if (current.Length > 0) lines.Add(current);
                current = word;
                // Hard-break a single word that is wider than the box.
                while (current.Length > 1 && measure(current) > maxWidth)
                {
                    int fit = 1;
                    while (fit < current.Length && measure(current[..(fit + 1)]) <= maxWidth) fit++;
                    lines.Add(current[..fit]);
                    current = current[fit..];
                }
            }
            lines.Add(current);
        }
        return lines;
    }
}
