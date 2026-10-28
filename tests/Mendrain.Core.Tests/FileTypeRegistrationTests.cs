using System.Runtime.InteropServices;

namespace Mendrain.Core.Tests;

public class FileTypeRegistrationTests
{
    private const string Exe = @"C:\Users\Ünï Cödé\AppData\Local\Mendrain App\current\Mendrain.exe";

    [Fact]
    public void Registers_own_progids_for_zip_rar_and_7z()
    {
        var entries = FileTypeRegistration.Entries(Exe);
        foreach (var extension in new[] { ".zip", ".rar", ".7z" })
        {
            var progId = FileTypeRegistration.ProgId(extension);
            Assert.StartsWith("MendrainApp.", progId);
            Assert.Contains(entries, e => e.Key == $@"Software\Classes\{progId}\shell\open\command"
                                          && e.Data == $"\"{Exe}\" --shell open \"%1\"");
            Assert.Contains(entries, e => e.Key == $@"Software\Classes\{extension}\OpenWithProgids" && e.Name == progId && e.Data is null);
            Assert.Contains(entries, e => e.Key == $@"{FileTypeRegistration.CapabilitiesKey}\FileAssociations" && e.Name == extension && e.Data == progId);
        }
        Assert.Contains(entries, e => e.Key == @"Software\RegisteredApplications" && e.Name == "Mendrain" && e.Data == FileTypeRegistration.CapabilitiesKey);
    }

    [Fact]
    public void Writes_only_inside_its_own_keys_or_as_its_own_named_values()
    {
        var owned = FileTypeRegistration.OwnedKeys;
        var shared = FileTypeRegistration.SharedValues;
        foreach (var entry in FileTypeRegistration.Entries(Exe))
        {
            var inOwnedKey = owned.Any(k => entry.Key.Equals(k, StringComparison.OrdinalIgnoreCase)
                                            || entry.Key.StartsWith(k + @"\", StringComparison.OrdinalIgnoreCase));
            var isOwnSharedValue = shared.Contains((entry.Key, entry.Name));
            Assert.True(inOwnedKey || isOwnSharedValue, $"{entry.Key} [{entry.Name}] is not Mendrain's own");
        }
        // Unregistering removes exactly these; every one of them is named after Mendrain.
        Assert.All(owned, k => Assert.Contains("Mendrain", k));
        Assert.All(shared, v => Assert.StartsWith("Mendrain", v.Name));
    }

    [Fact]
    public void Never_touches_extension_defaults_userchoice_or_other_programs()
    {
        foreach (var entry in FileTypeRegistration.Entries(Exe))
        {
            Assert.DoesNotContain("UserChoice", entry.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("FileExts", entry.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("WinRAR", entry.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CompressedFolder", entry.Key, StringComparison.OrdinalIgnoreCase);
            foreach (var (extension, _) in FileTypeRegistration.FileTypes)
                Assert.False(entry.Key.Equals($@"Software\Classes\{extension}", StringComparison.OrdinalIgnoreCase),
                    $"writes {entry.Key} [{entry.Name}], which would change the default app for {extension}");
        }
    }

    [Theory]
    [InlineData(@"C:\Users\Ünï Cödé\My Archives\Fotos 2026 – Ürlaub.zip")]
    [InlineData(@"D:\with space\日本語 アーカイブ.7z")]
    [InlineData(@"\\server\share\dir with spaces\a.rar")]
    public void Registered_command_delivers_the_full_path_to_the_open_request(string archive)
    {
        var command = FileTypeRegistration.Entries(Exe).First(e => e.Key.EndsWith(@"\shell\open\command")).Data!;
        // What Explorer runs, split exactly as Windows (and .NET's args) split a command line.
        var argv = CommandLineToArgs(command.Replace("%1", archive));
        Assert.Equal(Exe, argv[0]);
        var request = ShellCommand.Parse(argv[1..]);
        Assert.NotNull(request);
        Assert.Equal(ShellAction.Open, request.Action);
        Assert.Equal([archive], request.Paths);
    }

    [Fact]
    public void Program_path_is_read_back_from_the_command()
    {
        Assert.Equal(Exe, ShellCommand.ProgramOf(ShellCommand.CommandLine(Exe, ShellAction.Open)));
        Assert.Null(ShellCommand.ProgramOf(null));
        Assert.Null(ShellCommand.ProgramOf("Mendrain.exe \"%1\""));
    }

    private static string[] CommandLineToArgs(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        try
        {
            return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray();
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
