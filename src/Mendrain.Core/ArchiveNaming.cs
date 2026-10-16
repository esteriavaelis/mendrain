using System.Text.RegularExpressions;
using Mendrain.Core.Safety;

namespace Mendrain.Core;

/// <summary>File-name rules for the archive formats Mendrain extracts.</summary>
public static partial class ArchiveNaming
{
    /// <summary>Extensions Mendrain can extract. Creation is ZIP only.</summary>
    public static readonly IReadOnlyList<string> SupportedExtensions = [".zip", ".7z", ".rar"];

    [GeneratedRegex(@"\.part(?<n>\d+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RarVolumePattern();

    public static bool IsSupportedArchive(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the volume number for "name.partN.rar" files, or null for anything else.
    /// </summary>
    public static int? GetRarVolumeNumber(string path)
    {
        var match = RarVolumePattern().Match(Path.GetFileName(path));
        return match.Success && int.TryParse(match.Groups["n"].Value, out var n) ? n : null;
    }

    /// <summary>True for "name.part2.rar", "name.part03.rar", ... which are opened through part 1.</summary>
    public static bool IsSecondaryVolume(string path) => GetRarVolumeNumber(path) is > 1;

    /// <summary>
    /// Folder name for extracting <paramref name="archivePath"/>: the file name without its archive
    /// extension. Multi-volume RAR names drop the ".partN" part too ("Backup.part1.rar" → "Backup").
    /// </summary>
    public static string GetDestinationFolderName(string archivePath)
    {
        var fileName = Path.GetFileName(archivePath);
        var volume = RarVolumePattern().Match(fileName);
        string stem;
        if (volume.Success)
            stem = fileName[..volume.Index];
        else if (IsSupportedArchive(fileName))
            stem = Path.GetFileNameWithoutExtension(fileName);
        else
            stem = fileName;

        return WindowsNames.ToSafeFolderName(stem);
    }

    /// <summary>"Name", "Name (2)", "Name (3)", ... skipping names for which <paramref name="isTaken"/> is true.</summary>
    public static string MakeUnique(string name, Func<string, bool> isTaken)
    {
        if (!isTaken(name))
            return name;
        for (var i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (!isTaken(candidate))
                return candidate;
        }
    }

    /// <summary>Like <see cref="MakeUnique"/> but keeps a file extension at the end: "a (2).txt".</summary>
    public static string MakeUniqueFileName(string fileName, Func<string, bool> isTaken)
    {
        if (!isTaken(fileName))
            return fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!isTaken(candidate))
                return candidate;
        }
    }
}
