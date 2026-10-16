using System.Text.Json;
using Mendrain.Core.Services;
using Microsoft.UI.Xaml;

namespace Mendrain.Helpers;

/// <summary>User preferences and per-user storage locations (%LOCALAPPDATA%\Mendrain).</summary>
public sealed class AppSettings
{
    private sealed record Data(string Theme = "System");

    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mendrain");

    private static string SettingsPath => Path.Combine(DataFolder, "settings.json");

    public RecentArchives Recent { get; } = new(Path.Combine(DataFolder, "recent.json"));

    private Data _data = Load();

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme
    {
        get => _data.Theme;
        set
        {
            _data = _data with { Theme = value };
            Save();
        }
    }

    public ElementTheme ElementTheme => Theme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private static Data Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<Data>(File.ReadAllText(SettingsPath)) ?? new Data()
                : new Data();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Data();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_data));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are optional; the app keeps working with the in-memory value.
        }
    }
}
