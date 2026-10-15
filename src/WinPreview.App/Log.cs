namespace WinPreview;

/// <summary>Opt-in diagnostics: set WINPREVIEW_LOG=1 to append to %TEMP%\winpreview.log.</summary>
public static class Log
{
    private static readonly string? PathOrNull =
        Environment.GetEnvironmentVariable("WINPREVIEW_LOG") is { Length: > 0 } ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "winpreview.log") : null;
    private static readonly Lock Gate = new();

    public static bool Enabled => PathOrNull != null;

    public static void Write(string message)
    {
        if (PathOrNull == null) return;
        lock (Gate) File.AppendAllText(PathOrNull, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
    }
}
