using System.Text.Json;
using Y2KMusicServer.Server.Data;

namespace Y2KMusicServer.Server.Integrations;

/// <summary>
/// Which YouTube videos have already been downloaded, and where they landed.
/// Persisted as JSON at <c>&lt;DataPath&gt;\youtube-downloads.json</c>, a sibling
/// of <c>integrations.json</c>.
///
/// It exists purely so a re-paste of the same link is a no-op instead of a second
/// copy: the file itself is named <c>Artist - Title.mp3</c>, which carries no
/// video id, and the no-migrations rule rules out a column on <c>Tracks</c>. The
/// index is a convenience, not a source of truth — deleting it only means the
/// next paste of an old link downloads it again under a "(2)" name.
/// </summary>
public static class YouTubeDownloadIndex
{
    public sealed class Entry
    {
        public string VideoId { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public DateTime DownloadedUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class Index
    {
        public List<Entry> Entries { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static Index Load(IConfiguration cfg)
    {
        var path = DataPaths.YouTubeDownloadsIndexPath(cfg);
        lock (Gate)
        {
            try
            {
                if (File.Exists(path))
                {
                    var c = JsonSerializer.Deserialize<Index>(File.ReadAllText(path));
                    if (c != null) { c.Entries ??= new List<Entry>(); return c; }
                }
            }
            catch { /* missing / corrupt → empty */ }
            return new Index();
        }
    }

    /// <summary>The recorded landing spot for a video id, or null.</summary>
    public static Entry? Find(IConfiguration cfg, string videoId)
        => Load(cfg).Entries.FirstOrDefault(
            e => string.Equals(e.VideoId, videoId, StringComparison.Ordinal));

    /// <summary>Records (or replaces) where a downloaded video landed.</summary>
    public static void Record(IConfiguration cfg, string videoId, string filePath,
                              string? title, string? artist)
    {
        var path = DataPaths.YouTubeDownloadsIndexPath(cfg);
        lock (Gate)
        {
            Index idx;
            try
            {
                idx = File.Exists(path)
                    ? JsonSerializer.Deserialize<Index>(File.ReadAllText(path)) ?? new Index()
                    : new Index();
            }
            catch { idx = new Index(); }
            idx.Entries ??= new List<Entry>();

            idx.Entries.RemoveAll(e => string.Equals(e.VideoId, videoId, StringComparison.Ordinal));
            idx.Entries.Add(new Entry
            {
                VideoId = videoId,
                FilePath = filePath,
                Title = title,
                Artist = artist,
                DownloadedUtc = DateTime.UtcNow
            });

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(idx, Indented));
            }
            catch { /* the download still succeeded; only dedupe is lost */ }
        }
    }
}
