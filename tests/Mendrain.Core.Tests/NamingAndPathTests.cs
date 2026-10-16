using Mendrain.Core.Models;
using Mendrain.Core.Safety;
using Mendrain.Core.Services;

namespace Mendrain.Core.Tests;

public class ArchiveNamingTests
{
    [Theory]
    [InlineData(@"C:\Downloads\Project.zip", "Project")]
    [InlineData(@"C:\Downloads\Textures.7z", "Textures")]
    [InlineData(@"C:\Downloads\Backup.rar", "Backup")]
    [InlineData(@"C:\Downloads\Backup.part1.rar", "Backup")]
    [InlineData(@"C:\Downloads\Backup.PART01.RAR", "Backup")]
    [InlineData(@"C:\Downloads\my.project.v2.zip", "my.project.v2")]
    [InlineData(@"C:\Downloads\photos.tar.zip", "photos.tar")]
    [InlineData(@"C:\Downloads\con.zip", "_con")]
    [InlineData(@"C:\Downloads\name..zip", "name")]
    [InlineData(@"C:\Downloads\.zip", "Archive")]
    public void Destination_folder_name_drops_archive_extension(string path, string expected) =>
        Assert.Equal(expected, ArchiveNaming.GetDestinationFolderName(path));

    [Theory]
    [InlineData("a.part1.rar", false)]
    [InlineData("a.part2.rar", true)]
    [InlineData("a.part010.rar", true)]
    [InlineData("a.rar", false)]
    [InlineData("a.part2.zip", false)]
    public void Secondary_rar_volumes_are_recognized(string name, bool expected) =>
        Assert.Equal(expected, ArchiveNaming.IsSecondaryVolume(name));

    [Fact]
    public void MakeUnique_appends_counter()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Project", "project (2)" };
        Assert.Equal("Project (3)", ArchiveNaming.MakeUnique("Project", taken.Contains));
        Assert.Equal("Other", ArchiveNaming.MakeUnique("Other", taken.Contains));
    }

    [Fact]
    public void MakeUniqueFileName_keeps_extension() =>
        Assert.Equal("report (2).pdf", ArchiveNaming.MakeUniqueFileName("report.pdf", n => n == "report.pdf"));
}

