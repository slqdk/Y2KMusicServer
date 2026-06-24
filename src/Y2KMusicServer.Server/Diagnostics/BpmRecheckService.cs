using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Audio;
using Y2KMusicServer.Server.Data;

namespace Y2KMusicServer.Server.Diagnostics;

/// <summary>
/// Background library tempo sweep. While verbose logging is on it walks the
/// library one track at a time — a steady trickle, so it never competes with
/// playback or the streaming encoder — re-detecting each stored-BPM track and
/// comparing it to the stored value via <see cref="BpmCompare"/>. Clean
/// confirmations log at Debug; a fresh detection that only matches after an
/// octave fold ("likely off by an octave"), or an outright mismatch ("may be
/// wrong"), logs at Warning so library data problems stand out even amid the
/// Debug stream. One pass per verbose-on session: turning verbose off and on
/// again starts a fresh pass. When verbose is off the loop just idles — no
/// detection, no disk, no CPU.
///
/// Note this is stricter than the live mix-point check (BpmCheckService), which
/// treats an octave fold as a benign match: for *mixing* half/double tempo is
/// fine, but for *data quality* a stored value at the wrong octave is worth
/// flagging.
/// </summary>
public sealed class BpmRecheckService : BackgroundService
{
    private static readonly TimeSpan PerTrack = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);

    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly ILogger _log;
    private readonly BpmDetector _bpm = new();

    public BpmRecheckService(IDbContextFactory<Y2KDbContext> dbf, ILoggerFactory loggerFactory)
    {
        _dbf = dbf;
        _log = loggerFactory.CreateLogger("BpmRecheck");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        List<TrackRow>? pass = null;
        int cursor = 0, flagged = 0;
        bool completeLogged = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Verbose off: idle and reset so a fresh pass runs when it comes on.
            if (!_log.IsEnabled(LogLevel.Debug))
            {
                pass = null; cursor = 0; flagged = 0; completeLogged = false;
                await DelayAsync(Idle, stoppingToken);
                continue;
            }

            if (pass is null)
            {
                pass = await LoadAsync(stoppingToken);
                cursor = 0; flagged = 0; completeLogged = false;
                _log.LogDebug("sweep started ({Count} tracks with stored BPM)", pass.Count);
            }

            if (cursor >= pass.Count)
            {
                if (!completeLogged)
                {
                    _log.LogInformation("sweep complete ({Count} tracks, {Flagged} flagged)", pass.Count, flagged);
                    completeLogged = true;
                }
                await DelayAsync(Idle, stoppingToken);
                continue;
            }

            var tr = pass[cursor++];
            try
            {
                if (!string.IsNullOrEmpty(tr.Path) && File.Exists(tr.Path))
                {
                    var res = _bpm.AnalyzeFile(tr.Path);
                    if (res is not null)
                    {
                        var cmp = BpmCompare.Compare(tr.Stored, res.Bpm);
                        string file = Path.GetFileName(tr.Path);

                        if (cmp.IsMismatch)
                        {
                            flagged++;
                            _log.LogWarning(
                                "⚠ {File}: stored={Stored:F1} live={Live:F1} Δ={Delta:+0.0;-0.0}{Fold} — stored value may be wrong",
                                file, tr.Stored, res.Bpm, cmp.DirectDelta, cmp.Fold);
                        }
                        else if (cmp.Fold.Length > 0)
                        {
                            flagged++;
                            _log.LogWarning(
                                "⚠ {File}: stored={Stored:F1} live={Live:F1}{Fold} — stored value likely off by an octave",
                                file, tr.Stored, res.Bpm, cmp.Fold);
                        }
                        else
                        {
                            _log.LogDebug("{File}: {Verdict} stored={Stored:F1} live={Live:F1}",
                                file, cmp.Verdict, tr.Stored, res.Bpm);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "BpmRecheck failed for {Path}", tr.Path);
            }

            await DelayAsync(PerTrack, stoppingToken);
        }
    }

    private async Task<List<TrackRow>> LoadAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbf.CreateDbContextAsync(ct);
            // Project to an anonymous type (always translatable), then map in
            // memory — keeps EF from having to translate the struct constructor.
            var rows = await db.Tracks.AsNoTracking()
                .Where(t => t.Bpm != null)
                .OrderBy(t => t.Id)
                .Select(t => new { t.FilePath, Bpm = t.Bpm!.Value })
                .ToListAsync(ct);
            return rows.Select(r => new TrackRow(r.FilePath, r.Bpm)).ToList();
        }
        catch
        {
            return new List<TrackRow>();
        }
    }

    private static async Task DelayAsync(TimeSpan d, CancellationToken ct)
    {
        try { await Task.Delay(d, ct); }
        catch (OperationCanceledException) { }
    }

    private readonly record struct TrackRow(string Path, double Stored);
}
