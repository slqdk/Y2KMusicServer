using Y2KMusicServer.Server.Audio;

namespace Y2KMusicServer.Server.Playback;

/// <summary>
/// The Auto DJ loop. Replaces the legacy WinForms timer that called
/// <c>CheckAutoDjTopUp</c>: a hosted service that, while a track is playing and
/// Auto DJ is on,
/// <list type="bullet">
///   <item>keeps one entry queued on the engine so tracks chain automatically
///   (the engine fires the crossfade at the computed out-point — there is no
///   "track ended" event to wait on, so we queue ahead);</item>
///   <item>tops the playlist up via <see cref="PlaylistService"/> when two or
///   fewer entries remain after the current track;</item>
///   <item>reconciles the playlist head against the engine's current track on
///   each promotion — pruning consumed entries and recording history.</item>
/// </list>
///
/// Cold start is out of scope (decision): the operator starts the first track
/// (load + play); Auto DJ takes over from there. It never auto-starts a stopped
/// engine.
///
/// All work happens on this loop thread. We poll <see cref="AudioEngine.GetStatus"/>
/// rather than subscribing to engine events so DB work never runs on the engine's
/// tick thread.
/// </summary>
public sealed class AutoDjScheduler : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Top up when this many (or fewer) entries remain after current.</summary>
    private const int TopUpThreshold = 2;

    private readonly AudioEngine _engine;
    private readonly PlaylistService _playlist;
    private readonly ILogger<AutoDjScheduler> _log;

    private int? _currentTrackId;   // last track we reconciled against
    private bool _toppedUpThisTrack; // single-flight latch (legacy _autoDjQueued)

    public AutoDjScheduler(AudioEngine engine, PlaylistService playlist, ILogger<AutoDjScheduler> log)
    {
        _engine = engine;
        _playlist = playlist;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Auto DJ scheduler started (poll {Interval}s).", PollInterval.TotalSeconds);

        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogDebug(ex, "Auto DJ tick failed (will retry)."); }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var status = _engine.GetStatus();

        // ── Reconcile on track change (promotion or manual load) ──────────────
        if (status.State == PlaybackEngineState.Playing && status.TrackId is int nowId)
        {
            if (_currentTrackId != nowId)
            {
                if (_currentTrackId is int prevId)
                    await _playlist.NotePlayedAsync(prevId, ct);

                await _playlist.PruneConsumedAsync(nowId, ct);
                _currentTrackId = nowId;
                _toppedUpThisTrack = false; // allow a fresh top-up for the new track
            }
        }
        else if (status.State == PlaybackEngineState.Stopped)
        {
            // Stay idle; next play reconciles. Don't push a "played" record for a
            // track the operator explicitly stopped.
            _currentTrackId = null;
            _toppedUpThisTrack = false;
            return;
        }

        if (status.State != PlaybackEngineState.Playing || status.TrackId is null) return;

        // ── Chain: keep the engine armed with the playlist's NEXT entry ───────
        // Deliberately NOT gated on the Auto DJ toggle: a queue with entries is
        // a promise to play through — manual adds and activated playlists must
        // chain regardless. The toggle governs only the automatic REFILL below.
        // Arm Deck B when nothing is queued, OR re-arm when the queued track is
        // no longer the playlist's next entry. The latter is the fix for accepted
        // requests: a request is inserted just ahead of the previously-armed
        // scheduled track, so without re-arming the engine would crossfade to the
        // stale scheduled track and the request would be pruned unplayed.
        if (!status.Crossfading)
        {
            var nextId = await _playlist.NextUpcomingTrackIdAsync(status.TrackId, ct);
            if (nextId is int n && n != status.TrackId && n != status.NextTrackId)
            {
                var r = await _engine.QueueNextAsync(n, ct);
                if (r != QueueResult.Ok)
                    _log.LogDebug("Auto DJ queue-next for track {TrackId} returned {Result}.", n, r);
            }
        }

        // ── Top up the playlist when it runs low (Auto DJ only) ───────────────
        if (!await _playlist.IsAutoDjOnAsync(ct)) return;
        if (!_toppedUpThisTrack)
        {
            int upcoming = await _playlist.UpcomingCountAsync(status.TrackId, ct);
            if (upcoming <= TopUpThreshold)
            {
                _toppedUpThisTrack = true; // single-flight; reset on next track change
                await _playlist.TopUpAsync(ct);
            }
        }
    }
}
