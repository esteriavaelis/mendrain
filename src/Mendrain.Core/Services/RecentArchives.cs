using System.Text.Json;

namespace Mendrain.Core.Services;

/// <summary>Most-recently-opened archive paths, stored as a small JSON file.</summary>
public sealed class RecentArchives(string storePath, int capacity = 8)
{
    public IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(storePath))
                return [];
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(storePath))?.Take(capacity).ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> Add(string archivePath) =>
        Save(Load().Where(p => !string.Equals(p, archivePath, StringComparison.OrdinalIgnoreCase)).Prepend(archivePath));

    public IReadOnlyList<string> Remove(string archivePath) =>
        Save(Load().Where(p => !string.Equals(p, archivePath, StringComparison.OrdinalIgnoreCase)));

    public IReadOnlyList<string> Clear() => Save([]);

    private IReadOnlyList<string> Save(IEnumerable<string> paths)
    {
        var list = paths.Take(capacity).ToList();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
            var temp = storePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list));
            File.Move(temp, storePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Recent files are a convenience; failing to save them must not break the app.
        }
        return list;
    }
}
