namespace WinPreview.Core.Imaging;

/// <summary>Pixel operations for transparency: Instant Alpha (color flood fill) and applying segmentation masks.
/// All buffers are straight-alpha BGRA.</summary>
public static class AlphaTools
{
    /// <summary>
    /// Selects the region connected to (x, y) whose colors are within <paramref name="tolerance"/>
    /// (0–441, Euclidean RGB distance) of the seed color, like Preview's Instant Alpha.
    /// Returns one byte per pixel: 255 selected, 0 not.
    /// </summary>
    public static byte[] FloodSelect(ReadOnlySpan<byte> px, int width, int height, int x, int y, double tolerance)
    {
        var mask = new byte[width * height];
        if ((uint)x >= width || (uint)y >= height) return mask;
        int seed = (y * width + x) * 4;
        int sb = px[seed], sg = px[seed + 1], sr = px[seed + 2];
        double tol2 = tolerance * tolerance;
        var stack = new Stack<int>();
        stack.Push(y * width + x);
        mask[y * width + x] = 255;
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            int cx = i % width, cy = i / width;
            Span<int> neighbors = [cx > 0 ? i - 1 : -1, cx < width - 1 ? i + 1 : -1, cy > 0 ? i - width : -1, cy < height - 1 ? i + width : -1];
            foreach (int n in neighbors)
            {
                if (n < 0 || mask[n] != 0) continue;
                int o = n * 4;
                if (px[o + 3] == 0) { mask[n] = 255; stack.Push(n); continue; } // already transparent: include
                int db = px[o] - sb, dg = px[o + 1] - sg, dr = px[o + 2] - sr;
                if (db * db + dg * dg + dr * dr <= tol2)
                {
                    mask[n] = 255;
                    stack.Push(n);
                }
            }
        }
        return mask;
    }

    /// <summary>Makes selected pixels transparent, with a one-pixel soft edge to avoid jaggies.</summary>
    public static void ClearSelected(Span<byte> px, int width, int height, ReadOnlySpan<byte> selection)
    {
        var soft = new byte[selection.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                if (selection[i] == 255) { soft[i] = 255; continue; }
                // Unselected pixel bordering the selection: fade halfway.
                bool edge = x > 0 && selection[i - 1] == 255 || x < width - 1 && selection[i + 1] == 255
                    || y > 0 && selection[i - width] == 255 || y < height - 1 && selection[i + width] == 255;
                if (edge) soft[i] = 128;
            }
        for (int i = 0; i < soft.Length; i++)
            if (soft[i] != 0) px[i * 4 + 3] = (byte)(px[i * 4 + 3] * (255 - soft[i]) / 255);
    }

    /// <summary>
    /// Multiplies image alpha by a foreground mask (0 = background, 255 = keep) given at any resolution;
    /// the mask is bilinearly resampled to the image size.
    /// </summary>
    public static void ApplyMask(Span<byte> px, int width, int height, ReadOnlySpan<float> mask, int maskWidth, int maskHeight)
    {
        double sx = (double)maskWidth / width, sy = (double)maskHeight / height;
        for (int y = 0; y < height; y++)
        {
            double my = Math.Clamp((y + 0.5) * sy - 0.5, 0, maskHeight - 1);
            int y0 = (int)my, y1 = Math.Min(y0 + 1, maskHeight - 1);
            double fy = my - y0;
            for (int x = 0; x < width; x++)
            {
                double mx = Math.Clamp((x + 0.5) * sx - 0.5, 0, maskWidth - 1);
                int x0 = (int)mx, x1 = Math.Min(x0 + 1, maskWidth - 1);
                double fx = mx - x0;
                double m = (mask[y0 * maskWidth + x0] * (1 - fx) + mask[y0 * maskWidth + x1] * fx) * (1 - fy)
                         + (mask[y1 * maskWidth + x0] * (1 - fx) + mask[y1 * maskWidth + x1] * fx) * fy;
                int a = y * width * 4 + x * 4 + 3;
                px[a] = (byte)Math.Round(px[a] * Math.Clamp(m, 0, 1));
            }
        }
    }

    /// <summary>
    /// Builds a 1×3×size×size CHW float tensor from BGRA pixels: bilinear resize to a square, scale by the
    /// image's max channel value (as the reference U²-Net/ISNet pipelines do), then (v - mean) / std per RGB channel.
    /// </summary>
    public static float[] PrepareSquareInput(ReadOnlySpan<byte> px, int width, int height, int size, float[] mean, float[] std)
    {
        int max = 1;
        for (int i = 0; i < px.Length; i += 4)
            max = Math.Max(max, Math.Max(px[i], Math.Max(px[i + 1], px[i + 2])));
        var t = new float[3 * size * size];
        int plane = size * size;
        double sx = (double)width / size, sy = (double)height / size;
        for (int y = 0; y < size; y++)
        {
            double fyf = Math.Clamp((y + 0.5) * sy - 0.5, 0, height - 1);
            int y0 = (int)fyf, y1 = Math.Min(y0 + 1, height - 1);
            double fy = fyf - y0;
            for (int x = 0; x < size; x++)
            {
                double fxf = Math.Clamp((x + 0.5) * sx - 0.5, 0, width - 1);
                int x0 = (int)fxf, x1 = Math.Min(x0 + 1, width - 1);
                double fx = fxf - x0;
                for (int c = 0; c < 3; c++)
                {
                    int bgra = 2 - c; // RGB order from BGRA
                    double v = (px[(y0 * width + x0) * 4 + bgra] * (1 - fx) + px[(y0 * width + x1) * 4 + bgra] * fx) * (1 - fy)
                             + (px[(y1 * width + x0) * 4 + bgra] * (1 - fx) + px[(y1 * width + x1) * 4 + bgra] * fx) * fy;
                    t[c * plane + y * size + x] = (float)((v / max - mean[c]) / std[c]);
                }
            }
        }
        return t;
    }

    /// <summary>Min-max normalizes a model's raw saliency output to 0–1 in place.</summary>
    public static void Normalize(Span<float> values)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (float v in values) { min = Math.Min(min, v); max = Math.Max(max, v); }
        float range = Math.Max(1e-6f, max - min);
        for (int i = 0; i < values.Length; i++) values[i] = (values[i] - min) / range;
    }

    public static bool HasTransparency(ReadOnlySpan<byte> px)
    {
        for (int i = 3; i < px.Length; i += 4)
            if (px[i] != 255) return true;
        return false;
    }
}
