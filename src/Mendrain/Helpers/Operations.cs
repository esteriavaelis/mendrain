namespace Mendrain.Helpers;

/// <summary>
/// Keeps track of running archive operations so closing the window can cancel them and wait for
/// their cleanup (removal of staging folders and partial files) instead of abandoning it.
/// </summary>
public static class Operations
{
    private static readonly List<(CancellationTokenSource Cts, Task Task)> Running = [];

    public static bool AnyRunning
    {
        get { lock (Running) return Running.Any(r => !r.Task.IsCompleted); }
    }

    /// <summary>Registers <paramref name="task"/>; it is forgotten automatically when it finishes.</summary>
    public static void Track(CancellationTokenSource cts, Task task)
    {
        lock (Running) Running.Add((cts, task));
        task.ContinueWith(_ => { lock (Running) Running.RemoveAll(r => r.Task == task); }, TaskScheduler.Default);
    }

    /// <summary>Cancels everything and waits (bounded) for the operations to clean up.</summary>
    public static async Task CancelAllAsync(TimeSpan timeout)
    {
        List<(CancellationTokenSource Cts, Task Task)> snapshot;
        lock (Running) snapshot = [.. Running];
        foreach (var (cts, _) in snapshot)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        var all = Task.WhenAll(snapshot.Select(s => s.Task.ContinueWith(_ => { }, TaskScheduler.Default)));
        await Task.WhenAny(all, Task.Delay(timeout));
    }
}
