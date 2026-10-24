using System.Diagnostics;
using Mendrain.Core.Models;

namespace Mendrain.Core.Services;

public enum ConflictKind
{
    /// <summary>An earlier job in the queue already uses this folder name.</summary>
    DuplicateInQueue,
    /// <summary>A file or folder with this name already exists in the destination.</summary>
    AlreadyExists,
}

public sealed record DestinationConflict(ArchiveJob Job, ConflictKind Kind, string FolderName, ArchiveJob? OtherJob);

public enum ConflictResolution
{
    /// <summary>Give each conflicting archive its own new folder, e.g. "Project (2)".</summary>
    UseNewFolderName,
    /// <summary>Do not extract the conflicting archives.</summary>
    Skip,
}

/// <param name="Attempt">1 for the first prompt for this archive.</param>
/// <param name="Reason">Why the previous password did not work, e.g. "The password is incorrect."; null when none was tried yet.</param>
public sealed record PasswordRequest(ArchiveJob Job, int Attempt, string? Reason);

/// <summary>A password the user entered or picked.</summary>
/// <param name="Save">Save the password once it has been shown to work.</param>
public sealed record PasswordAnswer(string Password, bool Save = false)
{
    /// <summary>Never reveals the password (e.g. in logs or the debugger).</summary>
    public override string ToString() => $"PasswordAnswer {{ Save = {Save} }}";
}

public sealed record BatchSummary(
    int Total, int Completed, int Skipped, int Failed, int Cancelled, int CompletedWithWarnings,
    TimeSpan Duration, IReadOnlyList<ArchiveJob> NeedsAttention)
{
    public bool AllSucceeded => Completed == Total && CompletedWithWarnings == 0;
}

/// <summary>
/// Extracts a queue of archives one at a time, each into its own folder under a destination root.
/// A failure is recorded on its job and the queue moves on; cancellation stops the current archive,
/// removes only what that archive's extraction staged, and marks the remaining jobs as cancelled.
/// </summary>
public sealed class BatchExtractService
{
    public const int MaxPasswordAttempts = 3;
    public const string AlreadyTriedMessage = "That password was already tried for this archive and did not work.";
    public const string SavedPasswordFailedMessage = "The password saved for this archive did not work.";

    private readonly Func<ExtractRequest, IProgress<ExtractProgress>?, CancellationToken, Task<ExtractResult>> _extract;
    private readonly SavedPasswordStore? _savedPasswords;

    /// <param name="extract">Replaces the extraction step (used by tests); defaults to <see cref="ArchiveService.ExtractAsync"/>.</param>
    /// <param name="savedPasswords">
    /// Passwords saved for an archive with the same file name are tried before asking, and passwords the user
    /// asks to save are saved here once the archive extracted successfully.
    /// </param>
    public BatchExtractService(Func<ExtractRequest, IProgress<ExtractProgress>?, CancellationToken, Task<ExtractResult>>? extract = null,
        SavedPasswordStore? savedPasswords = null)
    {
        var service = new ArchiveService();
        _extract = extract ?? service.ExtractAsync;
        _savedPasswords = savedPasswords;
    }

    /// <summary>Raised when a job starts (with the job) and when the batch ends (with null).</summary>
    public event Action<ArchiveJob?>? CurrentJobChanged;

