using Mendrain.Core.Models;
using Mendrain.Helpers;
using Mendrain.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Mendrain.Views;

public sealed partial class BatchExtractPage : Page
{
    private static readonly SolidColorBrush Transparent = new(Microsoft.UI.Colors.Transparent);

    public BatchExtractViewModel ViewModel => App.Batch;

    public BatchExtractPage()
    {
        InitializeComponent();
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e) =>
        ViewModel.Remove(QueueList.SelectedItems.OfType<ArchiveJob>());

    private void QueueList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete)
        {
            ViewModel.Remove(QueueList.SelectedItems.OfType<ArchiveJob>());
            e.Handled = true;
        }
    }

    private void Queue_DragOver(object sender, DragEventArgs e)
    {
        if (ViewModel.IsRunning)
            return;
        DropHelper.DragOver(e, QueueZone, "Add to queue");
    }

    private void Queue_DragLeave(object sender, DragEventArgs e) => QueueZone.Background = Transparent;

    private async void Queue_Drop(object sender, DragEventArgs e)
    {
        QueueZone.Background = Transparent;
        ViewModel.AddPaths(await DropHelper.GetPathsAsync(e));
    }
}
