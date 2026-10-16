using Mendrain.Core;
using Windows.Storage.Pickers;

namespace Mendrain.Helpers;

/// <summary>File and folder pickers bound to the main window (required for unpackaged WinUI apps).</summary>
public static class Pickers
{
    private static T Init<T>(T picker)
    {
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        return picker;
    }

    public static async Task<string?> PickArchiveAsync()
    {
        var picker = Init(new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.Downloads });
        foreach (var ext in ArchiveNaming.SupportedExtensions)
            picker.FileTypeFilter.Add(ext);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public static async Task<IReadOnlyList<string>> PickArchivesAsync()
    {
        var picker = Init(new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.Downloads });
        foreach (var ext in ArchiveNaming.SupportedExtensions)
            picker.FileTypeFilter.Add(ext);
        var files = await picker.PickMultipleFilesAsync();
        return files?.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList() ?? [];
    }

    public static async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var picker = Init(new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.DocumentsLibrary });
        picker.FileTypeFilter.Add("*");
        var files = await picker.PickMultipleFilesAsync();
        return files?.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList() ?? [];
    }

    public static async Task<string?> PickFolderAsync()
    {
        var picker = Init(new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder });
        picker.FileTypeFilter.Add("*");
        return (await picker.PickSingleFolderAsync())?.Path;
    }
}
