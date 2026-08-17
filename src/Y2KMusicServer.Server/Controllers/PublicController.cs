using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Audio;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Data.Entities;
using Y2KMusicServer.Server.Playback;
using Y2KMusicServer.Server.Streaming;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// The public listener API (no admin auth). Backs the listener page: what's
/// playing, the stream info, search + request, the category-selection bar, and
/// the gated "skip" button. All read-only except request submission, the
/// category override, and the gated skip.
/// </summary>
[ApiController]
[Route("api")]
public sealed class PublicController : ControllerBase
{
    private readonly AudioEngine _engine;
    private readonly StreamingEncoder _stream;
    private readonly PlaylistService _playlist;
    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly IConfiguration _cfg;

    public PublicController(AudioEngine engine, StreamingEncoder stream, PlaylistService playlist,
        IDbContextFactory<Y2KDbContext> dbf, IConfiguration cfg)
    {
        _engine = engine;
        _stream = stream;
        _playlist = playlist;
        _dbf = dbf;
        _cfg = cfg;
    }

    public sealed record RequestBody(int TrackId, string? RequesterName, string? DeviceId);

    [HttpGet("nowplaying")]
    public async Task<object> NowPlaying(CancellationToken ct)
    {
        var s = _engine.GetStatus();
        bool allowNext;
        double? bpm = null;
        string? genre = null;
        int? year = null;
        string? type = null;
        var web = WebConfigStore.Load(_cfg);
        await using (var db = await _dbf.CreateDbContextAsync(ct))
        {
            allowNext = (await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct))?.AllowWebNext ?? false;

            // Timed skip: with a minutes gate set, the visitor Next only
            // unlocks once the current song has played that long. The client
            // hides the button until allowNext turns true.
            if (allowNext && web.WebNextAfterMinutes > 0)
                allowNext = s.TrackId != null && s.PositionSec >= web.WebNextAfterMinutes * 60;

            // Enrich with the current track's tags for the now-playing chips.
            if (s.TrackId is int tid)
            {
                var meta = await db.Tracks.AsNoTracking()
                    .Where(t => t.Id == tid)
                    .Select(t => new { t.Bpm, t.Genre, t.Year, t.Type })
                    .FirstOrDefaultAsync(ct);
                if (meta != null)
                {
                    bpm = meta.Bpm;
                    genre = meta.Genre;
                    year = meta.Year;
                    type = meta.Type;
                }
            }
        }

