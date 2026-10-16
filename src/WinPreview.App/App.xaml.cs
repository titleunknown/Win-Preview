using Microsoft.UI.Xaml;

namespace WinPreview;

public partial class App : Application
{
    private readonly List<MainWindow> _windows = [];

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Log.Write("UNHANDLED: " + e.Exception);
            System.Diagnostics.Debug.WriteLine(e.Exception);
            _windows.LastOrDefault()?.ShowError("Something went wrong", e.Exception.Message);
        };
    }

    public static new App Current => (App)Application.Current;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var files = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            OpenWindow(null);
            return;
        }
        // With tabs, everything opens in one window. Otherwise, like Preview: images given together share a window
        // and each PDF gets its own.
        if (AppSettings.Current.OpenInTabs)
        {
            OpenWindow(files);
            return;
        }
        var pdfs = files.Where(Session.DocumentSession.LooksLikePdf).ToList();
        var images = files.Except(pdfs).ToList();
        foreach (var pdf in pdfs) OpenWindow([pdf]);
        if (images.Count > 0) OpenWindow(images);
    }

    public MainWindow OpenWindow(IReadOnlyList<string>? files)
    {
        var w = NewWindow();
        if (files is { Count: > 0 }) _ = w.OpenFilesAsync(files);
        return w;
    }

    /// <summary>Opens a window that takes over an already-open document (moving a tab out of another window).</summary>
    public MainWindow OpenWindowWithSession(Session.DocumentSession session)
    {
        var w = NewWindow();
        _ = w.AdoptSessionAsync(session);
        return w;
    }

    private MainWindow NewWindow()
    {
        var w = new MainWindow();
        _windows.Add(w);
        w.Closed += (_, _) => _windows.Remove(w);
        w.Activate();
        return w;
    }

    /// <summary>
    /// Opens files from a window. They join that window as tabs when it is empty, when "Open Files in Tabs" is on,
    /// or when <paramref name="asTabs"/> is set; otherwise each opens in a new window.
    /// </summary>
    public async Task OpenFilesAsync(IReadOnlyList<string> files, MainWindow? requester, bool asTabs = false)
    {
        if (requester != null && (!requester.HasDocument || asTabs || AppSettings.Current.OpenInTabs))
        {
            await requester.OpenFilesAsync(files, asTabs);
            return;
        }
        OpenWindow(files);
    }
}
