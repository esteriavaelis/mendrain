using System.Reflection;
using Mendrain.Helpers;
using Mendrain.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Mendrain.Views;

public sealed partial class SettingsPage : Page
{
    private readonly bool _ready;

    public SettingsPage()
    {
        InitializeComponent();
        ThemeBox.SelectedItem = ThemeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == App.Settings.Theme)
                                ?? ThemeBox.Items[0];
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3)} · Uses SharpCompress and System.IO.Compression";
        // Single source of truth: the <Copyright> property in Mendrain.csproj.
        CopyrightText.Text = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";
        ShowSavedPasswords();
        ShowShellMenu();
        PreviewUpdatesBox.IsChecked = App.Settings.IncludePreviewUpdates;
        _ready = true;
    }

    public UpdateViewModel Updates => App.Updates;

    private void PreviewUpdatesBox_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.IncludePreviewUpdates = PreviewUpdatesBox.IsChecked == true;
        App.Updates.ResetSource();
    }

    private void ShowSavedPasswords()
    {
        var store = App.Settings.Passwords;
        var count = store.List().Count;
        var text = count > 0 ? $"{Format.Count(count, "password", "passwords")} saved, encrypted for your Windows account."
            : store.Problem is null ? "None saved. Tick “Save password” when entering one."
            : "";
        if (store.Problem is { } problem)
            text = $"{text} {problem}".TrimStart();
        SavedPasswordsText.Text = text;
        RemovePasswordsButton.IsEnabled = count > 0 || store.Problem is not null;
    }

    private void ShowShellMenu()
    {
        var registered = ShellMenu.RegisteredExecutable();
        var current = Environment.ProcessPath ?? "";
        var isThisCopy = registered is not null && string.Equals(registered, current, StringComparison.OrdinalIgnoreCase);
        ShellMenuText.Text = registered is null
            ? "Not added. Adds Open, Extract Here, Extract to Folder and Batch Extract for ZIP, 7Z and RAR files. Your default archive app is not changed."
            : isThisCopy
                ? "Right-click a ZIP, 7Z or RAR file and choose Mendrain. On Windows 11, look under “Show more options”."
                : $"Added for another copy of Mendrain ({registered}). Repair to use this copy.";
        var label = registered is null ? "Add" : isThisCopy ? "Repair" : "Use this copy";
        AddShellMenuButton.Content = label;
        AutomationProperties.SetName(AddShellMenuButton, $"{label} File Explorer menu");
        RemoveShellMenuButton.IsEnabled = registered is not null;
    }

    private async void AddShellMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ShellMenu.Register(Environment.ProcessPath!))
            await Dialogs.MessageAsync("Could not add the Explorer menu", "Windows did not allow Mendrain to change its menu entries. Try again later.");
        ShowShellMenu();
    }

    private async void RemoveShellMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ShellMenu.Unregister())
            await Dialogs.MessageAsync("Could not remove the Explorer menu", "Windows did not allow Mendrain to change its menu entries. Try again later.");
        ShowShellMenu();
    }

    private async void RemovePasswordsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await Dialogs.ConfirmAsync("Remove all saved passwords?",
                "Mendrain will forget every saved archive password. Your archives are not changed.", "Remove all", destructive: true))
            return;
        if (!App.Settings.Passwords.Clear())
            await Dialogs.MessageAsync("Could not remove saved passwords", "The saved-passwords file could not be deleted. Try again later.");
        ShowSavedPasswords();
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string theme })
            return;
        App.Settings.Theme = theme;
        App.MainWindow.ApplyTheme(App.Settings.ElementTheme);
    }
}
