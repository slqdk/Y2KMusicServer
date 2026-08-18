using Microsoft.AspNetCore.Mvc;
using Y2KMusicServer.Server.Integrations;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// Admin endpoints for optional third-party integrations — the YouTube fetch
/// path. Surfaces: a read-only preflight <c>check</c>; the on/off + cache-cap
/// <c>settings</c> (persisted to integrations.json, not the DB — no-migrations
/// rule); gated <c>search</c> / <c>fetch</c> that find a track on YouTube Music
/// and download it into the local cache as an ordinary library track; and
/// <c>cache</c> housekeeping (size + a clear). The caller queues a fetched track
/// via the existing <c>/api/admin/playlist/add</c>.
///
/// SECURITY POSTURE: unauthenticated by design, like the rest of
/// <c>/api/admin</c> (single-operator LAN tool — see architecture.md).
/// </summary>
[ApiController]
[Route("api/admin/integrations")]
public sealed class AdminIntegrationsController : ControllerBase
{
    private readonly IConfiguration _cfg;
    private readonly YouTubeProbe _probe;
    private readonly YouTubeFetchService _youtube;
    private readonly WebCacheHousekeeper _cache;
    private readonly YouTubeDownloadService _downloads;

    public AdminIntegrationsController(IConfiguration cfg, YouTubeProbe probe,
                                       YouTubeFetchService youtube, WebCacheHousekeeper cache,
                                       YouTubeDownloadService downloads)
    {
        _cfg = cfg;
        _probe = probe;
        _youtube = youtube;
        _cache = cache;
        _downloads = downloads;
    }

    // ── Preflight (always available — you test before enabling) ────────────

    /// <summary>
    /// Runs the YouTube preflight and returns a per-stage pass/fail report. May
    /// take up to ~a minute (a live dry-run extraction). Downloads no media and
    /// persists nothing; ignores the on/off flag on purpose.
    /// </summary>
    [HttpGet("youtube/check")]
    public async Task<IActionResult> CheckYouTube(CancellationToken ct)
        => Ok(await _probe.CheckAsync(ct));

    // ── On/off + cache caps (JSON store; no-migrations rule) ───────────────

    public sealed record YouTubeSettings(bool Enabled, int CacheMaxMB, int CacheMaxAgeDays,
                                        string DownloadFolder, string? FolderWarning);

    // Partial update: only the fields provided are changed, so a caller that
    // sends just { enabled } leaves the caps untouched (and vice versa).
    public sealed record YouTubeSettingsUpdate(bool? Enabled, int? CacheMaxMB, int? CacheMaxAgeDays,
                                              string? DownloadFolder);

    [HttpGet("youtube/settings")]
    public IActionResult GetSettings() => Ok(CurrentSettings());

    /// <summary>
    /// Saves the gate / caps / download folder. A download folder that overlaps a
    /// Music folder is rejected outright rather than stored: the folder-scoped
    /// library clear owns tracks by path prefix, so a nested YouTube folder would
    /// be pruned along with the collection it sits in.
    /// </summary>
    [HttpPut("youtube/settings")]
    public IActionResult PutSettings([FromBody] YouTubeSettingsUpdate body)
    {
        var c = IntegrationsStore.Load(_cfg);
        if (body?.Enabled is bool en) c.YouTubeEnabled = en;
        if (body?.CacheMaxMB is int mb) c.WebCacheMaxMB = System.Math.Max(0, mb);
        if (body?.CacheMaxAgeDays is int age) c.WebCacheMaxAgeDays = System.Math.Max(0, age);

        if (body?.DownloadFolder is string folder)
        {
            var trimmed = folder.Trim();
            var clash = Overlap(trimmed);
            if (clash != null)
                return BadRequest(new { error = clash });
            c.DownloadFolder = trimmed;
        }

        IntegrationsStore.Save(_cfg, c);
        return Ok(CurrentSettings());
    }

    private YouTubeSettings CurrentSettings()
    {
        var c = IntegrationsStore.Load(_cfg);
        return new YouTubeSettings(c.YouTubeEnabled, c.WebCacheMaxMB, c.WebCacheMaxAgeDays,
            _downloads.TargetFolder(), _downloads.ValidateFolder());
    }

    // A blank folder means "use the default under ProgramData", which is always
    // outside the Music folders, so only a typed path is checked.
    private string? Overlap(string folder)
    {
        if (folder.Length == 0) return null;
        var self = Y2KMusicServer.Server.Data.FolderScope.Prefix(folder);
        foreach (var scan in Y2KMusicServer.Server.Data.ScanFolderStore.AllPaths(_cfg))
        {
            var pre = Y2KMusicServer.Server.Data.FolderScope.Prefix(scan);
            if (self.StartsWith(pre, System.StringComparison.OrdinalIgnoreCase)
                || pre.StartsWith(self, System.StringComparison.OrdinalIgnoreCase))
                return $"That folder overlaps the Music folder \"{scan}\". Pick a folder outside your music library.";
        }
        return null;
    }

