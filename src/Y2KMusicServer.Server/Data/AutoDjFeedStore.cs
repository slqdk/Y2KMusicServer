using System.Text.Json;

namespace Y2KMusicServer.Server.Data;

/// <summary>
/// Which saved playlists Auto DJ may feed from RIGHT NOW regardless of their
/// schedule — the direct descendant of the old category enable/disable
/// switches. A playlist feeds the queue when it is toggled on here OR when one
/// of its enabled slots covers the current day and time; the two are a union,
/// so the schedule keeps running in the background while the operator can
/// force a playlist in (or leave everything to the clock).
///
/// Stored as JSON at <c>&lt;DataPath&gt;\autodj-feeds.json</c> rather than a
/// column on SavedPlaylists: the no-migrations rule keeps new persistent state
/// off the database schema, so this can ship without recreating the library.
/// </summary>
public static class AutoDjFeedStore
{
    private sealed class FeedFile
    {
        public List<int> PlaylistIds { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly object Gate = new();

    /// <summary>The ids currently toggled on. Empty set on a missing file.</summary>
    public static HashSet<int> Load(IConfiguration cfg)
    {
        var path = DataPaths.AutoDjFeedsPath(cfg);
        lock (Gate)
        {
            try
            {
                if (File.Exists(path))
                {
                    var f = JsonSerializer.Deserialize<FeedFile>(File.ReadAllText(path));
                    if (f?.PlaylistIds != null) return f.PlaylistIds.ToHashSet();
                }
            }
            catch { /* missing / corrupt → nothing forced on */ }
            return new HashSet<int>();
        }
    }

    /// <summary>Turns one playlist's feed flag on or off; returns the new set.</summary>
    public static HashSet<int> Set(IConfiguration cfg, int playlistId, bool on)
    {
        lock (Gate)
        {
            var path = DataPaths.AutoDjFeedsPath(cfg);
            HashSet<int> ids;
            try
            {
                ids = File.Exists(path)
                    ? (JsonSerializer.Deserialize<FeedFile>(File.ReadAllText(path))?.PlaylistIds ?? new List<int>()).ToHashSet()
                    : new HashSet<int>();
            }
            catch { ids = new HashSet<int>(); }

            if (on) ids.Add(playlistId); else ids.Remove(playlistId);

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(
                new FeedFile { PlaylistIds = ids.OrderBy(i => i).ToList() }, Indented));
            return ids;
        }
    }

    /// <summary>Drops ids that no longer exist (called after a delete).</summary>
    public static void Prune(IConfiguration cfg, IEnumerable<int> existingIds)
    {
        var keep = existingIds.ToHashSet();
        lock (Gate)
        {
            var path = DataPaths.AutoDjFeedsPath(cfg);
            if (!File.Exists(path)) return;
            try
            {
                var ids = (JsonSerializer.Deserialize<FeedFile>(File.ReadAllText(path))?.PlaylistIds ?? new List<int>())
                    .Where(keep.Contains).OrderBy(i => i).ToList();
                File.WriteAllText(path, JsonSerializer.Serialize(new FeedFile { PlaylistIds = ids }, Indented));
            }
            catch { /* best effort */ }
        }
    }
}
