using System.Text.RegularExpressions;

namespace Mendrain.Core;

/// <summary>Turns a release's Markdown notes into one short line for the update notification.</summary>
public static partial class ReleaseNotes
{
    public static string Summarize(string? markdown, int maxLength = 160)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "";
        // First meaningful line: skip headings, rules and blank lines; drop list markers and inline formatting.
        var line = markdown.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#') && !Rule().IsMatch(l)) ?? "";
        line = ListMarker().Replace(line, "");
        line = Link().Replace(line, "$1");
        line = line.Replace("**", "").Replace("__", "").Replace("`", "");
        line = Whitespace().Replace(line, " ").Trim();
        return line.Length <= maxLength ? line : line[..(maxLength - 1)].TrimEnd() + "…";
    }

    [GeneratedRegex(@"^(-{3,}|\*{3,}|_{3,})$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^([-*+]|\d+\.)\s+")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
