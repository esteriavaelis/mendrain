namespace Mendrain.Core.Tests;

public class ShellCommandTests
{
    [Fact]
    public void Parses_action_and_paths_with_spaces_and_non_ascii()
    {
        var path = @"C:\Users\Ünï Cödé\My Archives\Fotos 2026 – Ürlaub.zip";
        var command = ShellCommand.Parse([ShellCommand.Switch, "extract-here", path]);
        Assert.NotNull(command);
        Assert.Equal(ShellAction.ExtractHere, command.Action);
        Assert.Equal([path], command.Paths);
    }

    [Theory]
    [InlineData("open", ShellAction.Open)]
    [InlineData("EXTRACT-FOLDER", ShellAction.ExtractToFolder)]
    [InlineData("batch", ShellAction.Batch)]
    public void Knows_every_action(string name, ShellAction action) =>
        Assert.Equal(action, ShellCommand.Parse(["--shell", name, @"C:\a.zip"])!.Action);

    [Fact]
    public void Ordinary_arguments_are_not_a_shell_command()
    {
        Assert.Null(ShellCommand.Parse([@"C:\a.zip"]));
        Assert.Null(ShellCommand.Parse(["--shell"]));
        Assert.Null(ShellCommand.Parse(["--shell", "format-c", @"C:\a.zip"]));
    }

    [Fact]
    public void Relative_and_invalid_paths_are_dropped_not_resolved_against_the_working_directory()
    {
        var command = ShellCommand.Parse(["--shell", "batch", "a.zip", @"..\b.zip", "", "C:\\bad\0name.zip", @"D:\ok.7z"])!;
        Assert.Equal([@"D:\ok.7z"], command.Paths);
    }

    [Fact]
    public void Command_line_quotes_the_program_and_the_file()
    {
        Assert.Equal("\"C:\\Program Files\\Mendrain\\Mendrain.exe\" --shell extract-folder \"%1\"",
            ShellCommand.CommandLine(@"C:\Program Files\Mendrain\Mendrain.exe", ShellAction.ExtractToFolder));
    }

    [Fact]
    public void Selection_keeps_existing_primary_archives_and_explains_the_rest()
    {
        using var temp = new TempDir();
        var zip = temp.Combine("a b ü.zip");
        File.Copy(Fixtures.Get("sample.zip"), zip);
        var part1 = temp.Combine("v.part1.rar");
        var part2 = temp.Combine("v.part2.rar");
        File.WriteAllText(part1, "");
        File.WriteAllText(part2, "");
        var text = temp.Combine("notes.txt");
        File.WriteAllText(text, "");

        var selection = ArchiveSelection.From([zip, text, part1, part2, temp.Combine("gone.7z"), zip.ToUpperInvariant()]);

        Assert.Equal([zip, part1], selection.Archives);
        Assert.Equal((1, 1, 1), (selection.Unsupported, selection.Missing, selection.LaterVolumes));
        Assert.Contains("not a ZIP, 7Z or RAR", selection.Note);
        Assert.Contains("could not be found", selection.Note);
        Assert.Contains("part 1", selection.Note);
        Assert.Null(ArchiveSelection.From([zip]).Note);
    }
}
