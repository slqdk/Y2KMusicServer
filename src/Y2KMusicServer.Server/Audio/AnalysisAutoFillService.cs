using Y2KMusicServer.Server.Scanning;

namespace Y2KMusicServer.Server.Audio;

/// <summary>
/// Keeps the BPM/loudness columns filled ahead of playback (which is what lets
/// Auto DJ match BPM and the crossfade beat-align on the incoming track). It has
/// two triggers:
///
/// 1. Startup resume — shortly after boot it kicks a missing-only
///    <see cref="AudioAnalysisService"/> pass, so an analysis interrupted by a
///    restart picks up where it left off. The pass selects only tracks lacking
///    BPM/loudness, so a fully-analysed library does no work; it does NOT re-walk
///    the library (there is deliberately no startup scan — that re-read every
///    file's tags on each start and burned CPU). The kick is delayed so DB init
///    and the network-share reconnect settle first, and the analysis pass
///    authenticates shares itself as a backstop.
///
/// 2. Chain off scans — when a scan completes it kicks the same missing-only
///    pass, scoped to the scanned folder, so newly indexed tracks get measured.
///    Scans come only from a folder-add or a manual per-category rescan.
///
/// If a pass is already running when either trigger fires, the start is queued by
/// <see cref="AudioAnalysisService"/> rather than dropped.
/// </summary>
public sealed class AnalysisAutoFillService : IHostedService
{
    private readonly LibraryScanner _scanner;
    private readonly AudioAnalysisService _analysis;
    private readonly ILogger<AnalysisAutoFillService> _log;
    private readonly CancellationTokenSource _cts = new();

    // Let DB init + the network-share reconnect (which itself waits ~5s) settle
    // before the boot-time resume reads any files.
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(8);

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
        // Chain analysis off every scan completion (folder-add or manual rescan).
        _scanner.Progress += OnScanProgress;

        // Resume an interrupted analysis on boot — a missing-only pass over the
        // whole library, so anything a restart left unmeasured gets finished. No
        // startup scan: the library is not re-walked. Delayed so startup and the
        // share reconnect settle first; cancelled if the host stops before then.
        _ = ResumeAfterDelayAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task ResumeAfterDelayAsync(CancellationToken ct)
    {
        try { await Task.Delay(ResumeDelay, ct); }
        catch (OperationCanceledException) { return; }

        if (_analysis.TryStart(reanalyzeAll: false, folderId: null))
            _log.LogInformation("Startup: resuming analysis of any unmeasured tracks.");
        else
            _log.LogDebug("Startup: an analysis pass is already running; resume skipped.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
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
        if (_analysis.TryStart(reanalyzeAll: false, folderId: p.ScopeFolderId))
            _log.LogDebug("Scan complete — background analysis fill started (scope folder {Folder}).", p.ScopeFolderId);
        else
            _log.LogDebug("Scan complete, but an analysis pass is already running; "
                + "any new tracks will be picked up by the next scan.");
    }
}
