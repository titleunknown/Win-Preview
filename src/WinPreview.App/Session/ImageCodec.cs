using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WinPreview.Session;

/// <summary>One page of an image document: straight-alpha BGRA pixels.</summary>
public sealed class ImageFrame
{
    public required byte[] Pixels { get; set; }
    public required int Width { get; set; }
    public required int Height { get; set; }
    public double DpiX { get; set; } = 72;
    public double DpiY { get; set; } = 72;
    /// <summary>Animation delay in milliseconds (GIF), or 0.</summary>
    public int DelayMs { get; set; }

    public ImageFrame Clone() => new()
    {
        Pixels = (byte[])Pixels.Clone(), Width = Width, Height = Height, DpiX = DpiX, DpiY = DpiY, DelayMs = DelayMs,
    };
}

public sealed record ImageFormat(string Name, Guid EncoderId, string[] Extensions, bool SupportsQuality = false, bool MultiFrame = false);

public static class ImageResizer
{
    /// <summary>High-quality (Fant) resample of straight-alpha BGRA pixels via WIC.</summary>
    public static async Task<byte[]> ResizeAsync(byte[] pixels, int width, int height, int newWidth, int newHeight)
    {
        using var ras = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, ras);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
        ras.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(ras);
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)newWidth, ScaledHeight = (uint)newHeight, InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return data.DetachPixelData();
    }
}

public static class ImageCodec
{
    public static readonly string[] OpenExtensions =
    [
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".ico",
        ".heic", ".heif", ".hif", ".avif", ".webp", ".jxr", ".wdp", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2",
    ];

    private static readonly ImageFormat[] AllFormats =
    [
        new("PNG", BitmapEncoder.PngEncoderId, [".png"]),
        new("JPEG", BitmapEncoder.JpegEncoderId, [".jpg", ".jpeg", ".jpe", ".jfif"], SupportsQuality: true),
        new("HEIF", BitmapEncoder.HeifEncoderId, [".heic", ".heif", ".hif"], SupportsQuality: true),
        new("TIFF", BitmapEncoder.TiffEncoderId, [".tif", ".tiff"], MultiFrame: true),
        new("GIF", BitmapEncoder.GifEncoderId, [".gif"], MultiFrame: true),
        new("BMP", BitmapEncoder.BmpEncoderId, [".bmp", ".dib"]),
        new("JPEG XR", BitmapEncoder.JpegXREncoderId, [".jxr", ".wdp"], SupportsQuality: true),
    ];

    private static ImageFormat[]? _available;

    /// <summary>Formats with an encoder installed on this PC (HEIF needs the HEVC extension).</summary>
    public static IReadOnlyList<ImageFormat> SaveFormats => _available ??=
        AllFormats.Where(f => BitmapEncoder.GetEncoderInformationEnumerator().Any(i => i.CodecId == f.EncoderId)).ToArray();

    public static ImageFormat? FormatForExtension(string ext) =>
        SaveFormats.FirstOrDefault(f => f.Extensions.Contains(ext.ToLowerInvariant()));

