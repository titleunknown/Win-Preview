using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinPreview.Core.Pdf;
using WinPreview.Dialogs;

namespace WinPreview;

public sealed partial class MainWindow
{
    // ---------------- Password protection ----------------

    private async Task PasswordProtectAsync()
    {
        if (_session is not { IsPdf: true } session) return;
        if (!Qpdf.IsAvailable)
        {
            ShowError("Password protection isn't available", "The bundled qpdf tool is missing. Reinstall Win-Preview.");
            return;
        }
        DocView.CommitInlineEditor();
        var result = await PasswordDialog.ShowAsync(Root.XamlRoot, session.Encryption);
        if (result == null) return;
        session.SetEncryption(result.Remove ? null : result.Encryption);
        ShowInfo(result.Remove ? "Password will be removed" : "Password protection set",
            "Save the document to apply it.", InfoBarSeverity.Success);
    }

    // ---------------- Reduce file size ----------------

    private static readonly (CompressionLevel Level, string Title, string Detail)[] CompressionChoices =
    [
        (CompressionLevel.Balanced, "Balanced (recommended)", "Pictures above 150 dpi are reduced and saved as JPEG. Looks the same on screen and in print."),
        (CompressionLevel.Smallest, "Smallest file", "Pictures above 96 dpi are reduced more aggressively. Best for email and reading on screen."),
        (CompressionLevel.Lossless, "Lossless", "Only repacks the file. Nothing visible changes, but the savings are usually small."),
    ];

    private async Task ReduceFileSizeAsync()
    {
        if (_session is not { IsPdf: true } session) return;
        if (!Qpdf.IsAvailable)
        {
            ShowError("Reduce File Size isn't available", "The bundled qpdf tool is missing. Reinstall Win-Preview.");
            return;
        }
        DocView.CommitInlineEditor();
        DocView.KillFormFocus();

        var choices = new RadioButtons { SelectedIndex = 0, MaxColumns = 1 };
        foreach (var (_, title, detail) in CompressionChoices)
            choices.Items.Add(new StackPanel
            {
                Margin = new Thickness(0, 4, 0, 4),
                Children =
                {
                    new TextBlock { Text = title },
                    new TextBlock { Text = detail, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 },
                },
            });
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 6, 0, 0) };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "Reduce File Size",
            Content = new StackPanel
            {
                Width = 400,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Makes a smaller copy of this PDF. Your open document and the original file are not changed.",
                        TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 0, 0, 8),
                    },
                    choices, progress, status,
                },
            },
            PrimaryButtonText = "Reduce", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };

        using var cts = new CancellationTokenSource();
        CompressionResult? outcome = null;
        dialog.CloseButtonClick += (_, _) => cts.Cancel();
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            args.Cancel = true; // stay open while working
            dialog.IsPrimaryButtonEnabled = false;
            choices.IsEnabled = false;
            var level = CompressionChoices[Math.Max(0, choices.SelectedIndex)].Level;
            progress.Visibility = Visibility.Visible;
            progress.IsIndeterminate = level == CompressionLevel.Lossless;
            status.Text = level == CompressionLevel.Lossless ? "Repacking the file…" : "Compressing images…";
            try
            {
                outcome = await session.CompressAsync(level, new Progress<double>(p => progress.Value = p), cts.Token);
                deferral.Complete();
                dialog.Hide();
                return;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                status.Text = "Couldn't reduce the file: " + ex.Message;
                dialog.IsPrimaryButtonEnabled = true;
                choices.IsEnabled = true;
                progress.Visibility = Visibility.Collapsed;
            }
            deferral.Complete();
        };
        await dialog.ShowAsync();
        if (outcome is { } result) await OfferCompressedCopyAsync(session, result);
    }

    private async Task OfferCompressedCopyAsync(Session.DocumentSession session, CompressionResult r)
    {
        bool worthSaving = r.AfterBytes < r.BeforeBytes && r.Saved >= 0.01;
        string summary = worthSaving
            ? $"{FormatBytes(r.BeforeBytes)} → {FormatBytes(r.AfterBytes)}  ({r.Saved:P0} smaller)"
            : "This file is already well compressed, so there is little to gain.";
        var lines = new List<string> { summary };
        if (r.ImagesRecompressed > 0) lines.Add($"{r.ImagesRecompressed} image{(r.ImagesRecompressed == 1 ? "" : "s")} reduced.");
        if (r.FellBackToLossless) lines.Add("Reducing the images changed how some pages look, so only lossless optimization was applied.");
        if (session.Encryption != null) lines.Add("The copy will keep this document's password.");

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = worthSaving ? "Smaller copy ready" : "Nothing much to reduce",
            Content = new TextBlock { Text = string.Join("\n\n", lines), TextWrapping = TextWrapping.Wrap, MaxWidth = 400 },
            PrimaryButtonText = "Save Copy…", CloseButtonText = "Discard", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var picker = new FileSavePicker
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(session.FilePath ?? "Untitled") + " - reduced",
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add("PDF", [".pdf"]);
        InitPicker(picker);
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        try
        {
            await session.WritePdfAsync(r.Pdf, file.Path);
            ShowInfo("Saved", $"{file.Name} ({FormatBytes(r.AfterBytes)})", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowError("Couldn't save the copy", ex.Message); }
    }
}
