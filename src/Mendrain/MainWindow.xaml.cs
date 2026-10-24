using System.Runtime.InteropServices;
using Mendrain.Core;
using Mendrain.Helpers;
using Mendrain.ViewModels;
using Mendrain.Views;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace Mendrain;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["Home"] = typeof(HomePage),
        ["Batch"] = typeof(BatchExtractPage),
        ["CreateZip"] = typeof(CreateZipPage),
        ["Settings"] = typeof(SettingsPage),
    };

    private bool _closeConfirmed;
    private bool _syncingSelection;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Mendrain.ico"));
        AppWindow.Title = "Mendrain";

        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1120 * scale), (int)(740 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(720 * scale);
            presenter.PreferredMinimumHeight = (int)(520 * scale);
        }

        // Mica on Windows 11; on Windows 10 the themed solid background from XAML stays.
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
            Root.Background = null;
        }

        ApplyTheme(App.Settings.ElementTheme);
        Root.ActualThemeChanged += (_, _) => UpdateCaptionButtons();
        AppWindow.Closing += AppWindow_Closing;

        Navigate("Home");
        LegacyBar.IsOpen = App.Updates.CanUpdate && LegacyInstall.FindUninstaller() is not null;
    }

    public UpdateViewModel Updates => App.Updates;

    private async void RemoveLegacy_Click(object sender, RoutedEventArgs e)
    {
        RemoveLegacyButton.IsEnabled = false;
        if (await LegacyInstall.RemoveAsync())
        {
            LegacyBar.IsOpen = false;
            return;
        }
        RemoveLegacyButton.IsEnabled = true;
        await Dialogs.MessageAsync("Old version not removed",
            "The previous Mendrain is still installed. You can remove it in Windows Settings → Apps (it is listed as “Mendrain”, version 0.1).");
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    /// <summary>Restores and activates the window, e.g. when a request arrives from Explorer.</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    public void ApplyTheme(ElementTheme theme)
    {
        Root.RequestedTheme = theme;
        UpdateCaptionButtons();
    }

    private void UpdateCaptionButtons()
    {
        var bar = AppWindow.TitleBar;
        var dark = Root.ActualTheme == ElementTheme.Dark;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x80, 0, 0, 0);
        bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0, 0, 0);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
        bar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x0B, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0A, 0, 0, 0);
        bar.ButtonPressedForegroundColor = bar.ButtonForegroundColor;
    }

    // ---------------------------------------------------------------- navigation

    public void Navigate(string tag, object? parameter = null)
    {
        if (Pages.TryGetValue(tag, out var page))
            Navigate(page, parameter);
    }

    public void Navigate(Type page, object? parameter = null)
    {
        if (ContentFrame.CurrentSourcePageType == page && parameter is null)
            return;
        ContentFrame.Navigate(page, parameter, new EntranceNavigationTransitionInfo());
    }

    public void OpenArchive(string path) => Navigate(typeof(ArchivePage), path);

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection)
            return;
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
            Navigate(tag);
    }

    private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        // Keep the highlighted menu item in sync; the archive viewer belongs to "Home".
        var tag = Pages.FirstOrDefault(p => p.Value == e.SourcePageType).Key ?? "Home";
        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        _syncingSelection = true;
        Nav.SelectedItem = item;
        _syncingSelection = false;
    }

    /// <summary>
    /// Routes dropped or command-line paths: one archive opens the viewer, several go to Batch Extract,
    /// and ordinary files or folders go to Create ZIP.
    /// </summary>
    public void HandleIncomingPaths(IReadOnlyList<string> paths)
    {
        var archives = paths.Where(p => File.Exists(p) && ArchiveNaming.IsSupportedArchive(p)).ToList();
        if (archives.Count == paths.Count && archives.Count > 0)
        {
            var primary = archives.Where(a => !ArchiveNaming.IsSecondaryVolume(a)).ToList();
            if (primary.Count == 1)
            {
                OpenArchive(primary[0]);
                return;
            }
            App.Batch.AddPaths(archives);
            Navigate("Batch");
            return;
        }

        if (archives.Count == 0)
        {
            App.CreateZip.AddPaths(paths);
            Navigate("CreateZip");
            return;
        }

        _ = Dialogs.MessageAsync("Mixed selection",
            "Drop archives to open or extract them, or drop files and folders to put them in a new ZIP — not both at once.");
    }

    // ---------------------------------------------------------------- closing

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || !Operations.AnyRunning)
            return;

        args.Cancel = true;
        var stop = await Dialogs.ConfirmAsync("Stop and close?",
            "An archive operation is still running. Mendrain will cancel it, remove the files it created so far, and then close.",
            "Stop and close", destructive: true);
        if (!stop)
            return;

        await Operations.CancelAllAsync(TimeSpan.FromSeconds(15));
        _closeConfirmed = true;
        Close();
    }
}
