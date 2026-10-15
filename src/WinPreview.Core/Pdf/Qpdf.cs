using System.Diagnostics;
using System.Text;

namespace WinPreview.Core.Pdf;

/// <param name="UserPassword">Required to open the file.</param>
/// <param name="OwnerPassword">Required to change the restrictions below; defaults to the user password.</param>
public sealed record PdfEncryption(
    string UserPassword, string? OwnerPassword = null, bool AllowPrint = true, bool AllowCopy = true, bool AllowModify = true)
{
    public bool HasRestrictions => !AllowPrint || !AllowCopy || !AllowModify;
}

public sealed class QpdfException(string message) : Exception(message);

/// <summary>
/// Runs the bundled qpdf (Apache-2.0) as a child process: AES-256 encryption for password protection and
/// lossless structure optimization. Passwords go through an argument file, never the command line, so other
/// processes can't read them from the process list. The file is deleted as soon as qpdf exits.
/// </summary>
public static class Qpdf
{
    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "qpdf", "qpdf.exe");

    public static bool IsAvailable => File.Exists(ExePath);

    public static string? ValidatePassword(string password)
    {
        if (password.Length == 0) return "Enter a password.";
        if (password.Contains('\n') || password.Contains('\r')) return "A password can't contain line breaks.";
        // qpdf trims each line of an argument file.
        if (password != password.Trim()) return "A password can't start or end with a space.";
        return null;
    }

    public static Task<byte[]> EncryptAsync(byte[] pdf, PdfEncryption options, CancellationToken ct = default)
    {
        string owner = options.OwnerPassword ?? options.UserPassword;
        foreach (var pw in new[] { options.UserPassword, owner })
            if (ValidatePassword(pw) is { } problem) throw new ArgumentException(problem);

        return RunAsync(pdf, (inFile, outFile) =>
        {
            var args = new List<string> { "--encrypt", options.UserPassword, owner, "256" };
            if (!options.AllowPrint) args.Add("--print=none");
            if (!options.AllowCopy) args.Add("--extract=n");
            if (!options.AllowModify) args.Add("--modify=none");
            args.AddRange(["--", inFile, outFile]);
            return args;
        }, ct);
    }

    /// <summary>Rewrites the file with object streams and maximum Flate compression. Content is unchanged.</summary>
    public static Task<byte[]> OptimizeAsync(byte[] pdf, CancellationToken ct = default) =>
        RunAsync(pdf, (inFile, outFile) => ["--object-streams=generate", "--recompress-flate", "--compression-level=9", inFile, outFile], ct);

    private static async Task<byte[]> RunAsync(byte[] input, Func<string, string, IEnumerable<string>> buildArgs, CancellationToken ct)
    {
        if (!IsAvailable) throw new QpdfException("The bundled qpdf tool is missing (" + ExePath + ").");
        string dir = Path.Combine(Path.GetTempPath(), "WinPreview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string inFile = Path.Combine(dir, "in.pdf"), outFile = Path.Combine(dir, "out.pdf"), argFile = Path.Combine(dir, "args.txt");
            await File.WriteAllBytesAsync(inFile, input, ct);
            await File.WriteAllLinesAsync(argFile, buildArgs(inFile, outFile), new UTF8Encoding(false), ct);

            var psi = new ProcessStartInfo(ExePath)
            {
                ArgumentList = { "@" + argFile },
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            using var proc = Process.Start(psi) ?? throw new QpdfException("Couldn't start qpdf.");
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            try { await proc.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }
            await Task.WhenAll(stdout, stderr);

            // Exit code 3 means "finished with warnings"; the output is still written.
            if (proc.ExitCode is not (0 or 3) || !File.Exists(outFile))
                throw new QpdfException(FirstLine(await stderr) ?? $"qpdf failed (exit code {proc.ExitCode}).");
            return await File.ReadAllBytesAsync(outFile, ct);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
