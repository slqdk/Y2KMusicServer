using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Audio;
using Y2KMusicServer.Server.Data;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// Per-track endpoints for the admin beat-grid editor. Serves a cached waveform
/// plus the track's current beat grid. The write side (persisting a hand-edited
/// grid) lands in a later ship under the same route.
/// </summary>
[ApiController]
[Route("api/admin/track")]
public sealed class AdminTrackController : ControllerBase
{
    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly IConfiguration _cfg;

    public AdminTrackController(IDbContextFactory<Y2KDbContext> dbf, IConfiguration cfg)
    {
        _dbf = dbf;
        _cfg = cfg;
    }

    /// <summary>
    /// Waveform peaks (interleaved min/max per window, signed bytes -127..127)
    /// plus the track's stored beat grid. Peaks are computed from the file on
    /// the first request and cached on disk; later requests read the cache. The
    /// grid (<c>bpm</c> / <c>phaseOffsetSec</c>) always comes live from the
    /// database, so a future grid edit is reflected without recomputing the
    /// waveform. 404 if the track is unknown or its file cannot be decoded.
    /// </summary>
    [HttpGet("{id:int}/waveform")]
    public async Task<IActionResult> Waveform(int id, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);

        var t = await db.Tracks.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();

        WaveformData data;
        try
        {
            // Decode/cache off the request thread; the first build for a track
            // decodes the whole file (~hundreds of ms), later hits are instant.
            data = await Task.Run(() => WaveformPeaks.GetOrBuild(_cfg, id, t.FilePath), ct);
        }
        catch
        {
            // File missing or undecodable.
            return NotFound();
        }

        return Ok(new
        {
            samplesPerPoint = data.SamplesPerPoint,
            sampleRate = data.SampleRate,
            durationSec = t.DurationSec,
            bpm = t.Bpm,
            phaseOffsetSec = t.BeatPhaseOffsetSec,
            peaks = data.Peaks
        });
    }

    /// <summary>
    /// Audio-derived structure for the auto-mix planner: energy + vocal-presence
    /// curves, the derived vocal segments, instrumental intro/outro boundaries,
    /// and coarse drop markers. Computed from the file on the first request and
    /// cached on disk (<c>data\structure\&lt;id&gt;.json</c>); later requests read
    /// the cache. The beat / phrase grid is intentionally absent — it is derived
    /// live from the track's stored grid, so a grid edit never invalidates this.
    /// 404 if the track is unknown or its file cannot be decoded.
    /// </summary>
    [HttpGet("{id:int}/structure")]
    public async Task<IActionResult> Structure(int id, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);

        var t = await db.Tracks.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();

        TrackStructureData data;
        try
        {
            // Decode/analyse off the request thread; the first build decodes the
            // whole file, later hits read the cache.
            data = await Task.Run(() => TrackStructure.GetOrBuild(_cfg, id, t.FilePath), ct);
        }
        catch
        {
            return NotFound();
        }

        return Ok(data);
    }

    /// <summary>
    /// Persists a hand-edited beat grid (tempo + downbeat phase) onto the track
    /// and drops every cached mix pair that involves it, so mix points snapped
    /// to the old grid recompute against the new one on next use. Track update
    /// and cache deletes commit in one transaction. An already-armed crossfade
    /// keeps the points it was prepared with — the edit takes effect on the next
    /// queue. 404 if the track is unknown, 400 on an out-of-range tempo.
    /// </summary>
    [HttpPut("{id:int}/beatgrid")]
    public async Task<IActionResult> SetBeatGrid(int id, [FromBody] BeatGridUpdate? dto, CancellationToken ct)
    {
        if (dto is null) return BadRequest("Body required.");
        if (!(dto.Bpm > 20 && dto.Bpm < 400)) return BadRequest("Bpm out of range (20-400).");

        double phase = dto.PhaseOffsetSec;
        if (double.IsNaN(phase) || double.IsInfinity(phase) || phase < 0) phase = 0;

        await using var db = await _dbf.CreateDbContextAsync(ct);

        var t = await db.Tracks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();

        t.Bpm = dto.Bpm;
        t.BeatPhaseOffsetSec = phase;

        var stale = await db.MixCache
            .Where(m => m.FromTrackId == id || m.ToTrackId == id)
            .ToListAsync(ct);
        db.MixCache.RemoveRange(stale);

        await db.SaveChangesAsync(ct); // one transaction: track update + cache deletes

        return Ok(new
        {
            id,
            bpm = t.Bpm,
            phaseOffsetSec = t.BeatPhaseOffsetSec,
            mixCacheCleared = stale.Count
        });
    }
}

/// <summary>Body for a beat-grid edit: tempo (BPM) and downbeat phase (seconds).</summary>
public sealed record BeatGridUpdate(double Bpm, double PhaseOffsetSec);
