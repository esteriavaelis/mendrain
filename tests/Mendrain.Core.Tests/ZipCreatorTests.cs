using System.IO.Compression;
using Mendrain.Core.Models;
using Mendrain.Core.Services;

namespace Mendrain.Core.Tests;

public class ZipCreatorTests
{
    private static void MakeTree(TempDir temp)
    {
        Directory.CreateDirectory(temp.Combine("src", "Photos", "2024"));
        Directory.CreateDirectory(temp.Combine("src", "Photos", "Empty"));
        File.WriteAllText(temp.Combine("src", "Photos", "2024", "a.jpg"), "jpeg-a");
        File.WriteAllText(temp.Combine("src", "Photos", "cover.png"), "png");
        File.WriteAllText(temp.Combine("src", "notes.txt"), "notes");
    }

    [Fact]
    public void Creates_zip_with_selection_rules_and_round_trips()
    {
        using var temp = new TempDir();
        MakeTree(temp);
        var output = temp.Combine("out.zip");

        var result = new ZipCreator().Create(new ZipRequest([temp.Combine("src", "Photos"), temp.Combine("src", "notes.txt")], output));

        using (var zip = ZipFile.OpenRead(output))
        {
            Assert.Equal(["Photos/2024/a.jpg", "Photos/Empty/", "Photos/cover.png", "notes.txt"],
                zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }
        Assert.Equal(3, result.FilesAdded);
        Assert.Equal(1, result.FoldersAdded);
        Assert.Equal(14, result.SourceBytes);

        var dest = temp.Combine("roundtrip");
        new ArchiveService().Extract(new ExtractRequest(output, dest));
        Assert.Equal("jpeg-a", File.ReadAllText(Path.Combine(dest, "Photos", "2024", "a.jpg")));
        Assert.True(Directory.Exists(Path.Combine(dest, "Photos", "Empty")));
        Assert.DoesNotContain(temp.Snapshot(), p => p.EndsWith(".partial"));
    }

    [Fact]
    public void Duplicate_root_names_are_refused()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("x"));
        Directory.CreateDirectory(temp.Combine("y"));
        File.WriteAllText(temp.Combine("x", "same.txt"), "1");
        File.WriteAllText(temp.Combine("y", "SAME.txt"), "2");
        var error = Assert.Throws<ArchiveError>(() =>
            new ZipCreator().Create(new ZipRequest([temp.Combine("x", "same.txt"), temp.Combine("y", "SAME.txt")], temp.Combine("o.zip"))));
        Assert.Equal(ArchiveErrorKind.InvalidInput, error.Kind);
        Assert.False(File.Exists(temp.Combine("o.zip")));
    }

    [Fact]
    public void Existing_output_is_not_replaced_without_consent()
    {
        using var temp = new TempDir();
        MakeTree(temp);
        File.WriteAllText(temp.Combine("out.zip"), "precious");
        var request = new ZipRequest([temp.Combine("src", "notes.txt")], temp.Combine("out.zip"));

        var error = Assert.Throws<ArchiveError>(() => new ZipCreator().Create(request));
        Assert.Equal(ArchiveErrorKind.DestinationExists, error.Kind);
        Assert.Equal("precious", File.ReadAllText(temp.Combine("out.zip")));

        new ZipCreator().Create(request with { OverwriteExisting = true });
        using var zip = ZipFile.OpenRead(temp.Combine("out.zip"));
        Assert.Single(zip.Entries);
    }

    [Fact]
    public void Output_inside_selected_folder_is_not_added_to_itself()
    {
        using var temp = new TempDir();
        MakeTree(temp);
        var output = temp.Combine("src", "Photos", "photos.zip");
        new ZipCreator().Create(new ZipRequest([temp.Combine("src", "Photos")], output));
        using var zip = ZipFile.OpenRead(output);
        Assert.DoesNotContain(zip.Entries, e => e.Name == "photos.zip" || e.Name.EndsWith(".partial"));
    }

    [Fact]
    public void Junctions_in_selected_folders_are_not_followed()
    {
        using var temp = new TempDir();
        MakeTree(temp);
        Fixtures.CreateJunction(temp.Combine("src", "Photos", "loop"), temp.Combine("src"));
        var plan = new ZipCreator().Plan([temp.Combine("src", "Photos")], temp.Combine("o.zip"));
        Assert.DoesNotContain(plan.Items, i => i.EntryName.Contains("loop"));
        Assert.Single(plan.Skipped);
    }

    [Fact]
    public void Cancellation_removes_only_the_temporary_file()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("src"));
        File.WriteAllBytes(temp.Combine("src", "big.bin"), new byte[32 << 20]);
        var before = temp.Snapshot();
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<ExtractProgress>(p => { if (p.BytesDone > 0) cts.Cancel(); });

        var error = Assert.Throws<ArchiveError>(() =>
            new ZipCreator().Create(new ZipRequest([temp.Combine("src")], temp.Combine("out.zip"), CompressionLevel.NoCompression), progress, cts.Token));

        Assert.Equal(ArchiveErrorKind.Cancelled, error.Kind);
        Assert.Equal(before, temp.Snapshot());
    }

    [Fact]
    public void Missing_source_and_empty_selection_are_reported()
    {
        using var temp = new TempDir();
        Assert.Equal(ArchiveErrorKind.InvalidInput,
            Assert.Throws<ArchiveError>(() => new ZipCreator().Create(new ZipRequest([], temp.Combine("o.zip")))).Kind);
        Assert.Equal(ArchiveErrorKind.NotFound,
            Assert.Throws<ArchiveError>(() => new ZipCreator().Create(new ZipRequest([temp.Combine("nope.txt")], temp.Combine("o.zip")))).Kind);
        Assert.Equal(ArchiveErrorKind.NotFound,
            Assert.Throws<ArchiveError>(() => new ZipCreator().Create(new ZipRequest([temp.Path], temp.Combine("missing", "o.zip")))).Kind);
    }

    [Fact]
    public void Locked_source_file_fails_cleanly()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("src"));
        var locked = temp.Combine("src", "locked.txt");
        File.WriteAllText(locked, "x");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var error = Assert.Throws<ArchiveError>(() => new ZipCreator().Create(new ZipRequest([temp.Combine("src")], temp.Combine("o.zip"))));

        Assert.Equal(ArchiveErrorKind.IO, error.Kind);
        Assert.Contains("locked.txt", error.Message);
        Assert.False(File.Exists(temp.Combine("o.zip")));
        Assert.DoesNotContain(temp.Snapshot(), p => p.EndsWith(".partial"));
    }
}

public class RecentArchivesTests
{
    [Fact]
    public void Keeps_most_recent_first_without_duplicates_and_caps_size()
    {
        using var temp = new TempDir();
        var store = new RecentArchives(temp.Combine("sub", "recent.json"), capacity: 3);
        store.Add(@"C:\a.zip");
        store.Add(@"C:\b.zip");
        store.Add(@"C:\A.ZIP");
        store.Add(@"C:\c.zip");
        var list = store.Add(@"C:\d.zip");
        Assert.Equal([@"C:\d.zip", @"C:\c.zip", @"C:\A.ZIP"], list);
        Assert.Equal(list, new RecentArchives(temp.Combine("sub", "recent.json"), 3).Load());
    }

    [Fact]
    public void Corrupt_store_loads_as_empty()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("recent.json"), "{not json");
        Assert.Empty(new RecentArchives(temp.Combine("recent.json")).Load());
    }
}
