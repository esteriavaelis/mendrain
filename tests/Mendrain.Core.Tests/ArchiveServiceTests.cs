using System.IO.Compression;
using Mendrain.Core.Models;
using Mendrain.Core.Services;

namespace Mendrain.Core.Tests;

public class ArchiveServiceTests
{
    private readonly ArchiveService _service = new();

    [Theory]
    [InlineData("sample.zip", "ZIP")]
    [InlineData("sample.7z", "7Z")]
    [InlineData("solid.7z", "7Z")]
    [InlineData("sample.rar", "RAR")]
    [InlineData("solid.rar", "RAR")]
    [InlineData("volume.part1.rar", "RAR")]
    public void Opens_and_lists_supported_formats(string fixture, string format)
    {
        var contents = _service.Open(Fixtures.Get(fixture));
        Assert.Equal(format, contents.Format);
        Assert.Equal(3, contents.FileCount);
        Assert.Equal(201_215, contents.TotalSize);
        Assert.False(contents.NeedsPassword);
        Assert.Contains(contents.Items, i => i.Path == "docs/nested/data.bin" && i.Size == 200_000);
    }

    [Fact]
    public void Multi_volume_rar_reports_its_parts() =>
        Assert.Equal(4, _service.Open(Fixtures.Get("volume.part1.rar")).VolumeCount);

