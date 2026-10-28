using System.Diagnostics;
using System.Security;
using Mendrain.Core;
using Microsoft.Win32;

namespace Mendrain.Helpers;

/// <summary>
/// Registers Mendrain per user as an app that can open .zip, .rar and .7z files, so it is listed in "Open with"
/// and in Windows' Default apps. See <see cref="FileTypeRegistration"/> for the exact keys. Registration never
/// changes the default app: Windows protects that choice (UserChoice), and only the user can make it.
/// </summary>
public static class FileAssociations
{
    private const string UserChoiceKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{0}\UserChoice";

    /// <summary>Handles the register/unregister switches. Returns the exit code, or null for a normal start.</summary>
    public static int? HandleCommandLine(IReadOnlyList<string> args)
    {
        if (args.Contains(FileTypeRegistration.RegisterSwitch, StringComparer.OrdinalIgnoreCase))
            return Register(Environment.ProcessPath!) ? 0 : 1;
        if (args.Contains(FileTypeRegistration.UnregisterSwitch, StringComparer.OrdinalIgnoreCase))
            return Unregister() ? 0 : 1;
        return null;
    }

    /// <summary>Adds (or repairs) the registration for <paramref name="exePath"/>, replacing Mendrain's previous one.</summary>
    public static bool Register(string exePath)
    {
        try
        {
            RemoveEntries();
            foreach (var entry in FileTypeRegistration.Entries(exePath))
            {
                using var key = Registry.CurrentUser.CreateSubKey(entry.Key);
                if (entry.Data is null)
                    key.SetValue(entry.Name, Array.Empty<byte>(), RegistryValueKind.None);
                else
                    key.SetValue(entry.Name, entry.Data);
            }
            ShellMenu.NotifyExplorer();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            App.Log("Could not register the file types: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// After an update: rewrites the registration if it points at this program (or at one that no longer exists).
    /// A registration the user removed, or one that belongs to another copy, is left alone.
    /// </summary>
    public static void RepairAfterUpdate(string exePath)
    {
        if (RegisteredExecutable() is { } registered
            && (string.Equals(registered, exePath, StringComparison.OrdinalIgnoreCase) || !File.Exists(registered)))
            Register(exePath);
    }

    /// <summary>Removes Mendrain's own keys and values, and nothing else. Defaults chosen by the user are not touched.</summary>
    public static bool Unregister()
    {
        try
        {
            RemoveEntries();
            ShellMenu.NotifyExplorer();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            App.Log("Could not remove the file types: " + ex.Message);
            return false;
        }
    }

    /// <summary>The Mendrain.exe the registration starts, or null when it is not registered.</summary>
    public static string? RegisteredExecutable()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{FileTypeRegistration.ProgIdKey(".zip")}\shell\open\command");
        return ShellCommand.ProgramOf(key?.GetValue("") as string);
    }

    /// <summary>The extensions (".zip", …) whose default app the user has set to Mendrain. Only reads Windows' choice.</summary>
    public static IReadOnlyList<string> DefaultFor()
    {
        var result = new List<string>();
        foreach (var (extension, _) in FileTypeRegistration.FileTypes)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(string.Format(UserChoiceKey, extension));
                var progId = key?.GetValue("ProgId") as string;
                if (string.Equals(progId, FileTypeRegistration.ProgId(extension), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(progId, @"Applications\Mendrain.exe", StringComparison.OrdinalIgnoreCase))
                    result.Add(extension);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
            }
        }
        return result;
    }

    /// <summary>
    /// Opens Windows' Default apps settings. On Windows 11 the page for Mendrain itself (one button per file type);
    /// Windows 10 has no such link, so it opens the Default apps page.
    /// </summary>
    public static bool OpenDefaultAppsSettings()
    {
        var uri = IsWindows11 ? $"ms-settings:defaultapps?registeredAppUser={FileTypeRegistration.AppName}" : "ms-settings:defaultapps";
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            App.Log("Could not open Default apps: " + ex.Message);
            return false;
        }
    }

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    private static void RemoveEntries()
    {
        foreach (var key in FileTypeRegistration.OwnedKeys)
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        foreach (var (path, name) in FileTypeRegistration.SharedValues)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(path, writable: true))
                key?.DeleteValue(name, throwOnMissingValue: false);
        }
        // Registering may have created these keys; delete them again only if nothing else is in them.
        foreach (var (extension, _) in FileTypeRegistration.FileTypes)
        {
            DeleteIfEmpty(FileTypeRegistration.OpenWithProgidsKey(extension));
            DeleteIfEmpty($@"Software\Classes\{extension}");
        }
        DeleteIfEmpty(Path.GetDirectoryName(FileTypeRegistration.CapabilitiesKey)!);
    }

    private static void DeleteIfEmpty(string path)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(path))
        {
            if (key is null || key.ValueCount > 0 || key.SubKeyCount > 0)
                return;
        }
        Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
    }
}
