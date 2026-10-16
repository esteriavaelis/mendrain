using Mendrain.Helpers;
using Mendrain.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mendrain.Views;

public sealed partial class CreateZipPage : Page
{
    private static readonly SolidColorBrush Transparent = new(Microsoft.UI.Colors.Transparent);

    public CreateZipViewModel ViewModel => App.CreateZip;

    public CreateZipPage()
    {
        InitializeComponent();
        ViewModel.SelectedLevel ??= ViewModel.Levels[0];
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SourceItem item })
            ViewModel.RemoveCommand.Execute(item);
    }

    private void Items_DragOver(object sender, DragEventArgs e)
    {
        if (!ViewModel.IsBusy)
            DropHelper.DragOver(e, ItemsZone, "Add to ZIP");
    }

    private void Items_DragLeave(object sender, DragEventArgs e) => ItemsZone.Background = Transparent;

    private async void Items_Drop(object sender, DragEventArgs e)
    {
        ItemsZone.Background = Transparent;
        ViewModel.AddPaths(await DropHelper.GetPathsAsync(e));
    }
}
