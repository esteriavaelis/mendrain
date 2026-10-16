using Mendrain.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Mendrain.Views;

public sealed partial class ArchivePage : Page
{
    public ArchiveViewModel ViewModel { get; } = new();

    public ArchivePage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is string path)
            await ViewModel.LoadAsync(path);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => App.MainWindow.Navigate("Home");

    private void Breadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is Crumb crumb)
            ViewModel.NavigateTo(crumb.Path);
    }

    private void EntryList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ArchiveRow row)
            ViewModel.OpenRowCommand.Execute(row);
    }

    private void EntryList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && EntryList.SelectedItem is ArchiveRow row)
        {
            ViewModel.OpenRowCommand.Execute(row);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Back && ViewModel.CanGoUp)
        {
            ViewModel.UpCommand.Execute(null);
            e.Handled = true;
        }
    }
}
