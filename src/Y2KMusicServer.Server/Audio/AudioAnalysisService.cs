using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Network;

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
    private readonly NetworkShareConnector _connector;
    private readonly IConfiguration _cfg;
    private readonly ILogger<AudioAnalysisService> _log;

    private readonly object _gate = new();
    private readonly Queue<(bool All, int? Folder)> _queue = new();
    private bool _busy; // worker loop active (draining the queue)
    private volatile AnalysisProgress _current = new() { State = AnalysisState.Idle };

    public AudioAnalysisService(IDbContextFactory<Y2KDbContext> dbf, NetworkShareConnector connector,
        IConfiguration cfg, ILogger<AudioAnalysisService> log)
    {
        _dbf = dbf;
        _connector = connector;
        _cfg = cfg;
        _log = log;
    }

    public event Action<AnalysisProgress>? Progress;
    public AnalysisProgress Current => _current;
    public bool IsRunning { get { lock (_gate) return _busy; } }

    /// <summary>
    /// Queues an analysis pass and makes sure the worker is running. Only one
    /// pass runs at a time; further requests wait in FIFO order, so each scoped
    /// scan's chained analysis still runs even when scans are stacked back to
    /// back. An identical request already waiting is coalesced. Always returns
    /// true. <paramref name="folderId"/> scopes to one folder's tracks (null =
    /// whole library); <paramref name="reanalyzeAll"/> re-measures, not just fills.
    /// </summary>
    public bool TryStart(bool reanalyzeAll = false, int? folderId = null)
    {
        lock (_gate)
        {
            if (!_queue.Any(j => j.All == reanalyzeAll && j.Folder == folderId))
                _queue.Enqueue((reanalyzeAll, folderId));
            if (_busy) return true; // a worker is already draining the queue
            _busy = true;
        }

        _ = Task.Run(DrainQueue);
        return true;
    }

    private void DrainQueue()
    {
        while (true)
        {
            (bool All, int? Folder) job;
            lock (_gate)
            {
                if (_queue.Count == 0) { _busy = false; return; }
                job = _queue.Dequeue();
            }

            try
            {
                Run(job.All, job.Folder);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Audio analysis failed");
                Emit(new AnalysisProgress { State = AnalysisState.Failed, Message = ex.Message });
            }
        }
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
                var folder = ScanFolderStore.Find(_cfg, fid);
                if (folder != null)
                {
                    var allFolders = ScanFolderStore.AllPaths(_cfg);
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

        // Make sure any network shares holding these tracks are authenticated
        // before decoding — analysis can run from boot (the startup resume) before
        // the WNet session is up, and reads would otherwise fail. Mirrors the
        // scanner; local files have no share root and are skipped. Windows-only.
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in items
                .Select(i => NetworkShareConnector.ShareRoot(i.Path))
                .Where(r => r is not null).Select(r => r!)
                .Distinct(StringComparer.OrdinalIgnoreCase))
                _connector.EnsureConnected(root);
        }

        int processed = 0, updated = 0, failed = 0;
        long lastEmitTicks = 0;

        // Persist incrementally so an interrupted pass leaves its finished work on
        // disk. Measurement is CPU-bound and parallel, but SQLite is single-writer,
        // so completed results are handed to ONE writer task that commits them in
        // small batches — by count, or after a short interval, whichever comes
        // first. Because the pass is missing-only, every committed track is skipped
        // on the next run, so closing mid-pass costs at most the last uncommitted
        // batch instead of the whole library.
        const int flushEvery = 50;                    // checkpoint every N measured tracks
        var flushInterval = TimeSpan.FromSeconds(3);  // …or this often, whichever first
        var pending = new BlockingCollection<(int Id, Result Res)>();

        var writer = Task.Run(() =>
        {
            using var db = _dbf.CreateDbContext();
            var buf = new List<(int Id, Result Res)>(flushEvery);
            var lastFlush = DateTime.UtcNow;

            void Flush()
            {
                if (buf.Count == 0) return;
                var map = buf.ToDictionary(x => x.Id, x => x.Res);
                var ids = map.Keys.ToList();
                var tracks = db.Tracks.Where(t => ids.Contains(t.Id)).ToList();
                foreach (var t in tracks)
                {
                    var r = map[t.Id];
                    if (r.Lufs != null) t.LufsIntegrated = r.Lufs;
                    if (r.Bpm != null)
                    {
                        t.Bpm = r.Bpm;
                        t.BpmConfidence = r.BpmConfidence;
                        t.BeatPhaseOffsetSec = r.BeatPhase;
                    }
                }
                db.SaveChanges();
                db.ChangeTracker.Clear();   // one long-lived context — don't accumulate tracked rows
                buf.Clear();
                lastFlush = DateTime.UtcNow;
            }

            // Drain until the producers signal completion and the queue empties.
            // The take timeout lets a partial batch flush on the interval even while
            // every worker is busy decoding the next (slow) track.
            while (!pending.IsCompleted)
            {
                if (pending.TryTake(out var rec, 500)) buf.Add(rec);
                if (buf.Count >= flushEvery || (buf.Count > 0 && DateTime.UtcNow - lastFlush >= flushInterval))
                    Flush();
            }
            Flush();   // final partial batch on clean completion
        });

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

            if (res.Any) { pending.Add((item.Id, res)); Interlocked.Increment(ref updated); }
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

        pending.CompleteAdding();
        writer.Wait();   // let the writer commit the last results before we report done

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
