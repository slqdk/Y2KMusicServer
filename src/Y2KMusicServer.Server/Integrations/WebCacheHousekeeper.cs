using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Audio;
using Y2KMusicServer.Server.Data;

namespace Y2KMusicServer.Server.Integrations;

/// <summary>Current state of the web-download cache.</summary>
public sealed record WebCacheStats(int TrackCount, long Bytes, int PinnedCount, int MaxMB, int MaxAgeDays);

/// <summary>Outcome of a cache clear / eviction pass.</summary>
public sealed record WebCacheClearResult(int Removed, long FreedBytes, int Remaining);

/// <summary>
/// Housekeeping for the web-download cache — the yt-dlp MP3s under
/// <c>&lt;DataPath&gt;\webcache</c> and their Tracks rows. Reports size, clears on
/// demand, and enforces optional size / age caps (from integrations.json) after
/// each fetch. It NEVER evicts a track that is on air, armed as the next track, or
/// sitting in the playlist — only idle cached tracks are removed. Eviction is
/// oldest-first by cache time (<see cref="Track.ScannedAt"/>), since the
/// no-migrations rule rules out a last-played column.
/// </summary>
public sealed class WebCacheHousekeeper
{
    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly AudioEngine _engine;
    private readonly IConfiguration _cfg;
    private readonly ILogger<WebCacheHousekeeper> _log;

    public WebCacheHousekeeper(IDbContextFactory<Y2KDbContext> dbf, AudioEngine engine,
                               IConfiguration cfg, ILogger<WebCacheHousekeeper> log)
    {
        _dbf = dbf;
        _engine = engine;
        _cfg = cfg;
        _log = log;
    }

    private sealed record WebTrackFile(int Id, string FilePath, DateTime? ScannedAt, long Bytes);

    public async Task<WebCacheStats> StatsAsync(CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var pinned = await PinnedIdsAsync(db, ct);
        var web = await LoadWebTracksAsync(db, ct);
        var c = IntegrationsStore.Load(_cfg);
        return new WebCacheStats(
            web.Count, web.Sum(w => w.Bytes), web.Count(w => pinned.Contains(w.Id)),
            c.WebCacheMaxMB, c.WebCacheMaxAgeDays);
    }

    /// <summary>Removes every idle (not playing / armed / queued) cached track.</summary>
    public async Task<WebCacheClearResult> ClearAsync(CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var pinned = await PinnedIdsAsync(db, ct);
        var web = await LoadWebTracksAsync(db, ct);
        var remove = web.Where(w => !pinned.Contains(w.Id)).ToList();
        if (remove.Count == 0) return new WebCacheClearResult(0, 0, web.Count);
        long freed = await RemoveAsync(db, remove, ct);
        _log.LogInformation("Web cache: cleared {N} track(s), freed {MB:F1} MB", remove.Count, freed / 1048576.0);
        return new WebCacheClearResult(remove.Count, freed, web.Count - remove.Count);
    }

    /// <summary>Evicts oldest idle cached tracks to satisfy the size / age caps.
    /// No-op when both caps are 0 (the defaults). Safe to call after every fetch.</summary>
    public async Task EnforceBoundsAsync(CancellationToken ct)
    {
        var c = IntegrationsStore.Load(_cfg);
        if (c.WebCacheMaxMB <= 0 && c.WebCacheMaxAgeDays <= 0) return;

        await using var db = await _dbf.CreateDbContextAsync(ct);
        var pinned = await PinnedIdsAsync(db, ct);
        var web = (await LoadWebTracksAsync(db, ct))
            .Where(w => !pinned.Contains(w.Id))
            .OrderBy(w => w.ScannedAt ?? DateTime.MinValue)   // oldest first
            .ToList();

        var evict = new HashSet<int>();
        if (c.WebCacheMaxAgeDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-c.WebCacheMaxAgeDays);
            foreach (var w in web)
                if ((w.ScannedAt ?? DateTime.MinValue) < cutoff) evict.Add(w.Id);
        }
        if (c.WebCacheMaxMB > 0)
        {
            long cap = (long)c.WebCacheMaxMB * 1048576L;
            long total = web.Where(w => !evict.Contains(w.Id)).Sum(w => w.Bytes);
            foreach (var w in web)   // oldest first
            {
                if (total <= cap) break;
                if (!evict.Add(w.Id)) continue;
                total -= w.Bytes;
            }
        }
        if (evict.Count == 0) return;

        var remove = web.Where(w => evict.Contains(w.Id)).ToList();
        long freed = await RemoveAsync(db, remove, ct);
        _log.LogInformation("Web cache: evicted {N} track(s), freed {MB:F1} MB (cap {Cap} MB, age {Age} d)",
            remove.Count, freed / 1048576.0, c.WebCacheMaxMB, c.WebCacheMaxAgeDays);
    }

    // ── internals ───────────────────────────────────────────────────────────

    private string CachePrefix()
        => DataPaths.WebCacheDir(_cfg).TrimEnd('\\', '/') + "\\";

    // Tracks that must not be evicted: the on-air track, the armed next track, and
    // anything sitting in the playlist.
    private async Task<HashSet<int>> PinnedIdsAsync(Y2KDbContext db, CancellationToken ct)
    {
        var pinned = new HashSet<int>();
        var st = _engine.GetStatus();
        if (st.TrackId is int a) pinned.Add(a);
        if (st.NextTrackId is int n) pinned.Add(n);
        foreach (var id in await db.PlaylistEntries.Select(p => p.TrackId).Distinct().ToListAsync(ct))
            pinned.Add(id);
        return pinned;
    }

    private async Task<List<WebTrackFile>> LoadWebTracksAsync(Y2KDbContext db, CancellationToken ct)
    {
        var prefix = CachePrefix();
        var rows = await db.Tracks.Where(t => t.FilePath.StartsWith(prefix))
            .Select(t => new { t.Id, t.FilePath, t.ScannedAt })
            .ToListAsync(ct);
        return rows.Select(r => new WebTrackFile(r.Id, r.FilePath, r.ScannedAt, FileBytes(r.FilePath))).ToList();
    }

    private async Task<long> RemoveAsync(Y2KDbContext db, List<WebTrackFile> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.Id).ToList();
        // Delete order mirrors the folder-clear in AdminCategoriesController.
        await db.PlaylistEntries.Where(p => ids.Contains(p.TrackId)).ExecuteDeleteAsync(ct);
        await db.Requests.Where(r => ids.Contains(r.TrackId)).ExecuteDeleteAsync(ct);
        await db.MixCache.Where(m => ids.Contains(m.FromTrackId) || ids.Contains(m.ToTrackId)).ExecuteDeleteAsync(ct);
        await db.Tracks.Where(t => ids.Contains(t.Id)).ExecuteDeleteAsync(ct);

        long freed = 0;
        foreach (var i in items)
        {
            try { if (File.Exists(i.FilePath)) { File.Delete(i.FilePath); freed += i.Bytes; } }
            catch { /* momentarily locked (e.g. just went on air) → leave for a later pass */ }
        }
        return freed;
    }

    private static long FileBytes(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }
}
