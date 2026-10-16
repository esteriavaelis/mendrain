using Mendrain.Core.Models;
using Mendrain.Core.Safety;

namespace Mendrain.Core.Services;

/// <summary>
/// <see cref="Ignore"/> is for folder entries that only name the archive root (e.g. "." or "./"):
/// there is nothing to create and nothing worth reporting.
/// </summary>
public enum PlannedAction { CreateDirectory, WriteFile, Skip, Ignore }

/// <summary>The decision for one archive entry (same index as the archive's entry list).</summary>
public sealed record PlannedEntry(int Index, ArchiveItem Item, PlannedAction Action, string? RelativePath, string? Note);

public sealed record ExtractionPlan(IReadOnlyList<PlannedEntry> Entries)
{
    public IEnumerable<PlannedEntry> Files => Entries.Where(e => e.Action == PlannedAction.WriteFile);
    public IEnumerable<PlannedEntry> Directories => Entries.Where(e => e.Action == PlannedAction.CreateDirectory);
    public IEnumerable<PlannedEntry> Skipped => Entries.Where(e => e.Action == PlannedAction.Skip);

    /// <summary>Total bytes to write, or null when any file has an unknown size.</summary>
    public long? TotalBytes => Files.Any(f => f.Item.Size < 0) ? null : Files.Sum(f => f.Item.Size);
}

/// <summary>
/// Turns an archive listing into a safe extraction plan. Deterministic: the same listing always
/// produces the same plan, so conflict checks done before extraction match what extraction writes.
/// </summary>
public static class ExtractionPlanner
{
    public static ExtractionPlan Plan(IReadOnlyList<ArchiveItem> items)
    {
        var sanitized = new SanitizedPath?[items.Count];
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: validate names and collect every folder the plan will create
        // (explicit folder entries plus the parents of files).
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.IsLink || item.IsSpecial)
                continue;

            var path = EntryPathSanitizer.Sanitize(item.RawKey);
            sanitized[i] = path;
            if (!path.IsAccepted)
                continue;

            if (item.IsDirectory)
                AddWithParents(directories, path.RelativePath!);
            else
                AddParents(directories, path.RelativePath!);
        }

        // Pass 2: assign final paths. Files that collide with a folder or with an earlier file
        // (Windows compares names case-insensitively) get a unique name instead of overwriting.
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<PlannedEntry>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.IsDirectory && IsRootReference(item.RawKey))
            {
                plan.Add(new(i, item, PlannedAction.Ignore, null, null));
                continue;
            }
            if (item.IsLink)
            {
                plan.Add(new(i, item, PlannedAction.Skip, null, "Links are not extracted, for safety."));
                continue;
            }
            if (item.IsSpecial)
            {
                plan.Add(new(i, item, PlannedAction.Skip, null, "Special files (devices, pipes) are not extracted."));
                continue;
            }

            var path = sanitized[i]!;
            if (!path.IsAccepted)
            {
                plan.Add(new(i, item, PlannedAction.Skip, null, $"Blocked: {path.Rejection}"));
                continue;
            }

            var relative = path.RelativePath!;
            if (item.IsDirectory)
            {
                plan.Add(new(i, item, PlannedAction.CreateDirectory, relative,
                    path.WasRenamed ? "Renamed to a valid Windows name." : null));
                continue;
            }

            string? note = path.WasRenamed ? "Renamed to a valid Windows name." : null;
            if (directories.Contains(relative) || files.Contains(relative))
            {
                var parent = Path.GetDirectoryName(relative);
                var leaf = Path.GetFileName(relative);
                var unique = ArchiveNaming.MakeUniqueFileName(leaf, candidate =>
                {
                    var full = string.IsNullOrEmpty(parent) ? candidate : Path.Combine(parent, candidate);
                    return directories.Contains(full) || files.Contains(full);
                });
                relative = string.IsNullOrEmpty(parent) ? unique : Path.Combine(parent, unique);
                note = "Renamed because another entry has the same name.";
            }

            files.Add(relative);
            plan.Add(new(i, item, PlannedAction.WriteFile, relative, note));
        }

        return new ExtractionPlan(plan);
    }

    /// <summary>True when every path segment is empty or "." (the entry names the extraction root itself).</summary>
    public static bool IsRootReference(string key) =>
        key.Split('/', '\\').All(segment => segment.Length == 0 || segment == ".");

    private static void AddWithParents(HashSet<string> set, string relative)
    {
        set.Add(relative);
        AddParents(set, relative);
    }

    private static void AddParents(HashSet<string> set, string relative)
    {
        for (var parent = Path.GetDirectoryName(relative); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            set.Add(parent);
    }
}
