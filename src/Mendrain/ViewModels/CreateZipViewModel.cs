using System.Collections.ObjectModel;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mendrain.Core;
using Mendrain.Core.Models;
using Mendrain.Core.Safety;
using Mendrain.Core.Services;
using Mendrain.Helpers;
using Microsoft.UI.Xaml.Controls;

namespace Mendrain.ViewModels;

public sealed record SourceItem(string Path, bool IsFolder, long Size)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Glyph => IsFolder ? "\uF12B" : "\uE7C3";
    public string Detail => IsFolder ? $"Folder · {Path}" : $"{Format.Size(Size)} · {System.IO.Path.GetDirectoryName(Path)}";
    public override string ToString() => Name;
}

public sealed record CompressionOption(string Label, string Description, CompressionLevel Level)
{
    public override string ToString() => Label;
}

public sealed partial class CreateZipViewModel : ObservableObject
{
    private readonly ZipCreator _creator = new();
    private CancellationTokenSource? _cts;
    private string? _lastOutput;

    public CreateZipViewModel()
    {
        Items.CollectionChanged += (_, _) =>
        {
            HasItems = Items.Count > 0;
            OnPropertyChanged(nameof(CanCreate));
        };
    }

    public ObservableCollection<SourceItem> Items { get; } = [];

    public IReadOnlyList<CompressionOption> Levels { get; } =
    [
        new("Normal", "Good balance of size and speed", CompressionLevel.Optimal),
        new("Maximum", "Smallest file, slower", CompressionLevel.SmallestSize),
        new("Fast", "Larger file, faster", CompressionLevel.Fastest),
        new("Store", "No compression", CompressionLevel.NoCompression),
    ];

    [ObservableProperty] public partial CompressionOption? SelectedLevel { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanCreate))] public partial bool HasItems { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanCreate))] public partial string ZipName { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanCreate))] public partial string SaveFolder { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanCreate))]
    public partial bool IsBusy { get; set; }
    public bool IsIdle => !IsBusy;
    public bool CanCreate => !IsBusy && HasItems && !string.IsNullOrWhiteSpace(ZipName) && !string.IsNullOrWhiteSpace(SaveFolder);

    [ObservableProperty] public partial double ProgressValue { get; set; }
    [ObservableProperty] public partial string ProgressText { get; set; } = "";

    [ObservableProperty] public partial bool IsResultOpen { get; set; }
    [ObservableProperty] public partial InfoBarSeverity ResultSeverity { get; set; }
    [ObservableProperty] public partial string ResultTitle { get; set; } = "";
    [ObservableProperty] public partial string ResultMessage { get; set; } = "";
    [ObservableProperty] public partial bool CanShowResult { get; set; }

    // ---------------------------------------------------------------- selection

    public void AddPaths(IEnumerable<string> paths)
    {
        if (IsBusy)
            return;
        foreach (var raw in paths)
        {
            var path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(raw));
            if (Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (Directory.Exists(path))
                Items.Add(new SourceItem(path, true, 0));
            else if (File.Exists(path))
                Items.Add(new SourceItem(path, false, new FileInfo(path).Length));
        }

        if (Items.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(SaveFolder))
                SaveFolder = System.IO.Path.GetDirectoryName(Items[0].Path) ?? "";
            if (string.IsNullOrWhiteSpace(ZipName))
                ZipName = SuggestName();
        }
        SelectedLevel ??= Levels[0];
    }

    private string SuggestName()
    {
        var first = Items[0];
        var stem = Items.Count == 1
            ? (first.IsFolder ? first.Name : System.IO.Path.GetFileNameWithoutExtension(first.Name))
            : System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(first.Path)) ?? "Archive";
        return WindowsNames.ToSafeFolderName(stem) + ".zip";
    }

    [RelayCommand]
    private async Task AddFilesAsync() => AddPaths(await Pickers.PickFilesAsync());

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (await Pickers.PickFolderAsync() is { } folder)
            AddPaths([folder]);
    }

    [RelayCommand]
    private void Remove(SourceItem item)
    {
        if (!IsBusy)
            Items.Remove(item);
    }

    [RelayCommand]
    private void Clear()
    {
        if (IsBusy)
            return;
        Items.Clear();
        ZipName = "";
        IsResultOpen = false;
    }

    [RelayCommand]
    private async Task BrowseSaveFolderAsync()
    {
        if (await Pickers.PickFolderAsync() is { } folder)
            SaveFolder = folder;
    }

    // ---------------------------------------------------------------- create

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (!CanCreate)
            return;
        IsResultOpen = false;

        var name = ZipName.Trim();
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            name += ".zip";
        if (WindowsNames.ToSafeFolderName(name, "") != name)
        {
            ShowResult(InfoBarSeverity.Error, "Invalid file name", $"“{name}” cannot be used as a file name on Windows.");
            return;
        }
        if (!Directory.Exists(SaveFolder))
        {
            ShowResult(InfoBarSeverity.Error, "Folder not found", $"The folder “{SaveFolder}” does not exist.");
            return;
        }

        var output = System.IO.Path.Combine(SaveFolder, name);
        var overwrite = false;
        if (File.Exists(output))
        {
            if (!await Dialogs.ConfirmAsync("Replace existing file?",
                    $"“{name}” already exists in this folder. Replace it with the new ZIP? The old file is only replaced after the new one is complete.",
                    "Replace", destructive: true))
                return;
            overwrite = true;
        }

        _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressValue = 0;
        ProgressText = "Preparing…";
        var progress = new Progress<ExtractProgress>(p =>
        {
            if (!IsBusy) return;
            ProgressValue = (p.Fraction ?? 0) * 100;
            ProgressText = $"{Format.Size(p.BytesDone)} of {Format.Size(p.BytesTotal ?? 0)}  ·  {p.CurrentEntry}";
        });
        var request = new ZipRequest(Items.Select(i => i.Path).ToList(), output, (SelectedLevel ?? Levels[0]).Level, overwrite);
        var work = RunAsync(request, progress, _cts.Token);
        Operations.Track(_cts, work);
        await work;
    }

    private async Task RunAsync(ZipRequest request, IProgress<ExtractProgress> progress, CancellationToken token)
    {
        try
        {
            var result = await _creator.CreateAsync(request, progress, token);
            _lastOutput = result.OutputPath;
            var ratio = result.SourceBytes > 0 ? $" ({100.0 * result.ArchiveBytes / result.SourceBytes:0}% of original)" : "";
            var message = $"{Format.Count(result.FilesAdded, "file", "files")}, {Format.Size(result.ArchiveBytes)}{ratio}";
            if (result.Skipped.Count > 0)
                message += $". {Format.Count(result.Skipped.Count, "link was", "links were")} not followed.";
            ShowResult(result.Skipped.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                $"Created {System.IO.Path.GetFileName(result.OutputPath)}", message, canShow: true);
        }
        catch (ArchiveError e) when (e.Kind == ArchiveErrorKind.Cancelled)
        {
            ShowResult(InfoBarSeverity.Informational, "Cancelled", "No ZIP file was created.");
        }
        catch (Exception e)
        {
            ShowResult(InfoBarSeverity.Error, "Could not create the ZIP", ArchiveError.From(e).Message);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void ShowOutput()
    {
        if (_lastOutput is not null)
            Shell.ShowFile(_lastOutput);
    }

    private void ShowResult(InfoBarSeverity severity, string title, string message, bool canShow = false)
    {
        ResultSeverity = severity;
        ResultTitle = title;
        ResultMessage = message;
        CanShowResult = canShow;
        IsResultOpen = true;
    }
}