    [Theory]
    [InlineData("sample.zip")]
    [InlineData("sample.7z")]
    [InlineData("solid.7z")]
    [InlineData("sample.rar")]
    [InlineData("solid.rar")]
    [InlineData("volume.part1.rar")]
    public void Extracts_with_structure_and_exact_content(string fixture)
    {
        using var temp = new TempDir();
        var dest = temp.Combine("out");
        var result = _service.Extract(new ExtractRequest(Fixtures.Get(fixture), dest));

        Fixtures.AssertSampleContent(dest);
        Assert.True(Directory.Exists(Path.Combine(dest, "empty")), "empty folder should be recreated");
        Assert.Equal(3, result.FilesWritten);
        Assert.Equal(201_215, result.BytesWritten);
        Assert.False(result.HasWarnings);
        Assert.Equal(["out"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("encrypted.rar")]
    [InlineData("encrypted.7z")]
    [InlineData("encrypted-aes.zip")]
    [InlineData("encrypted-zipcrypto.zip")]
    [InlineData("encrypted-headers.rar")]
    [InlineData("encrypted-headers.7z")]
    public void Encrypted_archives_require_and_accept_the_password(string fixture)
    {
        using var temp = new TempDir();
        var dest = temp.Combine("out");

        var missing = Assert.Throws<ArchiveError>(() => _service.Extract(new ExtractRequest(Fixtures.Get(fixture), dest)));
        Assert.Equal(ArchiveErrorKind.PasswordRequired, missing.Kind);

        var wrong = Assert.Throws<ArchiveError>(() => _service.Extract(new ExtractRequest(Fixtures.Get(fixture), dest, "wrong")));
        Assert.Equal(ArchiveErrorKind.WrongPassword, wrong.Kind);
        Assert.Empty(temp.Snapshot());

        _service.Extract(new ExtractRequest(Fixtures.Get(fixture), dest, Fixtures.Password));
        Fixtures.AssertSampleContent(dest);
    }

    [Theory]
    [InlineData("encrypted-headers.rar")]
    [InlineData("encrypted-headers.7z")]
    public void Encrypted_headers_need_password_to_list(string fixture)
    {
        var error = Assert.Throws<ArchiveError>(() => _service.Open(Fixtures.Get(fixture)));
        Assert.Equal(ArchiveErrorKind.PasswordRequired, error.Kind);
        var contents = _service.Open(Fixtures.Get(fixture), Fixtures.Password);
        Assert.True(contents.HeadersEncrypted);
        Assert.Equal(3, contents.FileCount);
    }

    [Fact]
    public void Data_encrypted_archive_lists_without_password()
    {
        var contents = _service.Open(Fixtures.Get("encrypted.rar"));
        Assert.True(contents.HasEncryptedEntries);
        Assert.False(contents.HeadersEncrypted);
        Assert.True(contents.NeedsPassword);
    }

    [Theory]
    [InlineData("corrupt.zip", ArchiveErrorKind.Corrupt)]
    [InlineData("corrupt.7z", ArchiveErrorKind.Corrupt)]
    [InlineData("corrupt.rar", ArchiveErrorKind.Corrupt)]
    [InlineData("truncated.zip", ArchiveErrorKind.NotAnArchive)]
    [InlineData("not-an-archive.zip", ArchiveErrorKind.NotAnArchive)]
    public void Damaged_archives_fail_cleanly_without_leftovers(string fixture, ArchiveErrorKind kind)
    {
        using var temp = new TempDir();
        var error = Assert.Throws<ArchiveError>(() => _service.Extract(new ExtractRequest(Fixtures.Get(fixture), temp.Combine("out"))));
        Assert.Equal(kind, error.Kind);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Empty(temp.Snapshot()); // neither the destination nor the staging folder remains
    }

    [Fact]
    public void Secondary_volume_is_refused_with_guidance()
    {
        var error = Assert.Throws<ArchiveError>(() => _service.Open(Fixtures.Get("volume.part2.rar")));
        Assert.Equal(ArchiveErrorKind.InvalidInput, error.Kind);
        Assert.Contains("part1", error.Message);
    }

    [Fact]
    public void Missing_volume_fails_without_leftovers()
    {
        using var temp = new TempDir();
        var src = temp.Combine("src");
        Directory.CreateDirectory(src);
        foreach (var part in new[] { "volume.part1.rar", "volume.part2.rar", "volume.part4.rar" })
            File.Copy(Fixtures.Get(part), Path.Combine(src, part));

        var error = Assert.ThrowsAny<ArchiveError>(() =>
            _service.Extract(new ExtractRequest(Path.Combine(src, "volume.part1.rar"), temp.Combine("out"))));
        Assert.Contains(error.Kind, new[] { ArchiveErrorKind.MissingVolumes, ArchiveErrorKind.Corrupt });
        Assert.False(Directory.Exists(temp.Combine("out")));
        Assert.Equal(["src"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void CreateNew_refuses_existing_folder_and_leaves_it_untouched()
    {
        using var temp = new TempDir();
        var dest = temp.Combine("out");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "top.txt"), "mine");

        var error = Assert.Throws<ArchiveError>(() => _service.Extract(new ExtractRequest(Fixtures.Get("sample.zip"), dest)));
        Assert.Equal(ArchiveErrorKind.DestinationExists, error.Kind);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(dest, "top.txt")));
        Assert.Equal(["out", @"out\top.txt"], temp.Snapshot());
    }

    [Fact]
    public void FindExistingFiles_reports_conflicts()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("docs"));
        File.WriteAllText(temp.Combine("top.txt"), "mine");
        File.WriteAllText(temp.Combine("docs", "readme.txt"), "mine");
        var contents = _service.Open(Fixtures.Get("sample.zip"));
        Assert.Equal([@"docs\readme.txt", "top.txt"], _service.FindExistingFiles(contents, temp.Path).Order());
        Assert.Empty(_service.FindExistingFiles(contents, temp.Combine("nope")));
    }

    [Fact]
    public void Merge_with_skip_keeps_existing_files()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("top.txt"), "mine");
        var result = _service.Extract(new ExtractRequest(Fixtures.Get("sample.zip"), temp.Path, Mode: DestinationMode.MergeIntoExisting,
            ExistingFiles: ExistingFilePolicy.Skip));
        Assert.Equal("mine", File.ReadAllText(temp.Combine("top.txt")));
        Assert.Equal(1, result.ExistingFilesSkipped);
        Assert.Equal(2, result.FilesWritten);
        Assert.Equal(Fixtures.DataBin, File.ReadAllBytes(temp.Combine("docs", "nested", "data.bin")));
        Assert.DoesNotContain(temp.Snapshot(), p => p.Contains(".mendrain-"));
    }

    [Fact]
    public void Merge_with_keep_both_writes_renamed_copy()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("top.txt"), "mine");
        var result = _service.Extract(new ExtractRequest(Fixtures.Get("sample.zip"), temp.Path, Mode: DestinationMode.MergeIntoExisting,
            ExistingFiles: ExistingFilePolicy.KeepBoth));
        Assert.Equal("mine", File.ReadAllText(temp.Combine("top.txt")));
        Assert.Equal(Fixtures.TopTxt, File.ReadAllText(temp.Combine("top (2).txt")));
        Assert.Contains(result.RenamedEntries, r => r.WrittenAs == "top (2).txt");
    }

    [Fact]
    public void Merge_with_overwrite_replaces_files_but_never_folders()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("top.txt"), "mine");
        Directory.CreateDirectory(temp.Combine("docs", "readme.txt")); // a folder where the archive has a file
        File.WriteAllText(temp.Combine("docs", "readme.txt", "keep.me"), "x");

        var result = _service.Extract(new ExtractRequest(Fixtures.Get("sample.zip"), temp.Path, Mode: DestinationMode.MergeIntoExisting,
            ExistingFiles: ExistingFilePolicy.Overwrite));

        Assert.Equal(Fixtures.TopTxt, File.ReadAllText(temp.Combine("top.txt")));
        Assert.Equal(1, result.ExistingFilesOverwritten);
        Assert.Equal("x", File.ReadAllText(temp.Combine("docs", "readme.txt", "keep.me")));
        Assert.Equal(Fixtures.ReadmeTxt, File.ReadAllText(temp.Combine("docs", "readme (2).txt")));
    }

    [Fact]
    public void Merge_never_writes_through_an_existing_junction()
    {
        using var temp = new TempDir();
        var outside = temp.Combine("outside");
        var dest = temp.Combine("dest");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(dest);
        Fixtures.CreateJunction(Path.Combine(dest, "docs"), outside);

        var result = _service.Extract(new ExtractRequest(Fixtures.Get("sample.zip"), dest, Mode: DestinationMode.MergeIntoExisting));

        Assert.Empty(Directory.GetFileSystemEntries(outside));
        Assert.Equal(2, result.SkippedEntries.Count(s => s.Reason.Contains("link")));
        Assert.True(File.Exists(Path.Combine(dest, "top.txt")));
    }

    [Fact]
    public void Write_protected_destination_reports_access_denied()
    {
        using var temp = new TempDir();
        var locked = temp.Combine("locked");
        Directory.CreateDirectory(locked);
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        RunIcacls($"\"{locked}\" /deny \"{user}\":(WD,AD)");
        try
        {
            var error = Assert.Throws<ArchiveError>(() =>
                _service.Extract(new ExtractRequest(Fixtures.Get("sample.zip"), Path.Combine(locked, "out"))));
            Assert.Equal(ArchiveErrorKind.AccessDenied, error.Kind);
            Assert.Empty(Directory.GetFileSystemEntries(locked));
        }
        finally
        {
            RunIcacls($"\"{locked}\" /remove:d \"{user}\"");
        }
    }

    private static void RunIcacls(string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls.exe", args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public void Precancelled_token_writes_nothing()
    {
        using var temp = new TempDir();
        var error = Assert.Throws<ArchiveError>(() =>
            _service.Extract(new ExtractRequest(Fixtures.Get("sample.zip"), temp.Combine("out")), null, new CancellationToken(true)));
        Assert.Equal(ArchiveErrorKind.Cancelled, error.Kind);
        Assert.Empty(temp.Snapshot());
    }

    [Fact]
    public void Cancelling_mid_extraction_removes_staging_and_keeps_existing_data()
    {
        using var temp = new TempDir();
        var archive = temp.Combine("big.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            for (var f = 0; f < 4; f++)
            {
                using var s = zip.CreateEntry($"part{f}.bin", CompressionLevel.NoCompression).Open();
                var block = new byte[1 << 20];
                for (var i = 0; i < 8; i++) s.Write(block);
            }
        }
        var existing = temp.Combine("existing.txt");
        File.WriteAllText(existing, "user data");
        var before = temp.Snapshot();

        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<ExtractProgress>(p => { if (p.BytesDone > 0) cts.Cancel(); });
        var error = Assert.Throws<ArchiveError>(() =>
            _service.Extract(new ExtractRequest(archive, temp.Combine("out")), progress, cts.Token));

        Assert.Equal(ArchiveErrorKind.Cancelled, error.Kind);
        Assert.Equal(before, temp.Snapshot());
        Assert.Equal("user data", File.ReadAllText(existing));
    }

    [Fact]
    public void Progress_is_reported_with_known_total()
    {
        using var temp = new TempDir();
        var reports = new List<ExtractProgress>();
        _service.Extract(new ExtractRequest(Fixtures.Get("sample.7z"), temp.Combine("out")), new SyncProgress<ExtractProgress>(reports.Add));
        var last = reports.Last();
        Assert.Equal(201_215, last.BytesTotal);
        Assert.Equal(201_215, last.BytesDone);
        Assert.Equal(1.0, last.Fraction);
        Assert.Equal(3, last.FilesDone);
    }

    [Fact]
    public void Large_entry_streams_with_bounded_memory()
    {
        using var temp = new TempDir();
        var archive = temp.Combine("large.zip");
        const long size = 512L << 20; // 512 MiB that compresses to well under 1 MiB
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var s = zip.CreateEntry("big.bin", CompressionLevel.Optimal).Open())
        {
            var block = new byte[1 << 20];
            for (long i = 0; i < size; i += block.Length) s.Write(block);
        }

        GC.Collect();
        long peak = 0;
        var progress = new SyncProgress<ExtractProgress>(_ => peak = Math.Max(peak, GC.GetTotalMemory(false)));
        _service.Extract(new ExtractRequest(archive, temp.Combine("out")), progress);

        Assert.Equal(size, new FileInfo(temp.Combine("out", "big.bin")).Length);
        Assert.True(peak < 128L << 20, $"Managed heap peaked at {peak / (1 << 20)} MiB");
    }
}

