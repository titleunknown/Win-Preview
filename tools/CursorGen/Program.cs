// Draws Win-Preview's custom mouse cursors and packs them into a Windows resource file (.res) that
// src/WinPreview.Resources compiles into WinPreview.Cursors.dll.
//
//   dotnet run --project tools/CursorGen -- <output .res> [preview.png]
//
// Resource ids (RT_GROUP_CURSOR): 101 zoom in, 102 zoom out, 103 hand open, 104 hand closed.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

if (args.Length < 1) { Console.Error.WriteLine("usage: CursorGen <output.res> [preview.png]"); return 1; }

var cursors = new (int Id, string Name, Bitmap Image, int HotX, int HotY)[]
{
    (101, "zoom-in", Magnifier(plus: true), 12, 12),
    (102, "zoom-out", Magnifier(plus: false), 12, 12),
    (103, "hand-open", Hand(closed: false), 16, 16),
    (104, "hand-closed", Hand(closed: true), 16, 16),
};

using (var ms = new MemoryStream())
using (var w = new BinaryWriter(ms))
{
    // A .res file starts with an empty 32-byte resource entry.
    WriteEntry(w, 0, 0, 0, 0, []);
    int nextCursorId = 1;
    foreach (var c in cursors)
    {
        var dib = BuildCursorImage(c.Image, c.HotX, c.HotY);
        int cursorResId = nextCursorId++;
        WriteEntry(w, 1, cursorResId, 0x1030, 0x0409, dib);                              // RT_CURSOR
        using var group = new MemoryStream();
        using var gw = new BinaryWriter(group);
        gw.Write((ushort)0); gw.Write((ushort)2); gw.Write((ushort)1);                  // GRPCURSORDIR: reserved, type = cursor, count
        gw.Write((ushort)c.Image.Width); gw.Write((ushort)(c.Image.Height * 2));        // size (height covers XOR + AND masks)
        gw.Write((ushort)1); gw.Write((ushort)32);                                      // planes, bit count
        gw.Write((uint)dib.Length); gw.Write((ushort)cursorResId);                      // bytes, id of the RT_CURSOR above
        gw.Flush();
        WriteEntry(w, 12, c.Id, 0x1030, 0x0409, group.ToArray());                       // RT_GROUP_CURSOR
    }
    w.Flush();
    File.WriteAllBytes(args[0], ms.ToArray());
    Console.WriteLine($"Wrote {args[0]} ({ms.Length:N0} bytes)");
}

if (args.Length > 1)
{
    // Preview sheet: each cursor at 4x on a light and a dark background, with a dot at its hotspot.
    const int scale = 4;
    using var sheet = new Bitmap(cursors.Length * 32 * scale + 20 + (cursors.Length - 1) * 10, 2 * 32 * scale + 30);
    using var g = Graphics.FromImage(sheet);
    g.Clear(Color.Gray);
    g.InterpolationMode = InterpolationMode.NearestNeighbor;
    g.PixelOffsetMode = PixelOffsetMode.Half;
    for (int bg = 0; bg < 2; bg++)
        for (int i = 0; i < cursors.Length; i++)
        {
            var rect = new Rectangle(10 + i * (32 * scale + 10), 10 + bg * (32 * scale + 10), 32 * scale, 32 * scale);
            g.FillRectangle(bg == 0 ? Brushes.WhiteSmoke : Brushes.DimGray, rect);
            g.DrawImage(cursors[i].Image, rect);
            g.FillEllipse(Brushes.Red, rect.X + cursors[i].HotX * scale - 3, rect.Y + cursors[i].HotY * scale - 3, 6, 6);
        }
    sheet.Save(args[1], ImageFormat.Png);
}
return 0;

static void WriteEntry(BinaryWriter w, int type, int id, int memoryFlags, int language, byte[] data)
{
    // Entry header: sizes, numeric TYPE and NAME (0xFFFF + ordinal), then version, flags and language.
    w.Write((uint)data.Length); w.Write((uint)32);
    w.Write((ushort)0xFFFF); w.Write((ushort)type);
    w.Write((ushort)0xFFFF); w.Write((ushort)id);
    w.Write((uint)0); w.Write((ushort)memoryFlags); w.Write((ushort)language);
    w.Write((uint)0); w.Write((uint)0);
    w.Write(data);
    while (w.BaseStream.Position % 4 != 0) w.Write((byte)0);
}

