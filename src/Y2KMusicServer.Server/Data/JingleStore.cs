using System.Text.Json;

namespace Y2KMusicServer.Server.Data;

/// <summary>
/// Which saved playlist holds the jingles — the sing-alongs, anthems and
/// party-restart shots the DJ fires by hand.
///
/// A designation rather than a new kind of thing: any saved playlist can be the
/// jingle playlist, and un-designating hands it straight back to normal duty.
/// That keeps the editing UI, the import, the track grid and the move/copy
/// tooling working unchanged, and the no-migrations rule off the hook — the id
/// lives in <c>&lt;DataPath&gt;\jingles.json</c>, not in a column.
///
/// Designation is enforced in the places that would otherwise let a jingle play
/// itself: <see cref="AutoDjFeedStore"/> treats the id as explicitly OFF so
/// Auto DJ can never top up from it (and no timeslot can re-enable it), the
/// listener's playlist rail leaves it out, and the DJ page's Auto DJ toggles
/// skip it. The TRACKS stay ordinary library tracks: a guest can still find
/// "Sweet Caroline" by searching for it, which is the intended behaviour — only
/// the playlist is reserved, not its songs.
/// </summary>
public static class JingleStore
{
    private sealed class JingleFile
    {
        /// <summary>The designated playlist id, or null when none is set.</summary>
        public int? PlaylistId { get; set; }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly object Gate = new();

    /// <summary>The designated jingle playlist id, or null.</summary>
    public static int? PlaylistId(IConfiguration cfg)
    {
        lock (Gate) return ReadUnlocked(cfg).PlaylistId;
    }

    /// <summary>True when this playlist is the jingle playlist.</summary>
    public static bool IsJingle(IConfiguration cfg, int playlistId)
        => PlaylistId(cfg) == playlistId;

    /// <summary>
    /// Designates a playlist as the jingle playlist, or clears the designation
    /// with null. Only one playlist at a time: designating a second one moves
    /// the badge rather than adding to a set.
    /// </summary>
    public static int? Set(IConfiguration cfg, int? playlistId)
    {
        lock (Gate)
        {
            var f = ReadUnlocked(cfg);
            f.PlaylistId = playlistId;
            WriteUnlocked(cfg, f);
            return f.PlaylistId;
        }
    }

    private static JingleFile ReadUnlocked(IConfiguration cfg)
    {
        var path = DataPaths.JinglesPath(cfg);
        try
        {
            if (File.Exists(path))
            {
                var f = JsonSerializer.Deserialize<JingleFile>(File.ReadAllText(path));
                if (f != null) return f;
            }
        }
        catch { /* missing / corrupt → no designation */ }
        return new JingleFile();
    }

    private static void WriteUnlocked(IConfiguration cfg, JingleFile f)
    {
        var path = DataPaths.JinglesPath(cfg);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(f, Indented));
    }
}
