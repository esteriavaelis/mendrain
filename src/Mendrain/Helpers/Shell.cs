using System.Diagnostics;

namespace Mendrain.Helpers;

public static class Shell
{
    /// <summary>Opens a folder in File Explorer.</summary>
    public static void OpenFolder(string folder)
    {
        if (Directory.Exists(folder))
            Start("explorer.exe", $"\"{folder}\"");
    }

    /// <summary>Opens File Explorer with <paramref name="file"/> selected.</summary>
    public static void ShowFile(string file)
    {
        if (File.Exists(file))
            Start("explorer.exe", $"/select,\"{file}\"");
        else if (Path.GetDirectoryName(file) is { } dir)
            OpenFolder(dir);
    }

    private static void Start(string exe, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"Could not start {exe}: {ex.Message}");
        }
    }
}
