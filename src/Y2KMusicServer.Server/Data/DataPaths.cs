namespace Y2KMusicServer.Server.Data;

/// <summary>
/// Resolves the on-disk SQLite location from configuration. Production points
/// at <c>C:\ProgramData\Y2KMusicServer</c> (appsettings.json); development
/// overrides to <c>.\.dev-data</c> (appsettings.Development.json). The database
/// lives in a <c>data</c> subfolder, alongside <c>logs</c>.
/// </summary>
public static class DataPaths
{
    public static string DataDir(IConfiguration cfg) => cfg["DataPath"] ?? ".";

    public static string DbPath(IConfiguration cfg)
        => Path.Combine(DataDir(cfg), "data", "y2k.db");

    /// <summary>Ensures the <c>data</c> directory exists and returns the db path.</summary>
    public static string EnsureDbPath(IConfiguration cfg)
    {
        var dir = Path.Combine(DataDir(cfg), "data");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "y2k.db");
    }

    /// <summary>
    /// Per-track waveform-peak cache, under <c>data\peaks</c>. Lazily populated
    /// by the waveform endpoint (one <c>&lt;trackId&gt;.json</c> per opened
    /// track), not part of the schema and rebuildable by re-fetching.
    /// </summary>
    public static string PeaksDir(IConfiguration cfg)
        => Path.Combine(DataDir(cfg), "data", "peaks");

    /// <summary>Ensures the peaks cache directory exists and returns it.</summary>
    public static string EnsurePeaksDir(IConfiguration cfg)
    {
        var dir = PeaksDir(cfg);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Per-track audio-structure cache, under <c>data\structure</c>. Lazily
    /// populated by the structure endpoint (one <c>&lt;trackId&gt;.json</c> per
    /// analysed track), not part of the schema and rebuildable by re-fetching.
    /// </summary>
    public static string StructureDir(IConfiguration cfg)
        => Path.Combine(DataDir(cfg), "data", "structure");

    /// <summary>Ensures the structure cache directory exists and returns it.</summary>
    public static string EnsureStructureDir(IConfiguration cfg)
    {
        var dir = StructureDir(cfg);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Auto-mix rules config file, at <c>&lt;DataPath&gt;\mixrules.json</c> —
    /// a sibling of <c>data</c> / <c>logs</c>, signalling config rather than
    /// cache. Persisted as JSON because the no-migrations rule keeps new
    /// operator settings off the database schema.
    /// </summary>
    public static string MixRulesPath(IConfiguration cfg)
        => Path.Combine(DataDir(cfg), "mixrules.json");

    /// <summary>
    /// Network-share credentials file, at
    /// <c>&lt;DataPath&gt;\network-shares.json</c> — a sibling of
    /// <c>mixrules.json</c>, signalling config rather than cache. Stores the SMB
    /// host + username and a DPAPI-encrypted password so the LocalSystem service
    /// can authenticate to network music folders. JSON, not the database
    /// (no-migrations rule).
    /// </summary>
    public static string NetworkSharesPath(IConfiguration cfg)
        => Path.Combine(DataDir(cfg), "network-shares.json");

    /// <summary>
    /// Listener web-settings file, at <c>&lt;DataPath&gt;\web-config.json</c> —
    /// a sibling of <c>mixrules.json</c> / <c>network-shares.json</c>. Holds the
    /// "Listen Live" visibility flag and the per-device request-throttle
    /// settings. JSON, not the database (no-migrations rule).
    /// </summary>
    public static string WebConfigPath(IConfiguration cfg)
        => Path.Combine(DataDir(cfg), "web-config.json");

    /// <summary>
    /// Optional-integration settings file, at
    /// <c>&lt;DataPath&gt;\integrations.json</c> — a sibling of
    /// <c>web-config.json</c>. Holds the third-party integration flags (currently
    /// the YouTube fetch on/off gate). JSON, not the database (no-migrations rule).
    /// </summary>
    public static string IntegrationsConfigPath(IConfiguration cfg)
        => Path.Combine(DataDir(cfg), "integrations.json");

    /// <summary>
    /// Global scan-folder list, at <c>&lt;DataPath&gt;\scan-folders.json</c> —
    /// a sibling of <c>mixrules.json</c>. The one place music folders are
    /// assigned (the per-category folder model is retired). JSON, not the
    /// database, so the list survives a schema recreate.
    /// </summary>
    public static string ScanFoldersPath(IConfiguration cfg)
        => Path.Combine(DataDir(cfg), "scan-folders.json");

    /// <summary>
    /// Operator-editable genre map, at <c>&lt;DataPath&gt;\genre-map.json</c> —
    /// buckets + raw-tag→bucket rules, applied at query time so edits re-bucket
    /// the library instantly without a rescan.
    /// </summary>
    public static string GenreMapPath(IConfiguration cfg)
        => Path.Combine(DataDir(cfg), "genre-map.json");

    /// <summary>
    /// Download cache for web-fetched tracks, at <c>&lt;DataPath&gt;\webcache</c>.
    /// A sibling of <c>data</c> / <c>logs</c>, deliberately NOT under any assigned scan
    /// folder — so the folder-scoped library clear (which owns tracks purely by
    /// path prefix) can never prune these. Each cached track is
    /// <c>&lt;videoId&gt;.mp3</c> with a matching Tracks row; rebuildable by
    /// re-fetching.
    /// </summary>
    public static string WebCacheDir(IConfiguration cfg)
        => Path.Combine(DataDir(cfg), "webcache");

    /// <summary>Ensures the web-cache directory exists and returns it.</summary>
    public static string EnsureWebCacheDir(IConfiguration cfg)
    {
        var dir = WebCacheDir(cfg);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
