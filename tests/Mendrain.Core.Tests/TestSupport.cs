using System.Diagnostics;
using System.IO.Compression;

namespace Mendrain.Core.Tests;

/// <summary>A unique temporary directory removed after the test.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mendrain-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>All files and folders below the directory, relative, sorted.</summary>
    public IReadOnlyList<string> Snapshot() =>
        Directory.EnumerateFileSystemEntries(Path, "*", SearchOption.AllDirectories)
            .Select(p => System.IO.Path.GetRelativePath(Path, p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void Dispose()
    {
        try
        {
            // Remove junctions first so deleting never recurses into their targets.
            foreach (var dir in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories).ToList())
            {
                if (Directory.Exists(dir) && new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    Directory.Delete(dir);
            }
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}

public static class Fixtures
{
    public const string Password = "secret";

    public static string Get(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static readonly byte[] DataBin = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7 % 251)).ToArray();
    public const string TopTxt = "top level file\n";
    public static readonly string ReadmeTxt = string.Concat(Enumerable.Repeat("Mendrain fixture readme\n", 50));

    /// <summary>Asserts that <paramref name="root"/> holds the standard fixture content.</summary>
    public static void AssertSampleContent(string root)
    {
        Assert.Equal(TopTxt, File.ReadAllText(Path.Combine(root, "top.txt")));
        Assert.Equal(ReadmeTxt, File.ReadAllText(Path.Combine(root, "docs", "readme.txt")));
        Assert.Equal(DataBin, File.ReadAllBytes(Path.Combine(root, "docs", "nested", "data.bin")));
    }

    /// <summary>Builds a ZIP whose entry names (and optional Unix modes) are arbitrary, hostile ones included.</summary>
    public static string MakeZip(string path, params (string Name, string Content, int? UnixMode)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content, mode) in entries)
        {
            var entry = zip.CreateEntry(name);
            if (mode is { } m)
                entry.ExternalAttributes = m << 16;
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return path;
    }

    public static void CreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link), "Could not create the junction used by this test.");
    }
}
