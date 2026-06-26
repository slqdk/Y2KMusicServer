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

    public PublicController(AudioEngine engine, StreamingEncoder stream, PlaylistService playlist,
        IDbContextFactory<Y2KDbContext> dbf)
    {
        _engine = engine;
        _stream = stream;
        _playlist = playlist;
        _dbf = dbf;
    }

    public sealed record RequestBody(int TrackId, string? RequesterName);
    public sealed record CategorySelectBody(int[] CategoryIds);

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
        return Ok(new { enabled = st.Enabled, bitrate = st.Bitrate, listeners = st.Listeners });
    }

    [HttpGet("search")]
    public async Task<object> Search([FromQuery] string? q, [FromQuery] int take = 30, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 50);
        if (string.IsNullOrWhiteSpace(q)) return new { items = Array.Empty<object>() };

        var term = q.Trim();
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var items = await db.Tracks.AsNoTracking()
            .Where(t =>
                (t.Title != null && EF.Functions.Like(t.Title, $"%{term}%")) ||
                (t.Artist != null && EF.Functions.Like(t.Artist, $"%{term}%")) ||
                (t.Album != null && EF.Functions.Like(t.Album, $"%{term}%")))
            .OrderBy(t => t.Artist).ThenBy(t => t.Title)
            .Take(take)
            .Select(t => new { t.Id, t.Title, t.Artist, t.Album, t.DurationSec })
            .ToListAsync(ct);
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

        var name = body.RequesterName?.Trim();
        if (name is { Length: > 40 }) name = name[..40];

        db.Requests.Add(new Data.Entities.Request
        {
            TrackId = body.TrackId,
            RequesterName = string.IsNullOrWhiteSpace(name) ? null : name,
            Status = RequestStatus.Pending,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }

    /// <summary>Categories for the listener bar + the current override selection.</summary>
    [HttpGet("categories")]
    public async Task<object> Categories(CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var show = (await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct))?.ShowWebCategories ?? false;
        var cats = await db.Categories.AsNoTracking()
            .Where(c => c.Enabled)
            .OrderBy(c => c.DisplayOrder)
            .Select(c => new
            {
                c.Id,
                c.Name,
                Count = db.Tracks.Count(t => t.CategoryId == c.Id)
            })
            .ToListAsync(ct);
        return new { showSelector = show, selected = _playlist.GetWebCategories(), categories = cats };
    }

    [HttpPost("category-select")]
    public async Task<IActionResult> CategorySelect([FromBody] CategorySelectBody? body, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (!(settings?.ShowWebCategories ?? false))
            return StatusCode(403, new { error = "category selection is disabled" });

        // Only allow enabled category ids through (empty = clear the override).
        var enabled = await db.Categories.AsNoTracking().Where(c => c.Enabled).Select(c => c.Id).ToListAsync(ct);
        var ids = (body?.CategoryIds ?? Array.Empty<int>()).Where(enabled.Contains).ToArray();

        // Set the override first so the rebuild below draws from the picked
        // categories. Empty clears it and falls back to the Auto DJ schedule.
        _playlist.SetWebCategories(ids);

        // Apply the switch immediately: replace whatever is queued with tracks
        // from the new selection (or the schedule) and crossfade to the first of
        // them as soon as possible, so the music moves to the picks right away.
        // This needs Auto DJ on — it's what fills the queue; with it off we just
        // store the override for when it's turned on.
        if (settings is not { AutoDj: true })
            return Ok(new { selected = ids, applied = false, reason = "autoDjOff" });

        int? currentTrackId = _engine.GetStatus().TrackId;
        await _playlist.ClearUpcomingAsync(currentTrackId, ct);
        int added = await _playlist.TopUpAsync(ct);
        if (added == 0)
            return Ok(new { selected = ids, applied = false, reason = "noTracks" });

        // Crossfade off the current track into the first freshly-queued one
        // ("Next" to a track named on the spot — prepares Deck B and fades now).
        int? firstUpcoming = await _playlist.NextUpcomingTrackIdAsync(currentTrackId, ct);
        if (firstUpcoming is int tid)
            await _engine.NextAsync(tid, ct);

        return Ok(new { selected = ids, applied = true, added });
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
