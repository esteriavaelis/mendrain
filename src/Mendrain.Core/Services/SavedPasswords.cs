using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mendrain.Core.Services;

/// <summary>A saved archive password, without the password itself.</summary>
/// <param name="Label">Name of the archive the password was last saved for.</param>
/// <param name="Archives">File names of the archives this password was saved for, most recent first.</param>
public sealed record SavedPassword(string Id, string Label, IReadOnlyList<string> Archives, DateTimeOffset Saved)
{
    public bool IsFor(string archiveName) => Archives.Contains(archiveName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Used as the accessible name of list rows. Never contains the password.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// Passwords the user chose to save, in a small JSON file. Each password is encrypted with Windows DPAPI
/// for the current user account; the file holds only that ciphertext plus archive names and dates.
/// Entries that cannot be read are kept in the file untouched, and an unreadable file is set aside
/// (never silently replaced) before anything new is saved.
/// </summary>
public sealed class SavedPasswordStore(string storePath, int capacity = 50)
{
    private const int MaxArchivesPerEntry = 20;
    // Ties the ciphertext to Mendrain: other programs running as the same user can't decrypt it by accident.
    private static readonly byte[] Entropy = "Mendrain saved archive password v1"u8.ToArray();

    private readonly Lock _gate = new();

    /// <summary>Set when the last read found damaged data; null when everything could be read.</summary>
    public string? Problem { get; private set; }

    public IReadOnlyList<SavedPassword> List()
    {
        lock (_gate)
            return Read().Entries.Select(e => e.Info).ToList();
    }

    /// <summary>Saved passwords that were saved for an archive with this file name.</summary>
    public IReadOnlyList<SavedPassword> FindFor(string archivePath)
    {
        var name = Path.GetFileName(archivePath);
        return List().Where(p => p.IsFor(name)).ToList();
    }

    /// <summary>Decrypts one saved password. Returns null if it is gone or can't be decrypted (e.g. another Windows account saved it).</summary>
    public string? Reveal(string id)
    {
        lock (_gate)
        {
            var entry = Read().Entries.FirstOrDefault(e => e.Info.Id == id);
            return entry is null ? null : Unprotect(entry.Secret);
        }
    }

    /// <summary>
    /// Saves <paramref name="password"/> for <paramref name="archiveName"/>. If the same password is already saved,
    /// that entry is updated instead of adding a duplicate. Returns false if it could not be written.
    /// </summary>
    public bool Save(string archiveName, string password)
    {
        if (string.IsNullOrEmpty(password))
            return false;
        archiveName = Path.GetFileName(archiveName);
        lock (_gate)
        {
            var data = Read();
            var existing = data.Entries.FirstOrDefault(e => SamePassword(e.Secret, password));
            byte[] secret;
            string id;
            IEnumerable<string> archives;
            if (existing is not null)
            {
                data.Entries.Remove(existing);
                (id, secret) = (existing.Info.Id, existing.Secret);
                archives = existing.Info.Archives.Where(a => !string.Equals(a, archiveName, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                if (Protect(password) is not { } protectedSecret)
                    return false;
                (id, secret, archives) = (Guid.NewGuid().ToString("N"), protectedSecret, []);
            }

            var info = new SavedPassword(id, archiveName, archives.Prepend(archiveName).Take(MaxArchivesPerEntry).ToList(), DateTimeOffset.Now);
            data.Entries.Insert(0, new Entry(info, secret));
            if (data.Entries.Count > capacity)
                data.Entries.RemoveRange(capacity, data.Entries.Count - capacity);
            return Write(data);
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var data = Read();
            return data.Entries.RemoveAll(e => e.Info.Id == id) > 0 && Write(data);
        }
    }

    /// <summary>Removes every saved password, including entries and set-aside files that could not be read.</summary>
    public bool Clear()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(storePath))
                    File.Delete(storePath);
                var folder = Path.GetDirectoryName(storePath)!;
                if (Directory.Exists(folder))
                {
                    foreach (var file in Directory.EnumerateFiles(folder, Path.GetFileName(storePath) + ".unreadable-*"))
                        File.Delete(file);
                }
                Problem = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    // ---------------------------------------------------------------- file format

    private sealed record Entry(SavedPassword Info, byte[] Secret);

    /// <param name="Unreadable">Entries kept as they are because they could not be parsed.</param>
    /// <param name="FileDamaged">The file exists but is not valid; it must be set aside before writing.</param>
    private sealed record Data(List<Entry> Entries, List<JsonNode> Unreadable, bool FileDamaged);

    private Data Read()
    {
        var data = new Data([], [], false);
        Problem = null;
        string text;
        try
        {
            if (!File.Exists(storePath))
                return data;
            text = File.ReadAllText(storePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = "Saved passwords could not be opened.";
            // Unknown content: treat like a damaged file so it is set aside, not overwritten.
            return data with { FileDamaged = true };
        }

        JsonArray? entries;
        try
        {
            entries = JsonNode.Parse(text)?["entries"] as JsonArray;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            entries = null;
        }
        if (entries is null)
        {
            Problem = "The saved-passwords file is damaged and could not be read.";
            return data with { FileDamaged = true };
        }

        foreach (var node in entries)
        {
            if (node is not null && Parse(node) is { } entry && data.Entries.All(e => e.Info.Id != entry.Info.Id))
                data.Entries.Add(entry);
            else if (node is not null)
                data.Unreadable.Add(node.DeepClone());
        }
        if (data.Unreadable.Count > 0)
            Problem = data.Unreadable.Count == 1 ? "1 saved password could not be read." : $"{data.Unreadable.Count} saved passwords could not be read.";
        return data;
    }

    private static Entry? Parse(JsonNode node)
    {
        try
        {
            var id = (string?)node["id"];
            var label = (string?)node["label"];
            var secret = (string?)node["secret"];
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(label) || string.IsNullOrEmpty(secret))
                return null;
            var archives = (node["archives"] as JsonArray)?.Select(a => (string?)a).OfType<string>().ToList() ?? [];
            if (archives.Count == 0)
                archives.Add(label);
            var saved = DateTimeOffset.TryParse((string?)node["saved"], out var s) ? s : DateTimeOffset.MinValue;
            return new Entry(new SavedPassword(id, label, archives, saved), Convert.FromBase64String(secret));
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or JsonException)
        {
            return null;
        }
    }

    private bool Write(Data data)
    {
        var entries = new JsonArray();
        foreach (var e in data.Entries)
        {
            entries.Add(new JsonObject
            {
                ["id"] = e.Info.Id,
                ["label"] = e.Info.Label,
                ["archives"] = new JsonArray(e.Info.Archives.Select(a => (JsonNode?)a).ToArray()),
                ["saved"] = e.Info.Saved.ToString("o"),
                ["secret"] = Convert.ToBase64String(e.Secret),
            });
        }
        foreach (var raw in data.Unreadable)
            entries.Add(raw);
        var root = new JsonObject { ["version"] = 1, ["protection"] = "DPAPI (current user)", ["entries"] = entries };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
            if (data.FileDamaged && File.Exists(storePath))
                File.Move(storePath, $"{storePath}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}");
            var temp = storePath + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, storePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SamePassword(byte[] secret, string password)
    {
        var stored = UnprotectBytes(secret);
        if (stored is null)
            return false;
        var candidate = Encoding.UTF8.GetBytes(password);
        try
        {
            return CryptographicOperations.FixedTimeEquals(stored, candidate);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stored);
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    // ---------------------------------------------------------------- DPAPI

    private static byte[]? Protect(string password)
    {
        var plain = Encoding.UTF8.GetBytes(password);
        try
        {
            return Dpapi.Run(plain, protect: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static string? Unprotect(byte[] secret)
    {
        var plain = UnprotectBytes(secret);
        if (plain is null)
            return null;
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[]? UnprotectBytes(byte[] secret) => Dpapi.Run(secret, protect: false);

    /// <summary>CryptProtectData / CryptUnprotectData for the current user. Returns null on failure.</summary>
    private static class Dpapi
    {
        private const int UiForbidden = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public int Size;
            public nint Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref Blob dataIn, string? description, ref Blob entropy,
            nint reserved, nint prompt, int flags, out Blob dataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref Blob dataIn, nint description, ref Blob entropy,
            nint reserved, nint prompt, int flags, out Blob dataOut);

        [DllImport("kernel32.dll")]
        private static extern nint LocalFree(nint memory);

        public static byte[]? Run(byte[] input, bool protect)
        {
            if (!OperatingSystem.IsWindows())
                return null;
            var dataIn = Copy(input);
            var entropy = Copy(Entropy);
            var dataOut = default(Blob);
            try
            {
                var ok = protect
                    ? CryptProtectData(ref dataIn, null, ref entropy, 0, 0, UiForbidden, out dataOut)
                    : CryptUnprotectData(ref dataIn, 0, ref entropy, 0, 0, UiForbidden, out dataOut);
                if (!ok || dataOut.Data == 0)
                    return null;
                var result = new byte[dataOut.Size];
                Marshal.Copy(dataOut.Data, result, 0, dataOut.Size);
                return result;
            }
            finally
            {
                Free(dataIn);
                Free(entropy);
                if (dataOut.Data != 0)
                {
                    // The output of CryptUnprotectData is the plain password: wipe it before releasing it.
                    Marshal.Copy(new byte[dataOut.Size], 0, dataOut.Data, dataOut.Size);
                    LocalFree(dataOut.Data);
                }
            }
        }

        private static Blob Copy(byte[] bytes)
        {
            var blob = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(Math.Max(1, bytes.Length)) };
            Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
            return blob;
        }

        private static void Free(Blob blob)
        {
            Marshal.Copy(new byte[blob.Size], 0, blob.Data, blob.Size);
            Marshal.FreeHGlobal(blob.Data);
        }
    }
}
