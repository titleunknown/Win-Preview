using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinPreview.Services;
using WinPreview.Views;

namespace WinPreview;

public sealed partial class MainWindow
{
    private bool _removingBackground;

    private void BuildRemoveBackgroundMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        bool hq = BackgroundRemover.IsInstalled(BackgroundRemover.HighQuality);
        menu.Items.Add(DocumentView.Item("Remove Background (Standard)", "", () => _ = RemoveBackgroundAsync(BackgroundRemover.Standard)));
        menu.Items.Add(DocumentView.Item(
            hq ? "Remove Background (High Quality)" : $"Remove Background (High Quality)… {BackgroundRemover.SizeText(BackgroundRemover.HighQuality)} download",
            "", () => _ = RemoveBackgroundAsync(BackgroundRemover.HighQuality)));
        if (hq)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(DocumentView.Item("Delete High-Quality Model", "", () =>
            {
                try { File.Delete(BackgroundRemover.PathFor(BackgroundRemover.HighQuality)); }
                catch (IOException ex) { ShowError("Couldn't delete the model", ex.Message); }
            }));
        }
    }

    /// <param name="model">Null picks the best installed model.</param>
    private async Task RemoveBackgroundAsync(SegmentationModel? model)
    {
        if (_session is not { IsPdf: false } || _removingBackground) return;
        model ??= BackgroundRemover.IsInstalled(BackgroundRemover.HighQuality) ? BackgroundRemover.HighQuality : BackgroundRemover.Standard;
        if (!BackgroundRemover.IsInstalled(model) && !await DownloadModelAsync(model)) return;

        int page = DocView.CurrentPage;
        var frame = _session.Pages[page].Frame!.Clone();
        _removingBackground = true;
        ShowInfo("Removing background…", $"Using the {model.Name.ToLowerInvariant()} model.");
        try
        {
            var mask = await BackgroundRemover.PredictMaskAsync(frame, model);
            if (_session is { IsPdf: false } s && page < s.PageCount)
            {
                s.ApplyAlphaMask(page, mask, model.InputSize);
                Info.IsOpen = false;
            }
        }
        catch (Exception ex)
        {
            ShowError("Couldn't remove the background", ex.Message);
        }
        finally { _removingBackground = false; }
    }

    /// <summary>Asks before downloading an optional model, then downloads it with progress.</summary>
    private async Task<bool> DownloadModelAsync(SegmentationModel model)
    {
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 6, 0, 0) };
        var content = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"The high-quality background remover (ISNet, Apache-2.0 license) is a one-time {BackgroundRemover.SizeText(model)} download " +
                           "from github.com/danielgatis/rembg. It runs entirely on this PC; your images are never uploaded.",
                },
                new TextBlock { Text = $"Saved to {BackgroundRemover.DownloadFolder}", Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) },
                progress,
                status,
            },
        };
        var dlg = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "Download High-Quality Model?", Content = content,
            PrimaryButtonText = "Download", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        using var cts = new CancellationTokenSource();
        bool ok = false;
        dlg.PrimaryButtonClick += async (_, args) =>
        {
            // Keep the dialog open while downloading; Cancel stops the download.
            var deferral = args.GetDeferral();
            args.Cancel = true;
            dlg.IsPrimaryButtonEnabled = false;
            progress.Visibility = Visibility.Visible;
            try
            {
                await BackgroundRemover.DownloadAsync(model, new Progress<double>(p =>
                {
                    progress.Value = p;
                    status.Text = $"{p * model.Bytes / 1_000_000:0} of {BackgroundRemover.SizeText(model)}";
                }), cts.Token);
                ok = true;
                deferral.Complete();
                dlg.Hide();
                return;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                status.Text = $"Download failed: {ex.Message}";
                dlg.IsPrimaryButtonEnabled = true;
            }
            deferral.Complete();
        };
        dlg.CloseButtonClick += (_, _) => cts.Cancel();
        await dlg.ShowAsync();
        return ok;
    }
}
