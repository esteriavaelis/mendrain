using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mendrain.Helpers;

namespace Mendrain.ViewModels;

public sealed record RecentItem(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public bool Exists => File.Exists(Path);
    public string Detail => Exists ? Folder : $"{Folder} · missing";
    public override string ToString() => Name;
}

public sealed partial class HomeViewModel : ObservableObject
{
    public ObservableCollection<RecentItem> Recent { get; } = [];

    [ObservableProperty]
    public partial bool HasRecent { get; set; }

    public void Refresh()
    {
        Recent.Clear();
        foreach (var path in App.Settings.Recent.Load())
            Recent.Add(new RecentItem(path));
        HasRecent = Recent.Count > 0;
    }

    [RelayCommand]
    private async Task OpenArchiveAsync()
    {
        if (await Pickers.PickArchiveAsync() is { } path)
            App.MainWindow.OpenArchive(path);
    }

    [RelayCommand]
    private async Task OpenRecentAsync(RecentItem item)
    {
        if (!item.Exists)
        {
            await Dialogs.MessageAsync("Archive not found", $"“{item.Name}” is no longer at\n{item.Folder}.");
            RemoveRecent(item);
            return;
        }
        App.MainWindow.OpenArchive(item.Path);
    }

    [RelayCommand]
    private void RemoveRecent(RecentItem item)
    {
        App.Settings.Recent.Remove(item.Path);
        Refresh();
    }

    [RelayCommand]
    private void ClearRecent()
    {
        App.Settings.Recent.Clear();
        Refresh();
    }

    [RelayCommand]
    private void GoToBatch() => App.MainWindow.Navigate("Batch");

    [RelayCommand]
    private void GoToCreateZip() => App.MainWindow.Navigate("CreateZip");
}
