using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mendrain.Core;
using Mendrain.Core.Models;
using Mendrain.Core.Safety;
using Mendrain.Core.Services;
using Mendrain.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mendrain.ViewModels;

public sealed record ArchiveRow(string Name, string Path, bool IsFolder, long Size, long Packed, DateTime? Modified, bool IsEncrypted, int ItemCount)
{
    public string Glyph => IsFolder ? "\uF12B" : "\uE7C3";
    public string SizeText => IsFolder ? Format.Count(ItemCount, "file", "files") : Format.Size(Size);
    // Solid archives compress files together and report no per-file packed size.
    public string PackedText => IsFolder ? "" : Packed <= 0 && Size > 0 ? "—" : Format.Size(Packed);
    public string ModifiedText => Format.Date(Modified);
    public Visibility LockVisibility => IsEncrypted ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Used as the list item's accessible name.</summary>
    public override string ToString() => Name;
}

/// <remarks>BreadcrumbBar shows items via ToString(); its overflow button gets a null item, so no typed template is used.</remarks>
public sealed record Crumb(string Name, string Path)
{
    public override string ToString() => Name;
}

public sealed partial class ArchiveViewModel : ObservableObject
{
    private readonly ArchiveService _service = new();
    private ArchiveContents? _contents;
    private ArchiveFolder? _root;
    private ArchiveFolder? _current;
    private string? _password;
    // Save _password once it has been shown to work (the user ticked "Save password").
    private bool _savePassword;
    // Passwords that did not work for this archive; never tried again.
    private readonly HashSet<string> _failedPasswords = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private string? _lastDestination;

    public ObservableCollection<ArchiveRow> Rows { get; } = [];
    public ObservableCollection<Crumb> Crumbs { get; } = [];

