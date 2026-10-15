using System.Text.Json;

namespace WinPreview.Core.Signatures;

/// <summary>A saved signature: strokes normalized so the signature's bounding box is 1 unit tall, y-down.</summary>
public sealed class Signature
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Description { get; set; } = "";
    public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;
    public List<List<PointD>> Strokes { get; set; } = [];
    /// <summary>Width / height of the normalized bounding box.</summary>
    public double AspectRatio { get; set; } = 1;

    /// <summary>Builds a signature from raw strokes in any y-down coordinate space.</summary>
    public static Signature FromStrokes(IEnumerable<IReadOnlyList<PointD>> strokes)
    {
        var list = strokes.Where(s => s.Count > 0).Select(s => s.ToList()).ToList();
        var bounds = RectD.Bounding(list.SelectMany(s => s));
        double h = Math.Max(bounds.Height, 1e-6), w = Math.Max(bounds.Width, 1e-6);
        return new Signature
        {
            AspectRatio = w / h,
            Strokes = list.Select(s => s.Select(p => new PointD((p.X - bounds.X1) / h, (p.Y - bounds.Y1) / h)).ToList()).ToList(),
        };
    }
}

/// <summary>Persists signatures to %LOCALAPPDATA%\WinPreview\signatures.json.</summary>
public sealed class SignatureStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public SignatureStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPreview", "signatures.json");
    }

    public List<Signature> Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<List<Signature>>(File.ReadAllText(_path), Options) ?? [] : [];
        }
        catch (JsonException) { return []; }
    }

    public void Save(IEnumerable<Signature> signatures)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(signatures.ToList(), Options));
        File.Move(tmp, _path, overwrite: true);
    }

    public void Add(Signature s) { var all = Load(); all.Add(s); Save(all); }
    public void Remove(Guid id) { var all = Load(); all.RemoveAll(s => s.Id == id); Save(all); }
}
