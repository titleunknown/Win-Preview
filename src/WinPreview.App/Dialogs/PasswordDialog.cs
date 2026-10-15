using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinPreview.Core.Pdf;

namespace WinPreview.Dialogs;

/// <summary>Outcome of the password dialog: either remove protection, or apply these settings.</summary>
public sealed record PasswordDialogResult(bool Remove, PdfEncryption? Encryption);

/// <summary>Sets or removes password protection on a PDF (AES-256).</summary>
public static class PasswordDialog
{
    /// <param name="current">Existing protection, which enables the Remove button. Its password is never shown.</param>
    /// <returns>Null when the user cancels.</returns>
    public static async Task<PasswordDialogResult?> ShowAsync(XamlRoot root, PdfEncryption? current)
    {
        var password = new PasswordBox { Header = "Password to open the document", PlaceholderText = "Password" };
        var verify = new PasswordBox { Header = "Verify", PlaceholderText = "Enter the password again" };

        var restrict = new CheckBox { Content = "Also restrict what people can do" };
        var allowPrint = new CheckBox { Content = "Allow printing" };
        var allowCopy = new CheckBox { Content = "Allow copying text and images" };
        var allowModify = new CheckBox { Content = "Allow editing" };
        var owner = new PasswordBox { Header = "Permissions password", PlaceholderText = "Needed to change these restrictions" };
        var restrictions = new StackPanel
        {
            Spacing = 4, Margin = new Thickness(24, 0, 0, 0), Visibility = Visibility.Collapsed,
            Children =
            {
                allowPrint, allowCopy, allowModify, owner,
                new TextBlock
                {
                    Text = "Restrictions are enforced by most PDF apps, but they are not as strong as the password to open.",
                    FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
                },
            },
        };
        restrict.Checked += (_, _) => restrictions.Visibility = Visibility.Visible;
        restrict.Unchecked += (_, _) => restrictions.Visibility = Visibility.Collapsed;

        var error = new TextBlock
        {
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
        };
        var content = new StackPanel
        {
            Spacing = 10, Width = 360,
            Children =
            {
                new TextBlock
                {
                    Text = current == null
                        ? "Anyone opening this PDF will need the password. It applies when you save."
                        : "This PDF is password protected. Enter a new password to change it, or remove the protection. Changes apply when you save.",
                    TextWrapping = TextWrapping.Wrap, Opacity = 0.8,
                },
                password, verify, restrict, restrictions, error,
            },
        };

        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = current == null ? "Password Protect" : "Change Password",
            Content = new ScrollViewer { Content = content, MaxHeight = 520 },
            PrimaryButtonText = current == null ? "Protect" : "Change",
            SecondaryButtonText = current != null ? "Remove Password" : "",
            CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };

        PasswordDialogResult? result = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            string? problem = Validate();
            if (problem != null)
            {
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }
            bool limited = restrict.IsChecked == true;
            result = new PasswordDialogResult(false, new PdfEncryption(
                password.Password, limited ? owner.Password : null,
                AllowPrint: !limited || allowPrint.IsChecked == true,
                AllowCopy: !limited || allowCopy.IsChecked == true,
                AllowModify: !limited || allowModify.IsChecked == true));
        };
        dialog.SecondaryButtonClick += (_, _) => result = new PasswordDialogResult(true, null);
        password.Loaded += (_, _) => password.Focus(FocusState.Programmatic);

        string? Validate()
        {
            if (Qpdf.ValidatePassword(password.Password) is { } p) return p;
            if (password.Password != verify.Password) return "The two passwords don't match.";
            if (restrict.IsChecked != true) return null;
            if (Qpdf.ValidatePassword(owner.Password) is { } o) return "Permissions password: " + o;
            if (owner.Password == password.Password) return "The permissions password must be different from the password to open, or anyone who opens the file could lift the restrictions.";
            return null;
        }

        await dialog.ShowAsync();
        return result;
    }
}
