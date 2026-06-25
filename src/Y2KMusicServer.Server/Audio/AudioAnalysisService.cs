using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Data;

namespace Y2KMusicServer.Server.Audio;

/// <summary>
/// Runs the audio-analysis pass over the library: decodes each track and fills
/// the analysis columns — EBU R128 loudness (<c>LufsIntegrated</c>) and tempo
/// (<c>Bpm</c> / <c>BpmConfidence</c> / <c>BeatPhaseOffsetSec</c>). Mirrors
/// <c>LibraryScanner</c>: a singleton that raises <see cref="Progress"/> events
/// (forwarded to SignalR by <c>AnalysisHubBroadcaster</c>) and exposes a
/// pollable snapshot. Only one pass runs at a time. By default each track is
/// analysed only for the columns it's missing; a full re-run re-measures all.
/// </summary>
public sealed class AudioAnalysisService
{
    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly ILogger<AudioAnalysisService> _log;

    private int _running; // 0 = idle, 1 = running
    private volatile AnalysisProgress _current = new() { State = AnalysisState.Idle };

    public AudioAnalysisService(IDbContextFactory<Y2KDbContext> dbf, ILogger<AudioAnalysisService> log)
    {
        _dbf = dbf;
        _log = log;
    }

    public event Action<AnalysisProgress>? Progress;
    public AnalysisProgress Current => _current;
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public bool TryStart(bool reanalyzeAll = false, int? folderId = null)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return false;

        _ = Task.Run(() =>
        {
            try { Run(reanalyzeAll, folderId); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Audio analysis failed");
                Emit(new AnalysisProgress { State = AnalysisState.Failed, Message = ex.Message });
            }
            finally { Interlocked.Exchange(ref _running, 0); }
        });
        return true;
    }

    private sealed record Item(int Id, string Path, string? Title, bool HasLufs, bool HasBpm);

    private sealed class Result
    {
        public double? Lufs;
        public double? Bpm;
        public double? BpmConfidence;
        public double? BeatPhase;
        public bool Any => Lufs != null || Bpm != null;
    }

    private void Run(bool reanalyzeAll, int? folderId)
    {
        Emit(new AnalysisProgress { State = AnalysisState.Running, Message = "Selecting tracks" });

        List<Item> items;
        using (var db = _dbf.CreateDbContext())
        {
            var q = db.Tracks.AsNoTracking();
            if (!reanalyzeAll) q = q.Where(t => t.LufsIntegrated == null || t.Bpm == null);

            // Folder-scoped pass (chained after a single-folder scan): only that
            // folder's own tracks, with "innermost wins" so nested folders aren't
            // pulled in. Null folder → whole library (a full re-run or direct call).
            if (folderId is int fid)
            {
                var folder = db.CategoryFolders.AsNoTracking().FirstOrDefault(f => f.Id == fid);
                if (folder != null)
                {
                    var allFolders = db.CategoryFolders.AsNoTracking().Select(f => f.Path).ToList();
                    q = q.OwnedBy(folder.Path, FolderScope.NestedPrefixes(folder.Path, allFolders));
                }
            }

            items = q.OrderBy(t => t.Id)
                .Select(t => new Item(t.Id, t.FilePath, t.Title, t.LufsIntegrated != null, t.Bpm != null))
                .ToList();
        }

        int total = items.Count;
        if (total == 0)
        {
            Emit(new AnalysisProgress { State = AnalysisState.Completed, Total = 0, Message = "Nothing to analyse" });
            return;
        }

        int workers = ReadWorkers();
        var results = new ConcurrentDictionary<int, Result>();
        int processed = 0, updated = 0, failed = 0;
        long lastEmitTicks = 0;

        Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = workers }, item =>
        {
            var res = new Result();
            bool needLufs = reanalyzeAll || !item.HasLufs;
            bool needBpm = reanalyzeAll || !item.HasBpm;

            try
            {
                if (File.Exists(item.Path))
                {
                    if (needLufs)
                    {
                        var l = new LoudnessAnalyzer().AnalyzeFile(item.Path);
                        if (l is double v && !double.IsNaN(v) && !double.IsInfinity(v)) res.Lufs = v;
                    }
                    if (needBpm)
                    {
                        var b = new BpmDetector().AnalyzeFile(item.Path);
                        if (b != null) { res.Bpm = b.Bpm; res.BpmConfidence = b.Confidence; res.BeatPhase = b.BeatPhaseOffsetSec; }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Analysis failed for {Path}", item.Path);
            }

            if (res.Any) { results[item.Id] = res; Interlocked.Increment(ref updated); }
            else Interlocked.Increment(ref failed);

            int done = Interlocked.Increment(ref processed);
            long now = DateTime.UtcNow.Ticks;
            if (now - Interlocked.Read(ref lastEmitTicks) > TimeSpan.TicksPerMillisecond * 200 || done == total)
            {
                Interlocked.Exchange(ref lastEmitTicks, now);
                Emit(new AnalysisProgress
                {
                    State = AnalysisState.Running,
                    Total = total,
                    Processed = done,
                    Updated = Volatile.Read(ref updated),
                    Failed = Volatile.Read(ref failed),
                    CurrentTitle = item.Title
                });
            }
        });

        PersistResults(results);

        Emit(new AnalysisProgress
        {
            State = AnalysisState.Completed,
            Total = total,
            Processed = processed,
            Updated = updated,
            Failed = failed,
            Message = $"Analysed {updated} of {total} ({failed} unmeasurable/missing)"
        });
        _log.LogInformation("Audio analysis complete: {Updated}/{Total} analysed, {Failed} skipped.",
            updated, total, failed);
    }

    private void PersistResults(ConcurrentDictionary<int, Result> results)
    {
        if (results.IsEmpty) return;
        var ids = results.Keys.ToList();
        const int batch = 200;
        using var db = _dbf.CreateDbContext();
        for (int i = 0; i < ids.Count; i += batch)
        {
            var slice = ids.GetRange(i, Math.Min(batch, ids.Count - i));
            var tracks = db.Tracks.Where(t => slice.Contains(t.Id)).ToList();
            foreach (var t in tracks)
            {
                if (!results.TryGetValue(t.Id, out var r)) continue;
                if (r.Lufs != null) t.LufsIntegrated = r.Lufs;
                if (r.Bpm != null)
                {
                    t.Bpm = r.Bpm;
                    t.BpmConfidence = r.BpmConfidence;
                    t.BeatPhaseOffsetSec = r.BeatPhase;
                }
            }
            db.SaveChanges();
        }
    }

    private int ReadWorkers()
    {
        try
        {
            using var db = _dbf.CreateDbContext();
            var w = db.Settings.AsNoTracking().FirstOrDefault()?.ScanWorkers ?? 4;
            return Math.Max(1, Math.Min(w, 16));
        }
        catch { return 4; }
    }

    private void Emit(AnalysisProgress p)
    {
        _current = p;
        Progress?.Invoke(p);
    }
}