    public static bool IsImage(string path) => OpenExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static async Task<List<ImageFrame>> DecodeAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return decoder.DecoderInformation.CodecId == BitmapDecoder.GifDecoderId && decoder.FrameCount > 1
            ? await DecodeAnimatedGifAsync(decoder)
            : await DecodeFramesAsync(decoder);
    }

    public static async Task<List<ImageFrame>> DecodeAsync(IRandomAccessStream stream)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await DecodeFramesAsync(decoder);
    }

    private static async Task<List<ImageFrame>> DecodeFramesAsync(BitmapDecoder decoder)
    {
        var frames = new List<ImageFrame>();
        for (uint i = 0; i < decoder.FrameCount; i++)
        {
            var frame = await decoder.GetFrameAsync(i);
            using var bmp = await frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            frames.Add(new ImageFrame
            {
                Pixels = ToBytes(bmp), Width = bmp.PixelWidth, Height = bmp.PixelHeight,
                DpiX = frame.DpiX > 0 ? frame.DpiX : 72, DpiY = frame.DpiY > 0 ? frame.DpiY : 72,
            });
        }
        return frames;
    }

    /// <summary>GIF frames are deltas; composite each onto the logical screen honoring disposal.</summary>
    private static async Task<List<ImageFrame>> DecodeAnimatedGifAsync(BitmapDecoder decoder)
    {
        var screen = await decoder.BitmapContainerProperties.GetPropertiesAsync(["/logscrdesc/Width", "/logscrdesc/Height"]);
        int w = Convert.ToInt32(screen["/logscrdesc/Width"].Value), h = Convert.ToInt32(screen["/logscrdesc/Height"].Value);
        var canvas = new byte[w * h * 4];
        var frames = new List<ImageFrame>();
        for (uint i = 0; i < decoder.FrameCount; i++)
        {
            var frame = await decoder.GetFrameAsync(i);
            var props = await frame.BitmapProperties.GetPropertiesAsync(["/imgdesc/Left", "/imgdesc/Top", "/grctlext/Disposal", "/grctlext/Delay"]);
            int Get(string k) => props.TryGetValue(k, out var v) ? Convert.ToInt32(v.Value) : 0;
            int left = Get("/imgdesc/Left"), top = Get("/imgdesc/Top"), disposal = Get("/grctlext/Disposal"), delay = Get("/grctlext/Delay") * 10;

            using var bmp = await frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
            var px = ToBytes(bmp);
            var previous = disposal == 3 ? (byte[])canvas.Clone() : null;
            for (int y = 0; y < bmp.PixelHeight && top + y < h; y++)
                for (int x = 0; x < bmp.PixelWidth && left + x < w; x++)
                {
                    int s = (y * bmp.PixelWidth + x) * 4, d = ((top + y) * w + left + x) * 4;
                    if (px[s + 3] != 0) System.Buffer.BlockCopy(px, s, canvas, d, 4);
                }
            frames.Add(new ImageFrame { Pixels = (byte[])canvas.Clone(), Width = w, Height = h, DelayMs = delay });

            if (disposal == 2)
                for (int y = 0; y < bmp.PixelHeight && top + y < h; y++)
                    Array.Clear(canvas, ((top + y) * w + left) * 4, Math.Min(bmp.PixelWidth, w - left) * 4);
            else if (previous != null) canvas = previous;
        }
        return frames;
    }

    public static byte[] ToBytes(SoftwareBitmap bmp)
    {
        var bytes = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyToBuffer(bytes.AsBuffer());
        return bytes;
    }

    public static async Task EncodeAsync(Stream output, ImageFormat format, IReadOnlyList<ImageFrame> frames, double quality = 0.9)
    {
        using var ras = new InMemoryRandomAccessStream();
        BitmapEncoder encoder;
        if (format.SupportsQuality)
        {
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue((float)quality, Windows.Foundation.PropertyType.Single) };
            encoder = await BitmapEncoder.CreateAsync(format.EncoderId, ras, options);
        }
        else encoder = await BitmapEncoder.CreateAsync(format.EncoderId, ras);

        var toWrite = format.MultiFrame ? frames : frames.Take(1).ToList();
        for (int i = 0; i < toWrite.Count; i++)
        {
            var f = toWrite[i];
            if (i > 0) await encoder.GoToNextFrameAsync();
            // JPEG has no alpha: flatten onto white rather than letting transparent pixels go black.
            var pixels = format.EncoderId == BitmapEncoder.JpegEncoderId ? FlattenOnWhite(f.Pixels) : f.Pixels;
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)f.Width, (uint)f.Height, f.DpiX, f.DpiY, pixels);
        }
        await encoder.FlushAsync();
        ras.Seek(0);
        await ras.AsStreamForRead().CopyToAsync(output);
    }

    public static byte[] FlattenOnWhite(byte[] px)
    {
        var o = (byte[])px.Clone();
        for (int i = 0; i < o.Length; i += 4)
        {
            int a = o[i + 3];
            if (a == 255) continue;
            for (int c = 0; c < 3; c++) o[i + c] = (byte)((o[i + c] * a + 255 * (255 - a)) / 255);
            o[i + 3] = 255;
        }
        return o;
    }

    /// <summary>Composites straight-alpha pixels over a light checkerboard so transparency is visible on screen.</summary>
    public static byte[] OnCheckerboard(byte[] px, int width, int height)
    {
        var o = new byte[px.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4, a = px[i + 3];
                int bg = ((x >> 3) + (y >> 3)) % 2 == 0 ? 255 : 214;
                o[i] = (byte)((px[i] * a + bg * (255 - a)) / 255);
                o[i + 1] = (byte)((px[i + 1] * a + bg * (255 - a)) / 255);
                o[i + 2] = (byte)((px[i + 2] * a + bg * (255 - a)) / 255);
                o[i + 3] = 255;
            }
        return o;
    }

    /// <summary>Formats that can store an alpha channel.</summary>
    public static bool SupportsAlpha(ImageFormat f) =>
        f.EncoderId == BitmapEncoder.PngEncoderId || f.EncoderId == BitmapEncoder.TiffEncoderId
        || f.EncoderId == BitmapEncoder.GifEncoderId;

    /// <summary>Straight → premultiplied alpha, which XAML bitmaps expect.</summary>
    public static byte[] Premultiply(byte[] px)
    {
        var o = new byte[px.Length];
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            o[i] = (byte)(px[i] * a / 255);
            o[i + 1] = (byte)(px[i + 1] * a / 255);
            o[i + 2] = (byte)(px[i + 2] * a / 255);
            o[i + 3] = (byte)a;
        }
        return o;
    }
}
