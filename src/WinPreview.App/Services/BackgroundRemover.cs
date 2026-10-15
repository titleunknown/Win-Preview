using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using WinPreview.Core.Imaging;
using WinPreview.Session;

namespace WinPreview.Services;

/// <param name="Url">Null for the model bundled with the app.</param>
/// <param name="Sha256">Expected hash of a downloaded model, checked before it is used.</param>
public sealed record SegmentationModel(
    string Name, string FileName, int InputSize, float[] Mean, float[] Std, string? Url, long Bytes, string? Sha256);

/// <summary>
/// Removes image backgrounds with a salient-object segmentation model run locally through ONNX Runtime.
/// U²-Net-p ships with the app; the higher-quality ISNet model is an optional one-time download.
/// Both models are Apache-2.0 licensed.
/// </summary>
public static class BackgroundRemover
{
    public static readonly SegmentationModel Standard = new(
        "Standard", "u2netp.onnx", 320, [0.485f, 0.456f, 0.406f], [0.229f, 0.224f, 0.225f], null, 4_574_861, null);

    public static readonly SegmentationModel HighQuality = new(
        "High quality", "isnet-general-use.onnx", 1024, [0.485f, 0.456f, 0.406f], [1f, 1f, 1f],
        "https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx", 178_648_008,
        "60920E99C45464F2BA57BEE2AD08C919A52BBF852739E96947FBB4358C0D964A");

    public static string SizeText(SegmentationModel m) => $"{Math.Round(m.Bytes / 1_000_000.0)} MB";

    private static readonly Dictionary<string, InferenceSession> Sessions = [];
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string DownloadFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win-Preview", "models");

    public static string PathFor(SegmentationModel m) => m.Url == null
        ? Path.Combine(AppContext.BaseDirectory, "Models", m.FileName)
        : Path.Combine(DownloadFolder, m.FileName);

    public static bool IsInstalled(SegmentationModel m) => File.Exists(PathFor(m)) && new FileInfo(PathFor(m)).Length == m.Bytes;

    /// <summary>Downloads an optional model to %LOCALAPPDATA%\Win-Preview\models, verifying size (and hash when pinned).</summary>
    public static async Task DownloadAsync(SegmentationModel m, IProgress<double> progress, CancellationToken ct)
    {
        if (m.Url == null) return;
        Directory.CreateDirectory(DownloadFolder);
        string tmp = PathFor(m) + ".part";
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        using (var response = await http.GetAsync(m.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            var buffer = new byte[1 << 16];
            long total = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                total += n;
                progress.Report((double)total / m.Bytes);
            }
        }
        var info = new FileInfo(tmp);
        if (info.Length != m.Bytes)
        {
            File.Delete(tmp);
            throw new IOException($"The download was incomplete ({info.Length:N0} of {m.Bytes:N0} bytes).");
        }
        if (m.Sha256 != null)
        {
            await using var fs = File.OpenRead(tmp);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!hash.Equals(m.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                fs.Close();
                File.Delete(tmp);
                throw new IOException("The downloaded model failed its integrity check.");
            }
        }
        File.Move(tmp, PathFor(m), overwrite: true);
    }

    /// <summary>Predicts a foreground mask (0–1, InputSize²) for straight-alpha BGRA pixels.</summary>
    public static async Task<float[]> PredictMaskAsync(ImageFrame frame, SegmentationModel m)
    {
        await Gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                if (!Sessions.TryGetValue(m.FileName, out var session))
                    Sessions[m.FileName] = session = new InferenceSession(PathFor(m));
                int s = m.InputSize;
                var input = AlphaTools.PrepareSquareInput(frame.Pixels, frame.Width, frame.Height, s, m.Mean, m.Std);
                var tensor = new DenseTensor<float>(input, [1, 3, s, s]);
                string inputName = session.InputMetadata.Keys.First();
                using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
                // The first output is the fused saliency map, shape [1, 1, s, s].
                var mask = results.First().AsEnumerable<float>().Take(s * s).ToArray();
                AlphaTools.Normalize(mask);
                return mask;
            });
        }
        finally { Gate.Release(); }
    }
}
