using System.Diagnostics;
using Microsoft.Win32;

namespace Mendrain.Helpers;

/// <summary>
/// Mendrain 0.1.0 shipped with an Inno Setup installer (in %LOCALAPPDATA%\Programs\Mendrain), which cannot update
/// itself. After installing the current version, the old copy can be removed with its own uninstaller.
/// Its uninstaller removes only its program files and shortcuts; data in %LOCALAPPDATA%\Mendrain is kept.
/// </summary>
public static class LegacyInstall
{
    // AppId of installer/Mendrain.iss (0.1.0), as Inno Setup registers it.
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6B0E2F3A-8C4D-4E1B-9A57-3F2D8E6C1A90}_is1";

    /// <summary>The old installation's uninstaller command, or null if there is none.</summary>
    public static string? FindUninstaller()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(UninstallKey);
                if (key?.GetValue("UninstallString") is string command && ExePath(command) is { } exe && File.Exists(exe))
                    return exe;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }
        return null;
    }

    /// <summary>Runs the old uninstaller (it shows its own progress and may ask for permission). Returns true when it is gone.</summary>
    public static async Task<bool> RemoveAsync()
    {
        if (FindUninstaller() is not { } uninstaller)
            return true;
        var menuWasOurs = ShellMenu.RegisteredExecutable() is { } registered
                          && string.Equals(registered, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Mendrain.lnk");
        var shortcutBackup = File.Exists(shortcut) ? File.ReadAllBytes(shortcut) : null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(uninstaller, "/SILENT /NORESTART /SUPPRESSMSGBOXES") { UseShellExecute = true });
            if (process is not null)
                await process.WaitForExitAsync();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            App.Log("Removing the old installation failed: " + ex.Message);
            return false;
        }

        // Both installers use the Start menu shortcut name "Mendrain", so the old uninstaller removes the current one too.
        if (shortcutBackup is not null && !File.Exists(shortcut))
        {
            try { File.WriteAllBytes(shortcut, shortcutBackup); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Log("Could not restore the Start menu shortcut: " + ex.Message); }
        }
        if (menuWasOurs && ShellMenu.RegisteredExecutable() is null)
            ShellMenu.Register(Environment.ProcessPath!);
        return FindUninstaller() is null;
    }

    private static string? ExePath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }
}