    [ObservableProperty] public partial string FileName { get; set; } = "";
    [ObservableProperty] public partial string FormatName { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsLoaded { get; set; }
    [ObservableProperty] public partial string? LoadError { get; set; }
    [ObservableProperty] public partial bool IsFolderEmpty { get; set; }
    [ObservableProperty] public partial bool CanGoUp { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(DestinationPreview))]
    public partial string DestinationFolder { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(DestinationPreview))]
    public partial string DestinationName { get; set; } = "";

    public string DestinationPreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DestinationFolder))
                return "";
            var name = DestinationName.Trim();
            return name.Length == 0 ? DestinationFolder : System.IO.Path.Combine(DestinationFolder, name);
        }
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsExtracting { get; set; }
    public bool IsIdle => !IsExtracting;
    [ObservableProperty] public partial double ProgressValue { get; set; }
    [ObservableProperty] public partial bool ProgressIndeterminate { get; set; }
    [ObservableProperty] public partial string ProgressText { get; set; } = "";

    [ObservableProperty] public partial bool IsResultOpen { get; set; }
    [ObservableProperty] public partial InfoBarSeverity ResultSeverity { get; set; }
    [ObservableProperty] public partial string ResultTitle { get; set; } = "";
    [ObservableProperty] public partial string ResultMessage { get; set; } = "";
    [ObservableProperty] public partial string ResultDetails { get; set; } = "";
    [ObservableProperty] public partial bool CanOpenResult { get; set; }

    // ---------------------------------------------------------------- loading

    public async Task LoadAsync(string path)
    {
        FileName = System.IO.Path.GetFileName(path);
        IsLoading = true;
        LoadError = null;
        try
        {
            ArchiveContents? contents = null;
            for (var attempt = 0; contents is null; attempt++)
            {
                try
                {
                    contents = await _service.OpenAsync(path, _password);
                }
                catch (ArchiveError e) when (e.Kind is ArchiveErrorKind.PasswordRequired or ArchiveErrorKind.WrongPassword
                                             && attempt < BatchExtractService.MaxPasswordAttempts)
                {
                    // Names are encrypted too: a password is needed just to list this archive.
                    PasswordFailed();
                    IsLoading = false;
                    var answered = await AskPasswordAsync(e.Kind == ArchiveErrorKind.WrongPassword ? e.Message : null);
                    IsLoading = true;
                    if (!answered)
                        throw new ArchiveError(ArchiveErrorKind.PasswordRequired, "The archive's contents are encrypted. A password is needed to view them.");
                }
            }

            _contents = contents;
            _root = ArchiveFolder.Build(contents.Items);
            FormatName = contents.Format;
            Summary = BuildSummary(contents);
            DestinationFolder = System.IO.Path.GetDirectoryName(contents.FilePath) ?? "";
            DestinationName = ArchiveNaming.GetDestinationFolderName(contents.FilePath);
            NavigateTo("");
            IsLoaded = true;
            App.Settings.Recent.Add(contents.FilePath);
            // Listing encrypted names succeeded, so the password is right.
            if (SavePasswordIfRequested() is { } note)
                ShowResult(InfoBarSeverity.Warning, "Password not saved", note);
        }
        catch (ArchiveError e)
        {
            LoadError = e.Message;
        }
        catch (Exception e)
        {
            LoadError = ArchiveError.From(e).Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string BuildSummary(ArchiveContents c)
    {
        var parts = new List<string> { Format.Count(c.FileCount, "file", "files"), Format.Size(c.TotalSize) };
        if (c.VolumeCount > 1) parts.Add($"{c.VolumeCount} parts");
        if (c.IsSolid) parts.Add("Solid");
        if (c.NeedsPassword) parts.Add(c.HeadersEncrypted ? "Encrypted names and data" : "Encrypted");
        return string.Join("  ·  ", parts);
    }

    // ---------------------------------------------------------------- browsing

    public void NavigateTo(string folderPath)
    {
        if (_root?.Find(folderPath) is not { } folder)
            return;
        _current = folder;
        Rows.Clear();
        foreach (var f in folder.Folders)
            Rows.Add(new ArchiveRow(f.Name, f.Path, true, f.TotalSize, 0, f.Modified, false, f.TotalFileCount));
        foreach (var f in folder.Files)
            Rows.Add(new ArchiveRow(f.Name, f.Path, false, f.Size, f.CompressedSize, f.Modified, f.IsEncrypted, 0));
        IsFolderEmpty = Rows.Count == 0;
        CanGoUp = folder.Parent is not null;

        Crumbs.Clear();
        for (var f = folder; f is not null; f = f.Parent)
            Crumbs.Insert(0, new Crumb(f.Parent is null ? FileName : f.Name, f.Path));
    }

    [RelayCommand]
    private void OpenRow(ArchiveRow? row)
    {
        if (row is { IsFolder: true })
            NavigateTo(row.Path);
    }

    [RelayCommand]
    private void Up()
    {
        if (_current?.Parent is { } parent)
            NavigateTo(parent.Path);
    }

    [RelayCommand]
    private async Task BrowseDestinationAsync()
    {
        if (await Pickers.PickFolderAsync() is { } folder)
            DestinationFolder = folder;
    }

    // ---------------------------------------------------------------- extraction

    [RelayCommand]
    private async Task ExtractAsync()
    {
        if (_contents is null || IsExtracting)
            return;
        IsResultOpen = false;

        if (!Directory.Exists(DestinationFolder))
        {
            ShowResult(InfoBarSeverity.Error, "Choose a destination", "The destination folder does not exist.");
            return;
        }
        var name = DestinationName.Trim();
        if (name.Length > 0 && WindowsNames.ToSafeFolderName(name, "") != name)
        {
            ShowResult(InfoBarSeverity.Error, "Invalid folder name", $"“{name}” cannot be used as a folder name on Windows.");
            return;
        }
        var destination = name.Length == 0 ? DestinationFolder : System.IO.Path.Combine(DestinationFolder, name);
        if (File.Exists(destination))
        {
            ShowResult(InfoBarSeverity.Error, "Name already in use", $"A file named “{name}” already exists there. Choose another folder name.");
            return;
        }

        // Existing folder: merging is safe when nothing collides; otherwise the user decides.
        var mode = DestinationMode.CreateNew;
        var policy = ExistingFilePolicy.Skip;
        if (Directory.Exists(destination))
        {
            mode = DestinationMode.MergeIntoExisting;
            var existing = _service.FindExistingFiles(_contents, destination);
            if (existing.Count > 0)
            {
                if (await AskExistingFilePolicyAsync(destination, existing) is not { } chosen)
                    return;
                policy = chosen;
            }
        }

        if (_contents.NeedsPassword && _password is null && !await AskPasswordAsync(null))
            return;

        _cts = new CancellationTokenSource();
        IsExtracting = true;
        ProgressValue = 0;
        ProgressIndeterminate = _contents.Items.Any(i => !i.IsDirectory && i.Size < 0);
        ProgressText = "Preparing…";
        var progress = new Progress<ExtractProgress>(p =>
        {
            if (!IsExtracting) return;
            ProgressIndeterminate = p.Fraction is null;
            ProgressValue = (p.Fraction ?? 0) * 100;
            ProgressText = p.BytesTotal is { } total
                ? $"{Format.Size(p.BytesDone)} of {Format.Size(total)}  ·  {p.CurrentEntry}"
                : $"{Format.Count(p.FilesDone, "file", "files")}  ·  {p.CurrentEntry}";
        });

        var work = RunExtractionAsync(destination, mode, policy, progress, _cts.Token);
        Operations.Track(_cts, work);
        await work;
    }

    private async Task RunExtractionAsync(string destination, DestinationMode mode, ExistingFilePolicy policy,
        IProgress<ExtractProgress> progress, CancellationToken token)
    {
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var request = new ExtractRequest(_contents!.FilePath, destination, _password, mode, policy);
                    var result = await _service.ExtractAsync(request, progress, token);
                    ReportSuccess(result, SavePasswordIfRequested());
                    return;
                }
                catch (ArchiveError e) when (e.Kind is ArchiveErrorKind.WrongPassword or ArchiveErrorKind.PasswordRequired
                                             && attempt < BatchExtractService.MaxPasswordAttempts && !token.IsCancellationRequested)
                {
                    PasswordFailed();
                    if (!await AskPasswordAsync(e.Kind == ArchiveErrorKind.WrongPassword ? e.Message : null))
                    {
                        ShowResult(InfoBarSeverity.Informational, "Extraction cancelled", "No password was entered. Nothing was extracted.");
                        return;
                    }
                }
            }
        }
        catch (ArchiveError e) when (e.Kind == ArchiveErrorKind.Cancelled)
        {
            ShowResult(InfoBarSeverity.Informational, "Extraction cancelled", "Files from this archive were not kept. Existing files were not changed.");
        }
        catch (ArchiveError e)
        {
            if (e.Kind == ArchiveErrorKind.WrongPassword)
            {
                PasswordFailed();
                (_password, _savePassword) = (null, false);
            }
            ShowResult(InfoBarSeverity.Error, "Extraction failed", e.Message);
        }
        catch (Exception e)
        {
            ShowResult(InfoBarSeverity.Error, "Extraction failed", ArchiveError.From(e).Message);
        }
        finally
        {
            IsExtracting = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void ReportSuccess(ExtractResult result, string? note)
    {
        _lastDestination = result.Destination;
        var message = $"{BatchExtractService.Describe(result)} to {result.Destination}";
        if (note is not null)
            message += $"\n{note}";
        var details = new StringBuilder();
        foreach (var s in result.SkippedEntries.Take(50))
            details.AppendLine($"Not extracted: {s.EntryName} — {s.Reason}");
        foreach (var r in result.RenamedEntries.Take(50))
            details.AppendLine($"Renamed: {r.EntryName} → {r.WrittenAs.Replace(System.IO.Path.DirectorySeparatorChar, '/')} — {r.Reason}");
        var hidden = Math.Max(0, result.SkippedEntries.Count - 50) + Math.Max(0, result.RenamedEntries.Count - 50);
        if (hidden > 0)
            details.AppendLine($"…and {hidden} more.");
        ShowResult(result.HasWarnings ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
            result.HasWarnings ? "Extracted with warnings" : "Extraction complete", message, details.ToString().TrimEnd(), canOpen: true);
    }

    /// <summary>
    /// Asks for the password (the dialog offers saved ones). A password that already failed for this archive
    /// is not tried again: the user is asked for another. Returns false when the user cancels.
    /// </summary>
    private async Task<bool> AskPasswordAsync(string? reason)
    {
        while (true)
        {
            var answer = await Dialogs.PasswordAsync(FileName, reason);
            if (answer is null)
            {
                (_password, _savePassword) = (null, false);
                return false;
            }
            if (!_failedPasswords.Contains(answer.Password))
            {
                (_password, _savePassword) = (answer.Password, answer.Save);
                return true;
            }
            reason = BatchExtractService.AlreadyTriedMessage;
        }
    }

    private void PasswordFailed()
    {
        if (_password is not null)
            _failedPasswords.Add(_password);
    }

    /// <summary>Saves the password if the user asked to. Call only after it worked. Returns a note if saving failed.</summary>
    private string? SavePasswordIfRequested()
    {
        if (!_savePassword || _password is null || _contents is null)
            return null;
        _savePassword = false;
        return App.Settings.Passwords.Save(System.IO.Path.GetFileName(_contents.FilePath), _password)
            ? null
            : "The password worked, but it could not be saved.";
    }

    private static async Task<ExistingFilePolicy?> AskExistingFilePolicyAsync(string destination, IReadOnlyList<string> existing)
    {
        var list = string.Join("\n", existing.Take(5)) + (existing.Count > 5 ? $"\n…and {existing.Count - 5} more" : "");
        var options = new RadioButtons { Margin = new Thickness(0, 12, 0, 0) };
        options.Items.Add(new RadioButton { Content = "Keep both — save archive copies as “name (2)”", Tag = ExistingFilePolicy.KeepBoth });
        options.Items.Add(new RadioButton { Content = "Skip — keep the existing files", Tag = ExistingFilePolicy.Skip });
        options.Items.Add(new RadioButton { Content = "Replace the existing files", Tag = ExistingFilePolicy.Overwrite });
        options.SelectedIndex = 0;

        var panel = new StackPanel { Spacing = 8, MaxWidth = 460 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{Format.Count(existing.Count, "file", "files")} from this archive already exist in “{System.IO.Path.GetFileName(destination)}”:",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock { Text = list, Style = (Style)Application.Current.Resources["SecondaryTextStyle"], TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(options);

        var dialog = new ContentDialog
        {
            Title = "Some files already exist",
            Content = panel,
            PrimaryButtonText = "Extract",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary)
            return null;

        var policy = (ExistingFilePolicy)((RadioButton)options.SelectedItem).Tag;
        if (policy == ExistingFilePolicy.Overwrite &&
            !await Dialogs.ConfirmAsync("Replace existing files?",
                $"{Format.Count(existing.Count, "existing file", "existing files")} will be replaced by the archive's versions. This cannot be undone. Folders are never replaced.",
                "Replace", destructive: true))
            return null;
        return policy;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenDestination()
    {
        if (_lastDestination is not null)
            Shell.OpenFolder(_lastDestination);
    }

    private void ShowResult(InfoBarSeverity severity, string title, string message, string details = "", bool canOpen = false)
    {
        ResultSeverity = severity;
        ResultTitle = title;
        ResultMessage = message;
        ResultDetails = details;
        CanOpenResult = canOpen;
        IsResultOpen = true;
    }
}
