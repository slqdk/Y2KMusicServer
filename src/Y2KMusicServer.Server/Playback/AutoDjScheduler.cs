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
    private bool _toppedUpThisTrack;

    // When the idle queue was last primed, and how often that may be retried.
    private DateTime _idlePrimeUtc = DateTime.MinValue;
    private static readonly TimeSpan IdlePrimeInterval = TimeSpan.FromSeconds(20);

    // Quiet period before a top-up. The queue running low is not urgent — what
    // IS disruptive is topping up while the operator is still editing: delete
    // four tracks in a row and the first delete drops the count to the
    // threshold, Auto DJ appends three, and the remaining deletes are aimed at
    // a list that has already moved under the finger. Waiting until the queue
    // has been UNCHANGED for this long means a burst of edits settles first.
    private static readonly TimeSpan TopUpQuietPeriod = TimeSpan.FromSeconds(20);
    private DateTime _lowSinceUtc = DateTime.MinValue;
    private int _lastUpcomingSeen = -1; // single-flight latch (legacy _autoDjQueued)

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

        // ── Live selection swap ───────────────────────────────────────────────
        // Someone changed which playlists feed Auto DJ (listener chips or the DJ
        // page) and the 5s debounce has expired: sweep the upcoming queue, refill
        // from the new selection, and crossfade straight into the first new song
        // so the room hears the change immediately.
        if (_playlist.TakeDueSwap())
        {
            var (removed, added) = await _playlist.SwapQueueToActiveFeedsAsync(status.TrackId, ct);
            _toppedUpThisTrack = true;   // this IS the top-up for the current track

            if (added > 0 && status.State == PlaybackEngineState.Playing)
            {
                var nextId = await _playlist.NextUpcomingTrackIdAsync(status.TrackId, ct);
                if (nextId is int firstNew)
                {
                    _engine.ArmTransition(Transition.NormalCrossfade);
                    var q = await _engine.QueueNextAsync(firstNew, ct);
                    if (q == QueueResult.Ok)
                    {
                        await _engine.NextAsync(null, ct);
                        _log.LogInformation("Live selection swap: crossfading into track {TrackId}.", firstNew);
                    }
                }
            }
            else if (added > 0)
            {
                // Deck idle (fresh start, stopped, or the last track ran out):
                // choosing playlists is an explicit human action, so start the
                // music rather than leaving a full queue sitting silent.
                var startId = await _playlist.ResumeTrackIdAsync(ct);
                if (startId is int first && await _engine.LoadAsync(first, ct) == LoadResult.Ok && _engine.Play())
                {
                    _currentTrackId = first;
                    _toppedUpThisTrack = false;
                    _log.LogInformation("Live selection swap: deck was idle — started track {TrackId}.", first);
                }
            }
            else if (added == 0)
            {
                _log.LogWarning("Live selection swap: nothing to play from the current selection ({Removed} cleared).",
                    removed);
            }
        }

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

            // …but DO fill the queue, so there is something to press Play on.
            // The top-up below only runs while playing, which left a cold start
            // with Auto DJ on and every playlist eligible showing an empty queue
            // and a dead Play button — nothing could begin because nothing had
            // begun. Priming is not starting: the queue fills, the operator still
            // decides when the room hears it.
            await PrimeIdleQueueAsync(ct);
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
            var (nextId, currentInQueue) = await _playlist.NextUpcomingAsync(status.TrackId, ct);
            // Arm the next entry unless it's already armed. A next entry with the
            // same TrackId is only skipped when the current track ISN'T in the
            // queue — otherwise a deliberate repeat (same song twice in a row)
            // would leave Deck B empty and the queue would stall.
            if (nextId is int n && n != status.NextTrackId && (currentInQueue || n != status.TrackId))
            {
                var r = await _engine.QueueNextAsync(n, ct);
                if (r != QueueResult.Ok)
                    _log.LogDebug("Auto DJ queue-next for track {TrackId} returned {Result}.", n, r);
            }
        }

        // ── Top up the playlist when it runs low (Auto DJ only) ───────────────
        if (!await _playlist.IsAutoDjOnAsync(ct)) return;
        _idlePrimeUtc = DateTime.MinValue;   // playing again; allow a fresh prime later
        if (!_toppedUpThisTrack)
        {
            int upcoming = await _playlist.UpcomingCountAsync(status.TrackId, ct);
            var now = DateTime.UtcNow;

            if (upcoming > TopUpThreshold)
            {
                // Comfortably stocked: forget any pending wait.
                _lowSinceUtc = DateTime.MinValue;
                _lastUpcomingSeen = upcoming;
                return;
            }

            // Any change to the count — a delete, a request, a hand-queued track —
            // restarts the wait, so a burst of edits is treated as one edit.
            if (upcoming != _lastUpcomingSeen)
            {
                _lastUpcomingSeen = upcoming;
                _lowSinceUtc = now;
                return;
            }
            if (_lowSinceUtc == DateTime.MinValue) { _lowSinceUtc = now; return; }

            // Safety valve: an EMPTY queue with the current track nearly over
            // can't wait out the quiet period — that would be silence. Anything
            // else waits.
            double remaining = status.DurationSec - status.PositionSec;
            bool aboutToRunDry = upcoming == 0 && remaining > 0 && remaining < 45;

            if (!aboutToRunDry && now - _lowSinceUtc < TopUpQuietPeriod) return;

            _toppedUpThisTrack = true; // single-flight; reset on next track change
            _lowSinceUtc = DateTime.MinValue;
            int added = await _playlist.TopUpAsync(ct);
            if (added > 0)
                _log.LogInformation(
                    "Auto DJ topped up {Count} track(s) at the end of the queue after {Wait:0}s of no edits.",
                    added, aboutToRunDry ? 0 : TopUpQuietPeriod.TotalSeconds);
        }
    }

    /// <summary>
    /// Fills an empty queue while the deck is idle, so Play has something to
    /// start. Never starts playback — a stopped deck is a decision, and the
    /// scheduler does not overrule it.
    ///
    /// Rate-limited: with Auto DJ on but no playlist eligible (all switched off,
    /// or every schedule outside its slot) a top-up legitimately produces
    /// nothing, and retrying that every two seconds would hammer the database
    /// and the log for the whole silent stretch.
    /// </summary>
    private async Task PrimeIdleQueueAsync(CancellationToken ct)
    {
        if (!await _playlist.IsAutoDjOnAsync(ct)) return;
        if (DateTime.UtcNow - _idlePrimeUtc < IdlePrimeInterval) return;
        _idlePrimeUtc = DateTime.UtcNow;

        if (await _playlist.UpcomingCountAsync(null, ct) > 0) return;

        await _playlist.TopUpAsync(ct);
        int now = await _playlist.UpcomingCountAsync(null, ct);
        if (now > 0)
            _log.LogInformation("Auto DJ primed an empty queue with {Count} track(s) while stopped — press Play to start.", now);
    }
}
