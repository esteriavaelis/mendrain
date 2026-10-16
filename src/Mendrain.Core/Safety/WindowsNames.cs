namespace Mendrain.Core.Safety;

/// <summary>Rules for names that are safe to create on a Windows file system.</summary>
public static class WindowsNames
{
    public const int MaxSegmentLength = 255;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// True when <paramref name="segment"/> is a DOS device name such as "aux" or "com1.txt".
    /// Windows treats the part before the first dot (ignoring trailing spaces) as the device name.
    /// </summary>
    public static bool IsReservedDeviceName(string segment)
    {
        var dot = segment.IndexOf('.');
        var stem = (dot >= 0 ? segment[..dot] : segment).TrimEnd(' ');
        return ReservedDeviceNames.Contains(stem);
    }

    public static bool IsInvalidFileNameChar(char c) =>
        c < 32 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';

    /// <summary>
    /// Turns one untrusted path segment into a name Windows can create without surprises.
    /// Returns null when nothing usable is left. <paramref name="changed"/> reports whether the name differs.
    /// </summary>
    public static string? SanitizeSegment(string segment, out bool changed)
    {
        var chars = segment.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (IsInvalidFileNameChar(chars[i]))
                chars[i] = '_';
        }

        // Windows silently strips trailing dots and spaces, which would make two different
        // archive names land on the same file. Strip them explicitly so collisions are detectable.
        var result = new string(chars).TrimEnd('.', ' ');
        if (result.Length == 0 || result == "..")
        {
            changed = true;
            return null;
        }

        if (IsReservedDeviceName(result))
            result = "_" + result;

        changed = !string.Equals(result, segment, StringComparison.Ordinal);
        return result;
    }

    /// <summary>Makes a folder name from arbitrary text (e.g. an archive's file name).</summary>
    public static string ToSafeFolderName(string name, string fallback = "Archive")
    {
        var sanitized = SanitizeSegment(name.Trim(), out _);
        if (string.IsNullOrEmpty(sanitized))
            return fallback;
        return sanitized.Length > 200 ? sanitized[..200].TrimEnd('.', ' ') : sanitized;
    }
}