public class EntryPathSanitizerTests
{
    [Theory]
    [InlineData("../evil.txt")]
    [InlineData(@"..\evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("a/b/../../../evil.txt")]
    [InlineData("a/ .. /evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData(@"\Windows\evil.txt")]
    [InlineData(@"C:\Windows\evil.txt")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("C:evil.txt")]
    [InlineData(@"\\server\share\evil.txt")]
    [InlineData(@"\\?\C:\evil.txt")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("./.")]
    [InlineData("...")]
    public void Unsafe_names_are_rejected(string name)
    {
        var result = EntryPathSanitizer.Sanitize(name);
        Assert.False(result.IsAccepted);
        Assert.NotNull(result.Rejection);
    }

    [Theory]
    [InlineData("a/b/c.txt", @"a\b\c.txt", false)]
    [InlineData(@"a\b\c.txt", @"a\b\c.txt", false)]
    [InlineData("./a//b/./c.txt", @"a\b\c.txt", true)]
    [InlineData("a/b/", @"a\b", false)]
    [InlineData("aux.txt", "_aux.txt", true)]
    [InlineData("dir/CON/x.txt", @"dir\_CON\x.txt", true)]
    [InlineData("com1", "_com1", true)]
    [InlineData("a<b>|c?.txt", "a_b__c_.txt", true)]
    [InlineData("file.txt:stream", "file.txt_stream", true)]
    [InlineData("name. . ", "name", true)]
    [InlineData("dir./x.txt", @"dir\x.txt", true)]
    [InlineData("tab\there.txt", "tab_here.txt", true)]
    [InlineData("..a/b..", @"..a\b", true)]
    public void Accepted_names_are_normalized(string name, string expected, bool renamed)
    {
        var result = EntryPathSanitizer.Sanitize(name);
        Assert.True(result.IsAccepted, result.Rejection);
        Assert.Equal(expected, result.RelativePath);
        Assert.Equal(renamed, result.WasRenamed);
    }

    [Fact]
    public void Overlong_segment_is_rejected() =>
        Assert.False(EntryPathSanitizer.Sanitize(new string('a', 256) + ".txt").IsAccepted);

    [Fact]
    public void ResolveInside_blocks_escapes_and_sibling_prefixes()
    {
        Assert.Throws<UnsafePathException>(() => EntryPathSanitizer.ResolveInside(@"C:\out", @"..\x.txt"));
        Assert.Throws<UnsafePathException>(() => EntryPathSanitizer.ResolveInside(@"C:\out", @"..\out2\x.txt"));
        Assert.Throws<UnsafePathException>(() => EntryPathSanitizer.ResolveInside(@"C:\out", @"C:\x.txt"));
        Assert.Throws<UnsafePathException>(() => EntryPathSanitizer.ResolveInside(@"C:\out", "."));
        Assert.Equal(@"C:\out\a\b.txt", EntryPathSanitizer.ResolveInside(@"C:\out\", @"a\b.txt"));
    }

    [Theory]
    [InlineData("CON", true)]
    [InlineData("con.txt", true)]
    [InlineData("Lpt9.log", true)]
    [InlineData("COM¹", true)]
    [InlineData("console", false)]
    [InlineData("nul_file", false)]
    public void Reserved_device_names(string name, bool reserved) =>
        Assert.Equal(reserved, WindowsNames.IsReservedDeviceName(name));
}

public class ExtractionPlannerTests
{
    private static ArchiveItem File(string key) => new(key, false, 1, 1, null, false);
    private static ArchiveItem Dir(string key) => new(key, true, 0, 0, null, false);

    [Fact]
    public void Case_insensitive_duplicates_get_unique_names()
    {
        var plan = ExtractionPlanner.Plan([File("Readme.txt"), File("README.TXT"), File("readme.txt")]);
        Assert.Equal(["Readme.txt", "README (2).TXT", "readme (3).txt"], plan.Files.Select(f => f.RelativePath));
        Assert.All(plan.Files.Skip(1), f => Assert.Contains("same name", f.Note));
    }

    [Fact]
    public void File_colliding_with_folder_is_renamed_not_merged()
    {
        var plan = ExtractionPlanner.Plan([File("data"), File("Data/inner.txt")]);
        Assert.Equal(["data (2)", @"Data\inner.txt"], plan.Files.Select(f => f.RelativePath));
    }

    [Fact]
    public void Names_that_differ_only_by_trailing_dot_collide_safely()
    {
        var plan = ExtractionPlanner.Plan([File("a.txt"), File("a.txt.")]);
        Assert.Equal(["a.txt", "a (2).txt"], plan.Files.Select(f => f.RelativePath));
    }

    [Fact]
    public void Links_special_files_and_traversal_are_skipped_with_reasons()
    {
        var plan = ExtractionPlanner.Plan([
            new ArchiveItem("link", false, 10, 10, null, false, IsLink: true),
            new ArchiveItem("fifo", false, 0, 0, null, false, IsSpecial: true),
            File("../escape.txt"),
            File("ok.txt"),
            Dir("folder/"),
        ]);
        Assert.Equal(3, plan.Skipped.Count());
        Assert.All(plan.Skipped, s => Assert.False(string.IsNullOrEmpty(s.Note)));
        Assert.Equal(["ok.txt"], plan.Files.Select(f => f.RelativePath));
        Assert.Equal(["folder"], plan.Directories.Select(f => f.RelativePath));
        Assert.Equal(1, plan.TotalBytes);
    }

    [Fact]
    public void Folder_entries_naming_the_root_are_ignored_without_warnings()
    {
        var plan = ExtractionPlanner.Plan([Dir("."), Dir("./"), Dir("/"), File("./a.txt"), File(".")]);
        Assert.Equal(3, plan.Entries.Count(e => e.Action == PlannedAction.Ignore));
        Assert.Equal(["a.txt"], plan.Files.Select(f => f.RelativePath));
        Assert.Single(plan.Skipped); // a *file* called "." is still reported
    }

    [Fact]
    public void Plan_is_deterministic()
    {
        ArchiveItem[] items = [File("b"), File("B"), Dir("x/"), File("X"), File("../y")];
        var a = ExtractionPlanner.Plan(items);
        var b = ExtractionPlanner.Plan(items);
        Assert.Equal(a.Entries.Select(e => (e.Action, e.RelativePath)), b.Entries.Select(e => (e.Action, e.RelativePath)));
    }
}

public class ArchiveFolderTests
{
    [Fact]
    public void Folders_implied_by_file_paths_are_created()
    {
        var root = ArchiveFolder.Build([
            new ArchiveItem("docs/nested/data.bin", false, 10, 5, null, false),
            new ArchiveItem("docs/readme.txt", false, 5, 5, null, false),
            new ArchiveItem("top.txt", false, 1, 1, null, false),
            new ArchiveItem("empty/", true, 0, 0, null, false),
        ]);
        Assert.Equal(["docs", "empty"], root.Folders.Select(f => f.Name));
        Assert.Equal(["top.txt"], root.Files.Select(f => f.Name));
        Assert.Equal(16, root.TotalSize);
        Assert.Equal(3, root.TotalFileCount);
        var nested = root.Find("docs/nested");
        Assert.NotNull(nested);
        Assert.Equal("docs/nested", nested.Path);
        Assert.Equal("docs", nested.Parent!.Name);
        Assert.Null(root.Find("missing"));
    }

    [Fact]
    public void Dot_segments_do_not_create_folders()
    {
        var root = ArchiveFolder.Build([
            new ArchiveItem(".", true, 0, 0, null, false),
            new ArchiveItem("./stone/albedo.png", false, 5, 5, null, false),
        ]);
        Assert.Equal(["stone"], root.Folders.Select(f => f.Name));
        Assert.Empty(root.Files);
    }
}
