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

    /// <summary>
    /// Re-processes one track from its file: re-reads tags (title / artist /
    /// album / genre / year / type / duration) via TagLib — the same read the
    /// scanner does — and re-measures loudness + tempo (LUFS / BPM / beat
    /// phase) with the same analysers the analyze pass uses. Then it drops every
    /// cached mix pair that involves the track and its rebuildable on-disk
    /// waveform + structure caches, so everything re-derives from the current
    /// file. The decode runs off the request thread (a single file is roughly a
    /// second or two). Measurements that fail to read leave the prior value
    /// intact — a transient decode error never blanks a good BPM/LUFS. 404 if
    /// the track is unknown, 422 if its file is missing on disk.
    /// </summary>
    [HttpPost("{id:int}/rescan")]
    public async Task<IActionResult> Rescan(int id, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);

        var t = await db.Tracks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound(new { error = "track not found", trackId = id });

        var path = t.FilePath;
        if (!System.IO.File.Exists(path))
            return UnprocessableEntity(new { error = "file not found on disk", trackId = id });

        // Decode / analyse off the request thread.
        var probe = await Task.Run(() => ProbeFile(path), ct);

        // Tags: keep the scanner's leniency — a blank title falls back to the
        // file name so the row never goes nameless.
        t.Title = probe.Title ?? Path.GetFileNameWithoutExtension(path);
        t.Artist = probe.Artist;
        t.Album = probe.Album;
        t.Genre = probe.Genre;
        t.Year = probe.Year;
        t.Type = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (probe.DurationSec > 0) t.DurationSec = probe.DurationSec;

        // Analysis: only overwrite when the measurement succeeded.
        if (probe.Lufs is double lufs) t.LufsIntegrated = lufs;
        if (probe.Bpm is double bpm)
        {
            t.Bpm = bpm;
            t.BpmConfidence = probe.BpmConfidence;
            t.BeatPhaseOffsetSec = probe.BeatPhase;
        }
        t.ScannedAt = DateTime.UtcNow;

        // Grid / loudness may have changed: drop every cached mix pair that
        // involves this track (same rule as a beat-grid edit) so mix points
        // recompute on next use.
        var stale = await db.MixCache
            .Where(m => m.FromTrackId == id || m.ToTrackId == id)
            .ToListAsync(ct);
        db.MixCache.RemoveRange(stale);

        await db.SaveChangesAsync(ct); // one transaction: track update + cache deletes

        // Drop the rebuildable on-disk caches keyed by this id so the waveform
        // and auto-mix structure re-derive from the current file. Best effort.
        TryDeleteCache(DataPaths.PeaksDir(_cfg), id);
        TryDeleteCache(DataPaths.StructureDir(_cfg), id);

        return Ok(new
        {
            id,
            title = t.Title,
            artist = t.Artist,
            album = t.Album,
            durationSec = t.DurationSec,
            bpm = t.Bpm,
            lufs = t.LufsIntegrated,
            type = t.Type,
            mixCacheCleared = stale.Count
        });
    }

    /// <summary>
    /// Everything we know about one track: the stored DB row plus live
    /// file-system + audio properties read fresh from the file. The live read is
    /// cheap — tags and the container header only, no BPM/LUFS re-measure. 404 if
    /// the track is unknown; a track whose file has moved still returns the stored
    /// fields with <c>fileExists=false</c> and null live properties.
    /// </summary>
    public sealed record GenreOverrideBody(string? Value);

    /// <summary>
    /// Sets or clears the per-track genre override. A non-empty value pins the
    /// track to that genre bucket regardless of the map; null/empty follows the
    /// map again. The value is normalised against the current buckets at query
    /// time, so an override naming a later-deleted bucket degrades to Unknown.
    /// </summary>
    [HttpPost("{id:int}/genre-override")]
    public async Task<IActionResult> SetGenreOverride(int id, [FromBody] GenreOverrideBody? body, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var t = await db.Tracks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound(new { error = "track not found", trackId = id });

        t.GenreOverride = string.IsNullOrWhiteSpace(body?.Value) ? null : body!.Value!.Trim();
        await db.SaveChangesAsync(ct);

        var map = GenreMapStore.Load(_cfg);
        return Ok(new { id = t.Id, genreOverride = t.GenreOverride, genreBucket = GenreMapStore.EffectiveGenre(map, t) });
    }

    [HttpGet("{id:int}/properties")]
    public async Task<IActionResult> Properties(int id, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);

        var t = await db.Tracks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound(new { error = "track not found", trackId = id });

        var map = GenreMapStore.Load(_cfg);

        var path = t.FilePath;
        var exists = System.IO.File.Exists(path);
        var facts = exists ? await Task.Run(() => ReadFileFacts(path), ct) : FileFacts.Missing;

        return Ok(new
        {
            id = t.Id,
            filePath = path,
            fileName = Path.GetFileName(path),
            fileExists = exists,
            fileSizeBytes = facts.SizeBytes,
            modifiedUtc = facts.ModifiedUtc,
            title = t.Title,
            artist = t.Artist,
            album = t.Album,
            year = t.Year,
            genre = t.Genre,
            genreOverride = t.GenreOverride,
            genreBucket = GenreMapStore.EffectiveGenre(map, t),
            decade = GenreMapStore.Decade(t.Year),
            type = t.Type,
            durationSec = t.DurationSec,
            bpm = t.Bpm,
            bpmConfidence = t.BpmConfidence,
            beatPhaseOffsetSec = t.BeatPhaseOffsetSec,
            lufsIntegrated = t.LufsIntegrated,
            scannedAtUtc = t.ScannedAt,
            audioBitrateKbps = facts.BitrateKbps,
            sampleRateHz = facts.SampleRateHz,
            channels = facts.Channels,
            codec = facts.Codec
        });
    }

    /// <summary>Reads tags + measures loudness/tempo for one file. Each step is
    /// independently fault-tolerant: a failure leaves that field null so the
    /// caller can preserve the prior stored value.</summary>
    private static FileProbe ProbeFile(string path)
    {
        string? title = null, artist = null, album = null, genre = null;
        int? year = null;
        double duration = 0;
        try
        {
            using var tf = TagLib.File.Create(path);
            title = NullIfBlank(tf.Tag.Title);
            artist = NullIfBlank(tf.Tag.FirstPerformer) ?? NullIfBlank(tf.Tag.FirstAlbumArtist);
            album = NullIfBlank(tf.Tag.Album);
            genre = NullIfBlank(tf.Tag.FirstGenre);
            if (tf.Tag.Year > 0) year = (int)tf.Tag.Year;
            duration = tf.Properties?.Duration.TotalSeconds ?? 0;
        }
        catch { /* unreadable tags — keep nulls; caller falls back to file name */ }

        double? lufs = null;
        try
        {
            var l = new LoudnessAnalyzer().AnalyzeFile(path);
            if (l is double v && !double.IsNaN(v) && !double.IsInfinity(v)) lufs = v;
        }
        catch { /* unmeasurable — leave null, prior value kept */ }

        double? bpm = null, conf = null, phase = null;
        try
        {
            var b = new BpmDetector().AnalyzeFile(path);
            if (b != null) { bpm = b.Bpm; conf = b.Confidence; phase = b.BeatPhaseOffsetSec; }
        }
        catch { /* leave null */ }

        return new FileProbe(title, artist, album, genre, year, duration, lufs, bpm, conf, phase);
    }

    private static void TryDeleteCache(string dir, int id)
    {
        try
        {
            var file = Path.Combine(dir, id + ".json");
            if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
        }
        catch { /* best effort */ }
    }

    /// <summary>Cheap, fault-tolerant read of file-system size/date plus the
    /// audio container header (bitrate, sample rate, channels, codec). No tag
    /// re-parse beyond what the header needs and no DSP — safe to call on a
    /// request thread via Task.Run.</summary>
    private static FileFacts ReadFileFacts(string path)
    {
        long size = 0;
        DateTime? modified = null;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists) { size = fi.Length; modified = fi.LastWriteTimeUtc; }
        }
        catch { /* leave defaults */ }

        int? bitrate = null, sampleRate = null, channels = null;
        string? codec = null;
        try
        {
            using var tf = TagLib.File.Create(path);
            var pr = tf.Properties;
            if (pr is not null)
            {
                if (pr.AudioBitrate > 0) bitrate = pr.AudioBitrate;
                if (pr.AudioSampleRate > 0) sampleRate = pr.AudioSampleRate;
                if (pr.AudioChannels > 0) channels = pr.AudioChannels;
                codec = NullIfBlank(pr.Description);
            }
        }
        catch { /* unreadable header — leave nulls */ }

        return new FileFacts(size, modified, bitrate, sampleRate, channels, codec);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private sealed record FileProbe(
        string? Title, string? Artist, string? Album, string? Genre, int? Year,
        double DurationSec, double? Lufs, double? Bpm, double? BpmConfidence, double? BeatPhase);

    private sealed record FileFacts(
        long SizeBytes, DateTime? ModifiedUtc,
        int? BitrateKbps, int? SampleRateHz, int? Channels, string? Codec)
    {
        public static readonly FileFacts Missing = new(0, null, null, null, null, null);
    }
}

/// <summary>Body for a beat-grid edit: tempo (BPM) and downbeat phase (seconds).</summary>
public sealed record BeatGridUpdate(double Bpm, double PhaseOffsetSec);
