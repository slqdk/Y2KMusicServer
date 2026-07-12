using System.Text.Json;
using Y2KMusicServer.Server.Data;

namespace Y2KMusicServer.Server.Integrations;

/// <summary>
/// On/off state for optional third-party integrations that the no-migrations
/// rule keeps off the database schema (persistence.md). Persisted as JSON at
/// <c>&lt;DataPath&gt;\integrations.json</c>, a sibling of <c>web-config.json</c>.
/// Currently just the YouTube fetch gate. Note: the preflight check ignores this
/// flag on purpose (you test before you enable) — only search / fetch are gated.
/// </summary>
public static class IntegrationsStore
{
    public sealed class IntegrationsConfig
    {
        /// <summary>Master switch for the YouTube fetch feature (search + fetch).
        /// Off by default.</summary>
        public bool YouTubeEnabled { get; set; } = false;

        /// <summary>Web-cache size cap in MB; 0 = unlimited. When set, the oldest
        /// idle cached tracks are evicted after a fetch to stay under it.</summary>
        public int WebCacheMaxMB { get; set; } = 0;

        /// <summary>Web-cache age cap in days; 0 = no age limit. When set, idle
        /// cached tracks older than this are evicted after a fetch.</summary>
        public int WebCacheMaxAgeDays { get; set; } = 0;
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly object Gate = new();

    /// <summary>Loads the config, or defaults if missing / unreadable.</summary>
    public static IntegrationsConfig Load(IConfiguration cfg)
    {
        var path = DataPaths.IntegrationsConfigPath(cfg);
        lock (Gate)
        {
            try
            {
                if (File.Exists(path))
                {
                    var c = JsonSerializer.Deserialize<IntegrationsConfig>(File.ReadAllText(path));
                    if (c != null) return c;
                }
            }
            catch { /* missing / corrupt → defaults */ }
            return new IntegrationsConfig();
        }
    }

    /// <summary>Persists the config and returns the stored value.</summary>
    public static IntegrationsConfig Save(IConfiguration cfg, IntegrationsConfig c)
    {
        var path = DataPaths.IntegrationsConfigPath(cfg);
        lock (Gate)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(c, Indented));
        }
        return c;
    }
}