    /// <summary>Finds jobs whose folder name is used twice in the queue or already exists under <paramref name="root"/>.</summary>
    public static IReadOnlyList<DestinationConflict> FindConflicts(IEnumerable<ArchiveJob> jobs, string root)
    {
        var conflicts = new List<DestinationConflict>();
        var used = new Dictionary<string, ArchiveJob>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs.Where(j => j.Status == JobStatus.Ready))
        {
            var name = job.DestinationFolderName;
            if (used.TryGetValue(name, out var first))
                conflicts.Add(new(job, ConflictKind.DuplicateInQueue, name, first));
            else if (Exists(root, name))
                conflicts.Add(new(job, ConflictKind.AlreadyExists, name, null));
            used.TryAdd(name, job);
        }
        return conflicts;
    }

    /// <summary>
    /// Applies <paramref name="resolution"/> to every conflicting Ready job. The first job in the queue
    /// keeps a contested name unless that folder already exists on disk.
    /// </summary>
    public static void ResolveConflicts(IEnumerable<ArchiveJob> jobs, string root, ConflictResolution resolution)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs.Where(j => j.Status == JobStatus.Ready))
        {
            var name = job.DestinationFolderName;
            var inQueue = taken.Contains(name);
            if (!inQueue && !Exists(root, name))
            {
                taken.Add(name);
                continue;
            }

            if (resolution == ConflictResolution.Skip)
            {
                job.Status = JobStatus.Skipped;
                job.Message = inQueue
                    ? $"Another archive in the queue also extracts to \"{name}\"."
                    : $"The folder \"{name}\" already exists.";
                continue;
            }

            job.DestinationFolderName = ArchiveNaming.MakeUnique(name, n => taken.Contains(n) || Exists(root, n));
            taken.Add(job.DestinationFolderName);
        }
    }

    private static bool Exists(string root, string name)
    {
        var path = Path.Combine(root, name);
        return Directory.Exists(path) || File.Exists(path);
    }

    /// <summary>
    /// Runs every Ready job in order. Call <see cref="FindConflicts"/> / <see cref="ResolveConflicts"/> first;
    /// unresolved conflicts make this method throw before anything is extracted.
    /// </summary>
    /// <param name="passwordProvider">Asked for a password when an archive needs one; returning null skips that archive.</param>
    public async Task<BatchSummary> RunAsync(
        IReadOnlyList<ArchiveJob> jobs,
        string destinationRoot,
        Func<PasswordRequest, Task<PasswordAnswer?>>? passwordProvider,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(destinationRoot))
            throw new ArchiveError(ArchiveErrorKind.NotFound, $"The destination folder \"{destinationRoot}\" does not exist.");
        if (FindConflicts(jobs, destinationRoot).Count > 0)
            throw new ArchiveError(ArchiveErrorKind.DestinationExists, "Some archives would extract into the same folder. Resolve the conflicts first.");

        var clock = Stopwatch.StartNew();
        var queue = jobs.ToList();
        try
        {
            for (var index = 0; index < queue.Count; index++)
            {
                var job = queue[index];
                if (job.Status != JobStatus.Ready)
                    continue;
                if (cancellationToken.IsCancellationRequested)
                {
                    CancelRemaining(queue, index);
                    break;
                }

                await RunJobAsync(job, destinationRoot, passwordProvider, cancellationToken);

                if (job.Status == JobStatus.Cancelled || cancellationToken.IsCancellationRequested)
                {
                    CancelRemaining(queue, index + 1);
                    break;
                }
            }
        }
        finally
        {
            CurrentJobChanged?.Invoke(null);
        }

        return Summarize(queue, clock.Elapsed);
    }

    private async Task RunJobAsync(ArchiveJob job, string root, Func<PasswordRequest, Task<PasswordAnswer?>>? passwordProvider,
        CancellationToken cancellationToken)
    {
        job.Status = JobStatus.Extracting;
        job.Message = null;
        job.Progress = null;
        CurrentJobChanged?.Invoke(job);

        string? password = null;
        var save = false;
        var fromSaved = false;
        var prompts = 0;
        // Passwords that failed for this archive are never tried (or asked for) again in this run.
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var saved = new Queue<SavedPassword>(_savedPasswords?.FindFor(job.SourcePath) ?? []);
        while (true)
        {
            try
            {
                var progress = new Progress<ExtractProgress>(p =>
                {
                    if (job.Status == JobStatus.Extracting)
                        job.Progress = p.Fraction;
                });
                var request = new ExtractRequest(job.SourcePath, Path.Combine(root, job.DestinationFolderName), password, DestinationMode.CreateNew);
                var result = await _extract(request, progress, cancellationToken);
                job.Result = result;
                job.Progress = 1;
                job.Status = JobStatus.Completed;
                job.Message = Describe(result);
                // Saved only now: the archive extracted with it, so the password is known to be right.
                if (save && password is not null && _savedPasswords?.Save(job.FileName, password) == false)
                    job.Message += " · The password could not be saved";
                return;
            }
            catch (ArchiveError error) when (error.Kind is ArchiveErrorKind.PasswordRequired or ArchiveErrorKind.WrongPassword
                                             && !cancellationToken.IsCancellationRequested)
            {
                if (password is not null)
                    failed.Add(password);
                string? reason = error.Kind != ArchiveErrorKind.WrongPassword ? null
                    : fromSaved ? SavedPasswordFailedMessage
                    : error.Message;

                // Saved passwords for an archive of this name go first, without asking; the extraction itself validates them.
                if (NextSavedPassword(saved, failed) is { } next)
                {
                    (password, save, fromSaved) = (next, false, true);
                    continue;
                }

                PasswordAnswer? answer = null;
                while (answer is null)
                {
                    if (passwordProvider is null || prompts >= MaxPasswordAttempts)
                    {
                        Fail(job, reason ?? error.Message);
                        return;
                    }
                    prompts++;
                    answer = await passwordProvider(new PasswordRequest(job, prompts, reason));
                    if (cancellationToken.IsCancellationRequested)
                    {
                        MarkCancelled(job);
                        return;
                    }
                    if (string.IsNullOrEmpty(answer?.Password))
                    {
                        job.Status = JobStatus.Skipped;
                        job.Message = "No password was entered.";
                        return;
                    }
                    if (failed.Contains(answer.Password))
                    {
                        answer = null;
                        reason = AlreadyTriedMessage;
                    }
                }
                (password, save, fromSaved) = (answer.Password, answer.Save, false);
            }
            catch (Exception ex)
            {
                var error = ArchiveError.From(ex);
                job.Progress = null;
                switch (error.Kind)
                {
                    case ArchiveErrorKind.Cancelled:
                        MarkCancelled(job);
                        break;
                    case ArchiveErrorKind.DestinationExists:
                        job.Status = JobStatus.Skipped;
                        job.Message = error.Message;
                        break;
                    default:
                        job.Status = JobStatus.Failed;
                        job.Message = error.Message;
                        break;
                }
                return;
            }
        }
    }

    private string? NextSavedPassword(Queue<SavedPassword> saved, HashSet<string> failed)
    {
        while (saved.TryDequeue(out var entry))
        {
            if (_savedPasswords!.Reveal(entry.Id) is { } candidate && !failed.Contains(candidate))
                return candidate;
        }
        return null;
    }

    private static void Fail(ArchiveJob job, string message)
    {
        job.Progress = null;
        job.Status = JobStatus.Failed;
        job.Message = message;
    }

    private static void MarkCancelled(ArchiveJob job)
    {
        job.Progress = null;
        job.Status = JobStatus.Cancelled;
        job.Message = "Cancelled. Files from this archive were not kept.";
    }

    private static void CancelRemaining(List<ArchiveJob> queue, int from)
    {
        for (var i = from; i < queue.Count; i++)
        {
            if (queue[i].Status != JobStatus.Ready)
                continue;
            queue[i].Status = JobStatus.Cancelled;
            queue[i].Message = "Not started: the batch was cancelled.";
        }
    }

    public static string Describe(ExtractResult result)
    {
        var parts = new List<string> { $"{result.FilesWritten:N0} file{(result.FilesWritten == 1 ? "" : "s")} extracted" };
        if (result.SkippedEntries.Count > 0)
            parts.Add($"{result.SkippedEntries.Count} entr{(result.SkippedEntries.Count == 1 ? "y" : "ies")} blocked or skipped");
        if (result.RenamedEntries.Count > 0)
            parts.Add($"{result.RenamedEntries.Count} renamed");
        if (result.ExistingFilesSkipped > 0)
            parts.Add($"{result.ExistingFilesSkipped} existing kept");
        if (result.ExistingFilesOverwritten > 0)
            parts.Add($"{result.ExistingFilesOverwritten} replaced");
        return string.Join(" · ", parts);
    }

    public static BatchSummary Summarize(IReadOnlyList<ArchiveJob> jobs, TimeSpan duration)
    {
        int Count(JobStatus s) => jobs.Count(j => j.Status == s);
        var warnings = jobs.Count(j => j.Status == JobStatus.Completed && j.Result?.HasWarnings == true);
        var attention = jobs.Where(j => j.Status is JobStatus.Failed or JobStatus.Skipped or JobStatus.Cancelled
                                        || (j.Status == JobStatus.Completed && j.Result?.HasWarnings == true)).ToList();
        return new BatchSummary(jobs.Count, Count(JobStatus.Completed), Count(JobStatus.Skipped), Count(JobStatus.Failed),
            Count(JobStatus.Cancelled), warnings, duration, attention);
    }
}
