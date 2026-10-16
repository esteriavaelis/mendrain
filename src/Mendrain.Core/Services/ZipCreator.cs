using System.Diagnostics;
using System.IO.Compression;
using Mendrain.Core.Models;

namespace Mendrain.Core.Services;

public sealed record ZipRequest(
    IReadOnlyList<string> Sources,
    string OutputPath,
    CompressionLevel Level = CompressionLevel.Optimal,
    bool OverwriteExisting = false);

public sealed record ZipResult(string OutputPath, int FilesAdded, int FoldersAdded, long SourceBytes, long ArchiveBytes,
    IReadOnlyList<SkippedEntry> Skipped);

/// <summary>A file or empty folder that will go into the ZIP.</summary>
public sealed record ZipItem(string? SourcePath, string EntryName, long Size, DateTime LastWriteTime)
{
    public bool IsDirectory => SourcePath is null;
}

public sealed record ZipPlan(IReadOnlyList<ZipItem> Items, IReadOnlyList<SkippedEntry> Skipped)
{
    public long TotalBytes => Items.Sum(i => i.Size);
}

/// <summary>
/// Creates ZIP archives. Selection rules: a selected file is stored at the archive root under its
/// own name; a selected folder is stored as "FolderName/..." with its full structure, including
/// empty sub-folders. Links inside folders are not followed.
/// </summary>
public sealed class ZipCreator
{
    private const int BufferSize = 1 << 16;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(80);

    public ZipPlan Plan(IReadOnlyList<string> sources, string outputPath)
    {
        if (sources.Count == 0)
            throw new ArchiveError(ArchiveErrorKind.InvalidInput, "Add at least one file or folder.");

        var output = Path.GetFullPath(outputPath);
        var items = new List<ZipItem>();
        var skipped = new List<SkippedEntry>();
        var rootNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in sources)
        {
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            var name = Path.GetFileName(source);
            if (string.IsNullOrEmpty(name))
                throw new ArchiveError(ArchiveErrorKind.InvalidInput, $"\"{raw}\" is a drive. Add the folders inside it instead.");
            if (rootNames.TryGetValue(name, out var other))
                throw new ArchiveError(ArchiveErrorKind.InvalidInput,
                    $"Two selected items are both named \"{name}\":\n{other}\n{source}\nRemove or rename one of them.");
            rootNames[name] = source;

            if (File.Exists(source))
            {
                if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
                    throw new ArchiveError(ArchiveErrorKind.InvalidInput, "The ZIP file cannot contain itself.");
                var info = new FileInfo(source);
                items.Add(new ZipItem(source, name, info.Length, info.LastWriteTime));
            }
            else if (Directory.Exists(source))
            {
                // The output may live inside a selected folder; AddDirectory skips the output file itself.
                AddDirectory(new DirectoryInfo(source), name, output, items, skipped);
            }
            else
            {
                throw new ArchiveError(ArchiveErrorKind.NotFound, $"\"{source}\" no longer exists.");
            }
        }

