using System.Reflection;
using Mendrain.Core;
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
        ShowFileTypes();
        PreviewUpdatesBox.IsChecked = App.Settings.IncludePreviewUpdates;
        // The default app is chosen in Windows Settings; show the new state when the user comes back.
        Loaded += (_, _) => App.MainWindow.Activated += MainWindow_Activated;
        Unloaded += (_, _) => App.MainWindow.Activated -= MainWindow_Activated;
        _ready = true;
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
            ShowFileTypes();
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

    private void ShowFileTypes()
    {
        var registered = FileAssociations.RegisteredExecutable();
        var isThisCopy = registered is not null && string.Equals(registered, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        var defaults = isThisCopy ? FileAssociations.DefaultFor() : [];
        FileTypesText.Text = registered is null
            ? "Adds Mendrain to “Open with” and Default apps for ZIP, RAR and 7Z files, then opens Windows Settings so you can choose it. Your current default app is not changed."
            : !isThisCopy
                ? $"Registered for another copy of Mendrain ({registered}). The button switches to this copy."
                : defaults.Count == FileTypeRegistration.FileTypes.Count
                    ? "Mendrain is your default app for ZIP, RAR and 7Z files."
                    : defaults.Count > 0
                        ? $"Mendrain opens {string.Join(" and ", defaults)} files by default. Choose it for the other types in Windows Settings."
                        : "Mendrain is listed in “Open with” and Default apps. Windows keeps your current default until you choose Mendrain for each file type.";
        RemoveFileTypesButton.IsEnabled = registered is not null;
    }

    private async void SetDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        // Registers (or repairs) this copy; Windows itself must be used to make it the default.
        if (!FileAssociations.Register(Environment.ProcessPath!))
        {
            await Dialogs.MessageAsync("Could not register Mendrain", "Windows did not allow Mendrain to add itself for ZIP, RAR and 7Z files. Try again later.");
            return;
        }
        ShowFileTypes();

        var steps = FileAssociations.IsWindows11
            ? "1. Settings opens Mendrain’s page under Default apps (if it doesn’t, search for Mendrain there).\n"
              + "2. Click .zip, choose Mendrain and click Set default.\n"
              + "3. Do the same for .rar and .7z."
            : "1. In Default apps, click “Choose default apps by file type” at the bottom of the page.\n"
              + "2. Find .zip, click the app shown next to it and choose Mendrain.\n"
              + "3. Do the same for .rar and .7z.";
        var message = "Windows doesn’t let apps make themselves the default. You choose Mendrain once for each file type:\n\n"
                      + steps + "\n\n"
                      + "Another way: right-click an archive in File Explorer, choose Open with → Choose another app, "
                      + "pick Mendrain and tick “Always use this app to open … files”.";
        if (await Dialogs.ConfirmAsync("Choose Mendrain in Windows Settings", message, "Open Default apps")
            && !FileAssociations.OpenDefaultAppsSettings())
            await Dialogs.MessageAsync("Could not open Settings", "Open Windows Settings → Apps → Default apps yourself and choose Mendrain for .zip, .rar and .7z.");
    }

    private async void RemoveFileTypesButton_Click(object sender, RoutedEventArgs e)
    {
        if (FileAssociations.DefaultFor() is { Count: > 0 } defaults
            && !await Dialogs.ConfirmAsync("Remove Mendrain from Open with?",
                $"Mendrain is your default app for {string.Join(", ", defaults)} files. After removing it, Windows asks which app to use the next time you open one.",
                "Remove", destructive: true))
            return;
        if (!FileAssociations.Unregister())
            await Dialogs.MessageAsync("Could not remove Mendrain", "Windows did not allow Mendrain to change its file type entries. Try again later.");
        ShowFileTypes();
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
