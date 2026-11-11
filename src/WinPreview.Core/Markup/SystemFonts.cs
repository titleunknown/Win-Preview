using System.Buffers.Binary;
using System.Text;

namespace WinPreview.Core.Markup;

/// <summary>A font file (or one face of a .ttc collection) that text boxes can use and saved PDFs can embed.</summary>
/// <param name="Symbol">A symbol font (Wingdings, Webdings, …): its glyphs aren't letters, so a font menu shouldn't
/// write the font's name in it.</param>
public sealed record FontFace(string Family, string Path, int Index, int Weight, bool Italic, bool Symbol = false);

/// <summary>
/// The fonts installed on this PC that a text box can use. Only TrueType-outline fonts whose license allows
/// embedding are listed, since a saved PDF must carry the font to look the same everywhere. Families are read from
/// the font files' own name tables (the typographic family, so "Segoe UI Semibold" is part of "Segoe UI").
/// </summary>
public static class SystemFonts
{
    private static readonly Lazy<Dictionary<string, FontFace>> Catalog = new(Scan, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Family names, sorted. The first call scans the font folders (a few hundred files' headers).</summary>
    public static IReadOnlyList<string> Families => [.. Catalog.Value.Keys.Order(StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>The upright, regular-weight face of <paramref name="family"/>, or null when it isn't installed or usable.</summary>
    public static FontFace? Find(string family) => Catalog.Value.GetValueOrDefault(family);

    /// <summary>The face as a standalone font file (a face inside a .ttc is copied out into a .ttf of its own).</summary>
    public static byte[] LoadData(FontFace face)
    {
        var bytes = File.ReadAllBytes(face.Path);
        return IsCollection(bytes) ? ExtractFace(bytes, face.Index) : bytes;
    }

    private static Dictionary<string, FontFace> Scan()
    {
        var best = new Dictionary<string, FontFace>(StringComparer.OrdinalIgnoreCase);
        string[] dirs =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
        ];
        foreach (var dir in dirs.Where(Directory.Exists))
            foreach (var path in Directory.EnumerateFiles(dir))
            {
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext is not (".ttf" or ".ttc" or ".otf")) continue;
                try
                {
                    foreach (var face in ReadFaces(path))
                        if (!best.TryGetValue(face.Family, out var cur) || Rank(face) < Rank(cur)) best[face.Family] = face;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or EndOfStreamException) { }
            }
        return best;

        // Prefer upright, then the weight closest to Regular (400).
        static int Rank(FontFace f) => (f.Italic ? 10000 : 0) + Math.Abs(f.Weight - 400);
    }

    /// <summary>Usable faces in a font file: TrueType outlines, embedding allowed, with a family name.</summary>
    internal static List<FontFace> ReadFaces(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        var faces = new List<FontFace>();
        var head = ReadAt(fs, 0, 12);
        if (head.AsSpan(0, 4).SequenceEqual("ttcf"u8))
        {
            int n = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8));
            var offsets = ReadAt(fs, 12, 4 * Math.Min(n, 256));
            for (int i = 0; i < offsets.Length / 4; i++)
                if (ReadFace(fs, BinaryPrimitives.ReadUInt32BigEndian(offsets.AsSpan(4 * i)), path, i) is { } f) faces.Add(f);
        }
        else if (ReadFace(fs, 0, path, 0) is { } f) faces.Add(f);
        return faces;
    }

    private static FontFace? ReadFace(Stream s, long offset, string path, int index)
    {
        var tables = ReadTableDirectory(s, offset);
        // CFF-outline OpenType fonts can't be embedded the way the PDF writer embeds fonts (as TrueType).
        if (!tables.ContainsKey("glyf") || !tables.TryGetValue("name", out var name) || !tables.TryGetValue("OS/2", out var os2)) return null;

        var os2Data = ReadAt(s, os2.Offset, (int)Math.Min(os2.Length, 64));
        if (os2Data.Length < 64) return null;
        int weight = BinaryPrimitives.ReadUInt16BigEndian(os2Data.AsSpan(4));
        int fsType = BinaryPrimitives.ReadUInt16BigEndian(os2Data.AsSpan(8));
        int fsSelection = BinaryPrimitives.ReadUInt16BigEndian(os2Data.AsSpan(62));
        // Bit 1 = restricted license (unless a less restrictive bit is also set); bit 9 = bitmaps only.
        bool restricted = (fsType & 0x2) != 0 && (fsType & 0xC) == 0;
        if (restricted || (fsType & 0x200) != 0) return null;

        var names = ReadAt(s, name.Offset, (int)name.Length);
        string? family = NameString(names, 16) ?? NameString(names, 1);
        if (string.IsNullOrWhiteSpace(family) || family.StartsWith('.')) return null; // '.'-names are system UI fonts
        bool symbol = tables.TryGetValue("cmap", out var cmap) && IsSymbolCmap(ReadAt(s, cmap.Offset, (int)Math.Min(cmap.Length, 4 + 8 * 64)));
        return new FontFace(family.Trim(), path, index, weight, (fsSelection & 1) != 0, symbol);
    }