        return new ZipPlan(items, skipped);
    }

    private static void AddDirectory(DirectoryInfo root, string rootName, string output, List<ZipItem> items, List<SkippedEntry> skipped)
    {
        var pending = new Stack<(DirectoryInfo Dir, string EntryPrefix)>();
        pending.Push((root, rootName));
        while (pending.Count > 0)
        {
            var (dir, prefix) = pending.Pop();
            FileSystemInfo[] children;
            try
            {
                children = dir.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw ArchiveError.From(ex, context: dir.FullName);
            }

            if (children.Length == 0)
            {
                items.Add(new ZipItem(null, prefix + "/", 0, dir.LastWriteTime));
                continue;
            }

            foreach (var child in children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                var entryName = prefix + "/" + child.Name;
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    skipped.Add(new SkippedEntry(child.FullName, "Links and junctions are not followed."));
                    continue;
                }
                if (child is DirectoryInfo sub)
                {
                    pending.Push((sub, entryName));
                }
                else if (child is FileInfo file)
                {
                    if (string.Equals(file.FullName, output, StringComparison.OrdinalIgnoreCase))
                        continue;
                    items.Add(new ZipItem(file.FullName, entryName, file.Length, file.LastWriteTime));
                }
            }
        }
    }

    public Task<ZipResult> CreateAsync(ZipRequest request, IProgress<ExtractProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Create(request, progress, cancellationToken), CancellationToken.None);

    /// <summary>
    /// Writes the ZIP to a hidden temporary file in the output folder and renames it to the final name
    /// only after everything was written. A failed or cancelled run removes only that temporary file.
    /// </summary>
    public ZipResult Create(ZipRequest request, IProgress<ExtractProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var output = Path.GetFullPath(request.OutputPath);
        var folder = Path.GetDirectoryName(output)
            ?? throw new ArchiveError(ArchiveErrorKind.InvalidInput, "Choose a valid location for the ZIP file.");
        if (!Directory.Exists(folder))
            throw new ArchiveError(ArchiveErrorKind.NotFound, $"The folder \"{folder}\" does not exist.");
        if (Directory.Exists(output))
            throw new ArchiveError(ArchiveErrorKind.DestinationExists, $"A folder named \"{Path.GetFileName(output)}\" already exists there.");
        if (File.Exists(output) && !request.OverwriteExisting)
            throw new ArchiveError(ArchiveErrorKind.DestinationExists, $"\"{Path.GetFileName(output)}\" already exists.");

        var plan = Plan(request.Sources, output);
        var temp = Path.Combine(folder, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.partial");
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.FromDays(-1);
        long done = 0;
        var filesDone = 0;
        var filesTotal = plan.Items.Count(i => !i.IsDirectory);
        string? current = null;

        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferSize))
            {
                try { File.SetAttributes(temp, File.GetAttributes(temp) | FileAttributes.Hidden); } catch (IOException) { }
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
                var buffer = new byte[BufferSize];
                foreach (var item in plan.Items)
                {
                    cancellationToken.ThrowIfCancellationRequestedAsArchiveError();
                    current = item.SourcePath ?? item.EntryName;
                    var entry = zip.CreateEntry(item.EntryName, item.IsDirectory ? CompressionLevel.NoCompression : request.Level);
                    entry.LastWriteTime = ClampZipTime(item.LastWriteTime);
                    if (item.IsDirectory)
                        continue;

                    using var input = new FileStream(item.SourcePath!, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
                    using var entryStream = entry.Open();
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequestedAsArchiveError();
                        entryStream.Write(buffer, 0, read);
                        done += read;
                        if (clock.Elapsed - lastReport >= ProgressInterval)
                        {
                            lastReport = clock.Elapsed;
                            progress?.Report(new ExtractProgress(item.EntryName, done, plan.TotalBytes, filesDone, filesTotal));
                        }
                    }
                    filesDone++;
                }
                current = null;
            }

            cancellationToken.ThrowIfCancellationRequestedAsArchiveError();
            File.SetAttributes(temp, File.GetAttributes(temp) & ~FileAttributes.Hidden);
            File.Move(temp, output, request.OverwriteExisting);
            progress?.Report(new ExtractProgress("", done, plan.TotalBytes, filesDone, filesTotal));
            return new ZipResult(output, filesTotal, plan.Items.Count - filesTotal, plan.TotalBytes, new FileInfo(output).Length, plan.Skipped);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw ArchiveError.From(ex, context: current);
        }
    }

    private static DateTimeOffset ClampZipTime(DateTime time)
    {
        // The ZIP (DOS) timestamp range is 1980-2107.
        var min = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var max = new DateTime(2107, 12, 31, 0, 0, 0, DateTimeKind.Local);
        return time < min ? min : time > max ? max : time;
    }
}
