using System.Runtime.InteropServices;
using Microsoft.UI.Input;

namespace WinPreview.Views;

/// <summary>
/// Custom mouse cursors (zoom in/out, open and closed hand) that Windows doesn't provide. They live as native
/// resources in WinPreview.Cursors.dll, drawn by tools/CursorGen. If they can't be loaded the app falls back to
/// the nearest system cursor, so a missing file never breaks anything.
/// </summary>
public static class AppCursors
{
    private const string ModuleName = "WinPreview.Cursors.dll";
    private static readonly string ModulePath = Path.Combine(AppContext.BaseDirectory, ModuleName);
    private static readonly Dictionary<uint, InputCursor?> Cache = [];
    private static bool _loadFailed;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibrary(string fileName);

    public static InputCursor ZoomIn => Get(101, InputSystemCursorShape.Cross);
    public static InputCursor ZoomOut => Get(102, InputSystemCursorShape.Cross);
    public static InputCursor HandOpen => Get(103, InputSystemCursorShape.Hand);
    public static InputCursor HandClosed => Get(104, InputSystemCursorShape.SizeAll);

    private static InputCursor Get(uint id, InputSystemCursorShape fallback)
    {
        if (!Cache.TryGetValue(id, out var cursor))
        {
            cursor = TryLoad(id);
            Cache[id] = cursor;
        }
        return cursor ?? InputSystemCursor.Create(fallback);
    }

    private static InputCursor? TryLoad(uint id)
    {
        if (_loadFailed || !File.Exists(ModulePath)) return null;
        try
        {
            // WinUI finds the module by name among those already loaded in the process, so load it first and keep it loaded.
            if (LoadLibrary(ModulePath) == 0)
            {
                _loadFailed = true;
                Log.Write("custom cursors: couldn't load " + ModulePath + " (error " + Marshal.GetLastWin32Error() + ")");
                return null;
            }
            return InputDesktopResourceCursor.CreateFromModule(ModuleName, id);
        }
        catch (Exception ex)
        {
            _loadFailed = true; // don't retry (and log) on every mouse move
            Log.Write("custom cursors unavailable: " + ex.Message);
            return null;
        }
    }
}
