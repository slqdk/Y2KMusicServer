using Y2KMusicServer.Server.Scanning;

namespace Y2KMusicServer.Server.Audio;

/// <summary>
/// Chains a missing-only analysis pass off the end of every scan, so newly
/// indexed tracks get their BPM/loudness filled ahead of playback (which is what
/// lets Auto DJ match BPM and the crossfade beat-align on the incoming track).
///
/// There is deliberately NO startup scan: the library is not re-walked on boot
/// (that re-read every file's tags on each start and burned CPU during
/// playback). Scanning is triggered only by assigning a folder to a category, or
/// by a manual per-category rescan from the admin UI. Either way the
/// scan-complete handler here kicks a missing-only
/// <see cref="AudioAnalysisService"/> pass — it selects only tracks lacking
/// BPM/loudness, so it also mops up anything an interrupted earlier pass left.
///
/// If a pass is already running when a scan completes, the start is a no-op and
/// the new tracks are picked up by the next scan/pass.
/// </summary>
public sealed class AnalysisAutoFillService : IHostedService
{
    private readonly LibraryScanner _scanner;
    private readonly AudioAnalysisService _analysis;
    private readonly ILogger<AnalysisAutoFillService> _log;

    public AnalysisAutoFillService(
        LibraryScanner scanner,
        AudioAnalysisService analysis,
        ILogger<AnalysisAutoFillService> log)
    {
        _scanner = scanner;
        _analysis = analysis;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribe to scan completion only — no startup scan is kicked. Scans
        // come from a folder-add or a manual per-category rescan; this handler
        // then fills any missing analysis. Schema is created by DbInitializer.
        _scanner.Progress += OnScanProgress;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _scanner.Progress -= OnScanProgress;
        return Task.CompletedTask;
    }

    private void OnScanProgress(ScanProgress p)
    {
        // Only react to the terminal success event; ignore the per-file ticks.
        if (p.State != ScanState.Completed) return;

        // Debug-level: this fires after every scan, including ones with nothing
        // to analyse. A pass that actually measures tracks logs its own
        // Information-level completion from AudioAnalysisService.
        if (_analysis.TryStart(reanalyzeAll: false))
            _log.LogDebug("Scan complete — background analysis fill started for any unanalysed tracks.");
        else
            _log.LogDebug("Scan complete, but an analysis pass is already running; "
                + "any new tracks will be picked up by the next scan.");
    }
}
