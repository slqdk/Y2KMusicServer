using System.Text.Json;

namespace Y2KMusicServer.Server.Data;

/// <summary>
/// The Auto DJ's rotation memory, kept across restarts.
///
/// Why this exists: the shuffle already worked like a card deck — every playlist
/// has a "fed" bag, a track is dealt out at most once per pass, and the bag is
/// reshuffled only when the playlist is exhausted. But the bag, the
/// recently-played ring and the reference tempo all lived in memory, so every
/// service restart dealt a fresh deck. Restart at a party — for a build, a
/// reboot, a crash — and the rotation began again from a clean slate, which is
/// exactly what "I keep hearing the same songs" sounds like from the floor.
///
/// Held as JSON at <c>&lt;DataPath&gt;\shuffle-state.json</c> rather than in the
/// database (no-migrations rule). Losing the file costs nothing but a fresh
/// deck, so every read and write is best-effort: a corrupt or missing file just
/// means the old in-memory behaviour.
/// </summary>
public sealed class ShuffleState
{
    /// <summary>Track ids already dealt out of each playlist this pass, keyed by
    /// playlist id. Cleared per playlist when it runs out of unfed members.</summary>
    public Dictionary<int, List<int>> FedByPlaylist { get; set; } = new();

    /// <summary>Recently played track ids, oldest first.</summary>
    public List<int> RecentlyPlayed { get; set; } = new();

    /// <summary>Recently played artists (normalised), oldest first.</summary>
    public List<string> RecentlyPlayedArtists { get; set; } = new();

    /// <summary>When each track last played. Drives the least-recently-played
    /// bias, so a track heard an hour ago is less likely than one not heard for
    /// days — the thing a plain shuffle bag can't express.</summary>
    public Dictionary<int, DateTime> LastPlayedUtc { get; set; } = new();

    /// <summary>
    /// The running order each playlist is being dealt in this pass — a genuine
    /// shuffled permutation of its members, not a scoring artefact. Persisted so
    /// a restart resumes the same deal rather than starting a new one.
    /// </summary>
    public Dictionary<int, List<int>> BagOrderByPlaylist { get; set; } = new();

    /// <summary>The previous pass's running order per playlist. Kept only so the
    /// next shuffle can be checked against it and rejected if it comes out too
    /// close — this is what stops the rotation settling into one order.</summary>
    public Dictionary<int, List<int>> PrevBagOrderByPlaylist { get; set; } = new();

    /// <summary>Reference tempo carried over, so the first pick after a restart
    /// isn't a cold random one.</summary>
    public double RefBpm { get; set; }
}

public static class ShuffleStateStore
{
    /// <summary>Play timestamps kept. Beyond this the oldest are dropped — the
    /// bias only cares about the recent past, and the file stays small.</summary>
    private const int LastPlayedCap = 4000;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static ShuffleState Load(IConfiguration cfg)
    {
        var path = DataPaths.ShuffleStatePath(cfg);
        lock (Gate)
        {
            try
            {
                if (File.Exists(path))
                {
                    var s = JsonSerializer.Deserialize<ShuffleState>(File.ReadAllText(path));
                    if (s != null)
                    {
                        s.FedByPlaylist ??= new Dictionary<int, List<int>>();
                        s.RecentlyPlayed ??= new List<int>();
                        s.RecentlyPlayedArtists ??= new List<string>();
                        s.LastPlayedUtc ??= new Dictionary<int, DateTime>();
                        s.BagOrderByPlaylist ??= new Dictionary<int, List<int>>();
                        s.PrevBagOrderByPlaylist ??= new Dictionary<int, List<int>>();
                        return s;
                    }
                }
            }
            catch { /* missing / corrupt → a fresh deck, which is safe */ }
            return new ShuffleState();
        }
    }

    public static void Save(IConfiguration cfg, ShuffleState state)
    {
        lock (Gate)
        {
            try
            {
                // Trim the timestamp map before writing: oldest plays go first.
                if (state.LastPlayedUtc.Count > LastPlayedCap)
                {
                    var keep = state.LastPlayedUtc
                        .OrderByDescending(kv => kv.Value)
                        .Take(LastPlayedCap)
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
                    state.LastPlayedUtc = keep;
                }

                var path = DataPaths.ShuffleStatePath(cfg);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(state, Indented));
            }
            catch { /* the party continues; only the memory is lost */ }
        }
    }
}
