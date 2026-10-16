using System.Diagnostics;
using System.IO.Hashing;
using Mendrain.Core.Models;
using Mendrain.Core.Safety;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Mendrain.Core.Services;

/// <summary>
/// Opens and extracts a single archive safely. Extraction writes into a hidden staging folder next to
/// the destination and moves the result into place only after every entry was read and verified.
/// </summary>
public sealed class ArchiveService
{
    private const int BufferSize = 1 << 16;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(80);

    // ---------------------------------------------------------------- open / list

    public Task<ArchiveContents> OpenAsync(string path, string? password = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Open(path, password), cancellationToken);

    /// <summary>
    /// Lists an archive. Archives whose names are encrypted ("encrypted headers") need
    /// <paramref name="password"/>; otherwise the password is not needed to list entries.
    /// </summary>
    public ArchiveContents Open(string path, string? password = null)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new ArchiveError(ArchiveErrorKind.NotFound, $"The file \"{Path.GetFileName(path)}\" no longer exists.");
        if (ArchiveNaming.IsSecondaryVolume(path))
            throw new ArchiveError(ArchiveErrorKind.InvalidInput,
                $"\"{Path.GetFileName(path)}\" is one part of a multi-volume archive. Open the first part (.part1.rar) instead.");

        // List without a password first: that works for most encrypted archives and avoids
        // libraries rejecting a wrong password at open time with misleading errors.
        try
        {
            return List(path, null, headersEncrypted: false);
        }
        catch (Exception ex)
        {
            var error = ArchiveError.From(ex);
            if (error.Kind != ArchiveErrorKind.PasswordRequired || string.IsNullOrEmpty(password))
                throw error;
        }

