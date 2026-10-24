using System.Collections.Concurrent;
using Mendrain.Core;
using Mendrain.Views;
using Microsoft.UI.Dispatching;

namespace Mendrain.Helpers;

/// <summary>
/// Runs requests from the Explorer context menu. They arrive from this process's own command line and,
/// through <see cref="InstanceChannel"/>, from every later Mendrain process Explorer starts. Explorer starts one
/// process per selected file, so requests are collected briefly and merged per action, then run one at a
/// time, each after any archive operation that is already running.
/// </summary>
public static class ShellRequests
{
    private static readonly TimeSpan CollectDelay = TimeSpan.FromMilliseconds(700);
    private static readonly ConcurrentQueue<ShellCommand> Incoming = new();
    private static readonly Queue<ShellCommand> Work = new();
    private static DispatcherQueue? _dispatcher;
    private static DispatcherQueueTimer? _timer;
    private static bool _processing;

    /// <summary>Thread-safe; may be called before the window exists.</summary>
    public static void Add(ShellCommand command)
    {
        Incoming.Enqueue(command);
        _dispatcher?.TryEnqueue(Collect);
    }

    /// <summary>Starts handling requests. Call once, on the UI thread, after the main window was created.</summary>
    public static void Start(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = CollectDelay;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => Flush();
        Collect();
    }

    private static void Collect()
    {
        if (Incoming.IsEmpty || _timer is null)
            return;
        // Restart the wait with every arrival: the last process of a large selection may start late.
        _timer.Stop();
        _timer.Start();
    }

    private static void Flush()
    {
        var arrived = new List<ShellCommand>();
        while (Incoming.TryDequeue(out var command))
            arrived.Add(command);
        foreach (var group in arrived.GroupBy(c => c.Action))
            Work.Enqueue(new ShellCommand(group.Key, group.SelectMany(c => c.Paths).ToList()));
        if (!_processing)
            _ = ProcessAsync();
    }

    private static async Task ProcessAsync()
    {
        _processing = true;
        try
        {
            while (Work.TryDequeue(out var command))
            {
                App.MainWindow.BringToFront();
                // Never interrupt or interleave with an extraction or ZIP creation that is running.
                while (Operations.AnyRunning)
                    await Task.Delay(500);
                try
                {
                    await RunAsync(command);
                }
                catch (Exception ex)
                {
                    App.Log("Explorer request failed: " + ex);
                    await Dialogs.MessageAsync("Something went wrong", ex.Message);
                }
            }
        }
        finally
        {
            _processing = false;
        }
    }

    private static async Task RunAsync(ShellCommand command)
    {
        var selection = ArchiveSelection.From(command.Paths);
        if (selection.Archives.Count == 0)
        {
            await Dialogs.MessageAsync("Nothing to open", selection.Note ?? "No ZIP, 7Z or RAR archive was selected.");
            return;
        }

        switch (command.Action)
        {
            case ShellAction.Open:
                App.MainWindow.HandleIncomingPaths(selection.Archives);
                break;
            case ShellAction.ExtractHere:
            case ShellAction.ExtractToFolder:
                // One archive after another, each through the viewer's normal extraction (conflict and password dialogs included).
                foreach (var archive in selection.Archives)
                {
                    var request = new ArchiveExtractRequest(archive, command.Action == ShellAction.ExtractHere);
                    App.MainWindow.Navigate(typeof(ArchivePage), request);
                    await request.Done.Task;
                }
                break;
            case ShellAction.Batch:
                await App.Batch.ExtractFromExplorerAsync(selection.Archives);
                break;
        }

        if (selection.Note is { } note)
            await Dialogs.MessageAsync("Some items were left out", note);
    }
}

/// <summary>Navigation parameter for <see cref="ArchivePage"/>: open the archive and extract it right away.</summary>
/// <param name="Here">Extract into the archive's own folder instead of a new folder named after the archive.</param>
public sealed record ArchiveExtractRequest(string Path, bool Here)
{
    public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
