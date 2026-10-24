namespace Mendrain.Core.Tests;

public class ReleaseNotesTests
{
    [Fact]
    public void Takes_the_first_real_line_without_markdown()
    {
        var notes = "## Mendrain 1.2.0\n\n---\n- **Saved passwords** in the [password field](https://example.org/x) and `Batch`\n- Other";
        Assert.Equal("Saved passwords in the password field and Batch", ReleaseNotes.Summarize(notes));
    }

    [Fact]
    public void Empty_notes_give_empty_text() => Assert.Equal("", ReleaseNotes.Summarize(null));

    [Fact]
    public void Long_lines_are_shortened()
    {
        var summary = ReleaseNotes.Summarize(new string('a', 500), 50);
        Assert.Equal(50, summary.Length);
        Assert.EndsWith("…", summary);
    }
}
