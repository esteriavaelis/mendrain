namespace Mendrain.Core.Models;

/// <summary>Folder hierarchy of an archive for browsing. Folders implied by file paths are included.</summary>
public sealed class ArchiveFolder
{
    private readonly Dictionary<string, ArchiveFolder> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ArchiveItem> _files = [];

    private ArchiveFolder(string name, string path, ArchiveFolder? parent, DateTime? modified)
    {
        Name = name;
        Path = path;
        Parent = parent;
        Modified = modified;
    }

    public string Name { get; }
    /// <summary>'/'-separated path inside the archive; empty for the root.</summary>
    public string Path { get; }
    public ArchiveFolder? Parent { get; }
    public DateTime? Modified { get; private set; }

    public IReadOnlyList<ArchiveFolder> Folders => _folders.Values.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    public IReadOnlyList<ArchiveItem> Files => _files.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public long TotalSize => _files.Sum(f => Math.Max(0, f.Size)) + _folders.Values.Sum(f => f.TotalSize);
    public int TotalFileCount => _files.Count + _folders.Values.Sum(f => f.TotalFileCount);

    public static ArchiveFolder Build(IEnumerable<ArchiveItem> items)
    {
        var root = new ArchiveFolder("", "", null, null);
        foreach (var item in items)
        {
            var segments = item.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToArray();
            if (segments.Length == 0)
                continue;
            var folderSegments = item.IsDirectory ? segments.Length : segments.Length - 1;
            var folder = root;
            for (var i = 0; i < folderSegments; i++)
                folder = folder.GetOrAdd(segments[i]);
            if (item.IsDirectory)
                folder.Modified ??= item.Modified;
            else
                folder._files.Add(item);
        }
        return root;
    }

    /// <summary>Finds a folder by its '/'-separated path, or null.</summary>
    public ArchiveFolder? Find(string path)
    {
        var folder = this;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!folder._folders.TryGetValue(segment, out var next))
                return null;
            folder = next;
        }
        return folder;
    }

    private ArchiveFolder GetOrAdd(string name)
    {
        if (!_folders.TryGetValue(name, out var folder))
        {
            folder = new ArchiveFolder(name, Path.Length == 0 ? name : Path + "/" + name, this, null);
            _folders[name] = folder;
        }
        return folder;
    }
}
