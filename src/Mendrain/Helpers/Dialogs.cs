using Mendrain.Core.Services;
using Mendrain.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mendrain.Helpers;

/// <summary>ContentDialog helpers. WinUI allows only one open dialog at a time, so calls are serialized.</summary>
public static class Dialogs
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await Gate.WaitAsync();
        try
        {
            dialog.XamlRoot = App.MainWindow.Content.XamlRoot;
            dialog.Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
            dialog.RequestedTheme = ((FrameworkElement)App.MainWindow.Content).ActualTheme;
            return await dialog.ShowAsync();
        }
        finally
        {
            Gate.Release();
        }
    }

    public static Task<ContentDialogResult> MessageAsync(string title, string message) =>
        ShowAsync(new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        });

    public static async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
            // Destructive confirmations default to Cancel so Enter never replaces data by accident.
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Asks for an archive password, offering saved passwords. Returns null when the user cancels or skips.
    /// <paramref name="reason"/> explains why the previous password did not work (null on the first prompt).
    /// </summary>
    public static async Task<PasswordAnswer?> PasswordAsync(string archiveName, string? reason, string cancelText = "Cancel")
    {
        var dialog = new PasswordDialog(archiveName, reason, cancelText, App.Settings.Passwords);
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? dialog.Answer : null;
    }
}
