using System.Text.Json;
using Y2KMusicServer.Server.Data.Entities;

namespace Y2KMusicServer.Server.Data;

/// <summary>
/// Which saved playlists Auto DJ may feed from — the descendant of the old
/// category enable/disable switches.
///
/// Three states per playlist, and the operator's word beats the clock:
///   • explicitly ON  → feeds, whatever the schedule says
///   • explicitly OFF → does NOT feed, even while a timeslot covers now
///   • unset          → the schedule decides (the original behaviour)
///
/// The explicit OFF exists because "off" has to mean off: killing a genre
/// mid-party is exactly when a covering timeslot quietly re-enabling it is
/// least welcome.
///
/// Stored as JSON at <c>&lt;DataPath&gt;\autodj-feeds.json</c> rather than a
/// column on SavedPlaylists, per the no-migrations rule. Files written by the
/// earlier two-state version load unchanged: their ids become explicit ONs.
/// </summary>
public static class AutoDjFeedStore
{
    private sealed class FeedFile
    {
        /// <summary>Explicitly on (the legacy field — same meaning).</summary>
        public List<int> PlaylistIds { get; set; } = new();

        /// <summary>Explicitly off; overrides any covering timeslot.</summary>
        public List<int> DisabledIds { get; set; } = new();
    }

    /// <summary>The operator's overrides. An id in neither set = "let the schedule decide".</summary>
    public sealed class FeedState
    {
        public HashSet<int> On { get; init; } = new();
        public HashSet<int> Off { get; init; } = new();

        /// <summary>Whether Auto DJ may draw from this playlist right now.</summary>
        public bool IsActive(SavedPlaylist pl, DateTime now, Func<SavedPlaylist, DateTime, bool> scheduled)
        {
            if (Off.Contains(pl.Id)) return false;   // explicit off wins over everything
            if (On.Contains(pl.Id)) return true;
            return scheduled(pl, now);
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly object Gate = new();

    /// <summary>
    /// The operator's overrides, with the jingle playlist folded in as an
    /// explicit OFF. Doing it here rather than at each call site means Auto DJ
    /// top-up, the DJ page and the listener's feed browse all inherit the rule
    /// from one place: a jingle playlist can never top up the queue, and no
    /// timeslot can quietly re-enable it. The OFF is virtual — nothing is
    /// written to autodj-feeds.json — so un-designating restores whatever the
    /// playlist's own state was.
    /// </summary>
    public static FeedState LoadState(IConfiguration cfg)
    {
        lock (Gate)
        {
            var f = ReadFileUnlocked(cfg);
            var off = f.DisabledIds.ToHashSet();
            if (JingleStore.PlaylistId(cfg) is int jingleId) off.Add(jingleId);
            return new FeedState
            {
                On = f.PlaylistIds.ToHashSet(),
                Off = off
            };
        }
    }

    /// <summary>The explicitly-on ids, for callers that only need that set.</summary>
    public static HashSet<int> Load(IConfiguration cfg) => LoadState(cfg).On;

    /// <summary>
    /// Records the operator's choice: <c>true</c> forces the playlist on,
    /// <c>false</c> forces it off (schedule included).
    /// </summary>
    public static FeedState Set(IConfiguration cfg, int playlistId, bool on)
    {
        lock (Gate)
        {
            var f = ReadFileUnlocked(cfg);
            f.PlaylistIds.Remove(playlistId);
            f.DisabledIds.Remove(playlistId);
            if (on) f.PlaylistIds.Add(playlistId);
            else f.DisabledIds.Add(playlistId);
            WriteUnlocked(cfg, f);
            return new FeedState { On = f.PlaylistIds.ToHashSet(), Off = f.DisabledIds.ToHashSet() };
        }
    }

    /// <summary>
    /// Forgets every override, so the timeslots decide again — what an empty
    /// live selection means.
    /// </summary>
    public static void Clear(IConfiguration cfg)
    {
        lock (Gate) WriteUnlocked(cfg, new FeedFile());
    }

    /// <summary>Drops ids that no longer exist (called after a delete).</summary>
    public static void Prune(IConfiguration cfg, IEnumerable<int> existingIds)
    {
        var keep = existingIds.ToHashSet();
        lock (Gate)
        {
            var f = ReadFileUnlocked(cfg);
            f.PlaylistIds = f.PlaylistIds.Where(keep.Contains).ToList();
            f.DisabledIds = f.DisabledIds.Where(keep.Contains).ToList();
            WriteUnlocked(cfg, f);
        }
    }

    private static FeedFile ReadFileUnlocked(IConfiguration cfg)
    {
        var path = DataPaths.AutoDjFeedsPath(cfg);
        try
        {
            if (File.Exists(path))
            {
                var f = JsonSerializer.Deserialize<FeedFile>(File.ReadAllText(path));
                if (f != null)
                {
                    f.PlaylistIds ??= new List<int>();
                    f.DisabledIds ??= new List<int>();
                    return f;
                }
            }
        }
        catch { /* missing / corrupt → no overrides at all */ }
        return new FeedFile();
    }

    private static void WriteUnlocked(IConfiguration cfg, FeedFile f)
    {
        var path = DataPaths.AutoDjFeedsPath(cfg);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        f.PlaylistIds = f.PlaylistIds.Distinct().OrderBy(i => i).ToList();
        f.DisabledIds = f.DisabledIds.Distinct().OrderBy(i => i).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(f, Indented));
    }
}
