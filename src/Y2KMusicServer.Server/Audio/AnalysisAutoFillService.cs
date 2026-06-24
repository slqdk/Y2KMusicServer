using Y2KMusicServer.Server.Scanning;

namespace Y2KMusicServer.Server.Audio;

/// <summary>
/// Keeps the library scanned and analysed automatically, so the operator never
/// has to press a Scan or Analyse button (there are none). The library is kept
/// up to date and analysed ahead of playback, which is what lets Auto DJ match
/// BPM when it selects and the crossfade beat-align on the incoming track.
///
/// Two triggers, one chain:
/// 1. <b>At startup</b> it scans every category that has folders, so files
///    dropped into a registered folder while the service was down get indexed.
/// 2. <b>After every scan completes</b> (the startup scan, an auto-scan on
///    folder-add, or a scan started via the endpoint) it kicks a missing-only
///    <see cref="AudioAnalysisService"/> pass. The pass selects only tracks
///    lacking BPM/loudness, so the scan-complete chain fills both newly-added
///    tracks and any that were left unanalysed by an interrupted earlier pass —
///    there is no separate startup analysis step.
///
/// If a scan or pass is already running when a trigger fires, the start is a
/// no-op and the work is picked up by the next scan. The scan/analyse endpoints
/// remain for programmatic use; they are simply no longer surfaced in the UI.
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
        _scanner.Progress += OnScanProgress;

        // Startup scan, off the startup thread so the host isn't held up. Its
        // completion chains into a missing-only analysis pass via OnScanProgress,
        // so this one trigger also fills any unanalysed tracks. The schema is
        // already created by DbInitializer before hosted services start.
        _ = Task.Run(StartupScan, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _scanner.Progress -= OnScanProgress;
        return Task.CompletedTask;
    }

    private void StartupScan()
    {
        try
        {
            if (_scanner.TryStart(categoryId: null))
                _log.LogInformation("Startup: scanning categories for new files (analysis follows automatically).");
            else
                _log.LogDebug("Startup scan skipped — a scan is already running.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Startup scan failed to start; folder-add will still scan, manual endpoints still work.");
        }
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
