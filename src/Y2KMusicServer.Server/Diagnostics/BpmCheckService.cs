using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Audio;
using Y2KMusicServer.Server.Data;

namespace Y2KMusicServer.Server.Diagnostics;

/// <summary>
/// On every crossfade, re-detects the tempo actually playing on the outgoing
/// deck — a window of the file ending at the mix point — and logs it against the
/// stored Track.Bpm with ½ / 2× fold tolerance (via <see cref="BpmCompare"/>).
/// Debug-gated, so it runs only when verbose logging is on; the detection and DB
/// lookup run off the engine's event thread. Mirrors PlaybackLogger's
/// subscribe/offload idiom and keeps the engine free of any DB/detector
/// dependency.
/// </summary>
public sealed class BpmCheckService : IHostedService
{
    private const double WindowSec = 30.0;

    private readonly AudioEngine _engine;
    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly ILogger _log;
    private readonly BpmDetector _bpm = new();

    public BpmCheckService(AudioEngine engine, IDbContextFactory<Y2KDbContext> dbf, ILoggerFactory loggerFactory)
    {
        _engine = engine;
        _dbf = dbf;
        _log = loggerFactory.CreateLogger("BpmCheck");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _engine.TransitionStarted += OnTransition;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _engine.TransitionStarted -= OnTransition;
        return Task.CompletedTask;
    }

    private void OnTransition(TransitionInfo t)
    {
        if (!_log.IsEnabled(LogLevel.Debug)) return;   // verbose only
        if (t.FromTrackId is not int id) return;
        double pos = t.TriggerSec;

        // Detection + DB lookup off the engine's event thread.
        _ = Task.Run(async () =>
        {
            try
            {
                string? path;
                double stored;
                await using (var db = await _dbf.CreateDbContextAsync())
                {
                    var row = await db.Tracks.AsNoTracking()
                        .Where(x => x.Id == id)
                        .Select(x => new { x.FilePath, x.Bpm })
                        .FirstOrDefaultAsync();
                    if (row is null) return;
                    path = row.FilePath;
                    stored = row.Bpm ?? 0;
                }

                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

                double start = Math.Max(0, pos - WindowSec);
                var res = _bpm.AnalyzeFileWindow(path, start, WindowSec);
                if (res is null) return;

                var cmp = BpmCompare.Compare(stored, res.Bpm);
                _log.LogDebug(
                    "Deck A live BPM at {Pos:F1}s ({File}): stored={Stored:F1} live={Live:F1} {Verdict} (Δ {Delta:+0.0;-0.0}){Fold}",
                    pos, Path.GetFileName(path), stored, res.Bpm, cmp.Verdict, cmp.DirectDelta, cmp.Fold);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "BpmCheck failed");
            }
        });
    }
}