    // ── Downloads (paste a link, get the song in the library) ──────────────

    public sealed record DownloadRequest(string Urls);

    /// <summary>
    /// Queues one or more downloads. The body's text can hold links (one per line
    /// or space-separated), bare video ids, playlist / album links, or plain
    /// search words. Returns immediately — the queue is drained in the background;
    /// poll <c>GET youtube/downloads</c> for progress.
    /// </summary>
    [HttpPost("youtube/downloads")]
    public async Task<IActionResult> Enqueue([FromBody] DownloadRequest req, CancellationToken ct)
    {
        if (!Enabled()) return Disabled();
        if (req is null || string.IsNullOrWhiteSpace(req.Urls))
            return BadRequest(new { error = "Paste a YouTube link first." });

        var (queued, error) = await _downloads.EnqueueAsync(req.Urls, ct);
        if (queued == 0) return BadRequest(new { error = error ?? "Nothing could be queued." });
        return Ok(new { queued, warning = error, jobs = _downloads.Jobs() });
    }

    /// <summary>Current + recent download jobs, newest first. Ungated so the
    /// console can still show what happened after the feature is switched off.</summary>
    [HttpGet("youtube/downloads")]
    public IActionResult Downloads()
        => Ok(new { folder = _downloads.TargetFolder(), busy = _downloads.Busy, jobs = _downloads.Jobs() });

    /// <summary>Cancels a queued or running download.</summary>
    [HttpPost("youtube/downloads/{id:int}/cancel")]
    public IActionResult CancelDownload(int id)
        => Ok(new { cancelled = _downloads.Cancel(id) });

    /// <summary>Drops finished / failed jobs from the list (files are untouched).</summary>
    [HttpPost("youtube/downloads/clear")]
    public IActionResult ClearDownloads()
        => Ok(new { removed = _downloads.ClearFinished(), jobs = _downloads.Jobs() });

    // ── Search + fetch (gated by the on/off flag) ──────────────────────────

    /// <summary>YouTube Music search (metadata only, fast). Requires the feature on.</summary>
    [HttpGet("youtube/search")]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int? limit, CancellationToken ct)
    {
        if (!Enabled()) return Disabled();
        if (string.IsNullOrWhiteSpace(q)) return BadRequest(new { error = "q is required" });
        var items = await _youtube.SearchAsync(q, limit ?? 10, ct);
        return Ok(items);
    }

    public sealed record FetchRequest(string VideoId);

    /// <summary>
    /// Downloads a chosen result's audio into the cache and indexes it as a
    /// library track; returns the track id (queue it via /api/admin/playlist/add).
    /// Idempotent by video id. Enforces the cache caps afterwards. Requires the
    /// feature on.
    /// </summary>
    [HttpPost("youtube/fetch")]
    public async Task<IActionResult> Fetch([FromBody] FetchRequest req, CancellationToken ct)
    {
        if (!Enabled()) return Disabled();
        if (req is null || string.IsNullOrWhiteSpace(req.VideoId))
            return BadRequest(new { error = "videoId is required" });
        var r = await _youtube.FetchAsync(req.VideoId.Trim(), ct);
        if (r.Ok)
        {
            // Best-effort: a housekeeping hiccup must never fail the fetch.
            try { await _cache.EnforceBoundsAsync(ct); } catch { /* logged inside */ }
        }
        return r.Ok ? Ok(r) : StatusCode(502, r);
    }

    // ── Cache housekeeping (ungated — usable even after turning the feature off) ──

    /// <summary>Web-cache size + count, and how many cached tracks are pinned
    /// (playing / armed / queued and so not evictable).</summary>
    [HttpGet("youtube/cache")]
    public async Task<IActionResult> Cache(CancellationToken ct)
        => Ok(await _cache.StatsAsync(ct));

    /// <summary>Removes every idle cached track (keeps anything in use).</summary>
    [HttpPost("youtube/cache/clear")]
    public async Task<IActionResult> ClearCache(CancellationToken ct)
        => Ok(await _cache.ClearAsync(ct));

    private bool Enabled() => IntegrationsStore.Load(_cfg).YouTubeEnabled;

    private IActionResult Disabled()
        => StatusCode(403, new { error = "YouTube integration is off. Enable it in Settings." });
}
