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
        // Preview opens images given together in one window, and each PDF in its own.
        var pdfs = files.Where(Session.DocumentSession.LooksLikePdf).ToList();
        var images = files.Except(pdfs).ToList();
        foreach (var pdf in pdfs) OpenWindow([pdf]);
        if (images.Count > 0) OpenWindow(images);
    }

    public MainWindow OpenWindow(IReadOnlyList<string>? files)
    {
        var w = new MainWindow();
        _windows.Add(w);
        w.Closed += (_, _) => _windows.Remove(w);
        w.Activate();
        if (files is { Count: > 0 }) _ = w.OpenFilesAsync(files);
        return w;
    }

    /// <summary>Reuses an empty window if there is one, otherwise opens a new window.</summary>
    public async Task OpenFilesAsync(IReadOnlyList<string> files, MainWindow? requester)
    {
        if (requester is { HasDocument: false })
        {
            await requester.OpenFilesAsync(files);
            return;
        }
        OpenWindow(files);
    }
}
