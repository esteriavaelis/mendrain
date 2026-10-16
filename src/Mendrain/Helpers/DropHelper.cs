using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Mendrain.Helpers;

/// <summary>Shared drag-and-drop handling for file drop zones.</summary>
public static class DropHelper
{
    public static void DragOver(DragEventArgs e, Panel zone, string caption)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = caption;
        e.DragUIOverride.IsGlyphVisible = false;
        zone.Background = (Brush)Application.Current.Resources["DropZoneActiveBackgroundBrush"];
    }

    public static void Reset(Panel zone) =>
        zone.Background = (Brush)Application.Current.Resources["DropZoneBackgroundBrush"];

    public static async Task<IReadOnlyList<string>> GetPathsAsync(DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return [];
        var deferral = e.GetDeferral();
        try
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            return items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        }
        finally
        {
            deferral.Complete();
        }
    }
}
