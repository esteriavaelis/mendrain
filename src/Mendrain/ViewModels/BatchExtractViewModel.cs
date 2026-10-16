using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mendrain.Core;
using Mendrain.Core.Models;
using Mendrain.Core.Services;
using Mendrain.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mendrain.ViewModels;

public sealed partial class BatchExtractViewModel : ObservableObject
{
    private readonly BatchExtractService _service = new();
    private CancellationTokenSource? _cts;
    private List<ArchiveJob> _runSet = [];
    private ArchiveJob? _current;

    public BatchExtractViewModel()
    {
        Jobs.CollectionChanged += (_, _) => OnQueueChanged();
        _service.CurrentJobChanged += job =>
        {
            _current = job;
            CurrentJobText = job is null ? "" : $"Extracting {job.FileName}";
            UpdateOverallProgress();
        };
    }

    public ObservableCollection<ArchiveJob> Jobs { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanExtract))]
    public partial string Destination { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanExtract), nameof(CanEditQueue))]
    public partial bool IsRunning { get; set; }

    public bool IsIdle => !IsRunning;
    public bool CanEditQueue => !IsRunning && Jobs.Count > 0;
    public bool CanExtract => !IsRunning && Jobs.Any(j => j.Status == JobStatus.Ready) && !string.IsNullOrWhiteSpace(Destination);

    [ObservableProperty] public partial bool IsQueueEmpty { get; set; } = true;
    [ObservableProperty] public partial string QueueSummary { get; set; } = "";
    [ObservableProperty] public partial string CurrentJobText { get; set; } = "";
    [ObservableProperty] public partial double OverallProgress { get; set; }
    [ObservableProperty] public partial string OverallText { get; set; } = "";

    [ObservableProperty] public partial bool IsNoticeOpen { get; set; }
    [ObservableProperty] public partial string NoticeText { get; set; } = "";

    [ObservableProperty] public partial bool IsSummaryOpen { get; set; }
    [ObservableProperty] public partial InfoBarSeverity SummarySeverity { get; set; }
    [ObservableProperty] public partial string SummaryTitle { get; set; } = "";
    [ObservableProperty] public partial string SummaryMessage { get; set; } = "";
    [ObservableProperty] public partial string SummaryDetails { get; set; } = "";

    // ---------------------------------------------------------------- queue editing

    /// <summary>Adds archives to the queue. Later parts of multi-volume RAR sets and duplicates are left out, with a note.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        if (IsRunning)
            return;
        var notes = new List<string>();
        int volumes = 0, unsupported = 0, duplicates = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !ArchiveNaming.IsSupportedArchive(path)) { unsupported++; continue; }
            if (ArchiveNaming.IsSecondaryVolume(path)) { volumes++; continue; }
            if (Jobs.Any(j => string.Equals(j.SourcePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))) { duplicates++; continue; }
            Jobs.Add(new ArchiveJob(path));
        }

        if (string.IsNullOrWhiteSpace(Destination) && Jobs.Count > 0)
            Destination = Path.GetDirectoryName(Jobs[0].SourcePath) ?? "";

        if (volumes > 0) notes.Add(volumes == 1
            ? "1 additional part of a multi-volume archive was not added; it is extracted together with part 1."
            : $"{volumes} additional parts of multi-volume archives were not added; they are extracted together with part 1.");
        if (duplicates > 0) notes.Add($"{Format.Count(duplicates, "archive was", "archives were")} already in the queue.");
        if (unsupported > 0) notes.Add($"{Format.Count(unsupported, "item is not a supported archive", "items are not supported archives")} (ZIP, 7Z, RAR).");
        NoticeText = string.Join(" ", notes);
        IsNoticeOpen = notes.Count > 0;
    }

    [RelayCommand]
    private async Task AddArchivesAsync() => AddPaths(await Pickers.PickArchivesAsync());

    public void Remove(IEnumerable<ArchiveJob> jobs)
    {
        if (IsRunning)
            return;
        foreach (var job in jobs.ToList())
            Jobs.Remove(job);
    }

    [RelayCommand]
    private void Clear()
    {
        if (IsRunning)
            return;
        Jobs.Clear();
        IsSummaryOpen = false;
        IsNoticeOpen = false;
        OverallProgress = 0;
        OverallText = "";
    }

    [RelayCommand]
    private async Task BrowseDestinationAsync()
    {
        if (await Pickers.PickFolderAsync() is { } folder)
            Destination = folder;
    }

    [RelayCommand]
    private void OpenDestination() => Shell.OpenFolder(Destination);

    private void OnQueueChanged()
    {
        foreach (var job in Jobs)
        {
            job.PropertyChanged -= Job_PropertyChanged;
            job.PropertyChanged += Job_PropertyChanged;
        }
        IsQueueEmpty = Jobs.Count == 0;
        UpdateQueueSummary();
        OnPropertyChanged(nameof(CanExtract));
        OnPropertyChanged(nameof(CanEditQueue));
    }

    private void Job_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ArchiveJob.Status))
        {
            UpdateQueueSummary();
            OnPropertyChanged(nameof(CanExtract));
        }
        if (e.PropertyName is nameof(ArchiveJob.Status) or nameof(ArchiveJob.Progress))
            UpdateOverallProgress();
    }

    private void UpdateQueueSummary()
    {
        var ready = Jobs.Count(j => j.Status == JobStatus.Ready);
        QueueSummary = Jobs.Count == 0 ? "" : $"{Format.Count(Jobs.Count, "archive", "archives")} · {ready} ready";
    }

    private void UpdateOverallProgress()
    {
        if (_runSet.Count == 0)
            return;
        var finished = _runSet.Count(j => j.Status is not (JobStatus.Ready or JobStatus.Extracting));
        // Only real numbers: finished jobs, plus the current archive's share when its size is known.
        var current = _current is { Status: JobStatus.Extracting, Progress: { } p } ? p : 0;
        OverallProgress = (finished + current) / _runSet.Count * 100;
        OverallText = $"{finished} of {_runSet.Count} done";
    }

    // ---------------------------------------------------------------- run

    [RelayCommand]
    private async Task ExtractAllAsync()
    {
        if (!CanExtract)
            return;
        IsSummaryOpen = false;
        IsNoticeOpen = false;

        if (!Directory.Exists(Destination))
        {
            await Dialogs.MessageAsync("Destination not found", $"The folder “{Destination}” does not exist. Choose another destination.");
            return;
        }

        var conflicts = BatchExtractService.FindConflicts(Jobs, Destination);
        if (conflicts.Count > 0 && !await ResolveConflictsAsync(conflicts))
            return;

        _runSet = Jobs.Where(j => j.Status == JobStatus.Ready).ToList();
        if (_runSet.Count == 0)
            return;

        _cts = new CancellationTokenSource();
        IsRunning = true;
        OverallProgress = 0;
        UpdateOverallProgress();
        var work = _service.RunAsync(_runSet, Destination, AskPasswordAsync, _cts.Token);
        Operations.Track(_cts, work);
        try
        {
            ShowSummary(await work);
        }
        catch (ArchiveError e)
        {
            SummarySeverity = InfoBarSeverity.Error;
            SummaryTitle = "Batch not started";
            SummaryMessage = e.Message;
            SummaryDetails = "";
            IsSummaryOpen = true;
        }
        finally
        {
            IsRunning = false;
            CurrentJobText = "";
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task<bool> ResolveConflictsAsync(IReadOnlyList<DestinationConflict> conflicts)
    {
        var lines = conflicts.Take(6).Select(c => c.Kind == ConflictKind.AlreadyExists
            ? $"{c.Job.FileName} → “{c.FolderName}” already exists in the destination"
            : $"{c.Job.FileName} → “{c.FolderName}” is also used by {c.OtherJob!.FileName}");
        var text = string.Join("\n", lines) + (conflicts.Count > 6 ? $"\n…and {conflicts.Count - 6} more" : "");

        var panel = new StackPanel { Spacing = 10, MaxWidth = 480 };
        panel.Children.Add(new TextBlock
        {
            Text = "Each archive gets its own folder, and Mendrain never mixes archives or writes into folders that already exist.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.Resources["SecondaryTextStyle"], TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = "Use new folder names (for example “Project (2)”), or skip these archives?",
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            Title = conflicts.Count == 1 ? "A destination folder is already taken" : $"{conflicts.Count} destination folders are already taken",
            Content = panel,
            PrimaryButtonText = "Use new names",
            SecondaryButtonText = "Skip them",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        switch (await Dialogs.ShowAsync(dialog))
        {
            case ContentDialogResult.Primary:
                BatchExtractService.ResolveConflicts(Jobs, Destination, ConflictResolution.UseNewFolderName);
                return true;
            case ContentDialogResult.Secondary:
                BatchExtractService.ResolveConflicts(Jobs, Destination, ConflictResolution.Skip);
                return true;
            default:
                return false;
        }
    }

    private static Task<string?> AskPasswordAsync(PasswordRequest request) =>
        Dialogs.PasswordAsync(request.Job.FileName, request.Attempt == 1 ? null : request.Reason, "Skip archive");

    private void ShowSummary(BatchSummary summary)
    {
        UpdateOverallProgress();
        var parts = new List<string> { $"{summary.Completed} extracted" };
        if (summary.Failed > 0) parts.Add($"{summary.Failed} failed");
        if (summary.Skipped > 0) parts.Add($"{summary.Skipped} skipped");
        if (summary.Cancelled > 0) parts.Add($"{summary.Cancelled} cancelled");
        if (summary.CompletedWithWarnings > 0) parts.Add($"{summary.CompletedWithWarnings} with warnings");

        SummarySeverity = summary.Failed > 0 ? InfoBarSeverity.Error
            : summary.AllSucceeded ? InfoBarSeverity.Success
            : InfoBarSeverity.Warning;
        SummaryTitle = summary.Cancelled > 0 ? "Batch cancelled" : "Batch finished";
        SummaryMessage = $"{string.Join(" · ", parts)} — {summary.Duration:m\\:ss}";
        SummaryDetails = string.Join("\n", summary.NeedsAttention.Select(j => $"{j.FileName}: {Format.StatusText(j.Status)} — {j.Message}"));
        IsSummaryOpen = true;
    }
}
