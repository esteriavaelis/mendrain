namespace Mendrain.Core;

public enum ShellAction { Open, ExtractHere, ExtractToFolder, Batch }

/// <summary>
/// A request from the Explorer context menu: <c>Mendrain.exe --shell &lt;action&gt; "&lt;path&gt;"</c>.
/// Explorer starts one process per selected file; the running instance merges them (see the app's ShellRequests).
/// </summary>
public sealed record ShellCommand(ShellAction Action, IReadOnlyList<string> Paths)
{
    public const string Switch = "--shell";

    private static readonly (string Name, ShellAction Action)[] Names =
    [
        ("open", ShellAction.Open),
        ("extract-here", ShellAction.ExtractHere),
        ("extract-folder", ShellAction.ExtractToFolder),
        ("batch", ShellAction.Batch),
    ];

    public static string NameOf(ShellAction action) => Names.First(n => n.Action == action).Name;

    /// <summary>Reads a shell command from command-line arguments; null when they are not one.</summary>
    public static ShellCommand? Parse(IReadOnlyList<string> args)
    {
        var index = args.ToList().FindIndex(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Count)
            return null;
        var match = Names.FirstOrDefault(n => string.Equals(n.Name, args[index + 1], StringComparison.OrdinalIgnoreCase));
        if (match.Name is null)
            return null;
        var paths = args.Skip(index + 2).Select(TryGetFullPath).OfType<string>().ToList();
        return new ShellCommand(match.Action, paths);
    }

    /// <summary>
    /// The command stored in the registry. Explorer substitutes the selected file for %1; the path is a single
    /// quoted argument, never part of a command string Mendrain builds itself.
    /// </summary>
    public static string CommandLine(string exePath, ShellAction action) => $"\"{exePath}\" {Switch} {NameOf(action)} \"%1\"";

    /// <summary>The quoted program at the start of a registered command line, or null.</summary>
    public static string? ProgramOf(string? commandLine)
    {
        if (commandLine is null || !commandLine.StartsWith('"'))
            return null;
        var end = commandLine.IndexOf('"', 1);
        return end > 1 ? commandLine[1..end] : null;
    }

    private static string? TryGetFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            // Explorer passes absolute paths; never resolve against the working directory.
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

/// <summary>The archives in an Explorer selection that Mendrain can act on, and what was left out.</summary>
public sealed record ArchiveSelection(IReadOnlyList<string> Archives, int Missing, int Unsupported, int LaterVolumes)
{
    public static ArchiveSelection From(IEnumerable<string> paths)
    {
        var archives = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int missing = 0, unsupported = 0, volumes = 0;
        foreach (var path in paths)
        {
            if (!seen.Add(path))
                continue;
            if (!ArchiveNaming.IsSupportedArchive(path))
                unsupported++;
            else if (!File.Exists(path))
                missing++;
            else if (ArchiveNaming.IsSecondaryVolume(path))
                volumes++;
            else
                archives.Add(path);
        }
        return new ArchiveSelection(archives, missing, unsupported, volumes);
    }

    /// <summary>Explains what was left out, or null when nothing was.</summary>
    public string? Note
    {
        get
        {
            var notes = new List<string>();
            if (Unsupported > 0)
                notes.Add(Unsupported == 1 ? "1 selected item is not a ZIP, 7Z or RAR archive and was ignored." : $"{Unsupported} selected items are not ZIP, 7Z or RAR archives and were ignored.");
            if (Missing > 0)
                notes.Add(Missing == 1 ? "1 selected archive could not be found." : $"{Missing} selected archives could not be found.");
            if (LaterVolumes > 0)
                notes.Add(LaterVolumes == 1
                    ? "1 later part of a multi-volume archive was left out; it is extracted together with part 1."
                    : $"{LaterVolumes} later parts of multi-volume archives were left out; they are extracted together with part 1.");
            return notes.Count == 0 ? null : string.Join(" ", notes);
        }
    }
}
