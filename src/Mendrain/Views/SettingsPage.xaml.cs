using System.Reflection;
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
        _ready = true;
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string theme })
            return;
        App.Settings.Theme = theme;
        App.MainWindow.ApplyTheme(App.Settings.ElementTheme);
    }
}
