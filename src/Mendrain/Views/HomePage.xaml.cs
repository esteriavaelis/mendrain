using Mendrain.Helpers;
using Mendrain.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Mendrain.Views;

public sealed partial class HomePage : Page
{
    public HomeViewModel ViewModel { get; } = new();

    public HomePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => ViewModel.Refresh();

    private void Recent_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentItem item)
            ViewModel.OpenRecentCommand.Execute(item);
    }

    private void RemoveRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecentItem item })
            ViewModel.RemoveRecentCommand.Execute(item);
    }

    private void DropZone_DragOver(object sender, DragEventArgs e) => DropHelper.DragOver(e, DropZone, "Drop to open");

    private void DropZone_DragLeave(object sender, DragEventArgs e) => DropHelper.Reset(DropZone);

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropHelper.Reset(DropZone);
        var paths = await DropHelper.GetPathsAsync(e);
        if (paths.Count > 0)
            App.MainWindow.HandleIncomingPaths(paths);
    }
}
