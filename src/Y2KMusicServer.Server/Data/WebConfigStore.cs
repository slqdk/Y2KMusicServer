using System.Text.Json;

namespace Y2KMusicServer.Server.Data;

/// <summary>
/// Listener-facing web settings that the no-migrations rule keeps off the
/// database schema (persistence.md): whether the "Listen Live" button shows,
/// and the per-device request throttle. Persisted as JSON at
/// <c>&lt;DataPath&gt;\web-config.json</c>, a sibling of <c>mixrules.json</c> /
/// <c>network-shares.json</c>. (The category-selector toggle predates this and
/// stays in the <see cref="Entities.Settings"/> row.)
/// </summary>
public static class WebConfigStore
{
    public sealed class WebConfig
    {
        /// <summary>Show the "Listen Live" button on the listener page.</summary>
        public bool ShowListenLive { get; set; } = true;

        /// <summary>Throttle how often one device may request a song.</summary>
        public bool RequestLimitEnabled { get; set; } = false;

        /// <summary>Minutes a device must wait between requests when limiting is on.</summary>
        public int RequestIntervalMinutes { get; set; } = 10;
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly object Gate = new();

    /// <summary>Loads the config, or sensible defaults if missing / unreadable.</summary>
    public static WebConfig Load(IConfiguration cfg)
    {
        var path = DataPaths.WebConfigPath(cfg);
        lock (Gate)
        {
            try
            {
                if (File.Exists(path))
                {
                    var c = JsonSerializer.Deserialize<WebConfig>(File.ReadAllText(path));
                    if (c != null) return Clamp(c);
                }
            }
            catch { /* missing / corrupt → defaults */ }
            return new WebConfig();
        }
    }

    /// <summary>Persists the config (clamped) and returns the stored value.</summary>
    public static WebConfig Save(IConfiguration cfg, WebConfig c)
    {
        c = Clamp(c);
        var path = DataPaths.WebConfigPath(cfg);
        lock (Gate)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(c, Indented));
        }
        return c;
    }

    private static WebConfig Clamp(WebConfig c)
    {
        c.RequestIntervalMinutes = Math.Clamp(c.RequestIntervalMinutes, 1, 1440);
        return c;
    }
}
