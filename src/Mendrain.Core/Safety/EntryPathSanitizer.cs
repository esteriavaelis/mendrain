namespace Mendrain.Core.Safety;

/// <summary>Outcome of validating one archive entry name.</summary>
/// <param name="RelativePath">Safe relative path using '\' separators, or null when the entry is rejected.</param>
/// <param name="Rejection">Why the entry was rejected; null when accepted.</param>
/// <param name="WasRenamed">True when the accepted path differs from what the archive stored.</param>
public sealed record SanitizedPath(string? RelativePath, string? Rejection, bool WasRenamed)
{
    public bool IsAccepted => RelativePath is not null;
}

/// <summary>
/// Converts untrusted archive entry names into relative paths that cannot leave the extraction root.
/// Every path produced here is still re-checked with <see cref="ResolveInside"/> before it is touched.
/// </summary>
public static class EntryPathSanitizer
{
    public static SanitizedPath Sanitize(string? entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
            return Reject("The entry has no name.");

        var name = entryName.Replace('/', '\\');

        // Absolute and rooted forms: "\x", "\\server\share", "\\?\C:\x", "C:\x", "C:x".
        if (name.StartsWith(@"\\", StringComparison.Ordinal))
            return Reject("The entry uses a network or device path.");
        if (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':')
            return Reject("The entry uses an absolute path with a drive letter.");
        if (name.StartsWith('\\'))
            return Reject("The entry uses an absolute path.");

        var segments = new List<string>();
        var renamed = false;
        foreach (var raw in name.Split('\\'))
        {
            if (raw.Length == 0 || raw == ".")
            {
                renamed |= raw == ".";
                continue;
            }

            if (raw.Trim() == "..")
                return Reject("The entry tries to leave the destination folder (\"..\").");

            var safe = WindowsNames.SanitizeSegment(raw, out var changed);
            if (safe is null)
                return Reject("The entry contains a name that cannot exist on Windows.");
            if (safe.Length > WindowsNames.MaxSegmentLength)
                return Reject("The entry contains a name longer than 255 characters.");

            renamed |= changed;
            segments.Add(safe);
        }

        if (segments.Count == 0)
            return Reject("The entry has no usable name.");

        return new SanitizedPath(string.Join('\\', segments), null, renamed);
    }

    /// <summary>
    /// Combines <paramref name="root"/> and a sanitized relative path and verifies the result
    /// stays strictly inside the root. Throws <see cref="UnsafePathException"/> otherwise.
    /// </summary>
    public static string ResolveInside(string root, string relativePath)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnsafePathException($"Blocked a path outside the destination folder: {relativePath}");
        return candidate;
    }

    private static SanitizedPath Reject(string reason) => new(null, reason, false);
}

public sealed class UnsafePathException(string message) : Exception(message);