// RT_CURSOR data: hotspot (x, y) followed by a DIB (32-bit BGRA, bottom-up) and a 1-bit AND mask.
static byte[] BuildCursorImage(Bitmap bmp, int hotX, int hotY)
{
    int w = bmp.Width, h = bmp.Height;
    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var top = new byte[w * h * 4];
    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, top, 0, top.Length);
    bmp.UnlockBits(data);

    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);
    bw.Write((ushort)hotX); bw.Write((ushort)hotY);
    bw.Write(40u); bw.Write(w); bw.Write(h * 2); bw.Write((ushort)1); bw.Write((ushort)32);
    bw.Write(0u); bw.Write((uint)(w * h * 4)); bw.Write(0); bw.Write(0); bw.Write(0u); bw.Write(0u);
    for (int y = h - 1; y >= 0; y--) bw.Write(top, y * w * 4, w * 4);
    int maskRow = ((w + 31) / 32) * 4;
    bw.Write(new byte[maskRow * h]); // all zero: the alpha channel does the shaping
    bw.Flush();
    return ms.ToArray();
}

static Bitmap Magnifier(bool plus)
{
    var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    var dark = Color.FromArgb(255, 35, 35, 38);
    using (var halo = new Pen(Color.White, 6.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        g.DrawLine(halo, 19.5f, 19.5f, 28f, 28f);
    using (var handle = new Pen(dark, 3.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        g.DrawLine(handle, 19.5f, 19.5f, 27.5f, 27.5f);
    var lens = new RectangleF(3, 3, 18, 18);
    using (var fill = new SolidBrush(Color.FromArgb(110, 255, 255, 255))) g.FillEllipse(fill, lens);
    using (var halo = new Pen(Color.White, 4.5f)) g.DrawEllipse(halo, lens);
    using (var ring = new Pen(dark, 2.2f)) g.DrawEllipse(ring, lens);
    using var sign = new Pen(dark, 2.2f) { StartCap = LineCap.Square, EndCap = LineCap.Square };
    g.DrawLine(sign, 8, 12, 16, 12);
    if (plus) g.DrawLine(sign, 12, 8, 12, 16);
    return bmp;
}

static GraphicsPath Pill(float x, float y, float w, float h)
{
    var p = new GraphicsPath();
    float r = Math.Min(w, h) / 2;
    p.AddArc(x, y, 2 * r, 2 * r, 180, 90); p.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
    p.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90); p.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
    p.CloseFigure();
    return p;
}

static Bitmap Hand(bool closed)
{
    var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    var shapes = new List<GraphicsPath>();
    if (!closed)
    {
        shapes.Add(Pill(8, 15, 17, 13));                                   // palm
        foreach (var (x, top) in new[] { (8.5f, 6.5f), (12.7f, 4f), (16.9f, 5f), (21.1f, 8f) })
            shapes.Add(Pill(x, top, 3.7f, 16f));                           // fingers
        var thumb = Pill(2.5f, 14f, 3.9f, 11f);
        using (var m = new Matrix()) { m.RotateAt(-32, new PointF(5, 24)); thumb.Transform(m); }
        shapes.Add(thumb);
    }
    else
    {
        shapes.Add(Pill(7, 14, 19, 13));                                   // fist
        foreach (var x in new[] { 8.5f, 12.6f, 16.7f, 20.8f }) shapes.Add(Pill(x, 11.5f, 4f, 7f));  // knuckles
        shapes.Add(Pill(5f, 19f, 10f, 4.5f));                              // thumb
    }
    using (var outline = new Pen(Color.FromArgb(255, 25, 25, 28), 3.2f) { LineJoin = LineJoin.Round })
        foreach (var s in shapes) { g.FillPath(Brushes.Black, s); g.DrawPath(outline, s); }
    foreach (var s in shapes) g.FillPath(Brushes.White, s);
    using var line = new Pen(Color.FromArgb(255, 70, 70, 75), 1f);
    if (!closed) foreach (var x in new[] { 12.6f, 16.8f, 21f }) g.DrawLine(line, x, 17, x, 22);
    else foreach (var x in new[] { 12.5f, 16.6f, 20.7f }) g.DrawLine(line, x, 12f, x, 17f);
    foreach (var s in shapes) s.Dispose();
    return bmp;
}
