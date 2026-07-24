using System.Text.Json;

namespace Y2KMusicServer.Server.Data;

/// <summary>
/// The single global list of music folders the library is built from —
/// replaces the retired per-category folder assignments. Persisted as JSON at
/// <c>&lt;DataPath&gt;\scan-folders.json</c>, a sibling of
/// <c>mixrules.json</c> / <c>web-config.json</c>. Each folder carries a stable
/// integer id so folder-scoped operations (rescan / analyze / clear, and the
/// scan→analyze chaining's <c>ScopeFolderId</c>) keep working. "Innermost
/// folder wins" ownership (<see cref="FolderScope"/>) applies across this list
/// exactly as it did across category folders.
/// </summary>
public static class ScanFolderStore
{
    public sealed class ScanFolder
    {
        public int Id { get; set; }
        public string Path { get; set; } = "";
    }

    public sealed class ScanFolders
    {
        public int NextId { get; set; } = 1;
        public List<ScanFolder> Folders { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static ScanFolders Load(IConfiguration cfg)
    {
        var path = DataPaths.ScanFoldersPath(cfg);
        lock (Gate)
        {
            try
            {
                if (File.Exists(path))
                {
                    var c = JsonSerializer.Deserialize<ScanFolders>(File.ReadAllText(path));
                    if (c != null) return Sanitise(c);
                }
            }
            catch { /* missing / corrupt → empty list */ }
            return new ScanFolders();
        }
    }

    /// <summary>Every stored folder path (for FolderScope exclusions and the
    /// share reconnector).</summary>
    public static List<string> AllPaths(IConfiguration cfg)
        => Load(cfg).Folders.Select(f => f.Path).ToList();

    /// <summary>The folder with the given id, or null.</summary>
    public static ScanFolder? Find(IConfiguration cfg, int id)
        => Load(cfg).Folders.FirstOrDefault(f => f.Id == id);

    /// <summary>
    /// Adds a folder path (normalised, no trailing separator) and returns the
    /// stored entry — or the existing entry if the path is already listed
    /// (case-insensitive match).
    /// </summary>
    public static ScanFolder Add(IConfiguration cfg, string folderPath)
    {
        var norm = folderPath.Trim().TrimEnd('\\', '/');
        lock (Gate)
        {
            var c = LoadUnlocked(cfg);
            var existing = c.Folders.FirstOrDefault(
                f => string.Equals(f.Path, norm, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            var entry = new ScanFolder { Id = c.NextId++, Path = norm };
            c.Folders.Add(entry);
            SaveUnlocked(cfg, c);
            return entry;
        }
    }

    /// <summary>Removes a folder by id. Returns the removed entry, or null.</summary>
    public static ScanFolder? Remove(IConfiguration cfg, int id)
    {
        lock (Gate)
        {
            var c = LoadUnlocked(cfg);
            var entry = c.Folders.FirstOrDefault(f => f.Id == id);
            if (entry == null) return null;
            c.Folders.Remove(entry);
            SaveUnlocked(cfg, c);
            return entry;
        }
    }

    // ── Internals (callers hold Gate) ─────────────────────────────────────────

    private static ScanFolders LoadUnlocked(IConfiguration cfg)
    {
        var path = DataPaths.ScanFoldersPath(cfg);
        try
        {
            if (File.Exists(path))
            {
                var c = JsonSerializer.Deserialize<ScanFolders>(File.ReadAllText(path));
                if (c != null) return Sanitise(c);
            }
        }
        catch { }
        return new ScanFolders();
    }

    private static void SaveUnlocked(IConfiguration cfg, ScanFolders c)
    {
        var path = DataPaths.ScanFoldersPath(cfg);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(c, Indented));
    }

    private static ScanFolders Sanitise(ScanFolders c)
    {
        c.Folders ??= new List<ScanFolder>();
        c.Folders.RemoveAll(f => string.IsNullOrWhiteSpace(f.Path));
        foreach (var f in c.Folders) f.Path = f.Path.Trim().TrimEnd('\\', '/');
        if (c.Folders.Count > 0)
            c.NextId = Math.Max(c.NextId, c.Folders.Max(f => f.Id) + 1);
        return c;
    }
}
