using System.Runtime.InteropServices;
using System.Security;
using Mendrain.Core;
using Microsoft.Win32;

namespace Mendrain.Helpers;

/// <summary>
/// The "Mendrain" submenu in File Explorer's right-click menu for .zip, .rar and .7z files.
/// Registered per user as static verbs under HKCU\Software\Classes\SystemFileAssociations\&lt;ext&gt;\shell\Mendrain,
/// which adds menu entries without touching the default app for those file types. On Windows 11 the
/// entries appear under "Show more options" (a top-level Windows 11 entry needs a packaged, signed app).
/// </summary>
public static class ShellMenu
{
    public const string RegisterSwitch = "--register-shell-menu";
    public const string UnregisterSwitch = "--unregister-shell-menu";

    private static readonly string[] Extensions = [".zip", ".rar", ".7z"];

    // Key names sort the submenu; labels follow the app's own wording.
    private static readonly (string Key, string Label, ShellAction Action)[] Verbs =
    [
        ("1open", "Open with Mendrain", ShellAction.Open),
        ("2extracthere", "Extract Here", ShellAction.ExtractHere),
        ("3extractfolder", "Extract to Folder", ShellAction.ExtractToFolder),
        ("4batch", "Batch Extract", ShellAction.Batch),
    ];

    private static string KeyPath(string extension) => $@"Software\Classes\SystemFileAssociations\{extension}\shell\Mendrain";

    /// <summary>Handles the installer's register/unregister switches. Returns the exit code, or null for a normal start.</summary>
    public static int? HandleCommandLine(IReadOnlyList<string> args)
    {
        if (args.Contains(RegisterSwitch, StringComparer.OrdinalIgnoreCase))
            return Register(Environment.ProcessPath!) ? 0 : 1;
        if (args.Contains(UnregisterSwitch, StringComparer.OrdinalIgnoreCase))
            return Unregister() ? 0 : 1;
        return null;
    }

    /// <summary>Adds (or repairs) the menu so it starts <paramref name="exePath"/>. Replaces Mendrain's previous entries, so upgrades never duplicate them.</summary>
    public static bool Register(string exePath)
    {
        try
        {
            RemoveKeys();
            foreach (var extension in Extensions)
            {
                using var menu = Registry.CurrentUser.CreateSubKey(KeyPath(extension));
                menu.SetValue("MUIVerb", "Mendrain");
                menu.SetValue("Icon", $"\"{exePath}\",0");
                // An empty SubCommands value makes this a submenu built from its own "shell" subkey.
                menu.SetValue("SubCommands", "");
                // "Player" keeps the menu available when more than 15 files are selected.
                menu.SetValue("MultiSelectModel", "Player");
                foreach (var (key, label, action) in Verbs)
                {
                    using var verb = menu.CreateSubKey($@"shell\{key}");
                    verb.SetValue("MUIVerb", label);
                    verb.SetValue("MultiSelectModel", "Player");
                    using var command = verb.CreateSubKey("command");
                    command.SetValue("", ShellCommand.CommandLine(exePath, action));
                }
            }
            NotifyExplorer();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            App.Log("Could not register the Explorer menu: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// After an update: rewrites the entries if they point at this program (or at one that no longer exists),
    /// so they stay correct. A menu the user removed, or one that belongs to another copy, is left alone.
    /// </summary>
    public static void RepairAfterUpdate(string exePath)
    {
        if (RegisteredExecutable() is { } registered
            && (string.Equals(registered, exePath, StringComparison.OrdinalIgnoreCase) || !File.Exists(registered)))
            Register(exePath);
    }

    /// <summary>Removes Mendrain's own menu entries, and nothing else.</summary>
    public static bool Unregister()
    {
        try
        {
            RemoveKeys();
            NotifyExplorer();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            App.Log("Could not remove the Explorer menu: " + ex.Message);
            return false;
        }
    }

    /// <summary>The Mendrain.exe the menu starts, or null when the menu is not registered.</summary>
    public static string? RegisteredExecutable()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{KeyPath(".zip")}\shell\{Verbs[0].Key}\command");
        if (key?.GetValue("") is not string command || !command.StartsWith('"'))
            return null;
        var end = command.IndexOf('"', 1);
        return end > 1 ? command[1..end] : null;
    }

    private static void RemoveKeys()
    {
        foreach (var extension in Extensions)
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath(extension), throwOnMissingSubKey: false);
    }

    private static void NotifyExplorer() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, 0, 0);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
