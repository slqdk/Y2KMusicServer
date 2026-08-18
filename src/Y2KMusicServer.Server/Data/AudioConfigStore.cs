using System.Text.Json;

namespace Y2KMusicServer.Server.Data;

/// <summary>
/// Audio-output settings that the no-migrations rule keeps off the database
/// schema (persistence.md): currently just whether the decks play on the
/// server machine's sound card. Persisted as JSON at
/// <c>&lt;DataPath&gt;\audio-config.json</c>, a sibling of
/// <c>web-config.json</c> / <c>integrations.json</c>.
/// </summary>
public static class AudioConfigStore
{
    public sealed class AudioConfig
    {
        /// <summary>Decks try the machine's sound card (true, the historical
        /// behaviour) or are forced to the silent pump (false). The /stream
        /// broadcast is independent of this either way.</summary>
        public bool LocalAudioEnabled { get; set; } = true;

        /// <summary>Level the music is held at while the DJ page's talk-over
        /// button is held, as a percentage of normal.</summary>
        public int DuckLevelPercent { get; set; } = 20;

        /// <summary>Seconds for the talk-over and fade-pause ramps, each way.</summary>
        public double FadeSeconds { get; set; } = 5;
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly object Gate = new();

    /// <summary>Loads the config, or defaults if missing / unreadable.</summary>
    public static AudioConfig Load(IConfiguration cfg)
    {
        var path = DataPaths.AudioConfigPath(cfg);
        lock (Gate)
        {
            try
            {
                if (File.Exists(path))
                {
                    var c = JsonSerializer.Deserialize<AudioConfig>(File.ReadAllText(path));
                    if (c != null)
                    {
                        c.DuckLevelPercent = Math.Clamp(c.DuckLevelPercent, 0, 100);
                        c.FadeSeconds = Math.Clamp(c.FadeSeconds, 0.2, 30);
                        return c;
                    }
                }
            }
            catch { /* missing / corrupt → defaults */ }
            return new AudioConfig();
        }
    }

    /// <summary>Persists the config and returns the stored value.</summary>
    public static AudioConfig Save(IConfiguration cfg, AudioConfig c)
    {
        var path = DataPaths.AudioConfigPath(cfg);
        lock (Gate)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(c, Indented));
        }
        return c;
    }
}
