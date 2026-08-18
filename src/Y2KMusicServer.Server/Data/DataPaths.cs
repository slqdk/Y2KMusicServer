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
    /// How far the live queue has played, at
    /// <c>&lt;DataPath&gt;\playhead.json</c> — survives a restart so playback
    /// resumes at the first unplayed entry. JSON, not the database
    /// (no-migrations rule).
    /// </summary>
    public static string PlayheadPath(IConfiguration cfg)
        => Path.Combine(DataDir(cfg), "playhead.json");

    /// <summary>
    /// The Auto DJ feed toggles (which saved playlists feed the queue right now
    /// irrespective of their schedule), at
    /// <c>&lt;DataPath&gt;\autodj-feeds.json</c>. JSON, not the database
    /// (no-migrations rule).
    /// </summary>
    public static string AutoDjFeedsPath(IConfiguration cfg)
        => Path.Combine(DataDir(cfg), "autodj-feeds.json");

    /// <summary>
    /// Google Cast settings + the remembered speaker allow-list, at
    /// <c>&lt;DataPath&gt;\cast-config.json</c>. JSON, not the database
    /// (no-migrations rule).
    /// </summary>
    public static string CastConfigPath(IConfiguration cfg)
        => Path.Combine(DataDir(cfg), "cast-config.json");

    /// <summary>
    /// Audio black-box dumps, under <c>&lt;DataPath&gt;\diagnostics</c> — WAV
    /// captures of the deck taps / post-mix ring written on demand or on a
    /// detected audio anomaly. Pure diagnostics output, safe to delete.
    /// </summary>
    public static string DiagnosticsDir(IConfiguration cfg)
        => Path.Combine(DataDir(cfg), "diagnostics");

    /// <summary>Ensures the diagnostics directory exists and returns it.</summary>
    public static string EnsureDiagnosticsDir(IConfiguration cfg)
    {
        var dir = DiagnosticsDir(cfg);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Audio-output settings file, at <c>&lt;DataPath&gt;\audio-config.json</c> —
    /// a sibling of <c>web-config.json</c>. Holds the local-audio (server
    /// speakers) on/off switch. JSON, not the database (no-migrations rule).
    /// </summary>
    public static string AudioConfigPath(IConfiguration cfg)
        => Path.Combine(DataDir(cfg), "audio-config.json");

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

    /// <summary>
    /// Default landing folder for pasted-link YouTube downloads, at
    /// <c>&lt;DataPath&gt;\youtube</c>. The operator can point this anywhere
    /// (integrations.json → DownloadFolder), but wherever it points it must sit
    /// OUTSIDE every Music folder: the folder-scoped library clear owns tracks
    /// purely by path prefix, so a YouTube folder nested in a scanned one would
    /// be pruned with it.
    /// </summary>
    public static string YouTubeDownloadDir(IConfiguration cfg)
        => Path.Combine(DataDir(cfg), "youtube");

    /// <summary>
    /// Scratch space for in-flight downloads, at
    /// <c>&lt;DataPath&gt;\youtube-tmp</c>. Downloads and the ffmpeg transcode
    /// always happen here on local disk — even when the target folder is an SMB
    /// share — so only the finished file crosses the network and a half-written
    /// file never appears in the library folder. Safe to delete when idle.
    /// </summary>
    public static string YouTubeTempDir(IConfiguration cfg)
        => Path.Combine(DataDir(cfg), "youtube-tmp");

    /// <summary>Ensures the download scratch directory exists and returns it.</summary>
    public static string EnsureYouTubeTempDir(IConfiguration cfg)
    {
        var dir = YouTubeTempDir(cfg);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Record of which YouTube videos have already been downloaded and where they
    /// landed, at <c>&lt;DataPath&gt;\youtube-downloads.json</c>. Lets a re-pasted
    /// link be recognised even though the file is named after artist and title.
    /// </summary>
    public static string YouTubeDownloadsIndexPath(IConfiguration cfg)
        => Path.Combine(DataDir(cfg), "youtube-downloads.json");
}
