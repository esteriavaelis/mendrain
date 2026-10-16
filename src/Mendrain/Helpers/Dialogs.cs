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

    /// <summary>Asks for an archive password. Returns null when the user cancels or skips.</summary>
    public static async Task<string?> PasswordAsync(string archiveName, string? reason, string cancelText = "Cancel")
    {
        var box = new PasswordBox { PlaceholderText = "Password", Margin = new Thickness(0, 12, 0, 0) };
        var panel = new StackPanel { Spacing = 4, MinWidth = 340 };
        panel.Children.Add(new TextBlock
        {
            Text = $"“{archiveName}” is encrypted.",
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrEmpty(reason) && reason.Contains("incorrect", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Error,
                IsOpen = true,
                IsClosable = false,
                Message = reason,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }
        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            Title = "Enter password",
            Content = panel,
            PrimaryButtonText = "OK",
            CloseButtonText = cancelText,
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        box.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Password.Length > 0;
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);

        return await ShowAsync(dialog) == ContentDialogResult.Primary ? box.Password : null;
    }
}