public class MaliciousArchiveTests
{
    private const int UnixRegular = 0x81A4;   // -rw-r--r--
    private const int UnixSymlink = 0xA1FF;   // lrwxrwxrwx
    private const int UnixFifo = 0x11A4;      // prw-r--r--

    [Fact]
    public void Hostile_zip_cannot_write_outside_destination()
    {
        using var temp = new TempDir();
        var archive = Fixtures.MakeZip(temp.Combine("evil.zip"),
            ("../evil1.txt", "x", null),
            (@"..\evil2.txt", "x", null),
            ("sub/../../evil3.txt", "x", null),
            ("/evil4.txt", "x", null),
            ("C:/evil5.txt", "x", null),
            ("C:evil6.txt", "x", null),
            (@"\\server\share\evil7.txt", "x", null),
            ("good/ok.txt", "ok", UnixRegular),
            ("aux.txt", "reserved", null),
            ("con/inner.txt", "reserved dir", null),
            ("A.txt", "first", null),
            ("a.txt", "second", null),
            ("trailing.txt.", "dot", null),
            ("ads.txt:hidden", "stream", null),
            ("link-to-outside", "../../../outside", UnixSymlink),
            ("pipe", "", UnixFifo));
        var dest = temp.Combine("work", "out");
        Directory.CreateDirectory(temp.Combine("work"));

        var result = new ArchiveService().Extract(new ExtractRequest(archive, dest));

        // Nothing outside the destination: the temp folder holds only the archive and work\out.
        Assert.All(temp.Snapshot(), p => Assert.True(p == "evil.zip" || p == "work" || p.StartsWith(@"work\out"), $"unexpected: {p}"));
        Assert.Equal("ok", File.ReadAllText(Path.Combine(dest, "good", "ok.txt")));
        Assert.Equal("reserved", File.ReadAllText(Path.Combine(dest, "_aux.txt")));
        Assert.Equal("reserved dir", File.ReadAllText(Path.Combine(dest, "_con", "inner.txt")));
        Assert.Equal("first", File.ReadAllText(Path.Combine(dest, "A.txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(dest, "a (2).txt")));
        Assert.Equal("dot", File.ReadAllText(Path.Combine(dest, "trailing.txt")));
        Assert.Equal("stream", File.ReadAllText(Path.Combine(dest, "ads.txt_hidden")));
        Assert.False(File.Exists(Path.Combine(dest, "link-to-outside")));
        Assert.False(File.Exists(Path.Combine(dest, "pipe")));

        Assert.Equal(7, result.SkippedEntries.Count(s => s.Reason.StartsWith("Blocked")));
        Assert.Contains(result.SkippedEntries, s => s.EntryName == "link-to-outside" && s.Reason.Contains("Links"));
        Assert.Contains(result.SkippedEntries, s => s.EntryName == "pipe" && s.Reason.Contains("Special"));
        Assert.Contains(result.RenamedEntries, r => r.WrittenAs == "_aux.txt");
        Assert.True(result.HasWarnings);
    }

    [Fact]
    public void Archive_with_only_unsafe_entries_extracts_nothing_and_reports_why()
    {
        using var temp = new TempDir();
        var archive = Fixtures.MakeZip(temp.Combine("evil.zip"), ("../../x.txt", "x", null));
        var result = new ArchiveService().Extract(new ExtractRequest(archive, temp.Combine("out")));
        Assert.Equal(0, result.FilesWritten);
        Assert.Single(result.SkippedEntries);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Combine("out")));
    }
}
