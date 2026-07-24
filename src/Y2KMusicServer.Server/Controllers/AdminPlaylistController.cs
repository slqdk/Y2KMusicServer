using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Audio;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Data.Entities;
using Y2KMusicServer.Server.Playback;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// Admin playlist + Auto DJ control. The playlist is the ordered queue the
/// engine drains; Auto DJ tops it up. The <c>autodj/settings</c> and
/// <c>autodj/fill</c> endpoints exist so the feature is testable before the
/// Phase 4 settings UI lands — mirroring the streaming-toggle precedent.
/// </summary>
[ApiController]
[Route("api/admin")]
public sealed class AdminPlaylistController : ControllerBase
{
    private readonly PlaylistService _playlist;
    private readonly AudioEngine _engine;
    private readonly IDbContextFactory<Y2KDbContext> _dbf;

    public AdminPlaylistController(
        PlaylistService playlist, AudioEngine engine, IDbContextFactory<Y2KDbContext> dbf)
    {
        _playlist = playlist;
        _engine = engine;
        _dbf = dbf;
    }

    // ── Playlist ──────────────────────────────────────────────────────────────

    [HttpGet("playlist")]
    public async Task<IReadOnlyList<PlaylistItemDto>> Get(CancellationToken ct)
        => await _playlist.GetAsync(ct);

    /// <summary>
    /// Adds a track. <c>source</c> defaults to <c>Manual</c>; Manual/Request
    /// adds insert before the next Auto entry, Auto adds append. <c>atEnd=true</c>
    /// appends the pick to the very end of the queue instead.
    /// </summary>
    [HttpPost("playlist/add")]
    public async Task<IActionResult> Add(
        [FromQuery] int trackId, [FromQuery] string? source, [FromQuery] bool atEnd, CancellationToken ct)
    {
        if (!Enum.TryParse<PlaylistSource>(source ?? "Manual", ignoreCase: true, out var src))
            return BadRequest(new { error = "source must be Auto, Manual, or Request" });

        int? current = _engine.GetStatus().TrackId;
        var r = await _playlist.AddAsync(trackId, src, addedBy: src.ToString(), current, ct, atEnd: atEnd);
        return r switch
        {
            PlaylistAddResult.Ok => Ok(await _playlist.GetAsync(ct)),
            PlaylistAddResult.NotFound => NotFound(new { error = "track not found", trackId }),
            _ => StatusCode(500)
        };
    }

    [HttpDelete("playlist/{id:int}")]
    public async Task<IActionResult> Remove(int id, CancellationToken ct)
        => await _playlist.RemoveAsync(id, ct)
            ? Ok(await _playlist.GetAsync(ct))
            : NotFound(new { error = "playlist entry not found", id });

    /// <summary>Clears upcoming entries, keeping the currently playing track.</summary>
    [HttpPost("playlist/clear")]
    public async Task<IActionResult> Clear(CancellationToken ct)
    {
        int? current = _engine.GetStatus().TrackId;
        int removed = await _playlist.ClearUpcomingAsync(current, ct);
        return Ok(new { removed, playlist = await _playlist.GetAsync(ct) });
    }

    /// <summary>
    /// Activates a saved playlist: the live queue is replaced by its tracks
    /// (pending requests survive and play first), and the engine crossfades off
    /// the current track into the first upcoming entry. With the engine
    /// stopped, the first entry is loaded and started instead of crossfaded.
    /// 404 unknown playlist, 422 empty playlist.
    /// </summary>
    [HttpPost("playlist/activate")]
    public async Task<IActionResult> Activate([FromQuery] int playlistId, CancellationToken ct)
    {
        var status = _engine.GetStatus();
        int? current = status.TrackId;

        var r = await _playlist.ActivateSavedAsync(playlistId, current, ct);
        if (r.Result == PlaylistService.ActivateResult.NotFound)
            return NotFound(new { error = "playlist not found", playlistId });
        if (r.Result == PlaylistService.ActivateResult.Empty)
            return UnprocessableEntity(new { error = "playlist is empty", playlistId });

        int? first = await _playlist.NextUpcomingTrackIdAsync(current, ct);
        string action = "queuedOnly";
        if (first is int tid)
        {
            if (status.State == PlaybackEngineState.Playing && current != null)
            {
                var q = await _engine.NextAsync(tid, ct);
                action = q == QueueResult.Ok ? "crossfaded" : $"crossfadeFailed:{q}";
            }
            else
            {
                var l = await _engine.LoadAsync(tid, ct);
                if (l == LoadResult.Ok) { _engine.Play(); action = "started"; }
                else action = $"loadFailed:{l}";
            }
        }

        return Ok(new
        {
            activated = playlistId,
            action,
            queued = r.Added,
            skippedMissing = r.SkippedMissing,
            skippedAlreadyQueued = r.SkippedDuplicate,
            playlist = await _playlist.GetAsync(ct)
        });
    }

    // ── Auto DJ ─────────────────────────────────────────────────────────────

    /// <summary>Forces a top-up now (handy for testing). Returns the count added.</summary>
    [HttpPost("autodj/fill")]
    public async Task<IActionResult> Fill(CancellationToken ct)
    {
        int added = await _playlist.TopUpAsync(ct);
        return Ok(new { added, playlist = await _playlist.GetAsync(ct) });
    }

    /// <summary>
    /// Reads / updates the Auto DJ settings without hand-editing the database.
    /// Any omitted query param is left unchanged.
    /// </summary>
    [HttpGet("autodj/settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
        => Ok(await ReadAutoDjAsync(ct));

    [HttpPost("autodj/settings")]
    public async Task<IActionResult> SetSettings(
        [FromQuery] bool? on, [FromQuery] int? tracks, [FromQuery] int? bpmDev, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var s = await db.Settings.FirstOrDefaultAsync(ct);
        if (s == null) return Conflict(new { error = "settings row missing" });

        if (on is bool b) s.AutoDj = b;
        if (tracks is int t) s.AutoDjTracks = Math.Clamp(t, 1, 20);
        if (bpmDev is int d) s.AutoDjBpmDev = Math.Clamp(d, 0, 50);
        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            autoDj = s.AutoDj,
            tracks = s.AutoDjTracks,
            bpmDev = s.AutoDjBpmDev
        });
    }

    private async Task<object> ReadAutoDjAsync(CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        return new
        {
            autoDj = s?.AutoDj ?? false,
            tracks = s?.AutoDjTracks ?? 3,
            bpmDev = s?.AutoDjBpmDev ?? 5
        };
    }
}