        try
        {
            return List(path, password, headersEncrypted: true);
        }
        catch (Exception ex)
        {
            throw ArchiveError.From(ex, passwordSupplied: true, knownEncrypted: true);
        }
    }

    private static ArchiveContents List(string path, string? password, bool headersEncrypted)
    {
        using var archive = OpenArchive(path, password, out var volumeCount);
        var items = archive.Entries.Select(ToItem).ToList(); // must come before IsSolid/Type (see Extract)
        return new ArchiveContents(path, FormatName(archive.Type), archive.IsSolid,
            items.Any(i => i.IsEncrypted), headersEncrypted, volumeCount, items);
    }

    private static IArchive OpenArchive(string path, string? password, out int volumeCount)
    {
        var options = new ReaderOptions { Password = password, LeaveStreamOpen = false };
        IArchive archive;
        volumeCount = 1;
        if (ArchiveNaming.GetRarVolumeNumber(path) == 1)
        {
            var parts = ArchiveFactory.GetFileParts(path).Select(p => new FileInfo(p)).ToList();
            volumeCount = parts.Count;
            archive = parts.Count > 1 ? ArchiveFactory.OpenArchive(parts, options) : ArchiveFactory.OpenArchive(path, options);
        }
        else
        {
            archive = ArchiveFactory.OpenArchive(path, options);
        }

        if (archive.Type is not (ArchiveType.Zip or ArchiveType.SevenZip or ArchiveType.Rar))
        {
            var type = archive.Type;
            archive.Dispose();
            throw new ArchiveError(ArchiveErrorKind.UnsupportedFormat,
                $"This is a {type} archive. Mendrain currently extracts ZIP, 7Z and RAR archives.");
        }
        return archive;
    }

    private static string FormatName(ArchiveType type) => type switch
    {
        ArchiveType.Zip => "ZIP",
        ArchiveType.SevenZip => "7Z",
        ArchiveType.Rar => "RAR",
        _ => type.ToString().ToUpperInvariant(),
    };

    internal static ArchiveItem ToItem(IEntry entry)
    {
        var attrib = entry.Attrib ?? 0;
        var unixType = ((uint)attrib >> 16) & 0xF000;          // Unix st_mode file type, when present
        const int windowsReparsePoint = 0x400;
        var isLink = !string.IsNullOrEmpty(entry.LinkTarget) || (attrib & windowsReparsePoint) != 0 || unixType == 0xA000;
        var isSpecial = !isLink && unixType is not (0 or 0x8000 or 0x4000);
        return new ArchiveItem(entry.Key ?? "", entry.IsDirectory, entry.Size, entry.CompressedSize,
            entry.LastModifiedTime, entry.IsEncrypted, isLink, isSpecial, ReadCrc(entry));
    }

    /// <summary>Stored CRC-32, or 0 when unavailable. RAR folder entries throw when asked for a CRC.</summary>
    private static uint ReadCrc(IEntry entry)
    {
        if (entry.IsDirectory)
            return 0;
        try { return unchecked((uint)entry.Crc); }
        catch (ArgumentException) { return 0; }
        catch (InvalidOperationException) { return 0; }
    }

    // ---------------------------------------------------------------- conflict checks

    /// <summary>Relative paths of files in the plan that already exist under <paramref name="destination"/>.</summary>
    public IReadOnlyList<string> FindExistingFiles(ArchiveContents contents, string destination)
    {
        if (!Directory.Exists(destination))
            return [];
        var plan = ExtractionPlanner.Plan(contents.Items);
        return plan.Files
            .Where(f => File.Exists(EntryPathSanitizer.ResolveInside(destination, f.RelativePath!)))
            .Select(f => f.RelativePath!)
            .ToList();
    }

    // ---------------------------------------------------------------- extract

    public Task<ExtractResult> ExtractAsync(ExtractRequest request, IProgress<ExtractProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Extract(request, progress, cancellationToken), CancellationToken.None);

    public ExtractResult Extract(ExtractRequest request, IProgress<ExtractProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequestedAsArchiveError();

        var contents = Open(request.ArchivePath, request.Password);
        var passwordSupplied = !string.IsNullOrEmpty(request.Password);
        if (contents.NeedsPassword && !passwordSupplied)
            throw new ArchiveError(ArchiveErrorKind.PasswordRequired, "This archive is encrypted. Enter its password to continue.");

        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.DestinationDirectory));
        var parent = Path.GetDirectoryName(destination)
            ?? throw new ArchiveError(ArchiveErrorKind.InvalidInput, "Choose a folder inside a drive, not the drive itself.");
        if (!Directory.Exists(parent))
            throw new ArchiveError(ArchiveErrorKind.NotFound, $"The folder \"{parent}\" does not exist.");
        if (File.Exists(destination))
            throw new ArchiveError(ArchiveErrorKind.DestinationExists, $"A file named \"{Path.GetFileName(destination)}\" already exists there.");
        if (request.Mode == DestinationMode.CreateNew && Directory.Exists(destination))
            throw new ArchiveError(ArchiveErrorKind.DestinationExists, $"The folder \"{Path.GetFileName(destination)}\" already exists.");

        var plan = ExtractionPlanner.Plan(contents.Items);
        string staging;
        try
        {
            staging = CreateStagingDirectory(parent);
        }
        catch (Exception ex)
        {
            throw ArchiveError.From(ex, context: parent);
        }
        string? currentEntry = null;
        try
        {
            using (var archive = OpenArchive(contents.FilePath, contents.NeedsPassword ? request.Password : null, out _))
            {
                // Materialize entries before reading any other archive property: for RAR archives with
                // encrypted headers, reading IsSolid first consumes the lazy entry enumeration.
                var entries = archive.Entries.ToList();
                if (entries.Count != plan.Entries.Count)
                    throw new ArchiveError(ArchiveErrorKind.Corrupt, "The archive changed while it was being read.");

                foreach (var dir in plan.Directories)
                    Directory.CreateDirectory(EntryPathSanitizer.ResolveInside(staging, dir.RelativePath!));

                var writer = new StagingWriter(staging, plan, archive.Type == ArchiveType.Zip, progress, cancellationToken);
                if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
                {
                    // Solid data must be decoded front to back; random access would re-decode for every entry.
                    // The reader's order can differ from the listing (7z yields folders first), so match by name.
                    using var reader = archive.ExtractAllEntries();
                    var byKey = plan.Entries.GroupBy(e => e.Item.RawKey, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => new Queue<PlannedEntry>(g), StringComparer.Ordinal);
                    while (reader.MoveToNextEntry())
                    {
                        cancellationToken.ThrowIfCancellationRequestedAsArchiveError();
                        if (!byKey.TryGetValue(reader.Entry.Key ?? "", out var queue) || !queue.TryDequeue(out var planned))
                            throw new ArchiveError(ArchiveErrorKind.Corrupt, "The archive's contents do not match its table of contents.");
                        if (planned.Action != PlannedAction.WriteFile)
                            continue;
                        currentEntry = planned.Item.Path;
                        using var source = reader.OpenEntryStream();
                        writer.WriteFile(planned, source);
                    }
                }
                else
                {
                    foreach (var planned in plan.Files)
                    {
                        cancellationToken.ThrowIfCancellationRequestedAsArchiveError();
                        var entry = entries[planned.Index];
                        if (!string.Equals(entry.Key, planned.Item.RawKey, StringComparison.Ordinal))
                            throw new ArchiveError(ArchiveErrorKind.Corrupt, "The archive changed while it was being read.");
                        currentEntry = planned.Item.Path;
                        using var source = entry.OpenEntryStream();
                        writer.WriteFile(planned, source);
                    }
                }
                currentEntry = null;
                if (writer.FilesWritten != writer.FilesTotal)
                    throw new ArchiveError(ArchiveErrorKind.Corrupt,
                        $"{writer.FilesTotal - writer.FilesWritten} file(s) listed in the archive could not be found in its data.");
                writer.ReportFinal();
            }

            cancellationToken.ThrowIfCancellationRequestedAsArchiveError();
            // From here on the operation finishes: moving staged files is quick and stopping halfway
            // would leave a half-merged folder.
            return Commit(staging, destination, plan, request);
        }
        catch (Exception ex)
        {
            TryDeleteStaging(staging);
            throw ArchiveError.From(ex, passwordSupplied, contents.NeedsPassword, currentEntry);
        }
    }

    private static string CreateStagingDirectory(string parent)
    {
        string staging;
        do
        {
            staging = Path.Combine(parent, $".mendrain-{Guid.NewGuid():N}.partial");
        } while (Directory.Exists(staging) || File.Exists(staging));

        var info = Directory.CreateDirectory(staging);
        try { info.Attributes |= FileAttributes.Hidden; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return staging;
    }

    /// <summary>Removes the staging folder. It was created by this operation and holds only files written by it.</summary>
    private static void TryDeleteStaging(string staging)
    {
        try
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not remove staging folder {staging}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- commit

    private static ExtractResult Commit(string staging, string destination, ExtractionPlan plan, ExtractRequest request)
    {
        var skipped = plan.Skipped.Select(s => new SkippedEntry(s.Item.Path, s.Note ?? "Skipped.")).ToList();
        var renamed = plan.Entries
            .Where(e => e.Action != PlannedAction.Skip && e.Note is not null)
            .Select(e => new RenamedEntry(e.Item.Path, e.RelativePath!, e.Note!))
            .ToList();
        var bytes = plan.Files.Sum(f => Math.Max(0, f.Item.Size));

        if (!Directory.Exists(destination))
        {
            try
            {
                Directory.Move(staging, destination);
            }
            catch (IOException) when (Directory.Exists(destination) || File.Exists(destination))
            {
                TryDeleteStaging(staging);
                throw new ArchiveError(ArchiveErrorKind.DestinationExists,
                    $"The folder \"{Path.GetFileName(destination)}\" was created by something else during extraction.");
            }

            try { new DirectoryInfo(destination).Attributes &= ~FileAttributes.Hidden; } catch (IOException) { }
            var folders = Directory.GetDirectories(destination, "*", SearchOption.AllDirectories).Length;
            return new ExtractResult(destination, plan.Files.Count(), folders, bytes, 0, 0, skipped, renamed);
        }

        if (request.Mode == DestinationMode.CreateNew)
        {
            TryDeleteStaging(staging);
            throw new ArchiveError(ArchiveErrorKind.DestinationExists, $"The folder \"{Path.GetFileName(destination)}\" already exists.");
        }

        var result = Merge(staging, destination, plan, request.ExistingFiles, skipped, renamed);
        TryDeleteStaging(staging);
        return result;
    }

    /// <summary>
    /// Moves staged files into an existing folder. Not atomic: every move is checked first, so
    /// problems found up front change nothing, but an I/O failure mid-way can leave a partial merge.
    /// Existing folders are never deleted or replaced; existing files are replaced only for
    /// <see cref="ExistingFilePolicy.Overwrite"/>.
    /// </summary>
    private static ExtractResult Merge(string staging, string destination, ExtractionPlan plan, ExistingFilePolicy policy,
        List<SkippedEntry> skipped, List<RenamedEntry> renamed)
    {
        // Pass 1: decide every move without touching the destination.
        var moves = new List<(string Source, string Target, bool Overwrite, long Size)>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingSkipped = 0;
        var overwritten = 0;
        foreach (var file in plan.Files)
        {
            var relative = file.RelativePath!;
            if (FindBlockingComponent(destination, relative) is { } blocker)
            {
                skipped.Add(new SkippedEntry(file.Item.Path, blocker));
                continue;
            }

            var target = EntryPathSanitizer.ResolveInside(destination, relative);
            var overwrite = false;
            if (Directory.Exists(target))
            {
                target = UniqueTarget(target, claimed);
                renamed.Add(new RenamedEntry(file.Item.Path, Path.GetRelativePath(destination, target), "A folder with this name already exists."));
            }
            else if (File.Exists(target))
            {
                switch (policy)
                {
                    case ExistingFilePolicy.Skip:
                        existingSkipped++;
                        continue;
                    case ExistingFilePolicy.KeepBoth:
                        target = UniqueTarget(target, claimed);
                        renamed.Add(new RenamedEntry(file.Item.Path, Path.GetRelativePath(destination, target), "Kept both: a file with this name already exists."));
                        break;
                    case ExistingFilePolicy.Overwrite:
                        overwrite = true;
                        overwritten++;
                        break;
                }
            }

            claimed.Add(target);
            moves.Add((EntryPathSanitizer.ResolveInside(staging, relative), target, overwrite, Math.Max(0, file.Item.Size)));
        }

        // Pass 2: create folders, then move files.
        var foldersCreated = 0;
        foreach (var dir in plan.Directories)
        {
            if (FindBlockingComponent(destination, dir.RelativePath! + "\\x") is not null)
                continue;
            var target = EntryPathSanitizer.ResolveInside(destination, dir.RelativePath!);
            if (!Directory.Exists(target) && !File.Exists(target))
            {
                Directory.CreateDirectory(target);
                foldersCreated++;
            }
        }

        long bytes = 0;
        foreach (var (source, target, overwrite, size) in moves)
        {
            var targetDir = Path.GetDirectoryName(target)!;
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
                foldersCreated++;
            }
            File.Move(source, target, overwrite);
            bytes += size;
        }

        return new ExtractResult(destination, moves.Count, foldersCreated, bytes, existingSkipped, overwritten, skipped, renamed);
    }

    /// <summary>
    /// Checks the folders between <paramref name="root"/> and the target. Returns a reason when one of
    /// them is a link (never written through) or is an existing file, otherwise null.
    /// </summary>
    private static string? FindBlockingComponent(string root, string relativeFile)
    {
        var current = root;
        var parts = relativeFile.Split('\\');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            current = Path.Combine(current, parts[i]);
            if (File.Exists(current))
                return $"A file named \"{parts[i]}\" exists where a folder is needed.";
            if (Directory.Exists(current) && new DirectoryInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint))
                return $"The existing folder \"{parts[i]}\" is a link; Mendrain does not write through links.";
            if (!Directory.Exists(current))
                return null;
        }
        return null;
    }

    private static string UniqueTarget(string target, HashSet<string> claimed)
    {
        var dir = Path.GetDirectoryName(target)!;
        var name = ArchiveNaming.MakeUniqueFileName(Path.GetFileName(target), candidate =>
        {
            var full = Path.Combine(dir, candidate);
            return File.Exists(full) || Directory.Exists(full) || claimed.Contains(full);
        });
        return Path.Combine(dir, name);
    }

    // ---------------------------------------------------------------- writing

    private sealed class StagingWriter(
        string staging, ExtractionPlan plan, bool verifyCrc, IProgress<ExtractProgress>? progress, CancellationToken cancellationToken)
    {
        private readonly byte[] _buffer = new byte[BufferSize];
        private readonly long? _total = plan.TotalBytes;
        private readonly int _filesTotal = plan.Files.Count();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TimeSpan _lastReport = TimeSpan.FromDays(-1);
        private long _done;
        private int _filesDone;
        private string _current = "";

        public int FilesWritten => _filesDone;
        public int FilesTotal => _filesTotal;

        public void WriteFile(PlannedEntry planned, Stream source)
        {
            var item = planned.Item;
            _current = item.Path;
            var target = EntryPathSanitizer.ResolveInside(staging, planned.RelativePath!);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            var crc = verifyCrc ? new Crc32() : null;
            long written = 0;
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize))
            {
                int read;
                while ((read = source.Read(_buffer, 0, _buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequestedAsArchiveError();
                    output.Write(_buffer, 0, read);
                    crc?.Append(_buffer.AsSpan(0, read));
                    written += read;
                    _done += read;
                    MaybeReport();
                }
            }

            if (item.Size >= 0 && written != item.Size)
                throw new ChecksumMismatchException($"\"{item.Path}\" is incomplete ({written:N0} of {item.Size:N0} bytes).");
            // ZIP data is not CRC-checked by the library. AES-encrypted ZIP entries (AE-2) store CRC 0.
            if (crc is not null && item.Crc != 0 && crc.GetCurrentHashAsUInt32() != item.Crc)
                throw new ChecksumMismatchException($"\"{item.Path}\" failed its CRC check.");

            if (item.Modified is { } modified && modified.Year >= 1980)
            {
                try { File.SetLastWriteTime(target, modified); } catch (IOException) { } catch (ArgumentException) { }
            }

            _filesDone++;
            MaybeReport();
        }

        public void ReportFinal() =>
            progress?.Report(new ExtractProgress(_current, _done, _total, _filesDone, _filesTotal));

        private void MaybeReport()
        {
            var now = _clock.Elapsed;
            if (now - _lastReport < ProgressInterval)
                return;
            _lastReport = now;
            progress?.Report(new ExtractProgress(_current, _done, _total, _filesDone, _filesTotal));
        }
    }
}

internal static class CancellationExtensions
{
    public static void ThrowIfCancellationRequestedAsArchiveError(this CancellationToken token)
    {
        if (token.IsCancellationRequested)
            throw new ArchiveError(ArchiveErrorKind.Cancelled, "The operation was cancelled.");
    }
}
