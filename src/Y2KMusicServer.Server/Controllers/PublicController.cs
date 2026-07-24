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
        await using (var db = await _dbf.CreateDbContextAsync(ct))
        {
            allowNext = (await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct))?.AllowWebNext ?? false;

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
            type
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
    /// Listener search + browse. Free-text <paramref name="q"/> plus optional
    /// comma-separated <paramref name="genre"/> (genre-map buckets, incl.
    /// "Unknown") and <paramref name="decade"/> (decade start years; 0 =
    /// unknown decade) filters, mirroring the admin facets. With filters set,
    /// an empty <paramref name="q"/> browses the filtered library instead of
    /// returning nothing. Take is clamped 1..30 (browse asks for more than the
    /// old 6-row search).
    /// </summary>
    [HttpGet("search")]
    public async Task<object> Search(
        [FromQuery] string? q, [FromQuery] string? genre, [FromQuery] string? decade,
        [FromQuery] int take = 6, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 30);

        var genres = (genre ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var decades = (decade ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var d) ? (int?)d : null)
            .Where(d => d != null).Select(d => d!.Value).ToHashSet();

        bool hasText = !string.IsNullOrWhiteSpace(q);
        if (!hasText && genres.Count == 0 && decades.Count == 0)
            return new { items = Array.Empty<object>() };

        await using var db = await _dbf.CreateDbContextAsync(ct);
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

        var map = GenreMapStore.Load(_cfg);
        var items = rows
            .Where(t => genres.Count == 0 || genres.Contains(GenreMapStore.EffectiveGenre(map, t)))
            .Where(t => decades.Count == 0 || decades.Contains(GenreMapStore.Decade(t.Year) ?? 0))
            .Take(take)
            .Select(t => new { t.Id, t.Title, t.Artist, t.Album, t.DurationSec })
            .ToList();
        return new { items };
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

        return Ok(new { ok = true, accepted = autoAccept });
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
