using System.Text.Json;

namespace WinPreview;

/// <summary>User preferences, saved to %LOCALAPPDATA%\WinPreview\settings.json and shared by every window.</summary>
public sealed class AppSettings
{
    /// <summary>Open files as tabs in the current window instead of a new window each.</summary>
    public bool OpenInTabs { get; set; }

    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPreview", "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Current { get; } = Load();

    public static event Action? Changed;

    private static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(); // a damaged or unreadable file just means defaults
        }
    }

    /// <summary>Saves the settings and tells every window they changed.</summary>
    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("couldn't save settings: " + ex.Message);
        }
        Changed?.Invoke();
    }
}
