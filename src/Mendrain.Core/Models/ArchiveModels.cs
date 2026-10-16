namespace Mendrain.Core.Models;

/// <summary>One archive entry, as listed in the viewer and as input to extraction planning.</summary>
/// <param name="RawKey">The entry name exactly as stored in the archive (untrusted).</param>
/// <param name="IsLink">Symbolic link, hard link or reparse point. Never extracted.</param>
/// <param name="IsSpecial">Device, pipe or other non-regular file. Never extracted.</param>
public sealed record ArchiveItem(
    string RawKey,
    bool IsDirectory,
    long Size,
    long CompressedSize,
    DateTime? Modified,
    bool IsEncrypted,
    bool IsLink = false,
    bool IsSpecial = false,
    uint Crc = 0)
{
    /// <summary>Display path: '/' separators, no leading or trailing separator.</summary>
    public string Path { get; } = RawKey.Replace('\\', '/').Trim('/');

    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>What Mendrain knows about an opened archive.</summary>
public sealed record ArchiveContents(
    string FilePath,
    string Format,
    bool IsSolid,
    bool HasEncryptedEntries,
    bool HeadersEncrypted,
    int VolumeCount,
    IReadOnlyList<ArchiveItem> Items)
{
    public long TotalSize => Items.Where(i => !i.IsDirectory).Sum(i => Math.Max(0, i.Size));
    public int FileCount => Items.Count(i => !i.IsDirectory);
    public bool NeedsPassword => HasEncryptedEntries || HeadersEncrypted;
}

/// <summary>What to do with a file that already exists in the destination folder.</summary>
public enum ExistingFilePolicy
{
    /// <summary>Keep the existing file; the archived copy is not written.</summary>
    Skip,
    /// <summary>Write the archived copy under a new name such as "report (2).pdf".</summary>
    KeepBoth,
    /// <summary>Replace the existing file. Only used after explicit user confirmation. Folders are never replaced.</summary>
    Overwrite,
}

public enum DestinationMode
{
    /// <summary>The destination folder must not exist yet (used by Batch Extract).</summary>
    CreateNew,
    /// <summary>The destination may exist; files are merged according to <see cref="ExistingFilePolicy"/>.</summary>
    MergeIntoExisting,
}

public sealed record ExtractRequest(
    string ArchivePath,
    string DestinationDirectory,
    string? Password = null,
    DestinationMode Mode = DestinationMode.CreateNew,
    ExistingFilePolicy ExistingFiles = ExistingFilePolicy.Skip);

/// <summary>Progress snapshot. <see cref="BytesTotal"/> is null when sizes are unknown.</summary>
public sealed record ExtractProgress(string CurrentEntry, long BytesDone, long? BytesTotal, int FilesDone, int FilesTotal)
{
    public double? Fraction => BytesTotal is > 0 ? Math.Clamp((double)BytesDone / BytesTotal.Value, 0, 1) : null;
}

/// <summary>An entry Mendrain deliberately did not write, with the reason.</summary>
public sealed record SkippedEntry(string EntryName, string Reason);

/// <summary>An entry written under a different name than stored in the archive.</summary>
public sealed record RenamedEntry(string EntryName, string WrittenAs, string Reason);

public sealed record ExtractResult(
    string Destination,
    int FilesWritten,
    int FoldersCreated,
    long BytesWritten,
    int ExistingFilesSkipped,
    int ExistingFilesOverwritten,
    IReadOnlyList<SkippedEntry> SkippedEntries,
    IReadOnlyList<RenamedEntry> RenamedEntries)
{
    public bool HasWarnings => SkippedEntries.Count > 0 || RenamedEntries.Count > 0 || ExistingFilesSkipped > 0;
}
