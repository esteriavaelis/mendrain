namespace Mendrain.Core;

/// <summary>One registry value under HKEY_CURRENT_USER. A null <see cref="Data"/> is an empty REG_NONE value (an OpenWithProgids entry).</summary>
public sealed record RegistryEntry(string Key, string Name, string? Data);

/// <summary>
/// The per-user registration that makes Windows offer Mendrain for .zip, .rar and .7z files (Open with,
/// Default apps) without making it the default. Mendrain writes its own ProgIDs (<c>MendrainApp.zip</c>, …),
/// its own Capabilities key and application key, and adds only named values to the shared
/// <c>OpenWithProgids</c> and <c>RegisteredApplications</c> keys. It never writes an extension's default value
/// (the current owner, e.g. WinRAR), another app's ProgID, or the protected UserChoice keys: choosing Mendrain
/// as the default is always the user's decision in Windows' own UI.
/// </summary>
public static class FileTypeRegistration
{
    public const string RegisterSwitch = "--register-file-types";
    public const string UnregisterSwitch = "--unregister-file-types";

    /// <summary>The name under RegisteredApplications, shown in Default apps (and used by ms-settings:defaultapps?registeredAppUser=).</summary>
    public const string AppName = "Mendrain";
    public const string CapabilitiesKey = @"Software\MendrainApp\Capabilities";
    public const string RegisteredApplicationsKey = @"Software\RegisteredApplications";
    public const string ApplicationKey = @"Software\Classes\Applications\Mendrain.exe";

    public static readonly IReadOnlyList<(string Extension, string TypeName)> FileTypes =
    [
        (".zip", "ZIP archive"),
        (".rar", "RAR archive"),
        (".7z", "7Z archive"),
    ];

    public static string ProgId(string extension) => "MendrainApp" + extension;

    public static string ProgIdKey(string extension) => $@"Software\Classes\{ProgId(extension)}";

    public static string OpenWithProgidsKey(string extension) => $@"Software\Classes\{extension}\OpenWithProgids";

    /// <summary>Keys that belong to Mendrain alone; unregistering deletes them with everything inside.</summary>
    public static IReadOnlyList<string> OwnedKeys =>
        [.. FileTypes.Select(t => ProgIdKey(t.Extension)), CapabilitiesKey, ApplicationKey];

    /// <summary>Mendrain's values inside keys other programs share; unregistering removes only these values.</summary>
    public static IReadOnlyList<(string Key, string Name)> SharedValues =>
        [.. FileTypes.Select(t => (OpenWithProgidsKey(t.Extension), ProgId(t.Extension))), (RegisteredApplicationsKey, AppName)];

    /// <summary>Everything registration writes, for the program at <paramref name="exePath"/>.</summary>
    public static IReadOnlyList<RegistryEntry> Entries(string exePath)
    {
        var icon = $"\"{exePath}\",0";
        // Through the running instance, like the Explorer menu: several files selected and opened at once
        // become one request (one archive → viewer, several → Batch Extract).
        var open = ShellCommand.CommandLine(exePath, ShellAction.Open);
        var entries = new List<RegistryEntry>();

        foreach (var (extension, typeName) in FileTypes)
        {
            var progId = ProgIdKey(extension);
            entries.Add(new(progId, "", typeName));
            entries.Add(new($@"{progId}\DefaultIcon", "", icon));
            entries.Add(new($@"{progId}\shell", "", "open"));
            entries.Add(new($@"{progId}\shell\open", "FriendlyAppName", AppName));
            // "Player" keeps Open available when more than 15 files are selected.
            entries.Add(new($@"{progId}\shell\open", "MultiSelectModel", "Player"));
            entries.Add(new($@"{progId}\shell\open\command", "", open));
            // Lists Mendrain in "Open with" for this extension; the extension's default value is left alone.
            entries.Add(new(OpenWithProgidsKey(extension), ProgId(extension), null));
        }

        entries.Add(new(CapabilitiesKey, "ApplicationName", AppName));
        entries.Add(new(CapabilitiesKey, "ApplicationDescription", "A modern archive utility for Windows"));
        entries.Add(new(CapabilitiesKey, "ApplicationIcon", icon));
        foreach (var (extension, _) in FileTypes)
            entries.Add(new($@"{CapabilitiesKey}\FileAssociations", extension, ProgId(extension)));
        entries.Add(new(RegisteredApplicationsKey, AppName, CapabilitiesKey));

        // Used when the user picks Mendrain.exe through "Choose another app → Look for another app on this PC".
        entries.Add(new(ApplicationKey, "FriendlyAppName", AppName));
        entries.Add(new($@"{ApplicationKey}\DefaultIcon", "", icon));
        entries.Add(new($@"{ApplicationKey}\shell\open", "MultiSelectModel", "Player"));
        entries.Add(new($@"{ApplicationKey}\shell\open\command", "", open));
        foreach (var (extension, _) in FileTypes)
            entries.Add(new($@"{ApplicationKey}\SupportedTypes", extension, ""));

        return entries;
    }
}
