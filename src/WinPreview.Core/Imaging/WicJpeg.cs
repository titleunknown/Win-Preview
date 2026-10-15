using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace WinPreview.Core.Imaging;

/// <summary>JPEG encoding through Windows' built-in imaging codecs (WIC).</summary>
public static class WicJpeg
{
    /// <summary>Encodes BGRA pixels (alpha ignored) as a baseline JPEG. Blocks; call from a background thread.</summary>
    public static byte[] Encode(byte[] bgra, int width, int height, int quality)
    {
        // Run on the thread pool so a caller with a synchronization context can't deadlock.
        return Task.Run(() => EncodeAsync(bgra, width, height, quality)).GetAwaiter().GetResult();
    }

    private static async Task<byte[]> EncodeAsync(byte[] bgra, int width, int height, int quality)
    {
        using var stream = new InMemoryRandomAccessStream();
        var options = new BitmapPropertySet
        {
            ["ImageQuality"] = new BitmapTypedValue(Math.Clamp(quality, 1, 100) / 100f, PropertyType.Single),
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, options);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }
}
