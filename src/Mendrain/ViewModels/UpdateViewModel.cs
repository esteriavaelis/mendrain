using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mendrain.Core;
using Mendrain.Helpers;
using Velopack;
using Velopack.Sources;

namespace Mendrain.ViewModels;

public enum UpdateStatus { NotChecked, Checking, UpToDate, Available, Downloading, ReadyToRestart, Failed, NotSupported }

/// <summary>
/// Checks GitHub Releases for a newer Mendrain with Velopack, downloads it when the user agrees and applies it
/// when the user restarts. Velopack verifies every downloaded package against the checksums in the release feed.
/// Only one check or download runs at a time; nothing is ever applied without the user's click.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    public const string RepositoryUrl = "https://github.com/esteriavaelis/mendrain";
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    private UpdateManager? _manager;
    private bool _managerIncludesPreviews;
    private UpdateInfo? _update;
    private CancellationTokenSource? _download;
    private bool _startupCheckStarted;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsIdle), nameof(CanDownload), nameof(IsDownloading), nameof(IsReady), nameof(BannerTitle))]
    public partial UpdateStatus Status { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; } = "Mendrain checks for updates shortly after it starts.";
    [ObservableProperty] public partial string NewVersion { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BannerMessage))] public partial string Notes { get; set; } = "";
    [ObservableProperty] public partial double DownloadPercent { get; set; }
    [ObservableProperty] public partial bool IsBannerOpen { get; set; }

    public bool IsBusy => Status is UpdateStatus.Checking or UpdateStatus.Downloading;
    public bool IsIdle => !IsBusy;
    public bool CanDownload => Status == UpdateStatus.Available;
    public bool IsDownloading => Status == UpdateStatus.Downloading;
    public bool IsReady => Status == UpdateStatus.ReadyToRestart;

    public string BannerTitle => Status switch
    {
        UpdateStatus.Downloading => $"Downloading Mendrain {NewVersion}",
        UpdateStatus.ReadyToRestart => $"Mendrain {NewVersion} is ready",
        _ => $"Mendrain {NewVersion} is available",
    };

    public string BannerMessage => Status == UpdateStatus.ReadyToRestart
        ? "Restart Mendrain to finish updating. Your settings and saved passwords are kept."
        : Notes;

    /// <summary>True for copies installed by Mendrain's Setup or unpacked from its portable ZIP (both can update).</summary>
    public bool CanUpdate => Manager is not null;

    /// <summary>The installed version as Velopack sees it, or the program version for other copies.</summary>
    public string CurrentVersion =>
        Manager?.CurrentVersion?.ToString() ?? typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>Checks once per session, a few seconds after start, in the background. Failures are only shown in Settings.</summary>
    public async Task CheckOnStartupAsync()
    {
        if (_startupCheckStarted)
            return;
        _startupCheckStarted = true;
        await Task.Delay(StartupDelay);
        await CheckAsync();
    }

    /// <summary>Background (startup) and manual checks alike: a failure is only reported in Settings, never as a dialog.</summary>
    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsBusy)
            return;
        if (Status == UpdateStatus.ReadyToRestart)
        {
            IsBannerOpen = true;
            return;
        }
        if (Manager is not { } manager)
        {
            Status = UpdateStatus.NotSupported;
            StatusText = "This copy of Mendrain can't update itself. Install it with Mendrain-Setup-x64.exe, or use the portable ZIP, from the Releases page.";
            return;
        }
        if (manager.UpdatePendingRestart is { } pending)
        {
            // Downloaded in an earlier session but not applied yet.
            NewVersion = pending.Version.ToString();
            Status = UpdateStatus.ReadyToRestart;
            StatusText = $"Mendrain {NewVersion} has been downloaded. Restart to finish updating.";
            IsBannerOpen = true;
            return;
        }

        Status = UpdateStatus.Checking;
        StatusText = "Checking for updates…";
        try
        {
            _update = await manager.CheckForUpdatesAsync();
            if (_update is null)
            {
                Status = UpdateStatus.UpToDate;
                StatusText = $"Mendrain {CurrentVersion} is up to date. Last checked {DateTime.Now:t}.";
                return;
            }
            NewVersion = _update.TargetFullRelease.Version.ToString();
            Notes = ReleaseNotes.Summarize(_update.TargetFullRelease.NotesMarkdown);
            Status = UpdateStatus.Available;
            StatusText = $"Mendrain {NewVersion} is available (you have {CurrentVersion}).";
            IsBannerOpen = true;
        }
        catch (Exception ex)
        {
            App.Log("Update check failed: " + ex.GetType().Name + ": " + ex.Message);
            Status = UpdateStatus.Failed;
            StatusText = Describe(ex, "Couldn't check for updates");
        }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (_update is null || Manager is not { } manager || IsBusy)
            return;
        var dispatcher = App.MainWindow.DispatcherQueue;
        _download = new CancellationTokenSource();
        DownloadPercent = 0;
        Status = UpdateStatus.Downloading;
        StatusText = $"Downloading Mendrain {NewVersion}…";
        try
        {
            await manager.DownloadUpdatesAsync(_update, p => dispatcher.TryEnqueue(() => DownloadPercent = p), _download.Token);
            Status = UpdateStatus.ReadyToRestart;
            StatusText = $"Mendrain {NewVersion} has been downloaded. Restart to finish updating.";
            IsBannerOpen = true;
        }
        catch (OperationCanceledException)
        {
            Status = UpdateStatus.Available;
            StatusText = "Download cancelled. Nothing was changed.";
        }
        catch (Exception ex)
        {
            App.Log("Update download failed: " + ex.GetType().Name + ": " + ex.Message);
            Status = UpdateStatus.Available;
            StatusText = Describe(ex, "The update could not be downloaded") + " Nothing was changed.";
            await Dialogs.MessageAsync("Update not downloaded", StatusText);
        }
        finally
        {
            _download.Dispose();
            _download = null;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _download?.Cancel();

    [RelayCommand]
    private void Later() => IsBannerOpen = false;

    [RelayCommand]
    private async Task RestartAsync()
    {
        if (Manager is not { } manager)
            return;
        if (Operations.AnyRunning)
        {
            await Dialogs.MessageAsync("Finish the current work first",
                "An archive operation is still running. Restart to update once it has finished.");
            return;
        }
        var asset = _update?.TargetFullRelease ?? manager.UpdatePendingRestart;
        if (asset is null)
            return;
        try
        {
            // Velopack's updater waits for Mendrain to exit, replaces the program files (never user data) and starts it again.
            manager.ApplyUpdatesAndRestart(asset);
        }
        catch (Exception ex)
        {
            App.Log("Applying the update failed: " + ex.GetType().Name + ": " + ex.Message);
            await Dialogs.MessageAsync("Update not installed", Describe(ex, "The update could not be installed") + " Mendrain was not changed.");
        }
    }

    /// <summary>Called when the preview-releases setting changes.</summary>
    public void ResetSource()
    {
        if (IsBusy)
            return;
        _manager = null;
        _update = null;
        if (Status is UpdateStatus.Available or UpdateStatus.UpToDate)
        {
            Status = UpdateStatus.NotChecked;
            StatusText = "";
            IsBannerOpen = false;
        }
    }

    /// <summary>Null when this copy wasn't installed by Velopack (e.g. a development build or an old portable ZIP).</summary>
    private UpdateManager? Manager
    {
        get
        {
            var previews = App.Settings.IncludePreviewUpdates;
            if (_manager is not null && _managerIncludesPreviews == previews)
                return _manager;
            try
            {
                var manager = CreateManager(previews);
                _manager = manager.IsInstalled ? manager : null;
                _managerIncludesPreviews = previews;
            }
            catch (Exception ex)
            {
                App.Log("Updater unavailable: " + ex.Message);
                _manager = null;
            }
            return _manager;
        }
    }

    private static UpdateManager CreateManager(bool previews)
    {
#if UPDATE_FEED_OVERRIDE
        // Test builds only: a local folder produced by "vpk pack", to try updates without GitHub.
        if (Environment.GetEnvironmentVariable("MENDRAIN_UPDATE_FEED") is { Length: > 0 } feed)
            return new UpdateManager(feed);
#endif
        // Public repository over HTTPS; no access token is stored in the app.
        return new UpdateManager(new GithubSource(RepositoryUrl, null, previews, null));
    }

    private static string Describe(Exception ex, string what)
    {
        var status = (ex as HttpRequestException)?.StatusCode ?? (ex.InnerException as HttpRequestException)?.StatusCode;
        if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            return $"{what}: GitHub is limiting requests right now. Try again later.";
        if (ex is HttpRequestException or TaskCanceledException or TimeoutException or IOException
            || ex.InnerException is HttpRequestException or IOException)
            return $"{what}. Check your internet connection and try again.";
        return $"{what}. Try again later.";
    }
}