    /// <summary>A symbol font maps its glyphs with a Windows Symbol cmap (3,0) and has no Unicode one (3,1 / 3,10).</summary>
    private static bool IsSymbolCmap(byte[] cmap)
    {
        if (cmap.Length < 4) return false;
        int count = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(2));
        bool symbol = false, unicode = false;
        for (int i = 0; i < count && 4 + 8 * i + 4 <= cmap.Length; i++)
        {
            int platform = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(4 + 8 * i));
            int encoding = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(6 + 8 * i));
            if (platform == 3 && encoding == 0) symbol = true;
            if (platform == 3 && encoding is 1 or 10 || platform == 0) unicode = true;
        }
        return symbol && !unicode;
    }

    private readonly record struct TableRecord(uint Checksum, uint Offset, uint Length);

    private static Dictionary<string, TableRecord> ReadTableDirectory(Stream s, long offset)
    {
        var header = ReadAt(s, offset, 12);
        int count = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        var recs = ReadAt(s, offset + 12, 16 * count);
        var tables = new Dictionary<string, TableRecord>();
        for (int i = 0; i < count; i++)
        {
            var r = recs.AsSpan(16 * i);
            tables[Encoding.ASCII.GetString(r[..4])] = new(
                BinaryPrimitives.ReadUInt32BigEndian(r[4..]), BinaryPrimitives.ReadUInt32BigEndian(r[8..]), BinaryPrimitives.ReadUInt32BigEndian(r[12..]));
        }
        return tables;
    }

    /// <summary>A name-table string (Windows platform, Unicode), preferring US English.</summary>
    private static string? NameString(byte[] table, int nameId)
    {
        if (table.Length < 6) return null;
        int count = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(2));
        int strings = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(4));
        string? any = null;
        for (int i = 0; i < count && 6 + 12 * i + 12 <= table.Length; i++)
        {
            var r = table.AsSpan(6 + 12 * i);
            int platform = BinaryPrimitives.ReadUInt16BigEndian(r), encoding = BinaryPrimitives.ReadUInt16BigEndian(r[2..]);
            int lang = BinaryPrimitives.ReadUInt16BigEndian(r[4..]), id = BinaryPrimitives.ReadUInt16BigEndian(r[6..]);
            int len = BinaryPrimitives.ReadUInt16BigEndian(r[8..]), off = BinaryPrimitives.ReadUInt16BigEndian(r[10..]);
            if (id != nameId || platform != 3 || encoding is not (1 or 10) || strings + off + len > table.Length) continue;
            string value = Encoding.BigEndianUnicode.GetString(table, strings + off, len);
            if (lang == 0x0409) return value;
            any ??= value;
        }
        return any;
    }

    private static bool IsCollection(byte[] b) => b.Length >= 4 && b.AsSpan(0, 4).SequenceEqual("ttcf"u8);

    /// <summary>Copies one face of a .ttc into a standalone .ttf: its table directory plus the tables it uses.</summary>
    internal static byte[] ExtractFace(byte[] ttc, int index)
    {
        int n = (int)BinaryPrimitives.ReadUInt32BigEndian(ttc.AsSpan(8));
        if (index < 0 || index >= n) throw new ArgumentOutOfRangeException(nameof(index));
        int faceOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(ttc.AsSpan(12 + 4 * index));
        int count = BinaryPrimitives.ReadUInt16BigEndian(ttc.AsSpan(faceOffset + 4));

        var output = new MemoryStream();
        output.Write(ttc, faceOffset, 12);          // sfnt version, numTables, search hints: unchanged
        output.Write(new byte[16 * count]);         // table records, filled in below
        var records = new byte[16 * count];
        for (int i = 0; i < count; i++)
        {
            var src = ttc.AsSpan(faceOffset + 12 + 16 * i, 16);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(src[8..]);
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(src[12..]);
            src[..8].CopyTo(records.AsSpan(16 * i));  // tag and checksum
            BinaryPrimitives.WriteUInt32BigEndian(records.AsSpan(16 * i + 8), (uint)output.Position);
            BinaryPrimitives.WriteUInt32BigEndian(records.AsSpan(16 * i + 12), (uint)length);
            output.Write(ttc, offset, length);
            while (output.Position % 4 != 0) output.WriteByte(0); // tables are 4-byte aligned
        }
        var result = output.ToArray();
        records.CopyTo(result, 12);
        return result;
    }

    private static byte[] ReadAt(Stream s, long offset, int length)
    {
        var buf = new byte[length];
        s.Position = offset;
        s.ReadExactly(buf);
        return buf;
    }
}
