using System.Text.Json;

namespace Y2KMusicServer.Server.Data;

/// <summary>
/// Google Cast (Chromecast / Google Home / Nest speaker) settings and the
/// remembered speaker list. Persisted as JSON at
/// <c>&lt;DataPath&gt;\cast-config.json</c>, a sibling of <c>web-config.json</c>
/// (no-migrations rule — new persistent state never touches the DB schema).
///
/// Casting works by telling a speaker to fetch <c>/stream</c> itself, so the
/// speaker needs a URL that resolves on ITS network — hence
/// <see cref="CastConfig.StreamUrl"/>, which overrides the auto-detected
/// <c>http://&lt;server LAN ip&gt;:&lt;port&gt;/stream</c> when the guess is wrong.
/// </summary>
public static class CastConfigStore
{
    public sealed class CastSpeaker
    {
        /// <summary>Stable device key (mDNS id when the speaker reports one,
        /// else host:port). Used by every API call.</summary>
        public string Id { get; set; } = "";

        /// <summary>Friendly name as last seen ("Kitchen speaker").</summary>
        public string Name { get; set; } = "";

        /// <summary>Model string as last seen ("Google Home Mini").</summary>
        public string Model { get; set; } = "";

        /// <summary>Last known address, so a speaker can be cast to even when a
        /// discovery pass misses it (mDNS is lossy on busy Wi-Fi).</summary>
        public string Host { get; set; } = "";
        public int Port { get; set; } = 8009;

        /// <summary>Whether this speaker may be used at all. Off by default:
        /// nothing gets cast to a device the operator hasn't ticked.</summary>
        public bool Allowed { get; set; } = false;

        /// <summary>Whether WEBSITE VISITORS may start this speaker. Strictly
        /// narrower than <see cref="Allowed"/>: a guest-startable speaker must
        /// also be allowed, and the listener list additionally requires the
        /// global ShowOnListener switch. Off by default — the operator picks
        /// which speakers the party can touch.</summary>
        public bool GuestAllowed { get; set; } = false;
    }

    public sealed class CastConfig
    {
        /// <summary>Master switch. Off = no discovery, no casting, and the
        /// listener endpoints behave as if the feature didn't exist.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Let website visitors start/stop casts themselves. Off means
        /// only the operator (admin UI) can.</summary>
        public bool ShowOnListener { get; set; } = false;

        /// <summary>Explicit stream URL handed to the speakers. Empty = derive
        /// it from the server's LAN address and the Kestrel port.</summary>
        public string StreamUrl { get; set; } = "";

        /// <summary>Volume set on a speaker when a cast starts, 0–1.
        /// 0 = leave the speaker's own volume alone.</summary>
        public double Volume { get; set; } = 0.4;

        /// <summary>Remembered speakers (allow-list + last known address).</summary>
        public List<CastSpeaker> Speakers { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly object Gate = new();

    /// <summary>Loads the config, or defaults if missing / unreadable.</summary>
    public static CastConfig Load(IConfiguration cfg)
    {
        var path = DataPaths.CastConfigPath(cfg);
        lock (Gate)
        {
            try
            {
                if (File.Exists(path))
                {
                    var c = JsonSerializer.Deserialize<CastConfig>(File.ReadAllText(path));
                    if (c != null) return Clamp(c);
                }
            }
            catch { /* missing / corrupt → defaults */ }
            return new CastConfig();
        }
    }

    /// <summary>Persists the config (clamped) and returns the stored value.</summary>
    public static CastConfig Save(IConfiguration cfg, CastConfig c)
    {
        c = Clamp(c);
        var path = DataPaths.CastConfigPath(cfg);
        lock (Gate)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(c, Indented));
        }
        return c;
    }

    /// <summary>
    /// Read-modify-write under the same lock the file uses, so two API calls
    /// arriving together can't lose each other's edit.
    /// </summary>
    public static CastConfig Update(IConfiguration cfg, Action<CastConfig> mutate)
    {
        lock (Gate)
        {
            var path = DataPaths.CastConfigPath(cfg);
            CastConfig c;
            try
            {
                c = File.Exists(path)
                    ? JsonSerializer.Deserialize<CastConfig>(File.ReadAllText(path)) ?? new CastConfig()
                    : new CastConfig();
            }
            catch { c = new CastConfig(); }

            mutate(c);
            c = Clamp(c);

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(c, Indented));
            return c;
        }
    }

    private static CastConfig Clamp(CastConfig c)
    {
        c.Volume = Math.Clamp(c.Volume, 0, 1);
        c.StreamUrl = (c.StreamUrl ?? "").Trim();
        c.Speakers ??= new List<CastSpeaker>();
        foreach (var s in c.Speakers)
        {
            s.Id = (s.Id ?? "").Trim();
            s.Name = (s.Name ?? "").Trim();
            s.Model = (s.Model ?? "").Trim();
            s.Host = (s.Host ?? "").Trim();
            if (s.Port <= 0 || s.Port > 65535) s.Port = 8009;
            if (!s.Allowed) s.GuestAllowed = false;   // guests can never exceed the operator gate
        }
        c.Speakers = c.Speakers.Where(s => s.Id.Length > 0).ToList();
        return c;
    }
}
