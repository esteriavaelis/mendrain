using Mendrain.Core.Models;
using Mendrain.Core.Services;

namespace Mendrain.Core.Tests;

public class BatchExtractTests
{
    private static List<ArchiveJob> Jobs(params string[] fixtures) => fixtures.Select(f => new ArchiveJob(Fixtures.Get(f))).ToList();

    [Fact]
    public void Each_archive_gets_its_own_folder_name()
    {
        var jobs = Jobs("sample.zip", "solid.7z", "encrypted.rar", "volume.part1.rar");
        Assert.Equal(["sample", "solid", "encrypted", "volume"], jobs.Select(j => j.DestinationFolderName));
    }

    [Fact]
    public void Detects_duplicate_names_in_queue_and_existing_folders()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("solid"));
        var jobs = Jobs("sample.zip", "sample.7z", "SAMPLE.rar".ToLowerInvariant(), "solid.7z", "solid.rar");

        var conflicts = BatchExtractService.FindConflicts(jobs, temp.Path);

        Assert.Collection(conflicts,
            c => { Assert.Same(jobs[1], c.Job); Assert.Equal(ConflictKind.DuplicateInQueue, c.Kind); Assert.Same(jobs[0], c.OtherJob); },
            c => { Assert.Same(jobs[2], c.Job); Assert.Equal(ConflictKind.DuplicateInQueue, c.Kind); },
            c => { Assert.Same(jobs[3], c.Job); Assert.Equal(ConflictKind.AlreadyExists, c.Kind); },
            c => { Assert.Same(jobs[4], c.Job); Assert.Equal(ConflictKind.DuplicateInQueue, c.Kind); });
    }

    [Fact]
    public void Resolve_with_new_names_never_reuses_a_folder()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("sample (2)"));
        var jobs = Jobs("sample.zip", "sample.7z", "sample.rar");

        BatchExtractService.ResolveConflicts(jobs, temp.Path, ConflictResolution.UseNewFolderName);

        Assert.Equal(["sample", "sample (3)", "sample (4)"], jobs.Select(j => j.DestinationFolderName));
        Assert.Empty(BatchExtractService.FindConflicts(jobs, temp.Path));
        Assert.All(jobs, j => Assert.Equal(JobStatus.Ready, j.Status));
    }

    [Fact]
    public void Resolve_with_skip_marks_conflicting_jobs_skipped()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("solid"));
        var jobs = Jobs("sample.zip", "sample.7z", "solid.7z");

        BatchExtractService.ResolveConflicts(jobs, temp.Path, ConflictResolution.Skip);

        Assert.Equal([JobStatus.Ready, JobStatus.Skipped, JobStatus.Skipped], jobs.Select(j => j.Status));
        Assert.Contains("queue", jobs[1].Message);
        Assert.Contains("already exists", jobs[2].Message);
    }

    [Fact]
    public async Task Run_refuses_unresolved_conflicts_before_extracting_anything()
    {
        using var temp = new TempDir();
        var calls = 0;
        var service = new BatchExtractService((_, _, _) => { calls++; throw new InvalidOperationException(); });
        var error = await Assert.ThrowsAsync<ArchiveError>(() =>
            service.RunAsync(Jobs("sample.zip", "sample.7z"), temp.Path, null, CancellationToken.None));
        Assert.Equal(ArchiveErrorKind.DestinationExists, error.Kind);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Real_batch_extracts_sequentially_and_isolates_failures()
    {
        using var temp = new TempDir();
        var jobs = Jobs("sample.zip", "corrupt.zip", "solid.7z", "not-an-archive.zip", "volume.part1.rar", "sample.rar");
        BatchExtractService.ResolveConflicts(jobs, temp.Path, ConflictResolution.UseNewFolderName);

        var summary = await new BatchExtractService().RunAsync(jobs, temp.Path, null, CancellationToken.None);

        Assert.Equal([JobStatus.Completed, JobStatus.Failed, JobStatus.Completed, JobStatus.Failed, JobStatus.Completed, JobStatus.Completed],
            jobs.Select(j => j.Status));
        Fixtures.AssertSampleContent(temp.Combine("sample"));
        Fixtures.AssertSampleContent(temp.Combine("solid"));
        Fixtures.AssertSampleContent(temp.Combine("volume"));
        Fixtures.AssertSampleContent(temp.Combine("sample (2)"));
        Assert.False(Directory.Exists(temp.Combine("corrupt")));
        Assert.False(Directory.Exists(temp.Combine("not-an-archive")));
        Assert.Contains("damaged", jobs[1].Message);
        Assert.DoesNotContain(temp.Snapshot(), p => p.Contains(".mendrain-"));

        Assert.Equal(6, summary.Total);
        Assert.Equal(4, summary.Completed);
        Assert.Equal(2, summary.Failed);
        Assert.Equal([jobs[1], jobs[3]], summary.NeedsAttention);
        Assert.False(summary.AllSucceeded);
    }

    [Fact]
    public async Task Jobs_run_one_at_a_time_in_queue_order()
    {
        using var temp = new TempDir();
        var order = new List<string>();
        var running = 0;
        var maxRunning = 0;
        var service = new BatchExtractService(async (request, _, _) =>
        {
            maxRunning = Math.Max(maxRunning, Interlocked.Increment(ref running));
            order.Add(Path.GetFileName(request.DestinationDirectory));
            await Task.Delay(30);
            Interlocked.Decrement(ref running);
            return new ExtractResult(request.DestinationDirectory, 1, 0, 1, 0, 0, [], []);
        });
        var jobs = Enumerable.Range(1, 6).Select(i => new ArchiveJob(temp.Combine($"a{i}.zip"))).ToList();

        await service.RunAsync(jobs, temp.Path, null, CancellationToken.None);

        Assert.Equal(1, maxRunning);
        Assert.Equal(["a1", "a2", "a3", "a4", "a5", "a6"], order);
        Assert.All(jobs, j => Assert.Equal(JobStatus.Completed, j.Status));
    }

    [Fact]
    public async Task Cancel_stops_current_job_and_does_not_start_others()
    {
        using var temp = new TempDir();
        using var cts = new CancellationTokenSource();
        var started = new List<string>();
        var service = new BatchExtractService(async (request, _, token) =>
        {
            var name = Path.GetFileName(request.DestinationDirectory);
            started.Add(name);
            if (name == "a2")
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, token).ContinueWith(_ => { });
                throw new ArchiveError(ArchiveErrorKind.Cancelled, "cancelled");
            }
            return new ExtractResult(request.DestinationDirectory, 1, 0, 1, 0, 0, [], []);
        });
        var jobs = Enumerable.Range(1, 4).Select(i => new ArchiveJob(temp.Combine($"a{i}.zip"))).ToList();

        var summary = await service.RunAsync(jobs, temp.Path, null, cts.Token);

        Assert.Equal(["a1", "a2"], started);
        Assert.Equal([JobStatus.Completed, JobStatus.Cancelled, JobStatus.Cancelled, JobStatus.Cancelled], jobs.Select(j => j.Status));
        Assert.Contains("Not started", jobs[3].Message);
        Assert.Equal(3, summary.Cancelled);
        Assert.Equal(1, summary.Completed);
    }

    [Fact]
    public async Task Real_cancellation_during_extraction_leaves_no_partial_folder()
    {
        using var temp = new TempDir();
        var src = temp.Combine("src");
        Directory.CreateDirectory(src);
        var big = Path.Combine(src, "big.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(big, System.IO.Compression.ZipArchiveMode.Create))
        using (var s = zip.CreateEntry("x.bin", System.IO.Compression.CompressionLevel.NoCompression).Open())
            for (var i = 0; i < 64; i++) s.Write(new byte[1 << 20]);
        var dest = temp.Combine("dest");
        Directory.CreateDirectory(dest);

        using var cts = new CancellationTokenSource();
        var service = new ArchiveService();
        var batch = new BatchExtractService((req, _, token) =>
            service.ExtractAsync(req, new SyncProgress<ExtractProgress>(p => { if (p.BytesDone > 0) cts.Cancel(); }), token));
        var jobs = new List<ArchiveJob> { new(big), new(Fixtures.Get("sample.zip")) };

        await batch.RunAsync(jobs, dest, null, cts.Token);

        Assert.Equal([JobStatus.Cancelled, JobStatus.Cancelled], jobs.Select(j => j.Status));
        Assert.Empty(Directory.GetFileSystemEntries(dest));
    }

    [Fact]
    public async Task Password_prompt_retries_after_wrong_password()
    {
        using var temp = new TempDir();
        var requests = new List<PasswordRequest>();
        var answers = new Queue<string?>(["wrong", Fixtures.Password]);
        var jobs = Jobs("encrypted.rar", "sample.zip");

        await new BatchExtractService().RunAsync(jobs, temp.Path, r => { requests.Add(r); return Task.FromResult(answers.Dequeue()); },
            CancellationToken.None);

        Assert.Equal([JobStatus.Completed, JobStatus.Completed], jobs.Select(j => j.Status));
        Assert.Equal([1, 2], requests.Select(r => r.Attempt));
        Assert.Contains("incorrect", requests[1].Reason);
        Fixtures.AssertSampleContent(temp.Combine("encrypted"));
    }

    [Fact]
    public async Task No_password_skips_archive_and_continues()
    {
        using var temp = new TempDir();
        var jobs = Jobs("encrypted.7z", "sample.zip");
        var summary = await new BatchExtractService().RunAsync(jobs, temp.Path, _ => Task.FromResult<string?>(null), CancellationToken.None);
        Assert.Equal([JobStatus.Skipped, JobStatus.Completed], jobs.Select(j => j.Status));
        Assert.Contains("No password", jobs[0].Message);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(["sample"], Directory.GetDirectories(temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Repeated_wrong_passwords_fail_after_limit()
    {
        using var temp = new TempDir();
        var prompts = 0;
        var jobs = Jobs("encrypted-zipcrypto.zip");
        await new BatchExtractService().RunAsync(jobs, temp.Path, _ => { prompts++; return Task.FromResult<string?>("nope"); }, CancellationToken.None);
        Assert.Equal(JobStatus.Failed, jobs[0].Status);
        Assert.Equal(BatchExtractService.MaxPasswordAttempts, prompts);
        Assert.Contains("password", jobs[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Folder_created_after_planning_is_skipped_not_merged()
    {
        using var temp = new TempDir();
        var jobs = Jobs("sample.zip");
        var service = new ArchiveService();
        var batch = new BatchExtractService((req, p, t) =>
        {
            Directory.CreateDirectory(req.DestinationDirectory);   // something else creates it meanwhile
            File.WriteAllText(Path.Combine(req.DestinationDirectory, "top.txt"), "theirs");
            return service.ExtractAsync(req, p, t);
        });

        await batch.RunAsync(jobs, temp.Path, null, CancellationToken.None);

        Assert.Equal(JobStatus.Skipped, jobs[0].Status);
        Assert.Equal("theirs", File.ReadAllText(temp.Combine("sample", "top.txt")));
    }

    [Fact]
    public void Summary_counts_warnings()
    {
        var ok = new ArchiveJob(@"C:\a.zip") { Status = JobStatus.Completed, Result = new ExtractResult("", 1, 0, 1, 0, 0, [], []) };
        var warn = new ArchiveJob(@"C:\b.zip")
        {
            Status = JobStatus.Completed,
            Result = new ExtractResult("", 1, 0, 1, 0, 0, [new SkippedEntry("../x", "Blocked")], []),
        };
        var failed = new ArchiveJob(@"C:\c.zip") { Status = JobStatus.Failed };
        var summary = BatchExtractService.Summarize([ok, warn, failed], TimeSpan.Zero);
        Assert.Equal(2, summary.Completed);
        Assert.Equal(1, summary.CompletedWithWarnings);
        Assert.Equal([warn, failed], summary.NeedsAttention);
    }
}