        return new
        {
            trackId = s.TrackId,
            title = s.Title,
            artist = s.Artist,
            album = s.Album,
            positionSec = s.PositionSec,
            durationSec = s.DurationSec,
            playing = s.State == PlaybackEngineState.Playing,
            allowNext,
            bpm,
            genre,
            year,
            type,
            bannerText = string.IsNullOrWhiteSpace(web.BannerText) ? null : web.BannerText,
            bannerColor = web.BannerColor
        };
    }

    [HttpGet("stream/info")]
    public IActionResult StreamInfo()
    {
        var st = _stream.GetStatus();
        var web = WebConfigStore.Load(_cfg);
        // The broadcast is always on (no enable switch); the field is kept so
        // the listener page's off-air handling stays wired for the future.
        return Ok(new { enabled = true, bitrate = st.Bitrate, listeners = st.Listeners, showListenLive = web.ShowListenLive });
    }

    /// <summary>
    /// Listener search + browse. Modes, first match wins:
    /// <c>albumName</c> — that album's songs (the album-tile drill-down);
    /// <c>playlist</c> — a saved playlist's songs in playlist order, optionally
    /// narrowed by <paramref name="q"/>; free text — matching songs plus an
    /// <c>albums</c> group for the album row. Legacy <paramref name="genre"/> /
    /// <paramref name="decade"/> facets still apply to text mode.
    /// Every mode dedupes same-song format twins: FLAC is preferred, the MP3
    /// shows only when no FLAC matches. Take is clamped 1..30.
    /// </summary>
    [HttpGet("search")]
    public async Task<object> Search(
        [FromQuery] string? q, [FromQuery] string? genre, [FromQuery] string? decade,
        [FromQuery] int? playlist, [FromQuery] string? albumName,
        [FromQuery] int take = 6, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 30);
        bool hasText = !string.IsNullOrWhiteSpace(q);

        await using var db = await _dbf.CreateDbContextAsync(ct);

        // ── Album drill-down ──────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(albumName))
        {
            var name = albumName.Trim();
            var albumRows = await db.Tracks.AsNoTracking()
                .Where(t => t.Album != null && t.Album.ToLower() == name.ToLower())
                .OrderBy(t => t.Artist).ThenBy(t => t.Title)
                .ToListAsync(ct);
            albumRows = ApplyFolderVisibility(albumRows);
            return new { items = ToItems(PreferFlac(albumRows).Take(take)) };
        }

        // ── Saved-playlist browse (playlist order; optional text narrow) ──
        if (playlist is int plId)
        {
            var plRows = await db.SavedPlaylistTracks.AsNoTracking()
                .Where(pt => pt.SavedPlaylistId == plId && pt.Track != null)
                .OrderBy(pt => pt.Position)
                .Select(pt => pt.Track!)
                .ToListAsync(ct);
            if (hasText)
            {
                var t2 = q!.Trim();
                plRows = plRows.Where(t =>
                    (t.Title ?? "").Contains(t2, StringComparison.OrdinalIgnoreCase) ||
                    (t.Artist ?? "").Contains(t2, StringComparison.OrdinalIgnoreCase) ||
                    (t.Album ?? "").Contains(t2, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            return new { items = ToItems(PreferFlac(plRows).Take(take)) };
        }

        // ── Free text (+ legacy facets) ───────────────────────────────────
        var genres = (genre ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var decades = (decade ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var d) ? (int?)d : null)
            .Where(d => d != null).Select(d => d!.Value).ToHashSet();

        if (!hasText && genres.Count == 0 && decades.Count == 0)
            return new { items = Array.Empty<object>(), albums = Array.Empty<object>() };

        var query = db.Tracks.AsNoTracking().AsQueryable();
        if (hasText)
        {
            var term = q!.Trim();
            query = query.Where(t =>
                (t.Title != null && EF.Functions.Like(t.Title, $"%{term}%")) ||
                (t.Artist != null && EF.Functions.Like(t.Artist, $"%{term}%")) ||
                (t.Album != null && EF.Functions.Like(t.Album, $"%{term}%")));
        }

        var rows = await query
            .OrderBy(t => t.Artist).ThenBy(t => t.Title)
            .ToListAsync(ct);
        rows = ApplyFolderVisibility(rows);

        // ── Fallback: nothing matched the literal phrase ─────────────────
        // "metallica the black" finds nothing (the album is titled just
        // "Metallica"), so loosen: first every meaningful word anywhere on the
        // row, then the single word with the broadest hit (usually the artist
        // or album) — and tell the page what was actually searched so it can
        // say so. Only for plain text searches (no legacy facets).
        string? fallbackQuery = null;
        if (hasText && rows.Count == 0 && genres.Count == 0 && decades.Count == 0)
        {
            var all = ApplyFolderVisibility(await db.Tracks.AsNoTracking()
                .OrderBy(t => t.Artist).ThenBy(t => t.Title)
                .ToListAsync(ct));
            (rows, fallbackQuery) = FallbackSearch(all, q!.Trim());
        }

        var map = GenreMapStore.Load(_cfg);
        var deduped = PreferFlac(rows
            .Where(t => genres.Count == 0 || genres.Contains(GenreMapStore.EffectiveGenre(map, t)))
            .Where(t => decades.Count == 0 || decades.Contains(GenreMapStore.Decade(t.Year) ?? 0)))
            .ToList();

        // Album row: grouped over the FULL (deduped) match set, not the taken
        // page, so an album whose songs sit past the take still surfaces.
        var albums = deduped
            .Where(t => !string.IsNullOrWhiteSpace(t.Album))
            .GroupBy(t => t.Album!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var artists = g.Select(t => t.Artist).Where(a => !string.IsNullOrWhiteSpace(a))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
                return new
                {
                    album = g.Key,
                    artist = artists.Count == 1 ? artists[0] : "Various artists",
                    trackId = g.First().Id,     // representative, for the cover art
                    count = g.Count()
                };
            })
            .OrderByDescending(a => a.count).ThenBy(a => a.album, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        return new { items = ToItems(deduped.Take(take)), albums, fallbackQuery };
    }

    private static readonly HashSet<string> StopTokens = new(StringComparer.OrdinalIgnoreCase)
        { "the", "a", "an", "of", "and", "in", "on", "to", "og", "med", "feat", "ft" };

    /// <summary>
    /// Loose matching for a phrase that found nothing. Stage 1: every
    /// meaningful (non-stopword) token appears somewhere on the row — any
    /// field. Stage 2: the single token with the most hits, longer tokens
    /// winning ties, which surfaces the artist or album the listener probably
    /// meant. Returns the matched rows plus the query actually used, or an
    /// empty set when even single tokens hit nothing.
    /// </summary>
    private static (List<Track> rows, string? usedQuery) FallbackSearch(List<Track> all, string term)
    {
        static bool Hit(Track t, string tok) =>
            (t.Title ?? "").Contains(tok, StringComparison.OrdinalIgnoreCase) ||
            (t.Artist ?? "").Contains(tok, StringComparison.OrdinalIgnoreCase) ||
            (t.Album ?? "").Contains(tok, StringComparison.OrdinalIgnoreCase);

        var tokens = term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var core = tokens.Where(t => !StopTokens.Contains(t)).ToList();
        if (core.Count == 0) core = tokens;

        if (core.Count > 1)
        {
            var hit = all.Where(t => core.All(tok => Hit(t, tok))).ToList();
            if (hit.Count > 0) return (hit, string.Join(' ', core));
        }

        var best = core
            .Select(tok => (tok, rows: all.Where(t => Hit(t, tok)).ToList()))
            .Where(x => x.rows.Count > 0)
            .OrderByDescending(x => x.rows.Count).ThenByDescending(x => x.tok.Length)
            .FirstOrDefault();
        return best.rows is { Count: > 0 } ? (best.rows, best.tok) : (new List<Track>(), null);
    }

    /// <summary>
    /// Same-song format twins collapse to one row: FLAC wins over MP3 (and any
    /// other type); the MP3 only shows when no FLAC matched. Twin = same
    /// artist + title, case-insensitive. Untitled rows never collapse. Keeps
    /// the incoming order (first occurrence of each song).
    /// </summary>
    private static IEnumerable<Track> PreferFlac(IEnumerable<Track> rows) =>
        rows.GroupBy(t => string.IsNullOrWhiteSpace(t.Title)
                ? $"#{t.Id}"
                : $"{(t.Artist ?? "").Trim().ToLowerInvariant()}|{t.Title!.Trim().ToLowerInvariant()}")
            .Select(g => g
                .OrderByDescending(t => string.Equals(t.Type, "FLAC", StringComparison.OrdinalIgnoreCase))
                .ThenBy(t => t.Id)
                .First());

    /// <summary>
    /// Drops tracks whose owning scan folder is deactivated (Folders dialog).
    /// A search/browse filter only — playlist browsing and playback never
    /// pass through here. No-op (and no per-track cost) while every folder
    /// is active.
    /// </summary>
    private List<Track> ApplyFolderVisibility(List<Track> rows)
    {
        var hidden = ScanFolderStore.HiddenPathPredicate(_cfg);
        return hidden == null ? rows : rows.Where(t => !hidden(t.FilePath)).ToList();
    }

    private static List<object> ToItems(IEnumerable<Track> rows) =>
        rows.Select(t => (object)new { t.Id, t.Title, t.Artist, t.Album, t.DurationSec }).ToList();

    /// <summary>
    /// The saved playlists for the listener browse band (replaces the retired
    /// genre/decade chips): every playlist, admin-tile order, with its track
    /// count. <c>showSelector</c> carries the operator's Settings toggle
    /// (formerly the category-selector flag).
    /// </summary>
    [HttpGet("playlists")]
    public async Task<object> Playlists(CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var show = (await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct))?.ShowWebCategories ?? false;
        var playlists = await db.SavedPlaylists.AsNoTracking()
            .OrderBy(p => p.TileOrder).ThenBy(p => p.Name)
            .Select(p => new { id = p.Id, name = p.Name, count = p.Tracks.Count })
            .ToListAsync(ct);
        return new { showSelector = show, playlists };
    }

    /// <summary>
    /// The public playlist (now-playing head + upcoming), trimmed for listeners.
    /// Read-only; returns a bare array ordered as the engine serves it.
    /// </summary>
    [HttpGet("playlist")]
    public async Task<object> Playlist(CancellationToken ct)
    {
        var items = await _playlist.GetAsync(ct);
        return items.Select(p => new
        {
            position = p.Position,
            trackId = p.TrackId,
            title = p.Title,
            artist = p.Artist,
            durationSec = p.DurationSec,
            source = p.Source
        });
    }

    [HttpPost("request")]
    public async Task<IActionResult> Request([FromBody] RequestBody? body, CancellationToken ct)
    {
        if (body == null) return BadRequest(new { error = "request body required" });

        await using var db = await _dbf.CreateDbContextAsync(ct);
        if (!await db.Tracks.AnyAsync(t => t.Id == body.TrackId, ct))
            return NotFound(new { error = "track not found", body.TrackId });

        // Per-device request throttle (web-config.json). Keyed by the device id
        // the listener page sends; falls back to the caller IP if absent.
        var web = WebConfigStore.Load(_cfg);
        if (web.RequestLimitEnabled)
        {
            var key = string.IsNullOrWhiteSpace(body.DeviceId)
                ? (HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown")
                : body.DeviceId.Trim();
            var wait = RequestThrottle.Check(key, TimeSpan.FromMinutes(web.RequestIntervalMinutes));
            if (wait is TimeSpan w)
                return StatusCode(429, new
                {
                    error = "rate_limited",
                    retryAfterSec = (int)Math.Ceiling(w.TotalSeconds),
                    totalSec = web.RequestIntervalMinutes * 60,
                    intervalMinutes = web.RequestIntervalMinutes
                });
        }

        var name = body.RequesterName?.Trim();
        if (name is { Length: > 40 }) name = name[..40];

        // Auto-accept (web-config.json) short-circuits the DJ approve step: the
        // request is stored as Accepted and dropped straight into the playlist as
        // a Request entry, exactly as AdminRequestsController.Accept would.
        bool autoAccept = web.AutoAcceptRequests;

        db.Requests.Add(new Data.Entities.Request
        {
            TrackId = body.TrackId,
            RequesterName = string.IsNullOrWhiteSpace(name) ? null : name,
            Status = autoAccept ? RequestStatus.Accepted : RequestStatus.Pending,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);

        if (autoAccept)
        {
            int? current = _engine.GetStatus().TrackId;
            await _playlist.AddAsync(body.TrackId, PlaylistSource.Request, name ?? "Request", current, ct);
        }

        return Ok(new
        {
            ok = true,
            accepted = autoAccept,
            cooldownSec = web.RequestLimitEnabled ? web.RequestIntervalMinutes * 60 : 0
        });
    }

    /// <summary>
    /// The device's remaining request cooldown, for the listener page's
    /// countdown line on load/refresh. Read-only — never stamps a request.
    /// Zeros when the limit is off or the window has elapsed.
    /// </summary>
    [HttpGet("request/cooldown")]
    public object RequestCooldown([FromQuery] string? deviceId)
    {
        var web = WebConfigStore.Load(_cfg);
        if (!web.RequestLimitEnabled) return new { remainingSec = 0, totalSec = 0 };

        var key = string.IsNullOrWhiteSpace(deviceId)
            ? (HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown")
            : deviceId.Trim();
        var wait = RequestThrottle.Remaining(key, TimeSpan.FromMinutes(web.RequestIntervalMinutes));
        return new
        {
            remainingSec = wait is TimeSpan w ? (int)Math.Ceiling(w.TotalSeconds) : 0,
            totalSec = web.RequestIntervalMinutes * 60
        };
    }

    /// <summary>
    /// The listener browse filters: the genre buckets and decades present in
    /// the library, each with a live count, mirroring the admin facets.
    /// <c>showSelector</c> carries the operator's Settings toggle (formerly the
    /// category-selector flag). These filter the request browser only — the
    /// old play-by-category queue override is retired with the category model
    /// (Auto DJ is driven by the saved playlists' schedules now).
    /// </summary>
    [HttpGet("browse-filters")]
    public async Task<object> BrowseFilters(CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var show = (await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct))?.ShowWebCategories ?? false;

        var rows = await db.Tracks.AsNoTracking()
            .Select(t => new { t.Genre, t.GenreOverride, t.Year })
            .ToListAsync(ct);

        var map = GenreMapStore.Load(_cfg);
        var byBucket = rows
            .GroupBy(r => !string.IsNullOrWhiteSpace(r.GenreOverride)
                ? GenreMapStore.Resolve(map, r.GenreOverride)
                : GenreMapStore.Resolve(map, r.Genre))
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var genres = map.Buckets
            .Select(b => new { name = b, count = byBucket.TryGetValue(b, out var n) ? n : 0 })
            .Append(new
            {
                name = GenreMapStore.Unknown,
                count = byBucket.TryGetValue(GenreMapStore.Unknown, out var u) ? u : 0
            })
            .Where(g => g.count > 0)
            .ToList();

        var decades = rows
            .GroupBy(r => GenreMapStore.Decade(r.Year) ?? 0)
            .Select(g => new { decade = g.Key, count = g.Count() })
            .Where(d => d.count > 0)
            .OrderBy(d => d.decade == 0 ? int.MaxValue : d.decade)
            .ToList();

        return new { showSelector = show, genres, decades };
    }

    [HttpPost("next")]
    public async Task<IActionResult> Next(CancellationToken ct)
    {
        await using (var db = await _dbf.CreateDbContextAsync(ct))
            if (!((await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct))?.AllowWebNext ?? false))
                return StatusCode(403, new { error = "web skip is disabled" });

        // The timed gate is enforced here too — hiding the button client-side
        // is UX, this is the rule.
        var webCfg = WebConfigStore.Load(_cfg);
        if (webCfg.WebNextAfterMinutes > 0)
        {
            var st = _engine.GetStatus();
            if (st.TrackId == null || st.PositionSec < webCfg.WebNextAfterMinutes * 60)
                return StatusCode(403, new { error = "web skip not unlocked yet" });
        }

        var r = await _engine.NextAsync(null, ct);
        return r == QueueResult.Ok ? Ok(new { ok = true }) : Conflict(new { error = r.ToString() });
    }

    [HttpGet("albumart")]
    public async Task<IActionResult> AlbumArt([FromQuery] int trackId, CancellationToken ct)
    {
        string? path;
        await using (var db = await _dbf.CreateDbContextAsync(ct))
            path = await db.Tracks.AsNoTracking().Where(t => t.Id == trackId)
                .Select(t => t.FilePath).FirstOrDefaultAsync(ct);

        if (path == null || !System.IO.File.Exists(path)) return NotFound();

        try
        {
            using var tf = TagLib.File.Create(path);
            var pic = tf.Tag.Pictures.FirstOrDefault();
            if (pic == null || pic.Data.Count == 0) return NotFound();
            var mime = string.IsNullOrEmpty(pic.MimeType) ? "image/jpeg" : pic.MimeType;
            return File(pic.Data.Data, mime);
        }
        catch { return NotFound(); }
    }
}
