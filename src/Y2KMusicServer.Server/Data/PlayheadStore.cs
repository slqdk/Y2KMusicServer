using System.Text.Json;

namespace Y2KMusicServer.Server.Data;

/// <summary>
/// Remembers how far the live queue has been played, so a service restart or a
/// power cut doesn't lose the thread: on the next Play the engine resumes at
/// the first entry AFTER the last one that played, instead of restarting from
/// the top of a queue whose first rows are history.
///
/// Only the playhead is stored — the entry id of the last track that played —
/// because the queue itself already lives in the database and its order is what
/// defines "played" (everything before the playhead) versus "still to come"
/// (everything after). JSON at <c>&lt;DataPath&gt;\playhead.json</c>, keeping
/// the no-migrations rule intact.
/// </summary>
public static class PlayheadStore
{
    public sealed class Playhead
    {
        /// <summary>PlaylistEntry.Id of the last track that played. 0 = none.</summary>
        public int EntryId { get; set; }

        /// <summary>The track that entry pointed at — a sanity check, so a
        /// recycled entry id can't resume in the wrong place.</summary>
        public int TrackId { get; set; }

        public DateTime SavedUtc { get; set; }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static Playhead Load(IConfiguration cfg)
    {
        var path = DataPaths.PlayheadPath(cfg);
        lock (Gate)
        {
            try
            {
                if (File.Exists(path))
                {
                    var p = JsonSerializer.Deserialize<Playhead>(File.ReadAllText(path));
                    if (p != null) return p;
                }
            }
            catch { /* missing / corrupt → start from the top */ }
            return new Playhead();
        }
    }

    public static void Save(IConfiguration cfg, int entryId, int trackId)
    {
        var path = DataPaths.PlayheadPath(cfg);
        lock (Gate)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(
                    new Playhead { EntryId = entryId, TrackId = trackId, SavedUtc = DateTime.UtcNow }, Indented));
            }
            catch { /* best effort: losing the playhead costs a resume, not data */ }
        }
    }

    /// <summary>Forgets the playhead — used when the queue is cleared.</summary>
    public static void Clear(IConfiguration cfg) => Save(cfg, 0, 0);
}
