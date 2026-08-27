using Microsoft.EntityFrameworkCore;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Data.Entities;

namespace Y2KMusicServer.Server.Audio;

public enum LoadResult { Ok, NotFound, FileMissing, Unreadable }

public enum QueueResult { Ok, NoCurrent, NotFound, FileMissing, Unreadable }

public sealed record PlaybackStatus
{
    public int? TrackId { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public double PositionSec { get; init; }
    public double DurationSec { get; init; }
    public PlaybackEngineState State { get; init; }
    public bool Crossfading { get; init; }
    public int? NextTrackId { get; init; }
    public string? NextTitle { get; init; }
    public string? NextArtist { get; init; }
    public bool NextStarted { get; init; }   // cued Deck B's silent preview is running
    public IsoMode IsoA { get; init; } = IsoMode.None;   // Deck A EQ isolator mode
    public IsoMode IsoB { get; init; } = IsoMode.None;   // Deck B EQ isolator mode
    // The transition planned for the next crossfade (or the one running during a
    // crossfade). Null when there is no cued/active Deck B. PlannedReason is the
    // planner's human-readable explanation. ArmedTransition is the operator's
    // one-shot force for the next crossfade, or null.
    public string? PlannedTransition { get; init; }
    public string? PlannedReason { get; init; }
    public string? ArmedTransition { get; init; }
}

/// <summary>
/// Dual-deck playback engine. Deck A is current; Deck B is the incoming track
/// during a crossfade. A ~50 ms tick loop advances the fade ramp and fires the
/// scheduled transition. The transition for each pair is chosen by
/// <see cref="MixPlanner"/> under the operator's <see cref="MixRules"/> (the
/// Crossfade and Mixing section toggles); mix points come from <c>MixCache</c>
/// (computed via <see cref="MixAnalyser"/> on first use). The fade is shortened
/// if needed so Deck B reaches full volume by Deck A's end (EOF contract, policy a).
///
/// Beat drop crossfade: when the chosen transition is a Beat drop and Deck A has
/// a live kick at the start instant, Deck B is held silent through A's last beats
/// and faded in only once A goes quiet (otherwise it falls back to a plain ramp).
/// An operator can arm any transition for the next crossfade only (ArmTransition).
/// </summary>
public sealed class AudioEngine
{
    private const double TickMs = 50.0;
    private const float CrossFadeMinVol = 0.001f;

    /// <summary>How fast A is taken out once a beat drop actually fires — long
    /// enough to avoid a click, short enough to read as a cut.</summary>
    private const double BeatDropCutSec = 0.35;

    // How long before the scheduled crossfade trigger an auto/queued Deck B starts
    // pumping silently, to warm its decode pipeline, the OS file cache (the music
    // lives on a network share), and the JIT, and to spin up the source's sequential
    // read-ahead — so the fade does not open on a cold decode that stalls and
    // crackles. Deck B is re-seeked back to its in-point when the fade actually
    // starts, so the pre-roll never changes where B enters the mix.
    private const double PrerollSec = 3.0;

    /// <summary>
    /// A short warm burst run as soon as Deck B is cued, on top of the timed
    /// pre-roll before the trigger. Hand-fired transitions (Next on the deck
    /// panel, the DJ page, a listener skip) open the fade with no warning, so
    /// the timed window never gets to run and B's first samples come straight
    /// off the SMB share — the 34–46 ms read spikes in the log. Kept short so
    /// B can't decode itself to the end of the track while it waits.
    /// </summary>
    private const double ArmWarmSec = 1.5;

    // Fixed format the stream mixes at. Every deck is normalised to this rate
    // (and to stereo) ahead of its DeckTap, so the broadcast header stays
    // constant no matter what the source files are.
    private const int StreamSampleRate = 44100;

    // Rolling black-box capture per deck tap (and the encoder's post-mix ring):
    // enough to contain a whole transition around a heard glitch, small enough
    // (~3.5 MB per ring) to keep always-on.
    private const int BlackBoxSeconds = 10;

    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly ILogger<AudioEngine> _log;
    private readonly IConfiguration _cfg;

    private readonly object _gate = new();
    private Deck? _deckA;
    private Deck? _deckB;
    private PlaybackEngineState _state = PlaybackEngineState.Stopped;

    private PreparedNext? _prepared;

    // Crossfade ramp state (guarded by _gate).
    private bool _crossfading;

    // ── Master gain (talk-over duck / fade-to-pause) ─────────────────────────
    // _duckGain multiplies both decks post-fader. The tick loop walks it toward
    // _duckTarget at _duckStep per tick, so every change is a smooth ramp rather
    // than a click. _pauseAtSilence pauses the transport once the ramp lands on
    // zero (the "fade then pause" button).
    private volatile float _duckGain = 1f;

    // The DJ's live trim (0.1–1.0 of master) and the duck level last asked for.
    // Both feed TargetGain_Locked; neither touches the Settings row.
    private double _trim = 1.0;
    private double _duckLevel = 1.0;
    private float _duckTarget = 1f;
    private float _duckStep = 1f;
    private bool _pauseAtSilence;
    private bool _duckActive;      // talk-over is holding the level down
    private bool _fadePaused;      // paused via the fading pause button
    private float _fadeBEntry;   // Normal-crossfade B start level (0..1 of target), 0 otherwise
    // Normal crossfade only: B is held silent until A's volume has dropped to
    // this fraction of where it started. 1 = no wait (B rides the whole fade).
    private float _fadeBEnterAtA = 1f;
    private bool _fadeBHeld;     // true while B is still waiting for that point
    private double _fadeBStartPos;  // A's fade progress at the moment B entered
    private bool _bManualStarted;   // operator started the silent Deck B preview (pump running)

    // Jingle hand-back: a jingle is played out in full, the deck stops, and the
    // next song starts clean after a short silence. No fade in either direction —
    // the silence is the point, it's what makes the room look up.
    // Stuck-deck watchdog: a deck that says PLAYING while its reader position
    // never moves and its tap produces nothing. The room hears silence and the
    // console shows a happy transport, which is the worst possible pairing.
    private double _watchdogPosSec = -1;
    private int _watchdogStillTicks;
    private int _watchdogFiredForTrack = -1;

    // Whether the last stop was the OPERATOR's. A deck that ran out with nothing
    // armed also lands in Stopped, and Auto DJ should pick that up — but it must
    // never override a stop somebody asked for.
    private bool _stoppedByOperator;

    // A jingle fired while the deck was STOPPED. It plays once, on its own, with
    // no fade at either end and nothing armed behind it — and when it finishes
    // the engine goes back to stopped rather than carrying on. A stop is a
    // decision; a jingle is not a request to undo it.
    private bool _oneShotJingle;

    private bool _gapWaiting;
    private int _gapTicksLeft;
    private const double JingleGapSec = 1.0;

    /// <summary>How long a playing deck may sit at the same position before the
    /// watchdog calls it stuck. Long enough that a slow SMB read or a paused
    /// moment can't trip it, short enough that a party notices nothing worse
    /// than one awkward gap.</summary>
    private const double StuckMs = 6000;

    /// <summary>How long after a track starts the health tap tolerates slow
    /// reads and level steps without calling them anomalies. A first read off
    /// the SMB share routinely takes tens of milliseconds.</summary>
    private const double StartupGraceSec = 3.0;
    private double _crossFadePos;
    private double _crossFadeStep;
    private float _fadeStartVolA;
    private float _deckBTargetVol;
    private bool _deckBFading;

    // SmartBeat fader state (guarded by _gate).
    private bool _smartBeatActive;

    // Beat drop, waiting phase. The old code sampled A's bass onset once, at the
    // instant the fade opened, and fell back to a plain ramp if that single
    // sample missed the kick — which it usually did, turning a "drop" into a
    // long entry-less overlap. Now the fade OPENS but holds: A stays at full and
    // B stays silent until a real kick lands (or the window runs out).
    private bool _beatWaiting;
    private int _beatWaitTicksLeft;
    private float _beatWaitOnsetGate = 0.1f;
    private double _beatFallbackFadeSec;   // fade to run if no kick arrives
    private float _beatFadeInPos;
    private float _beatFadeInStep;

    // Auto-mix plan executor state (phase 4, guarded by _gate). _activePlan is the
    // plan running on the current crossfade (null = plain). _planOwnsB means the
    // plan drives Deck B's volume (the normal B fade-in is suspended); otherwise B
    // uses the normal ramp and the plan only drives isolators. _planASilentFired
    // gates the one-shot "A is silent" steps.
    private MixPlan? _activePlan;
    private bool _planOwnsB;
    private bool _planASilentFired;
    private double _planSwapAtSec;       // A-position (s) at which to fire the "downbeat" swap steps
    private bool _planDownbeatFired;

    // Operator-armed transition (guarded by _gate): a one-shot forced transition
    // that overrides the automatic pick on the NEXT A→B crossfade only, then
    // clears. null = use the automatic pick. Set by the force buttons (ArmTransition).
    private Transition? _armed;

    private volatile bool _tickRunning = true;

    public AudioEngine(IDbContextFactory<Y2KDbContext> dbf, ILogger<AudioEngine> log, IConfiguration cfg)
    {
        _dbf = dbf;
        _log = log;
        _cfg = cfg;

        // Operator switch for local (server-machine) sound. Persisted as JSON
        // (audio-config.json, no-migrations rule); default true = the
        // historical always-try-the-sound-card behaviour.
        _localAudio = AudioConfigStore.Load(cfg).LocalAudioEnabled;

        var tick = new Thread(TickLoop)
        {
            IsBackground = true,
            Name = "AudioEngineTick",
            Priority = ThreadPriority.AboveNormal
        };
        tick.Start();

        // Watch the audio endpoints so local deck output survives the real
        // world: plugging a headset (default device change), a device appearing
        // after service start, or starting with none at all. Any relevant event
        // rebuilds the live decks' outputs on the then-current default device.
        // Best-effort: without Core Audio (headless CI etc.) the engine just
        // runs without the watcher.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _mmEnum = new MMDeviceEnumerator();
                _mmWatch = new EndpointWatcher(this);
                _mmEnum.RegisterEndpointNotificationCallback(_mmWatch);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Audio endpoint watcher unavailable; output follows load-time device only.");
            }
        }
    }

    private MMDeviceEnumerator? _mmEnum;
    private EndpointWatcher? _mmWatch;
    private int _rebuildQueued;

    // Operator switch: decks try the sound card (true) or are forced to the
    // silent pump (false). The stream tap is upstream of the output either way.
    private volatile bool _localAudio = true;

    /// <summary>Whether decks currently try the machine's sound card.</summary>
    public bool LocalAudioEnabled => _localAudio;

    /// <summary>
    /// Turns local (server-machine) sound on or off, rebuilding every live
    /// deck's output immediately via the same swap machinery the endpoint
    /// watcher uses — play state and position are preserved, and /stream
    /// listeners never notice. Persisting the flag is the caller's job
    /// (the admin endpoint saves it to audio-config.json).
    /// </summary>
    public void SetLocalAudio(bool enabled)
    {
        if (_localAudio == enabled) return;
        _localAudio = enabled;
        _log.LogInformation("Local audio {State} by operator; rebuilding deck outputs.",
            enabled ? "ENABLED" : "DISABLED");
        try { RebuildDeckOutputs(); }
        catch (Exception ex) { _log.LogWarning(ex, "Deck output rebuild after local-audio toggle failed"); }
    }

    /// <summary>
    /// Anomalous ~1 s health window from a deck's HealthTap. Runs on that
    /// deck's output pump thread, so it must stay quick and must NOT take
    /// <c>_gate</c> — SwapOutput_Locked (held under the gate) waits for the
    /// pump to leave its callback, so taking the gate here can deadlock.
    /// Lock-free snapshot reads are fine for diagnostics.
    /// </summary>
    private void OnHealthWindow(HealthTap tap, HealthWindow win)
    {
        var a = _deckA; var b = _deckB; var p = _prepared?.DeckB;
        Deck? deck = (a?.Health == tap) ? a : (b?.Health == tap) ? b : (p?.Health == tap) ? p : null;

        double pos = -1;
        try { if (deck != null) pos = deck.Reader.CurrentTime.TotalSeconds; } catch { }

        string ctx = _crossfading ? (_activePlan != null ? "mix plan" : "crossfade")
                   : _prepared != null ? "armed" : "steady";

        // Grace period at the head of a track. The first seconds of a deck are
        // a cold SMB read: one slow read and a step in level as the fade-in
        // takes hold are NORMAL there, and reporting them as anomalies filled
        // the log and the diagnostics folder with WAVs of a healthy start.
        // A genuine fault — non-finite samples, clicks, a silent run, or the
        // ring running dry — is still reported from the first sample.
        bool startupGrace = pos >= 0 && pos < StartupGraceSec;
        bool onlyStartupNoise = win.NonFinite == 0 && win.Clicks == 0
                                && win.MaxZeroRunMs == 0 && win.Shortfalls == 0;
        if (startupGrace && onlyStartupNoise)
        {
            _log.LogDebug(
                "Audio health {Name}: settling in ({Slow} slow read(s), max {MaxRead:0.0}ms, maxDelta {MaxDelta:0.00}) " +
                "at {Pos:0.0}s — inside the {Grace:0.0}s start-up grace, not treated as an anomaly.",
                tap.Name, win.SlowReads, win.MaxReadMs, win.MaxDelta, pos, StartupGraceSec);
            return;
        }

        _log.LogWarning(
            "Audio health {Name}: nonFinite={NonFinite} clicks={Clicks} maxDelta={MaxDelta:0.00} " +
            "zeroRun={Zero:0}ms slowReads={Slow} maxRead={MaxRead:0.0}ms shortfalls={Short} peak={Peak:0.00} " +
            "| {Track} @ {Pos:0.0}s vol={Vol:0.00} iso={Iso} [{Ctx}]",
            tap.Name, win.NonFinite, win.Clicks, win.MaxDelta, win.MaxZeroRunMs, win.SlowReads,
            win.MaxReadMs, win.Shortfalls, win.Peak,
            deck != null ? TrackLabel(deck.Title, deck.Artist) : "?", pos,
            deck?.Vol.Volume ?? -1f, deck?.Iso.Mode.ToString() ?? "?", ctx);

        // Auto-dump the black box when verbose diagnosis is on (Settings.
        // DebugLogging flips the level switch, which this reflects). Cooldown
        // lives in AudioBlackBox; the file IO runs off the pump thread.
        if (_log.IsEnabled(LogLevel.Debug))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var files = AudioBlackBox.TryAutoDump(DataPaths.EnsureDiagnosticsDir(_cfg));
                    if (files is { Count: > 0 })
                        _log.LogWarning("Black box auto-dumped {Count} file(s) after anomaly on {Name}: {First} …",
                            files.Count, tap.Name, Path.GetFileName(files[0]));
                }
                catch (Exception ex) { _log.LogDebug(ex, "Black box auto-dump failed"); }
            });
        }
    }

    /// <summary>
    /// Diagnostic snapshot for the tray / admin: the local-audio flag, the
    /// default render device as THIS process sees it (LocalSystem in a
    /// session with no audio endpoint sees none — the usual reason for
    /// silence), and what output each live deck actually got.
    /// </summary>
    public Y2KMusicServer.Shared.LocalAudioStatusDto GetLocalAudioStatus()
    {
        string? device = null;
        int count = 0;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Fresh enumerator per call: cheap, and avoids COM-thread
                // affinity questions around the long-lived watcher instance.
                using var e = new MMDeviceEnumerator();
                try { count = e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Count; }
                catch { }
                try
                {
                    using var d = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    device = d.FriendlyName;
                }
                catch { /* no default render device visible to this process */ }
            }
            catch { /* Core Audio unavailable */ }
        }

        var decks = new List<Y2KMusicServer.Shared.LocalAudioDeckDto>();
        lock (_gate)
        {
            foreach (var deck in new[] { _deckA, _deckB, _prepared?.DeckB })
            {
                if (deck == null) continue;
                string state = "?";
                try { state = deck.Out.PlaybackState.ToString(); } catch { }
                decks.Add(new Y2KMusicServer.Shared.LocalAudioDeckDto
                {
                    Deck = deck.Label,
                    Output = deck.Out is SilentWavePlayer ? "silent" : "sound card",
                    State = state,
                    Track = string.IsNullOrEmpty(deck.Artist) ? deck.Title : $"{deck.Artist} – {deck.Title}"
                });
            }
        }

        return new Y2KMusicServer.Shared.LocalAudioStatusDto
        {
            Enabled = _localAudio,
            DefaultDevice = device,
            RenderDeviceCount = count,
            Decks = decks
        };
    }

    /// <summary>Debounced: endpoint events arrive in bursts (and on COM
    /// threads), so coalesce them and rebuild ~600 ms later off-thread.</summary>
    private void ScheduleOutputRebuild()
    {
        if (Interlocked.Exchange(ref _rebuildQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(600);
            Interlocked.Exchange(ref _rebuildQueued, 0);
            try { RebuildDeckOutputs(); }
            catch (Exception ex) { _log.LogWarning(ex, "Deck output rebuild failed"); }
        });
    }

    /// <summary>Reopens every live deck's output device on the current default
    /// render device, preserving play state. The stream tap sits upstream of
    /// the output, so /stream listeners never notice; locally there is a brief
    /// (~0.1 s) gap at the moment of the swap.</summary>
    private void RebuildDeckOutputs()
    {
        lock (_gate)
        {
            foreach (var deck in new[] { _deckA, _deckB, _prepared?.DeckB })
                if (deck != null)
                    SwapOutput_Locked(deck);
        }
    }

    private void SwapOutput_Locked(Deck deck)
    {
        if (deck.StopRequested) return;

        bool wasPlaying = false;
        try { wasPlaying = deck.Out.PlaybackState == PlaybackState.Playing; } catch { }

        // Unhook the stop handler BEFORE stopping the old device — otherwise the
        // swap itself would look like the track ending and fire auto-advance.
        try { if (deck.StoppedHandler != null) deck.Out.PlaybackStopped -= deck.StoppedHandler; } catch { }
        try { deck.Out.Stop(); } catch { }
        try { deck.Out.Dispose(); } catch { }

        IWavePlayer nu = CreateDeckOutput(deck.Label);
        try
        {
            nu.Init(deck.Wp);
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "Deck {Deck}: no usable output after device change; silent until one appears.", deck.Label);
            try { nu.Dispose(); } catch { }
            nu = new SilentWavePlayer();
            nu.Init(deck.Wp);
        }

        deck.Out = nu;
        if (deck.StoppedHandler != null) nu.PlaybackStopped += deck.StoppedHandler;
        if (wasPlaying) { try { nu.Play(); } catch { } }

        _log.LogInformation("Deck {Deck}: output reopened on the current default device ({Kind}){Resumed}.",
            deck.Label, nu is SilentWavePlayer ? "silent" : "sound card", wasPlaying ? ", resumed" : "");
    }

    /// <summary>Core Audio endpoint listener: any default-render change or a
    /// device becoming active queues a deck-output rebuild.</summary>
    private sealed class EndpointWatcher : IMMNotificationClient
    {
        private readonly AudioEngine _engine;
        public EndpointWatcher(AudioEngine engine) => _engine = engine;

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // Fires once per role; react to a single role to avoid triple rebuilds.
            if (flow == DataFlow.Render && role == Role.Multimedia)
                _engine.ScheduleOutputRebuild();
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (newState == DeviceState.Active)
                _engine.ScheduleOutputRebuild();
        }

        public void OnDeviceAdded(string pwstrDeviceId) => _engine.ScheduleOutputRebuild();
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    public event Action<NowPlayingInfo>? NowPlayingChanged;
    public event Action<DeckProgress>? ProgressChanged;
    public event Action<VuSample>? VuChanged;
    public event Action<TransitionInfo>? TransitionStarted;
    public event Action<BeatPulse>? BeatDetected;

    /// <summary>
    /// Raised whenever the live deck set changes (load / crossfade start /
    /// promote / stop). Carries the current Deck A and Deck B taps (B is null
    /// unless a crossfade is in progress). The streaming encoder subscribes so
    /// it always drains the live decks; the engine itself holds no streaming
    /// dependency, mirroring the SignalR-free event surface.
    /// </summary>
    public event Action<DeckTap?, DeckTap?>? TapsChanged;

    // ── Single-deck control ───────────────────────────────────────────────────

    public async Task<LoadResult> LoadAsync(int trackId, CancellationToken ct = default)
    {
        Track? track;
        Settings settings;
        bool isJingle;
        await using (var db = await _dbf.CreateDbContextAsync(ct))
        {
            track = await db.Tracks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == trackId, ct);
            settings = db.Settings.AsNoTracking().FirstOrDefault() ?? new Settings { Volume = 80 };
            isJingle = track != null && await IsJingleTrackAsync(db, trackId, ct);
        }

        if (track == null) return LoadResult.NotFound;
        if (!File.Exists(track.FilePath)) return LoadResult.FileMissing;

        // Start at the first audible sample — leading silence never airs on a
        // cold load. (Sub-¼-second lead-ins aren't worth a seek.)
        double leadIn = track.LeadInSec is double li && li > 0.25 ? li : 0;

        Deck deck;
        try
        {
            deck = BuildDeck(track, NormalizedVolume(track, settings, isJingle), leadIn, "A");
            deck.IsJingle = isJingle;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to open {Path}", track.FilePath);
            return LoadResult.Unreadable;
        }

        Deck? oldA, oldB, oldPrepared;
        lock (_gate)
        {
            oldA = _deckA;
            oldB = _deckB;
            oldPrepared = _prepared?.DeckB;
            ResetCrossfadeState_Locked();
            _deckA = deck;
            _deckB = null;
            _prepared = null;
            _state = PlaybackEngineState.Stopped;
            _stoppedByOperator = false;   // a freshly loaded deck is waiting, not refused
            _oneShotJingle = false;
        }

        DisposeOffThread(oldA, oldB, oldPrepared);
        EmitNowPlaying();
        EmitTaps();
        return LoadResult.Ok;
    }

    /// <summary>
    /// Talk-over duck: hold the music down at <paramref name="level"/> (0–1 of
    /// normal) while the DJ speaks, then back up when released. Both directions
    /// ramp over <paramref name="fadeSec"/>.
    /// </summary>
    public void SetDuck(bool on, double level, double fadeSec)
    {
        lock (_gate)
        {
            _duckActive = on;
            _duckLevel = Math.Clamp(level, 0, 1);
            if (_fadePaused && on) return;          // already silent; nothing to duck
            RampTo_Locked(TargetGain_Locked(), fadeSec, pauseAtSilence: false);
        }
    }

    /// <summary>
    /// The DJ's live volume trim: a fraction of the master volume, 10–100%,
    /// applied to the output rather than to the Settings row.
    ///
    /// It is deliberately NOT the master setting. Master decides each deck's
    /// build volume together with loudness normalisation, so changing it only
    /// reaches the next track — useless for turning the room down mid-song. The
    /// trim rides the same post-fader gain the talk-over duck uses, so it takes
    /// effect immediately and ramps instead of stepping.
    ///
    /// The floor is 10%: this control is for reading the room, not for muting.
    /// Silence has its own button (fade-pause) that also stops the transport.
    /// </summary>
    public void SetTrim(double trim, double fadeSec = 0.6)
    {
        lock (_gate)
        {
            _trim = Math.Clamp(trim, 0.1, 1.0);
            if (_fadePaused) return;                // held silent; applies on release
            RampTo_Locked(TargetGain_Locked(), fadeSec, pauseAtSilence: false);
        }
    }

    /// <summary>The trim as a 0.1–1.0 fraction of master.</summary>
    public double Trim { get { lock (_gate) return _trim; } }

    /// <summary>
    /// Where the post-fader gain should sit right now. Duck MULTIPLIES the trim
    /// rather than replacing it, so talking over a room already turned down to
    /// 50% ducks from there instead of jumping up to full and back.
    /// Caller holds <see cref="_gate"/>.
    /// </summary>
    private float TargetGain_Locked()
        => (float)Math.Clamp(_duckActive ? _trim * _duckLevel : _trim, 0.0, 1.0);

    /// <summary>
    /// Fading pause: ramp to silence over <paramref name="fadeSec"/> and pause
    /// the transport at the bottom; releasing plays first, then ramps back up.
    /// </summary>
    public void SetFadePause(bool on, double fadeSec)
    {
        lock (_gate)
        {
            _fadePaused = on;
            if (on)
            {
                RampTo_Locked(0f, fadeSec, pauseAtSilence: true);
            }
            else
            {
                _pauseAtSilence = false;
                if (_state == PlaybackEngineState.Paused) Play();
                // Back to whatever the duck and the trim say between them: still
                // held down if the DJ is talking, otherwise the trim level —
                // never a jump to full, which would undo a turned-down room.
                RampTo_Locked(TargetGain_Locked(), fadeSec, pauseAtSilence: false);
            }
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void RampTo_Locked(float target, double fadeSec, bool pauseAtSilence)
    {
        _duckTarget = Math.Clamp(target, 0f, 1f);
        var secs = Math.Max(0.05, fadeSec);
        _duckStep = (float)(TickMs / 1000.0 / secs);   // full 0→1 travel in fadeSec
        _pauseAtSilence = pauseAtSilence;
    }

    /// <summary>Caller holds <see cref="_gate"/>. Pushes the gain to every deck.</summary>
    private void ApplyDuckGain_Locked()
    {
        var g = _duckGain;
        if (_deckA != null) _deckA.Duck.Volume = g;
        if (_deckB != null) _deckB.Duck.Volume = g;
        var prep = _prepared?.DeckB;
        if (prep != null) prep.Duck.Volume = g;
    }

    /// <summary>Current master gain and what is holding it (for the DJ page).</summary>
    public (float Gain, bool Ducked, bool FadePaused) DuckState()
    {
        lock (_gate) return (_duckGain, _duckActive, _fadePaused);
    }

    /// <summary>
    /// True when Deck A is parked at the end of its track. Ending with nothing
    /// armed leaves the deck loaded and the reader at EOF, so the transport looks
    /// resumable when there is nothing left to resume — pressing Play just
    /// restarts the same end-of-file and stops again.
    /// </summary>
    public bool DeckSpent
    {
        get
        {
            lock (_gate)
            {
                if (_deckA == null) return false;
                try { return _deckA.Reader.CurrentTime.TotalSeconds >= _deckA.DurationSec - 0.25; }
                catch { return false; }
            }
        }
    }

    /// <summary>True when playback stopped because somebody stopped it, rather
    /// than because the music ran out.</summary>
    public bool StoppedByOperator { get { lock (_gate) return _stoppedByOperator; } }

    /// <summary>True while a one-shot jingle is on air. Auto DJ leaves the decks
    /// alone for its duration: nothing is armed behind it and the queue is not
    /// topped up, so it ends in silence instead of mixing into a show that was
    /// deliberately stopped.</summary>
    public bool OneShotJingle { get { lock (_gate) return _oneShotJingle; } }

    /// <summary>
    /// Plays one jingle from a stopped deck and stops again afterwards. No
    /// crossfade in (there is nothing to fade from) and none out (nothing is
    /// armed) — it starts, it plays, it ends, and the transport returns to
    /// stopped until somebody presses Play.
    /// </summary>
    public async Task<LoadResult> PlayJingleOnceAsync(int trackId, CancellationToken ct = default)
    {
        var loaded = await LoadAsync(trackId, ct);
        if (loaded != LoadResult.Ok) return loaded;

        lock (_gate) _oneShotJingle = true;   // set AFTER the load, which clears it
        if (!Play())
        {
            lock (_gate) _oneShotJingle = false;
            return LoadResult.NotFound;
        }
        _log.LogInformation("One-shot jingle: playing from a stopped deck; the show stays stopped afterwards.");
        return LoadResult.Ok;
    }

    public bool Play()
    {
        lock (_gate)
        {
            if (_deckA == null) return false;
            if (_state == PlaybackEngineState.Playing) { _stoppedByOperator = false; return true; }

            // Refuse to "resume" a finished track: the caller's fallback path
            // (load the next queue entry) is the only thing that can actually
            // start music from here.
            bool spent;
            try { spent = _deckA.Reader.CurrentTime.TotalSeconds >= _deckA.DurationSec - 0.25; }
            catch { spent = false; }
            if (spent) return false;

            _stoppedByOperator = false;
            _deckA.StopRequested = false;
            _deckA.Out.Play();
            if (_crossfading && !_fadeBHeld) _deckB?.Out.Play();   // a held-out B stays paused until it's let in
            if (_prepared?.Manual == true && _bManualStarted) _prepared.DeckB.Out.Play();
            if (_prepared?.PrerollStarted == true) _prepared.DeckB.Out.Play();
            _state = PlaybackEngineState.Playing;
        }
        EmitNowPlaying();
        return true;
    }

    public bool Pause()
    {
        lock (_gate)
        {
            if (_deckA == null || _state != PlaybackEngineState.Playing) return false;
            _deckA.Out.Pause();
            if (_crossfading) _deckB?.Out.Pause();
            if (_prepared?.Manual == true && _bManualStarted) _prepared.DeckB.Out.Pause();
            if (_prepared?.PrerollStarted == true) _prepared.DeckB.Out.Pause();
            _state = PlaybackEngineState.Paused;
        }
        EmitNowPlaying();
        return true;
    }

    public bool Stop()
    {
        Deck? oldB, oldPrepared;
        lock (_gate)
        {
            if (_deckA == null) return false;
            oldB = _crossfading ? _deckB : null;
            oldPrepared = _prepared?.DeckB;
            ResetCrossfadeState_Locked();
            _deckB = null;
            _prepared = null;
            _bManualStarted = false;
            _stoppedByOperator = true;
            _oneShotJingle = false;
            _deckA.StopRequested = true;
            _deckA.Out.Stop();
            try { _deckA.Reader.Position = 0; } catch { }
            _deckA.Vol.Volume = _deckA.BaseVolume;
            _state = PlaybackEngineState.Stopped;
        }
        DisposeOffThread(oldB, oldPrepared);
        EmitNowPlaying();
        EmitTaps();
        return true;
    }

    public bool Seek(double seconds)
    {
        lock (_gate)
        {
            if (_deckA == null) return false;
            var dur = _deckA.DurationSec;
            var s = Math.Clamp(seconds, 0, dur > 0 ? dur : seconds);
            try { _deckA.Reader.CurrentTime = TimeSpan.FromSeconds(s); }
            catch { return false; }
        }
        return true;
    }

    // ── Transitions ───────────────────────────────────────────────────────────

    /// <summary>
    /// Drops the armed Deck B if it holds this track — used when its queue entry
    /// is deleted.
    ///
    /// Arming and the queue are two different things: once a track is cued, Deck B
    /// owns an open reader for it and will crossfade in on the trigger whatever
    /// the database now says. Deleting the entry alone therefore does NOT stop
    /// it playing — the scheduler only re-arms when some OTHER entry takes its
    /// place, so with an empty (or Auto-DJ-off) queue the deleted song still
    /// arrives, which looks exactly like the queue ignoring a delete.
    ///
    /// Refuses while crossfading: at that point the track is already on air and
    /// pulling the deck out from under it would cut the music.
    /// </summary>
    public bool CancelPreparedIfTrack(int trackId)
    {
        Deck? doomed = null;
        lock (_gate)
        {
            if (_crossfading) return false;
            if (_prepared?.DeckB is not Deck b || b.TrackId != trackId) return false;
            doomed = b;
            _prepared = null;
            _bManualStarted = false;
        }

        DisposeOffThread(doomed);
        _log.LogInformation("Cue cleared: armed track {TrackId} was removed from the queue.", trackId);
        return true;
    }

    public async Task<QueueResult> QueueNextAsync(int trackId, CancellationToken ct = default, bool manual = false)
    {
        int fromId;
        string fromPath;
        double fromBpm;
        lock (_gate)
        {
            if (_deckA == null) return QueueResult.NoCurrent;
            fromId = _deckA.TrackId;
            fromPath = _deckA.FilePath;
            fromBpm = _deckA.Bpm ?? 0;
        }

        Track? next;
        Settings settings;
        MixCache? cached;
        double fromPhase;
        bool nextIsJingle;
        await using (var db = await _dbf.CreateDbContextAsync(ct))
        {
            next = await db.Tracks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == trackId, ct);
            settings = db.Settings.AsNoTracking().FirstOrDefault() ?? new Settings { Volume = 80 };
            cached = next == null
                ? null
                : await db.MixCache.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.FromTrackId == fromId && m.ToTrackId == trackId, ct);
            fromPhase = await db.Tracks.AsNoTracking()
                .Where(t => t.Id == fromId)
                .Select(t => t.BeatPhaseOffsetSec ?? 0.0)
                .FirstOrDefaultAsync(ct);
            nextIsJingle = next != null && await IsJingleTrackAsync(db, trackId, ct);
        }

        if (next == null) return QueueResult.NotFound;
        if (!File.Exists(next.FilePath)) return QueueResult.FileMissing;

        // Decimal seconds from mixrules.json win; the int on the Settings row is
        // the fallback for installs that never touched the new field.
        var fadeRules = MixRules.Load(_cfg);
        double configuredFade = fadeRules.NormalFadeSeconds > 0
            ? fadeRules.NormalFadeSeconds
            : settings.NextFadeSeconds;
        var rules = MixRules.Load(_cfg);

        double outPoint, inPoint, fadeSec, score;
        bool beatAligned;
        string? reason;
        string mixSource;

        if (cached != null)
        {
            outPoint = cached.OutPoint;
            inPoint = cached.InPoint;
            fadeSec = cached.FadeDurationSec;
            score = cached.PairScore;
            beatAligned = cached.BeatAligned;
            reason = cached.Reason;
            mixSource = "pre-analysed pair (cache)";
        }
        else
        {
            var mp = MixAnalyser.AnalysePair(
                fromPath, fromBpm, fromPhase,
                next.FilePath, next.Bpm ?? 0, next.BeatPhaseOffsetSec ?? 0,
                configuredFade, ct, smartMode: true,
                sameBars: rules.SameTempoBars, relatedBars: rules.RelatedTempoBars);

            if (mp.IsValid)
            {
                outPoint = mp.OutPoint;
                inPoint = mp.InPoint;
                fadeSec = mp.FadeDuration;
                score = mp.PairScore;
                beatAligned = mp.BeatAligned;
                reason = mp.Reason;
                mixSource = "computed live";

                try
                {
                    await using var db = await _dbf.CreateDbContextAsync(ct);
                    db.MixCache.Add(new MixCache
                    {
                        FromTrackId = fromId,
                        ToTrackId = trackId,
                        OutPoint = outPoint,
                        InPoint = inPoint,
                        FadeDurationSec = fadeSec,
                        PairScore = score,
                        Reason = reason,
                        BeatAligned = beatAligned,
                        ComputedAt = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "MixCache save failed for {From}->{To}", fromId, trackId);
                }
            }
            else
            {
                outPoint = 0;
                // No musical in-point known — at least skip the incoming
                // track's leading silence.
                inPoint = next.LeadInSec is double nli && nli > 0.25 ? nli : 0;
                fadeSec = configuredFade;
                score = 0;
                beatAligned = false;
                reason = "fallback (analysis unavailable)";
                mixSource = "fallback (analysis unavailable)";
            }
        }

        // ── Transition planner ───────────────────────────────────────────────
        // Resolve the transition for this pair now and carry it on the prepared
        // transition, so the operator sees what's planned ahead of the crossfade
        // (and it lands in the log). The planner is pure; the structure caches
        // build here, off the audio thread. An armed force overrides this at fire
        // time. The two section flags live inside the rules — the planner falls
        // back to a Normal Crossfade when neither section acts.
        MixPlan plan;
        {
            TrackStructureData? aStruct = TryStructure(fromId, fromPath);
            TrackStructureData? bStruct = TryStructure(next.Id, next.FilePath);
            var basePoints = new MixPoints
            {
                OutPoint = outPoint,
                InPoint = inPoint,
                FadeDuration = fadeSec,
                BeatAligned = beatAligned
            };
            plan = MixPlanner.Plan(
                basePoints,
                fromBpm > 0 ? fromBpm : (double?)null,
                next.Bpm, next.BeatPhaseOffsetSec,
                aStruct, bStruct, rules);

            _log.LogInformation("Next transition planned: {Transition} | {Reason}",
                plan.StrategyName, plan.Reason);
        }

        float targetVol = NormalizedVolume(next, settings, nextIsJingle);

        Deck deckB;
        try
        {
            deckB = BuildDeck(next, 0f, inPoint, "B");
            deckB.BaseVolume = targetVol;
            deckB.InPointSec = inPoint;
            deckB.IsJingle = nextIsJingle;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to open next {Path}", next.FilePath);
            return QueueResult.Unreadable;
        }

        Deck? oldPrepared;
        lock (_gate)
        {
            if (_deckA == null) { DisposeOffThread(deckB); return QueueResult.NoCurrent; }

            double durA = _deckA.DurationSec;

            // Effective end of A: the last audible sample when analysis measured
            // it (sanity-ranged), else the file length — so trailing silence
            // never delays the transition or plays out on air.
            double endA = _deckA.LeadOutSec is double lo && lo > 1 && lo < durA ? lo : durA;

            // By-transition fade rule: a Normal Crossfade can't beat-align (there
            // are no shared bars), so it's bounded by the operator's seconds cap
            // and placed to land on A's (audible) end. Beat-matched crossfades and
            // the moves keep their bar-based length from the analysis above.
            if (plan.Strategy == Transition.NormalCrossfade && configuredFade > 0)
            {
                fadeSec = Math.Min(fadeSec, configuredFade);
                if (endA > 0) outPoint = Math.Max(endA * 0.5, endA - fadeSec);
            }

            double trigger = outPoint > 0
                ? Math.Clamp(outPoint, 0, durA)
                : endA * Math.Clamp(settings.NextTriggerPct / 100.0, 0.05, 0.99);

            // Coming OUT of a jingle there is no mix at all: the jingle runs to
            // its last audible moment, stops, and the next song starts after a
            // beat of silence. So the trigger sits at A's end instead of a fade
            // length before it, and the fade is zeroed — StartCrossfade turns
            // this into the gap rather than a ramp.
            if (_deckA?.IsJingle == true && !deckB.IsJingle)
            {
                trigger = Math.Max(0, endA - 0.05);
                fadeSec = 0;
            }

            oldPrepared = _prepared?.DeckB;
            _prepared = new PreparedNext
            {
                DeckB = deckB,
                TriggerSec = trigger,
                FadeSec = fadeSec,
                TargetVol = targetVol,
                BeatAligned = beatAligned,
                Manual = manual,
                Reason = reason,
                OutPoint = outPoint,
                InPoint = inPoint,
                PairScore = score,
                MixSource = mixSource,
                Plan = plan
            };

            // Operator cue: Deck B is loaded silently (volume 0) and seeked to its
            // in-point, but its pump does NOT start here — the operator starts the
            // silent preview on demand (PlayDeckB), so the start moment is theirs to
            // pick for beat-matching. It stays inaudible (vol 0 + tap unpublished)
            // until the crossfade, which resets its tap so it enters live.
            if (manual)
            {
                deckB.Vol.Volume = 0f;
                _bManualStarted = false;
            }

            // [Trig] arm — only surfaces when verbose (Debug) logging is on.
            _log.LogDebug(
                "Trig: armed -> {To} | trigger {Trigger:F2}s ({Basis}) | fade {Fade:F2}s | {Mode} | {Source} | in {In:F2}s q={Score:F2} beat-aligned={Beat}",
                TrackLabel(next.Title, next.Artist), trigger,
                outPoint > 0 ? "out-point" : "NextTriggerPct",
                fadeSec, plan.StrategyName, mixSource,
                inPoint, score, beatAligned);
        }

        DisposeOffThread(oldPrepared);

        // Push B's grid once so the panel can render a static beat-clock at the
        // in-point before the operator starts the preview. (While stopped, the
        // position simply sits at the in-point.)
        if (manual)
            ProgressChanged?.Invoke(new DeckProgress
            {
                Deck = "B",
                TrackId = deckB.TrackId,
                PositionSec = inPoint,
                DurationSec = deckB.DurationSec,
                InPointSec = inPoint,
                Bpm = deckB.Bpm,
                PhaseOffsetSec = deckB.BeatPhaseOffsetSec,
                State = _state
            });

        return QueueResult.Ok;
    }

    public async Task<QueueResult> NextAsync(int? trackId, CancellationToken ct = default)
    {
        bool readyNow;
        lock (_gate)
        {
            readyNow = _prepared != null && (trackId == null || trackId == _prepared.DeckB.TrackId);
        }

        if (!readyNow)
        {
            if (trackId == null) return QueueResult.NotFound;
            var q = await QueueNextAsync(trackId.Value, ct);
            if (q != QueueResult.Ok) return q;
        }

        TransitionInfo? tr = null;
        lock (_gate)
        {
            if (_prepared != null && _deckA != null && !_crossfading)
                tr = StartCrossfade_Locked(_prepared, fromNext: true);
        }

        if (tr != null) { TransitionStarted?.Invoke(tr); EmitTaps(); }
        return tr != null ? QueueResult.Ok : QueueResult.NoCurrent;
    }

    public PlaybackStatus GetStatus()
    {
        lock (_gate)
        {
            if (_deckA == null)
                return new PlaybackStatus { State = PlaybackEngineState.Stopped };

            double pos = 0;
            try { pos = _deckA.Reader.CurrentTime.TotalSeconds; } catch { }

            var bDeck = _crossfading ? _deckB : _prepared?.DeckB;

            // Planned (or active) transition for the readout/log. During a
            // crossfade, report what is actually executing (_activePlan; null means
            // a crossfade). Otherwise report the armed force if one is set (it
            // overrides the next crossfade), else the plan carried on the cued Deck B.
            string? plannedTransition = null, plannedReason = null;
            if (_crossfading)
            {
                plannedTransition = (_activePlan?.Strategy ?? Transition.NormalCrossfade).ToString();
                plannedReason = _activePlan?.Reason ?? "normal crossfade";
            }
            else if (_prepared != null)
            {
                if (_armed is Transition armedNext)
                {
                    plannedTransition = armedNext.ToString();
                    plannedReason = "armed by operator (fires on next A->B)";
                }
                else if (_prepared.Plan != null)
                {
                    plannedTransition = _prepared.Plan.StrategyName;
                    plannedReason = _prepared.Plan.Reason;
                }
                else
                {
                    plannedTransition = Transition.NormalCrossfade.ToString();
                    plannedReason = "normal crossfade";
                }
            }

            return new PlaybackStatus
            {
                TrackId = _deckA.TrackId,
                Title = _deckA.Title,
                Artist = _deckA.Artist,
                Album = _deckA.Album,
                PositionSec = pos,
                DurationSec = _deckA.DurationSec,
                State = _state,
                Crossfading = _crossfading,
                NextTrackId = bDeck?.TrackId,
                NextTitle = bDeck?.Title,
                NextArtist = bDeck?.Artist,
                NextStarted = _bManualStarted,
                IsoA = _deckA.Iso.Mode,
                IsoB = bDeck?.Iso.Mode ?? IsoMode.None,
                PlannedTransition = plannedTransition,
                PlannedReason = plannedReason,
                ArmedTransition = _armed?.ToString()
            };
        }
    }

    // ── Tick loop ───────────────────────────────────────────────────────────

    private void TickLoop()
    {
        // The tick is the engine's heartbeat: crossfade triggers, the gain ramp,
        // spent-deck detection, the watchdog and every log line the engine emits
        // all happen here. Two things were wrong with the old loop.
        //
        // 1. Thread.Sleep(TickMs) sleeps AFTER the work, so the real period is
        //    TickMs + however long the tick took. That error accumulates: as the
        //    night goes on and the per-tick work grows, transitions are detected
        //    later and later than their trigger point, which is heard as dead air
        //    between songs that gets worse the longer the show runs. Sleeping to
        //    a DEADLINE instead keeps the period at TickMs regardless.
        //
        // 2. An exception anywhere in the body killed the thread outright. The
        //    transport then froze mid-track — still reporting PLAYING, position
        //    stuck, nothing arming, and no further engine log lines at all —
        //    which is exactly a log with nothing in it but WebServer entries.
        //    Catching per tick means one bad tick is one bad tick.
        var tickClock = System.Diagnostics.Stopwatch.StartNew();
        double nextDueMs = TickMs;
        double lastLateWarnMs = 0;

        while (_tickRunning)
        {
            double waitMs = nextDueMs - tickClock.Elapsed.TotalMilliseconds;
            if (waitMs > 0.5) Thread.Sleep((int)Math.Min(waitMs, TickMs));

            double lateMs = tickClock.Elapsed.TotalMilliseconds - nextDueMs;
            if (lateMs > 10 * TickMs && tickClock.Elapsed.TotalMilliseconds - lastLateWarnMs > 30_000)
            {
                lastLateWarnMs = tickClock.Elapsed.TotalMilliseconds;
                _log.LogWarning("Audio tick ran {Late:0}ms late — transitions will fire late while this persists.", lateMs);
            }

            // Never chase a backlog of missed ticks; just resume from now.
            nextDueMs = lateMs > 5 * TickMs
                ? tickClock.Elapsed.TotalMilliseconds + TickMs
                : nextDueMs + TickMs;

            try
            {

            NowPlayingInfo? np = null;
            TransitionInfo? tr = null;
            Deck? toDispose = null;
            float onsetA = 0f, onsetB = 0f;

            lock (_gate)
            {
                if (_deckA == null) continue;

                // ── Stuck-deck watchdog ─────────────────────────────────────
                // Playing, not crossfading, not paused — and the reader has not
                // advanced for StuckSeconds. That means the pump died (an
                // undecodable file usually, or an output that failed to start):
                // no exception reaches here, the transport just sits at 0:00
                // with an empty tap ring while the stream sends silence.
                //
                // Recovery is deliberately modest: log it loudly ONCE per track
                // with the path, then crossfade into the armed deck if there is
                // one. Nothing is deleted and no queue surgery happens — the
                // engine says what broke and gets the music moving again.
                if (_state == PlaybackEngineState.Playing && !_crossfading && !_fadePaused)
                {
                    double posNow = -1;
                    try { posNow = _deckA.Reader.CurrentTime.TotalSeconds; } catch { }

                    // First tick after a load: adopt the position rather than
                    // treating the -1 sentinel as "moved", so a deck that dies
                    // at 0:03 and never advances is caught from that point.
                    if (_watchdogPosSec < 0 && posNow >= 0) _watchdogPosSec = posNow;

                    if (posNow >= 0 && Math.Abs(posNow - _watchdogPosSec) < 0.02)
                        _watchdogStillTicks++;
                    else
                    {
                        _watchdogPosSec = posNow;
                        _watchdogStillTicks = 0;
                        if (_watchdogFiredForTrack != _deckA.TrackId) _watchdogFiredForTrack = -1;
                    }

                    if (_watchdogStillTicks * TickMs >= StuckMs && _watchdogFiredForTrack != _deckA.TrackId)
                    {
                        _watchdogFiredForTrack = _deckA.TrackId;
                        _log.LogError(
                            "Deck A is stuck: \"{Track}\" reports PLAYING but the position has not moved from " +
                            "{Pos:0.0}s for {Secs:0}s and the deck is producing no audio. The file is most likely " +
                            "not decodable on this machine. Path: {Path}",
                            TrackLabel(_deckA.Title, _deckA.Artist), _watchdogPosSec, StuckMs / 1000.0,
                            _deckA.FilePath);

                        if (_prepared != null && !_crossfading)
                        {
                            _log.LogWarning("Stuck deck: crossfading into the armed track to get the room moving.");
                            _armed = Transition.NormalCrossfade;
                            tr = StartCrossfade_Locked(_prepared, fromNext: false);
                        }
                        else
                        {
                            // Nothing armed, so there is nothing to mix into —
                            // but leaving a dead deck "playing" is the one
                            // outcome with no way out: the transport says
                            // PLAYING, so nothing else in the system thinks
                            // anything is wrong, and the room stays silent until
                            // somebody notices.
                            //
                            // Stopping it hands the problem to the path that
                            // already knows how to recover: the deck ends, the
                            // engine goes to Stopped WITHOUT marking it as an
                            // operator stop, and Auto DJ restarts the show from
                            // the next queue entry within a couple of seconds.
                            _log.LogWarning("Stuck deck: nothing is armed — stopping the dead deck so Auto DJ " +
                                            "can restart the show from the queue.");
                            _deckA.StopRequested = true;
                            try { _deckA.Out.Stop(); } catch { /* already gone */ }
                        }
                    }
                }

                // ── Master gain ramp (talk-over duck / fade pause) ──────────
                // Runs before the mix logic so both decks carry the same gain
                // through a crossfade. Pausing at the bottom of a fade happens
                // here, once, when the ramp actually lands on silence.
                if (_duckGain != _duckTarget)
                {
                    var g = _duckGain < _duckTarget
                        ? Math.Min(_duckTarget, _duckGain + _duckStep)
                        : Math.Max(_duckTarget, _duckGain - _duckStep);
                    _duckGain = Math.Clamp(g, 0f, 1f);
                    ApplyDuckGain_Locked();
                }

                if (_pauseAtSilence && _duckGain <= 0.001f)
                {
                    _pauseAtSilence = false;
                    if (_state == PlaybackEngineState.Playing)
                    {
                        _deckA.Out.Pause();
                        if (_crossfading) _deckB?.Out.Pause();
                        if (_prepared?.Manual == true && _bManualStarted) _prepared.DeckB.Out.Pause();
                        if (_prepared?.PrerollStarted == true) _prepared.DeckB.Out.Pause();
                        _state = PlaybackEngineState.Paused;
                        np ??= BuildNowPlaying_Locked();
                    }
                }

                onsetA = _deckA.Fft.BassOnset;

                if (_crossfading)
                {
                    onsetB = _deckB?.Fft.BassOnset ?? 0f;

                    // ── Beat drop: hold everything until A's kick ───────────
                    // While waiting the fade does not advance at all: A keeps
                    // playing at full and B stays silent, so a missed kick can
                    // never turn into a slow double-play. Either a kick lands
                    // (drop B in, cut A short) or the window expires and this
                    // becomes a Normal crossfade with the configured entry level.
                    bool frozen = false;

                    // Jingle hand-back gap: both decks silent, nothing ramping,
                    // until the count runs out. Then B starts at full and the
                    // transition completes on this same tick — no ramp is ever
                    // applied, so the song enters at its proper level.
                    if (_gapWaiting && _deckB != null)
                    {
                        _gapTicksLeft--;
                        if (_gapTicksLeft > 0)
                        {
                            // Null-forgiving rather than a null CHECK: testing
                            // _deckA here marks it maybe-null for the rest of the
                            // block, which lights up every later dereference in
                            // this tick. The surrounding crossfade code already
                            // guarantees both decks exist.
                            _deckA!.Vol.Volume = 0f;
                            frozen = true;
                        }
                        else
                        {
                            _gapWaiting = false;
                            try { _deckB.Reader.CurrentTime = TimeSpan.FromSeconds(_deckB.InPointSec); } catch { }
                            _deckB.Tap.Reset();
                            _deckB.Vol.Volume = _deckBTargetVol;
                            _deckBFading = false;
                            if (_state == PlaybackEngineState.Playing) _deckB.Out.Play();

                            _fadeStartVolA = 0f;
                            _crossFadePos = 0;
                            _crossFadeStep = 1f;   // completes below, this tick
                            _log.LogDebug("Jingle hand-back: silence over — \"{To}\" in at full.",
                                TrackLabel(_deckB.Title, _deckB.Artist));
                        }
                    }

                    if (_beatWaiting && _deckB != null)
                    {
                        _beatWaitTicksLeft--;
                        bool kick = onsetA > _beatWaitOnsetGate;

                        if (kick)
                        {
                            _beatWaiting = false;
                            _deckB.Vol.Volume = _deckBTargetVol;   // B lands at full, on the beat
                            _deckBFading = false;                  // and stays there; no ramp rewrites it
                            // A gets a short, clean cut rather than a click.
                            _crossFadePos = 0;
                            _crossFadeStep = CrossfadeMath.StepPerTick(TickMs, BeatDropCutSec);
                            _fadeStartVolA = _deckA.Vol.Volume;
                            _log.LogDebug("Beat drop: kick at onset {Onset:F2} — B in, A cut over {Cut:F2}s.",
                                onsetA, BeatDropCutSec);
                        }
                        else if (_beatWaitTicksLeft <= 0)
                        {
                            _beatWaiting = false;
                            _deckBFading = true;
                            _fadeBEntry = (float)Math.Clamp(MixRules.Load(_cfg).NormalEntryLevel, 0.0, 1.0);
                            _deckB.Vol.Volume = _fadeBEntry * _deckBTargetVol;
                            _crossFadePos = 0;
                            _crossFadeStep = CrossfadeMath.StepPerTick(TickMs, _beatFallbackFadeSec);
                            _fadeStartVolA = _deckA.Vol.Volume;
                            _log.LogInformation(
                                "Beat drop: no kick in the window — falling back to a normal crossfade (B enters at {Entry:P0}).",
                                _fadeBEntry);
                        }
                        else
                        {
                            // Still waiting: hold A where it is, leave B silent.
                            _deckA.Vol.Volume = _fadeStartVolA;
                            frozen = true;
                        }
                    }

                    if (!frozen)
                    {
                    _crossFadePos += _crossFadeStep;
                    float volA = CrossfadeMath.VolA(_fadeStartVolA, _crossFadePos);
                    _deckA.Vol.Volume = volA;

                    if (_activePlan != null)
                    {
                        // Auto-mix plan: A keeps its fade-out ramp (above); B's
                        // volume follows the normal fade-in only when the plan does
                        // NOT own B (e.g. bass-breakdown). Isolators come from the
                        // plan's steps; SmartBeat is suspended. The "downbeat" swap
                        // fires once when A reaches the planned bar boundary, and the
                        // "A is silent" steps fire once when A reaches the floor.
                        if (!_planOwnsB && _deckBFading && _deckB != null)
                            _deckB.Vol.Volume = CrossfadeMath.VolB(_deckBTargetVol, _crossFadePos);

                        if (!_planDownbeatFired && _planSwapAtSec != double.MaxValue)
                        {
                            double posA = 0;
                            try { posA = _deckA.Reader.CurrentTime.TotalSeconds; } catch { }
                            if (posA >= _planSwapAtSec)
                            {
                                ApplyPlanSteps_Locked("downbeat");
                                _planDownbeatFired = true;
                            }
                        }

                        if (!_planASilentFired && volA <= CrossFadeMinVol)
                        {
                            ApplyPlanSteps_Locked("aSilent");
                            _planASilentFired = true;
                        }
                    }
                    else
                    {
                        // Held-out B: silent until A has fallen to the configured
                        // level, then it enters at the entry level and ramps over
                        // whatever is left of the fade.
                        if (_fadeBHeld && _deckB != null)
                        {
                            if (_fadeStartVolA <= 0f || volA <= _fadeStartVolA * _fadeBEnterAtA)
                            {
                                // B joins here. Deck A's ramp is NOT touched — it
                                // keeps falling to zero on its original schedule,
                                // so the transition length stays exactly the
                                // configured Normal-crossfade time.
                                _fadeBHeld = false;
                                _deckBFading = true;
                                _fadeBStartPos = _crossFadePos;
                                // B has been paused at its in-point the whole
                                // time; re-seek (the warm burst may have moved
                                // it), clear the tap backlog, and start it now
                                // so it enters on its first bar, not partway in.
                                try { _deckB.Reader.CurrentTime = TimeSpan.FromSeconds(_deckB.InPointSec); } catch { }
                                _deckB.Tap.Reset();
                                _deckB.Vol.Volume = _fadeBEntry * _deckBTargetVol;
                                if (_state == PlaybackEngineState.Playing) _deckB.Out.Play();
                                _log.LogDebug(
                                    "Normal crossfade: A down to {Pct:P0} — Deck B in at {Entry:P0} (A keeps fading to 0).",
                                    _fadeBEnterAtA, _fadeBEntry);
                            }
                            else
                            {
                                _deckB.Vol.Volume = 0f;
                            }
                        }
                        else if (_deckBFading && _deckB != null)
                        {
                            // Progress measured from where B entered, so an entry
                            // level of 100% simply holds B at full while A falls.
                            double span = 1.0 - _fadeBStartPos;
                            double t = span <= 0.001 ? 1.0 : (_crossFadePos - _fadeBStartPos) / span;
                            double level = _fadeBEntry + (1.0 - _fadeBEntry) * Math.Clamp(t, 0.0, 1.0);
                            _deckB.Vol.Volume = (float)Math.Min(_deckBTargetVol, _deckBTargetVol * level);
                        }

                        // Legacy SmartBeat fader. Beat drop no longer arms this
                        // (it waits for a kick and then drops B in at full), so
                        // this is inert — kept only so a future move plan can
                        // reuse the ramp.
                        if (_smartBeatActive && _deckB != null && volA <= CrossFadeMinVol)
                        {
                            _beatFadeInPos += _beatFadeInStep;
                            float target = _deckB.BaseVolume;
                            _deckB.Vol.Volume = Math.Min(target, target * _beatFadeInPos);
                            if (_beatFadeInPos >= 1f)
                            {
                                _deckB.Vol.Volume = target;
                                _smartBeatActive = false;
                            }
                        }
                    }

                    if (_crossFadePos >= 1.0)
                        (np, toDispose) = FinishCrossfade_Locked();
                    }
                }
                else if (_prepared != null && _state == PlaybackEngineState.Playing)
                {
                    double pos = 0;
                    try { pos = _deckA.Reader.CurrentTime.TotalSeconds; } catch { }

                    // Arm-time warm burst: open the file, fill the OS cache and
                    // the read-ahead, then stop and re-seek to the in-point. This
                    // is what makes a hand-fired Next sound the same as a
                    // scheduled one — the decode path is already hot.
                    if (!_prepared.Manual && !_prepared.ArmWarmStarted)
                    {
                        _prepared.ArmWarmStarted = true;
                        _prepared.ArmWarmUntilSec = pos + ArmWarmSec;
                        _prepared.DeckB.SilentPreroll = true;
                        _prepared.DeckB.Out.Play();
                        _log.LogDebug("Warm: {Sec:F1}s burst on the cued Deck B (arm time).", ArmWarmSec);
                    }
                    else if (_prepared.ArmWarmStarted && !_prepared.ArmWarmDone
                             && !_prepared.PrerollStarted && pos >= _prepared.ArmWarmUntilSec)
                    {
                        _prepared.ArmWarmDone = true;
                        _prepared.DeckB.Out.Pause();
                        try { _prepared.DeckB.Reader.CurrentTime = TimeSpan.FromSeconds(_prepared.DeckB.InPointSec); }
                        catch { }
                    }

                    // Warm an auto Deck B ahead of the trigger: start its silent pump
                    // so the decode pipeline, OS file cache, and read-ahead are hot
                    // when the fade opens. B is re-seeked to its in-point at fade start
                    // (StartCrossfade_Locked), so this never moves where B enters.
                    // Manual B is operator-controlled and never auto-pre-rolled.
                    if (!_prepared.Manual && !_prepared.PrerollStarted
                        && pos >= _prepared.TriggerSec - PrerollSec)
                    {
                        _prepared.PrerollStarted = true;
                        _prepared.DeckB.SilentPreroll = true; // suppress its VU/progress until the fade
                        _prepared.DeckB.Out.Play();
                        _log.LogDebug(
                            "Preroll: warming Deck B {Pre:F1}s before trigger (pos {Pos:F2}s, trigger {Trig:F2}s)",
                            PrerollSec, pos, _prepared.TriggerSec);
                    }

                    if (!_prepared.Manual && pos >= _prepared.TriggerSec)
                    {
                        _log.LogDebug("Trig: fired at pos {Pos:F2}s (target {Target:F2}s)",
                            pos, _prepared.TriggerSec);
                        tr = StartCrossfade_Locked(_prepared, fromNext: false);
                    }
                }
            }

            if (toDispose != null) DisposeOffThread(toDispose);
            if (tr != null) { TransitionStarted?.Invoke(tr); EmitTaps(); }
            if (np != null) { NowPlayingChanged?.Invoke(np); EmitTaps(); }
            if (onsetA > 0f) BeatDetected?.Invoke(new BeatPulse { Deck = "A", Strength = onsetA });
            if (onsetB > 0f) BeatDetected?.Invoke(new BeatPulse { Deck = "B", Strength = onsetB });
                    }
            catch (Exception ex)
            {
                // One bad tick must not take the engine with it.
                _log.LogError(ex, "Audio tick failed; the engine keeps running.");
            }
        }
    }

    /// <summary>Starts the cued Deck B's silent preview pumping, so its beat-clock
    /// scrolls. No-op unless a manual B is cued and the engine is playing.</summary>
    public bool PlayDeckB()
    {
        lock (_gate)
        {
            if (_prepared?.Manual != true || _state != PlaybackEngineState.Playing) return false;
            _prepared.DeckB.Out.Play();
            _bManualStarted = true;
        }
        return true;
    }

    /// <summary>Pauses the cued Deck B's silent preview (its beat-clock freezes)
    /// without discarding it — toggle partner of <see cref="PlayDeckB"/>.</summary>
    public bool PauseDeckB()
    {
        lock (_gate)
        {
            if (_prepared?.Manual != true || !_bManualStarted) return false;
            _prepared.DeckB.Out.Pause();
            _bManualStarted = false;
        }
        return true;
    }

    /// <summary>Shifts the cued Deck B's playhead by deltaSec (negative = earlier)
    /// to nudge its beats into line with Deck A. Pushes a fresh B progress so the
    /// beat-clock reflects the new position immediately, even while paused.</summary>
    public bool NudgeDeckB(double deltaSec)
    {
        int tid; double pos, dur, inp; double? bpm, phase;
        lock (_gate)
        {
            if (_prepared?.Manual != true) return false;
            var b = _prepared.DeckB;
            double cur = 0;
            try { cur = b.Reader.CurrentTime.TotalSeconds; } catch { }
            double target = Math.Clamp(cur + deltaSec, 0, Math.Max(0, b.DurationSec - 0.05));
            try { b.Reader.CurrentTime = TimeSpan.FromSeconds(target); }
            catch { return false; }
            tid = b.TrackId; pos = target; dur = b.DurationSec; inp = b.InPointSec;
            bpm = b.Bpm; phase = b.BeatPhaseOffsetSec;
        }
        ProgressChanged?.Invoke(new DeckProgress
        {
            Deck = "B", TrackId = tid, PositionSec = pos, DurationSec = dur,
            InPointSec = inp, Bpm = bpm, PhaseOffsetSec = phase, State = _state
        });
        return true;
    }

    /// <summary>Clears the cued Deck B (discards the loaded preview). No-op while a
    /// crossfade is running.</summary>
    public bool EjectDeckB()
    {
        Deck? old;
        lock (_gate)
        {
            if (_crossfading || _prepared == null) return false;
            old = _prepared.DeckB;
            _prepared = null;
            _bManualStarted = false;
        }
        DisposeOffThread(old);
        EmitTaps();
        return true;
    }

    /// <summary>
    /// Operator-triggered crossfade: starts the A→B crossfade now, using whatever
    /// Deck B is currently prepared (manual or auto), ignoring the auto trigger
    /// position. Returns false if no B is cued or a crossfade is already running.
    /// </summary>
    public bool CrossfadeNow()
    {
        TransitionInfo? tr = null;
        lock (_gate)
        {
            if (_deckA == null || _crossfading || _prepared == null) return false;
            tr = StartCrossfade_Locked(_prepared, fromNext: false);
        }
        if (tr != null) { TransitionStarted?.Invoke(tr); EmitTaps(); }
        return true;
    }

    /// <summary>Arm a specific transition for the NEXT A→B crossfade only (the
    /// operator's force buttons). Arming the same transition again disarms it;
    /// arming a different one replaces it. The armed transition overrides the
    /// automatic pick and fires once — on whatever triggers the next crossfade
    /// (auto-advance, Next, or a hand-fired crossfade) — then clears. Returns the
    /// armed transition, or null if this call toggled it back off.</summary>
    public Transition? ArmTransition(Transition transition)
    {
        lock (_gate)
        {
            if (_armed == transition)
            {
                _armed = null;
                _log.LogInformation("Transition disarmed: {Transition}", transition);
                return null;
            }
            _armed = transition;
            _log.LogInformation("Transition armed: {Transition} (fires on next A->B)", transition);
            return transition;
        }
    }

    /// <summary>The currently armed transition, or null. Surfaced in the status so
    /// the operator can see what the next crossfade will force.</summary>
    public Transition? ArmedTransition
    {
        get { lock (_gate) return _armed; }
    }

    /// <summary>Build the plan for an armed transition against the current
    /// (Deck A → cued Deck B) pair — bypassing the auto-selection, preconditions,
    /// per-move toggles, and the section flags. Caller holds <c>_gate</c>.</summary>
    private MixPlan BuildForcedPlan_Locked(Transition transition, PreparedNext p)
    {
        var basePoints = new MixPoints
        {
            OutPoint = p.OutPoint,
            InPoint = p.InPoint,
            FadeDuration = p.FadeSec,
            BeatAligned = p.BeatAligned
        };
        TrackStructureData? aStruct = TryStructure(_deckA!.TrackId, _deckA.FilePath);
        TrackStructureData? bStruct = TryStructure(p.DeckB.TrackId, p.DeckB.FilePath);
        return MixPlanner.Plan(
            basePoints,
            _deckA.Bpm > 0 ? _deckA.Bpm : (double?)null,
            p.DeckB.Bpm, p.DeckB.BeatPhaseOffsetSec,
            aStruct, bStruct, MixRules.Load(_cfg), force: transition);
    }

    /// <summary>Set Deck A's EQ isolator mode (None / Bass / Vocal). Affects the
    /// soundcard and the /stream tap alike (both read downstream of the fader);
    /// the FFT/VU upstream are untouched. False if no track is on Deck A.</summary>
    public bool SetIsolationA(IsoMode mode)
    {
        lock (_gate)
        {
            if (_deckA == null) return false;
            _deckA.Iso.SetMode(mode);
            return true;
        }
    }

    /// <summary>Set the cued (or mixing) Deck B's EQ isolator mode. False if
    /// nothing is on Deck B.</summary>
    public bool SetIsolationB(IsoMode mode)
    {
        lock (_gate)
        {
            var b = _crossfading ? _deckB : _prepared?.DeckB;
            if (b == null) return false;
            b.Iso.SetMode(mode);
            return true;
        }
    }

    // ── Crossfade plumbing (callers hold _gate) ───────────────────────────────

    private TransitionInfo StartCrossfade_Locked(PreparedNext p, bool fromNext)
    {
        double triggerSec = 0;
        try { triggerSec = _deckA!.Reader.CurrentTime.TotalSeconds; } catch { }

        double endA = _deckA!.DurationSec;
        double effFade = CrossfadeMath.EffectiveFadeSec(triggerSec, p.FadeSec, endA);
        bool shortened = CrossfadeMath.WasShortened(triggerSec, p.FadeSec, endA);
        if (shortened)
            _log.LogInformation(
                "Crossfade fade shortened {Configured:F1}s -> {Effective:F1}s to fit before EOF (trigger {Trigger:F1}s, end {End:F1}s)",
                p.FadeSec, effFade, triggerSec, endA);

        _fadeStartVolA = _deckA.Vol.Volume;
        _deckBTargetVol = p.TargetVol;
        _deckB = p.DeckB;

        // If B was pre-rolled (auto warm-up) it has been decoding PAST its in-point
        // while silent. Re-seek it back so the mix still enters exactly where the
        // planner chose — the pre-roll only warmed the pipeline / cache / JIT, it must
        // not advance B. (Manual B is never pre-rolled; it keeps the position the
        // operator nudged it to.) Clearing SilentPreroll lets B's VU/progress flow now
        // that it is the live incoming deck.
        _deckB.SilentPreroll = false;
        if (p.PrerollStarted || p.ArmWarmStarted)
        {
            try { _deckB.Reader.CurrentTime = TimeSpan.FromSeconds(_deckB.InPointSec); } catch { }
        }

        // Drop B's tap backlog (silent pre-roll / manual-preview samples) so the stream
        // drains live audio the instant the crossfade publishes B's tap. No-op for an
        // auto B that was never pre-rolled (ring already empty).
        _deckB.Tap.Reset();

        // Scope both decks' decode-health counters to this fade, so the stats logged at
        // promotion describe the crossfade window only (DebugLogging diagnosis).
        _deckA.Tap.ResetStats();
        _deckB.Tap.ResetStats();

        _crossFadePos = 0;
        _crossFadeStep = CrossfadeMath.StepPerTick(TickMs, effFade);

        // ── Resolve the transition to run ────────────────────────────────────
        // An armed force (operator button) overrides the prepared automatic pick
        // for this one crossfade, then clears. Either way we always have a plan: a
        // move carries steps; a crossfade (Normal/Beatmatching/Beat drop) does not.
        MixPlan? plan;
        if (_armed is Transition armed)
        {
            plan = BuildForcedPlan_Locked(armed, p);
            _armed = null;
        }
        else if (_deckA?.IsJingle == true || p.DeckB.IsJingle)
        {
            // A jingle is a deliberate interruption, not a mix, so BOTH sides of
            // it are a plain Normal crossfade: into it and out of it. Beat-matching
            // a sing-along, or dropping its bass out, fights what the jingle was
            // for, and jingle clips are short and structurally odd enough that the
            // planner's picks aren't trustworthy on them either way. Firing by
            // hand already arms Normal; this is what makes a QUEUED jingle behave
            // the same when auto-advance reaches it.
            plan = BuildForcedPlan_Locked(Transition.NormalCrossfade, p);
        }
        else
        {
            plan = p.Plan;
        }
        bool isMove = plan != null && plan.IsMove;
        Transition winner = plan?.Strategy ?? Transition.NormalCrossfade;
        // Beat drop holds B silent until A's kick (the SmartBeat fader); every
        // other crossfade ramps B up from silence.
        bool beatDrop = winner == Transition.BeatDropCrossfade;

        // Every transition ramps B up over the fade. A NORMAL crossfade may start
        // B at the configured entry level (mixrules NormalEntryLevel) instead of
        // silence; every other transition keeps the from-silence ramp. A move's
        // steps (via _planOwnsB) or Beat drop's SmartBeat hold may override this.
        _deckBFading = true;
        var normalRules = MixRules.Load(_cfg);
        bool plainNormal = winner == Transition.NormalCrossfade && !isMove;
        _fadeBEntry = plainNormal ? (float)Math.Clamp(normalRules.NormalEntryLevel, 0.0, 1.0) : 0f;
        _fadeBEnterAtA = plainNormal ? (float)Math.Clamp(normalRules.NormalEntryAtA, 0.0, 1.0) : 1f;
        // Hold B out until A has come down far enough, when that's configured.
        _fadeBHeld = plainNormal && _fadeBEnterAtA < 0.999f;
        _fadeBStartPos = 0;
        _deckB.Vol.Volume = _fadeBHeld ? 0f : _fadeBEntry * _deckBTargetVol;

        // ── Move executor ────────────────────────────────────────────────────
        // A move runs its automation steps; a crossfade does not. When the plan
        // owns B's volume the auto B-ramp is suspended; the fade-start steps set
        // the isolators (and B's start volume). The "downbeat" swap fires once when
        // A reaches the planned bar boundary.
        _activePlan = isMove ? plan : null;
        _planOwnsB = _activePlan != null && PlanOwnsB(_activePlan);
        _planASilentFired = false;
        _planDownbeatFired = false;
        _planSwapAtSec = double.MaxValue;
        if (_activePlan != null && PlanHasTrigger(_activePlan, "downbeat"))
            _planSwapAtSec = ComputeSwapAt(triggerSec, effFade, _activePlan.SwapHoldSec, _deckA.Bpm, _deckA.BeatPhaseOffsetSec);
        if (_planOwnsB) _deckBFading = false;

        // ── Beat drop (SmartBeat fader) ──────────────────────────────────────
        // Only for a Beat drop crossfade: hold B silent until A's live kick, then
        // drop B in on the beat. If A isn't at a kick right now, fall back to the
        // plain ramp-in set above. Suspended whenever a move is running.
        _smartBeatActive = false;
        _beatFadeInPos = 0f;
        _beatFadeInStep = 0f;
        _beatWaiting = false;
        _beatWaitTicksLeft = 0;
        _beatFallbackFadeSec = effFade;
        // NB: _fadeBHeld is NOT reset here — it was armed a few lines above for
        // plain Normal crossfades. Clearing it here (as an earlier version did)
        // silently disabled the whole "Deck B waits until A is at X%" gate.
        string smartBeatState;
        if (_activePlan != null) smartBeatState = $"n/a (move {_activePlan.StrategyName})";
        else if (!beatDrop)
            smartBeatState = _fadeBHeld
                ? $"n/a (not beat-drop) — B held until A at {_fadeBEnterAtA:P0}, then enters at {_fadeBEntry:P0}"
                : $"n/a (not beat-drop) — B enters at {_fadeBEntry:P0} with the fade";
        else
        {
            // Wait up to two bars of A's tempo for a kick, but never past the
            // point where A would run out of track — a drop that arrives after
            // the song ends is just silence.
            double barSec = _deckA.Bpm is > 0 ? 4 * 60.0 / _deckA.Bpm.Value : 2.0;
            double roomSec = Math.Max(0, _deckA.DurationSec - triggerSec - effFade);
            double waitSec = Math.Clamp(Math.Min(2 * barSec, roomSec), 0, 4.0);

            if (waitSec >= 0.2)
            {
                // Hold the picture: A at full, B silent, fade ramp frozen.
                _deckB.Vol.Volume = 0f;
                _deckBFading = false;
                _beatWaiting = true;
                _beatWaitTicksLeft = (int)Math.Round(waitSec * 1000.0 / TickMs);
                smartBeatState = $"waiting up to {waitSec:F1}s for a kick";
            }
            else
            {
                // No room to wait — behave exactly like a Normal crossfade,
                // entry level included, instead of a silent-start overlap.
                _fadeBEntry = (float)Math.Clamp(MixRules.Load(_cfg).NormalEntryLevel, 0.0, 1.0);
                _deckB.Vol.Volume = _fadeBEntry * _deckBTargetVol;
                smartBeatState = "no room to wait -> normal crossfade";
            }
        }

        // ── Jingle hand-back: stop, silence, start ───────────────────────────
        // Not a crossfade at all. Deck A is cut and paused here; Deck B stays
        // paused and silent for JingleGapSec, then starts at full and the
        // transition completes on that tick. Every other special mode is stood
        // down so nothing can rewrite the volumes underneath it.
        _gapWaiting = false;
        _gapTicksLeft = 0;
        if (_deckA?.IsJingle == true && _deckB?.IsJingle == false)
        {
            _gapWaiting = true;
            _gapTicksLeft = Math.Max(1, (int)Math.Round(JingleGapSec * 1000.0 / TickMs));

            _deckA.Vol.Volume = 0f;
            try { _deckA.Out.Pause(); } catch { /* already stopping */ }
            _fadeStartVolA = 0f;

            _deckB.Vol.Volume = 0f;
            _deckBFading = false;
            _fadeBHeld = false;
            _beatWaiting = false;
            _smartBeatActive = false;
            _activePlan = null;
            _planOwnsB = false;

            _log.LogInformation("Transition: jingle hand-back — stop, {Gap:F1}s silence, then \"{To}\" starts clean.",
                JingleGapSec, TrackLabel(_deckB.Title, _deckB.Artist));
        }

        // Apply a move's fade-start steps (isolators + any B start volume). Then
        // log exactly what's running — for every transition, so it's always in the
        // log next to the planned line.
        if (_activePlan != null)
            ApplyPlanSteps_Locked("fadeStart");
        _log.LogInformation("Transition: {Transition} | {Reason} | actual fade {Fade:F2}s",
            winner.ToString(), plan?.Reason ?? "normal crossfade", effFade);

        // A held-out B stays PAUSED until it is let in. Starting it here would
        // let it play its opening bars silently and enter mid-phrase — the
        // listener hears the song "missing its first seconds".
        if (_state == PlaybackEngineState.Playing && !_fadeBHeld && !_gapWaiting) _deckB.Out.Play();
        _crossfading = true;
        _bManualStarted = false;

        var fromId = _deckA.TrackId;
        var toId = _deckB.TrackId;
        _prepared = null;

        // [Xfade] format dump + mix-decision card — verbose (Debug) only.
        if (_log.IsEnabled(LogLevel.Debug))
        {
            _log.LogDebug(
                "Xfade: begin -> {To} (fade {Fade:F2}s, {Mode}{Imm})\n" +
                "  Deck A reader={Ar} out={Ao}\n" +
                "  Deck B reader={Br} out={Bo}",
                TrackLabel(_deckB.Title, _deckB.Artist), effFade,
                winner.ToString(), fromNext ? ", immediate" : "",
                FmtDesc(_deckA.Reader.WaveFormat), FmtDesc(_deckA.Out.OutputWaveFormat),
                FmtDesc(_deckB.Reader.WaveFormat), FmtDesc(_deckB.Out.OutputWaveFormat));

            _log.LogDebug("{MixCard}", BuildMixCard(
                _deckA, _deckB, triggerSec, effFade, !isMove, smartBeatState,
                p.OutPoint, p.InPoint, p.PairScore, p.MixSource, p.BeatAligned, p.Reason));
        }

        return new TransitionInfo
        {
            FromTrackId = fromId,
            ToTrackId = toId,
            TriggerSec = triggerSec,
            FadeSeconds = effFade,
            SmartMix = !isMove,
            BeatAligned = plan?.BeatAligned ?? p.BeatAligned,
            FadeShortened = shortened,
            SmartBeatState = smartBeatState,
            Reason = plan?.Reason ?? p.Reason
        };
    }

    private (NowPlayingInfo np, Deck? toDispose) FinishCrossfade_Locked()
    {
        bool dbg = _log.IsEnabled(LogLevel.Debug);
        string? preA = dbg ? FmtDesc(_deckA?.Reader.WaveFormat) : null;
        string? preB = dbg ? FmtDesc(_deckB?.Reader.WaveFormat) : null;

        // Auto-mix plan: any fade-end steps run on the current decks before B is
        // promoted (the promotion then resets B's isolator to None).
        ApplyPlanSteps_Locked("fadeEnd");

        // The hand-back gap belongs to the transition that just ended; clearing it
        // here means a stopped or skipped jingle can never leave the next
        // transition frozen waiting on a count that will not run.
        _gapWaiting = false;
        _gapTicksLeft = 0;

        var old = _deckA;
        _deckA = _deckB;
        _deckB = null;

        if (_deckA != null)
        {
            _deckA.Label = "A";
            _deckA.Health.Name = "deckA";   // logs + black-box dumps follow the promotion
            // Promote to the incoming track's real target — NOT _deckBTargetVol,
            // which SmartBeat sets to 0 while holding B silent.
            _deckA.Vol.Volume = _deckA.BaseVolume;
            _deckA.Iso.SetMode(IsoMode.None); // policy: a finished mix starts on a clean isolator
        }

        if (dbg)
        {
            _log.LogDebug(
                "Finish: promote Deck B -> A | (pre) A={PreA} B={PreB} | (post) A={PostA} (VU handler rewired)",
                preA, preB, FmtDesc(_deckA?.Reader.WaveFormat));

            // Decode-health over the fade window (counters reset at StartCrossfade).
            // High slow / maxRead means a deck could not keep real time during the
            // fade — the upstream signature of the dropout "static" at the mix edges.
            if (old != null)
                _log.LogDebug("  decode A (outgoing): reads={R} slow={S} maxRead={M:F1}ms",
                    old.Tap.Reads, old.Tap.SlowReads, old.Tap.MaxReadMicros / 1000.0);
            if (_deckA != null)
                _log.LogDebug("  decode B (incoming): reads={R} slow={S} maxRead={M:F1}ms",
                    _deckA.Tap.Reads, _deckA.Tap.SlowReads, _deckA.Tap.MaxReadMicros / 1000.0);
        }

        ResetCrossfadeState_Locked();
        _state = _deckA != null ? PlaybackEngineState.Playing : PlaybackEngineState.Stopped;
        return (BuildNowPlaying_Locked(), old);
    }

    private void ResetCrossfadeState_Locked()
    {
        _crossfading = false;
        _deckBFading = false;
        _crossFadePos = 0;
        _crossFadeStep = 0;
        _fadeStartVolA = 0;
        _deckBTargetVol = 0;
        _smartBeatActive = false;
        _beatFadeInPos = 0;
        _beatFadeInStep = 0;
        _beatWaiting = false;
        _beatWaitTicksLeft = 0;
        _fadeBHeld = false;
        _fadeBStartPos = 0;
        _activePlan = null;
        _planOwnsB = false;
        _planASilentFired = false;
        _planSwapAtSec = double.MaxValue;
        _planDownbeatFired = false;
    }

    // ── Auto-mix plan executor helpers ─────────────────────────────────────────

    private static bool PlanOwnsB(MixPlan plan)
        => plan.Steps.Any(s => s.Deck == "B" && s.Vol.HasValue);

    private static bool PlanHasTrigger(MixPlan plan, string trigger)
        => plan.Steps.Any(s => s.At == trigger);

    /// <summary>The A-position (s) at which to swap on a downbeat: the first bar
    /// boundary on A's beat grid at or after the hold (or the fade midpoint when
    /// <paramref name="holdSec"/> is 0), falling back to the midpoint when A has no
    /// grid or the next bar lands past the fade.</summary>
    private static double ComputeSwapAt(double fromPos, double fade, double holdSec, double? bpm, double? phase)
    {
        double mid = fromPos + fade * 0.5;
        double target = holdSec > 0 ? fromPos + holdSec : mid;
        if (bpm is not double b || b <= 0) return Math.Min(target, fromPos + fade);
        double barSec = (60.0 / b) * 4.0;
        if (barSec <= 0) return Math.Min(target, fromPos + fade);
        double ph = phase is double p ? ((p % barSec) + barSec) % barSec : 0.0;
        long k = (long)Math.Ceiling((target - ph) / barSec);
        double downbeat = ph + k * barSec;
        return downbeat <= fromPos + fade ? downbeat : mid;
    }

    private TrackStructureData? TryStructure(int trackId, string path)
    {
        try { return TrackStructure.GetOrBuild(_cfg, trackId, path); }
        catch { return null; }
    }

    /// <summary>Apply every plan step whose trigger matches, on the current decks.
    /// A step's volume is a fraction of the deck's loudness-normalised BaseVolume;
    /// an isolator change is gapless. Caller holds _gate.</summary>
    private void ApplyPlanSteps_Locked(string trigger)
    {
        var plan = _activePlan;
        if (plan == null) return;

        foreach (var step in plan.Steps)
        {
            if (step.At != trigger) continue;

            Deck? deck = step.Deck == "A" ? _deckA : step.Deck == "B" ? _deckB : null;
            if (deck == null) continue;

            if (step.Iso != null) deck.Iso.SetMode(ParseIso(step.Iso));
            if (step.Vol is double v)
                deck.Vol.Volume = (float)Math.Clamp(v * deck.BaseVolume, 0.0, 1.0);

            _log.LogDebug("MixStep [{Trigger}] {Deck} iso={Iso} vol={Vol} | {Note}",
                trigger, step.Deck, step.Iso ?? "-", step.Vol, step.Note ?? "");
        }
    }

    private static IsoMode ParseIso(string? s) => s switch
    {
        "bass" => IsoMode.Bass,
        "vocal" => IsoMode.Vocal,
        "nobass" => IsoMode.NoBass,
        _ => IsoMode.None
    };

    // ── Verbose-logging helpers (Debug only) ──────────────────────────────────

    private static string FmtDesc(WaveFormat? wf) => wf == null
        ? "(none)"
        : $"{wf.Encoding} {wf.SampleRate}Hz {wf.Channels}ch {wf.BitsPerSample}bit avgBps={wf.AverageBytesPerSecond}";

    private static string TrackLabel(string? title, string? artist)
    {
        string t = string.IsNullOrWhiteSpace(title) ? "unknown title" : title!;
        string a = string.IsNullOrWhiteSpace(artist) ? "unknown artist" : artist!;
        return $"{t} — {a}";
    }

    /// <summary>
    /// Builds the multi-line mix-decision card from the data the engine already
    /// has at crossfade start. Emitted as a single log entry (leading newline so
    /// the framed block renders left-aligned under the entry's prefix in the
    /// admin log, and aligned in a flat-text dump).
    /// </summary>
    private static string BuildMixCard(
        Deck from, Deck to, double fromPos, double fadeSec, bool smartMix,
        string smartBeatState, double outPoint, double inPoint, double pairScore,
        string? mixSource, bool beatAligned, string? reason)
    {
        double fb = from.Bpm ?? 0, tb = to.Bpm ?? 0;
        double hi = Math.Max(fb, tb);
        double ratio = hi > 0 ? Math.Min(fb, tb) / hi : 0;
        string tier = hi <= 0 ? "n/a (BPM unknown)"
                    : ratio >= 0.99 ? "IDENTICAL"
                    : ratio >= 0.90 ? "CLOSE"
                    : "FAR";
        string mode = smartMix ? "TRUE CROSSFADE (A down, B up)" : "fade under (B full, A down)";

        var lines = new[]
        {
            "",
            "┌─ CROSSFADE (auto mix-out) ───────────────────────────────",
            FormattableString.Invariant($"│ A: {TrackLabel(from.Title, from.Artist)}  BPM {fb:F1}  pos {fromPos:F1}s"),
            FormattableString.Invariant($"│ B: {TrackLabel(to.Title, to.Artist)}  BPM {tb:F1}"),
            FormattableString.Invariant($"│ fade {fadeSec:F2}s   source: {mixSource ?? "?"}"),
            FormattableString.Invariant($"│ BPM ratio {ratio:F3} -> tier {tier}   beat-aligned: {(beatAligned ? "yes" : "no")}"),
            $"│ Deck B mode: {mode}",
            $"│ SmartBeat: {smartBeatState}",
            FormattableString.Invariant($"│ OutPoint {outPoint:F2}s   InPoint {inPoint:F2}s"),
            FormattableString.Invariant($"│ PairScore q={pairScore:F2}"),
            $"│ reason: {reason ?? "(none)"}",
            "└──────────────────────────────────────────────────────────"
        };
        return string.Join("\n", lines);
    }

    // ── Deck building + event helpers ─────────────────────────────────────────

    /// <summary>
    /// Picks a deck's audio output. Decks play to the default sound card whenever
    /// a render device is actually reachable, so the operator hears the decks
    /// locally — and the live stream taps the chain either way (the DeckTap sits
    /// upstream of the output device, so streaming and local sound are
    /// independent). A LocalSystem Windows Service runs in Session 0 with no
    /// audio endpoint, so there the probe reports no devices and the deck falls
    /// back to the silent pump; run the server interactively (console / as the
    /// logged-in user) for local sound. The choice is made when a deck is built,
    /// so a device appearing or vanishing takes effect on the next deck build
    /// (next load / crossfade), not the deck already playing.
    /// </summary>
    private IWavePlayer CreateDeckOutput(string label)
    {
        // Operator switch first: with local audio off, decks always get the
        // silent pump (the stream tap sits upstream, so /stream is unaffected).
        if (!_localAudio)
            return new SilentWavePlayer();

        // Otherwise always TRY the sound card — the pre-flight default-device
        // probe used here previously reported "no device" in some service
        // contexts even when audio would have worked, forcing silence. Now the
        // attempt itself decides: Init failure at the call site falls back to
        // SilentWavePlayer, and the endpoint watcher retries whenever devices
        // change/appear.
        return new WaveOutEvent { DesiredLatency = 60, NumberOfBuffers = 3 };
    }

    private Deck BuildDeck(Track track, float volume, double seekToSec, string label)
    {
        var reader = new SafeAudioFileReader(track.FilePath);

        // Normalise to the fixed stream format (44100 Hz, stereo) first, then run
        // the beat detector + VU meter BEFORE the volume fader and the stream tap
        // AFTER it. That way the FFT/VU reflect the track's content even when the
        // deck is faded to silence — so a cued Deck B's meters still move for
        // beat-matching — while the stream tap still captures the crossfade ramp.
        // (Volume is a scalar and normalisation is linear, so the audio reaching
        // the tap/output is identical to applying volume first; only where the
        // meter + FFT sample the signal changes.)
        ISampleProvider norm = reader.ToSampleProvider();
        if (norm.WaveFormat.Channels == 1)
            norm = new MonoToStereoSampleProvider(norm);
        if (norm.WaveFormat.SampleRate != StreamSampleRate)
            norm = new WdlResamplingSampleProvider(norm, StreamSampleRate);

        var fft = new FftAnalyser(norm);                    // content (pre-fader): beat/bass detection
        var meter = new MeteringSampleProvider(fft, 1024);  // content (pre-fader): VU
        var iso = new IsoFilter(meter);                     // EQ isolator (Bass/Vocal); bypass by default
        var vol = new VolumeSampleProvider(iso) { Volume = volume }; // output level + crossfade ramp
        // Master gain for the DJ's talk-over duck and the fade-to-pause. It sits
        // AFTER the crossfade fader deliberately: the mix logic keeps writing
        // Vol.Volume as it likes, and this multiplies whatever comes out, so the
        // two can never fight over one value.
        var duck = new VolumeSampleProvider(vol) { Volume = _duckGain };
        var health = new HealthTap(duck, "deck" + label, BlackBoxSeconds, OnHealthWindow); // diagnostics: sees exactly what output + stream get
        var tap = new DeckTap(health);                      // post-fader capture for the live stream

        var wp = new SampleToWaveProvider(tap);
        IWavePlayer outDev = CreateDeckOutput(label);
        try
        {
            outDev.Init(wp);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Deck {Deck}: output init failed; using silent output.", label);
            try { outDev.Dispose(); } catch { }
            outDev = new SilentWavePlayer();
            outDev.Init(wp);
        }

        var deck = new Deck
        {
            Reader = reader,
            Out = outDev,
            Wp = wp,
            Meter = meter,
            Vol = vol,
            Duck = duck,
            Iso = iso,
            Fft = fft,
            Tap = tap,
            Health = health,
            Label = label,
            BaseVolume = volume,
            TrackId = track.Id,
            FilePath = track.FilePath,
            Title = track.Title,
            Artist = track.Artist,
            Album = track.Album,
            Bpm = track.Bpm,
            BeatPhaseOffsetSec = track.BeatPhaseOffsetSec,
            DurationSec = reader.TotalTime.TotalSeconds,
            LeadOutSec = track.LeadOutSec
        };

        // Black-box capture: the ring dumps under the deck's CURRENT role
        // (label follows the B→A promotion), so a dump taken during a mix
        // names the files correctly. Unregistered by Deck.Dispose.
        deck.BlackBoxReg = AudioBlackBox.Register(() => "deck" + deck.Label, health.Ring);

        if (seekToSec > 0.1)
        {
            try { reader.CurrentTime = TimeSpan.FromSeconds(seekToSec); } catch { }
        }

        deck.MeterHandler = (_, ev) => OnMeter(deck, ev);
        meter.StreamVolume += deck.MeterHandler;
        deck.StoppedHandler = (_, _) => OnDeckStopped(deck);
        outDev.PlaybackStopped += deck.StoppedHandler;
        return deck;
    }

    /// <summary>
    /// Whether a track belongs to the designated jingle playlist. Asked while a
    /// context is already open, so it costs one extra indexed lookup per deck
    /// build and nothing at all when no playlist is designated.
    /// </summary>
    private async Task<bool> IsJingleTrackAsync(Y2KDbContext db, int trackId, CancellationToken ct)
    {
        if (JingleStore.PlaylistId(_cfg) is not int playlistId) return false;
        try
        {
            return await db.SavedPlaylistTracks.AsNoTracking()
                .AnyAsync(pt => pt.SavedPlaylistId == playlistId && pt.TrackId == trackId, ct);
        }
        catch (Exception ex)
        {
            // A lookup failure must not stop a track loading; it just plays as
            // an ordinary one.
            _log.LogDebug(ex, "Jingle membership lookup failed for track {TrackId}", trackId);
            return false;
        }
    }

    /// <summary>
    /// The deck's base volume: the master volume, adjusted by loudness
    /// normalisation so every track sits at the same perceived level.
    ///
    /// A jingle deliberately breaks both rules and plays at full scale. The
    /// point of a jingle is that the room notices it, and the master sits around
    /// 80% precisely so there is headroom to jump into — normalising a jingle
    /// back down to the target LUFS would undo the effect, and pulling a hot
    /// jingle file below the master volume would make it QUIETER than the music
    /// it interrupts. Consequence worth knowing: a quietly-mastered jingle file
    /// plays quietly, since nothing lifts it. Loudness of the file is now the
    /// operator's business.
    /// </summary>
    private float NormalizedVolume(Track t, Settings s, bool jingle = false)
        => jingle ? 1f : DeckVolumeFor(t.LufsIntegrated, s);

    /// <summary>
    /// The deck volume a track would get, as a linear 0–1 scale. Public and
    /// static so the admin PREVIEW player can hear exactly what the engine
    /// would do with a track, rather than an approximation of it — checking the
    /// normaliser by ear is only meaningful if both paths run the same maths.
    ///
    /// The clamps matter and are part of what is being judged: the correction is
    /// limited to ±12 dB, and the result can never exceed 1.0 — so with the
    /// master at 100% no track can be lifted at all, and a quiet track stays
    /// quiet. That ceiling is usually the reason a normaliser "isn't good
    /// enough".
    /// </summary>
    public static float DeckVolumeFor(double? lufsIntegrated, Settings s)
    {
        float baseVol = Math.Clamp(s.Volume / 100f, 0f, 1f);
        if (!s.NormalizeEnabled || lufsIntegrated is null or 0) return baseVol;

        double gainDb = Math.Clamp(s.TargetLufs - lufsIntegrated.Value, -12.0, 12.0);
        float gain = (float)Math.Pow(10.0, gainDb / 20.0);
        return Math.Min(1f, baseVol * gain);
    }

    private void OnMeter(Deck deck, StreamVolumeEventArgs ev)
    {
        // An auto Deck B pumps silently before the crossfade to warm its decode path;
        // don't surface its meters/position to the UI until it is the live incoming
        // deck (SilentPreroll is cleared at fade start).
        if (deck.SilentPreroll) return;

        var now = Environment.TickCount64;

        if (now - deck.LastVuTicks >= 100)
        {
            deck.LastVuTicks = now;
            var (l, r) = Peaks(ev.MaxSampleValues);
            VuChanged?.Invoke(new VuSample { Deck = deck.Label, Left = l, Right = r });
        }

        if (now - deck.LastProgTicks >= 250)
        {
            deck.LastProgTicks = now;
            double pos = 0;
            try { pos = deck.Reader.CurrentTime.TotalSeconds; } catch { }
            ProgressChanged?.Invoke(new DeckProgress
            {
                Deck = deck.Label,
                TrackId = deck.TrackId,
                PositionSec = pos,
                DurationSec = deck.DurationSec,
                InPointSec = deck.InPointSec,
                Bpm = deck.Bpm,
                PhaseOffsetSec = deck.BeatPhaseOffsetSec,
                State = _state
            });
        }
    }

    private void OnDeckStopped(Deck deck)
    {
        if (deck.StopRequested) return;

        NowPlayingInfo? np = null;
        Deck? toDispose = null;

        lock (_gate)
        {
            if (ReferenceEquals(deck, _deckA) && _crossfading)
            {
                (np, toDispose) = FinishCrossfade_Locked();
            }
            else if (ReferenceEquals(deck, _deckA) && _prepared != null)
            {
                // Deck A hit end-of-file BEFORE the armed trigger fired (decode
                // length vs. planner out-point rounding, or a trigger inside the
                // final buffer). Never stop with a loaded next: cut to it now.
                var p = _prepared;
                _prepared = null;

                var incoming = p.DeckB;
                incoming.SilentPreroll = false;
                if (p.PrerollStarted || p.ArmWarmStarted)
                {
                    // The warm-up decoded past the in-point while silent; re-seek
                    // so the cut still enters where the planner chose.
                    try { incoming.Reader.CurrentTime = TimeSpan.FromSeconds(incoming.InPointSec); } catch { }
                }
                incoming.Tap.Reset(); // drop pre-roll/preview backlog; stream gets live audio

                _activePlan = null;   // a cut runs no move steps
                _deckB = incoming;
                (np, toDispose) = FinishCrossfade_Locked(); // promote B → A, full volume
                try { _deckA!.Out.Play(); } catch { }

                _log.LogInformation(
                    "Deck A ended before the armed trigger — cut straight to {Title}.",
                    TrackLabel(_deckA?.Title, _deckA?.Artist));
            }
            else if (ReferenceEquals(deck, _deckA))
            {
                _state = PlaybackEngineState.Stopped;
                np = BuildNowPlaying_Locked();

                if (_oneShotJingle)
                {
                    // The deck was stopped before this jingle and goes back to
                    // stopped now. Marking it as an operator stop is what keeps
                    // Auto DJ from treating the silence as "the music ran out"
                    // and restarting the show behind it.
                    _oneShotJingle = false;
                    _stoppedByOperator = true;
                    _log.LogInformation("One-shot jingle finished — staying stopped until Play is pressed.");
                }
                else
                {
                    // Ran out with nothing armed. Leave the flag alone so Auto DJ
                    // can tell this apart from a stop somebody pressed.
                    _stoppedByOperator = false;
                    _log.LogWarning("Deck A reached the end with nothing armed — playback stopped.");
                }
            }
            else
            {
                return;
            }
        }

        if (toDispose != null) DisposeOffThread(toDispose);
        if (np != null) { NowPlayingChanged?.Invoke(np); EmitTaps(); }
    }

    private void EmitNowPlaying()
    {
        NowPlayingInfo info;
        lock (_gate) { info = BuildNowPlaying_Locked(); }
        NowPlayingChanged?.Invoke(info);
    }

    private void EmitTaps()
    {
        DeckTap? a, b;
        lock (_gate)
        {
            a = _deckA?.Tap;
            b = _crossfading ? _deckB?.Tap : null;
        }
        TapsChanged?.Invoke(a, b);
    }

    private NowPlayingInfo BuildNowPlaying_Locked()
        => _deckA == null
            ? new NowPlayingInfo { State = PlaybackEngineState.Stopped }
            : new NowPlayingInfo
            {
                TrackId = _deckA.TrackId,
                Title = _deckA.Title,
                Artist = _deckA.Artist,
                Album = _deckA.Album,
                DurationSec = _deckA.DurationSec,
                State = _state
            };

    private static (float Left, float Right) Peaks(float[]? max)
    {
        if (max == null || max.Length == 0) return (0f, 0f);
        var l = max[0];
        var r = max.Length > 1 ? max[1] : max[0];
        return (l, r);
    }

    private static void DisposeOffThread(params Deck?[] decks)
    {
        Task.Run(() =>
        {
            foreach (var d in decks) d?.Dispose();
        });
    }

    private sealed class PreparedNext
    {
        public required Deck DeckB { get; init; }
        public required double TriggerSec { get; init; }
        public required double FadeSec { get; init; }
        public required float TargetVol { get; init; }
        public required bool BeatAligned { get; init; }
        public bool Manual { get; init; }   // operator-started Deck B (silent preview): skip auto-fire, crossfade on demand
        public bool PrerollStarted { get; set; }   // auto Deck B warm-up pump has been started (re-seek to in-point at fade start)
        public bool ArmWarmStarted { get; set; }   // short warm burst at cue time has begun
        public bool ArmWarmDone { get; set; }      // …and has finished (B paused and re-seeked)
        public double ArmWarmUntilSec { get; set; }
        public string? Reason { get; init; }

        // Carried for the verbose mix-decision card (Debug logging only).
        public double OutPoint { get; init; }
        public double InPoint { get; init; }
        public double PairScore { get; init; }
        public string? MixSource { get; init; }

        // Auto-mix plan (phase 4), or null for a plain crossfade. Attached at
        // queue time when the rules are enabled; executed on an auto-trigger.
        public MixPlan? Plan { get; init; }
    }

    private sealed class Deck : IDisposable
    {
        public required SafeAudioFileReader Reader { get; init; }
        public required IWavePlayer Out { get; set; }
        /// <summary>The wave provider feeding the output — kept so a new output
        /// device can be initialised mid-track when the default changes.</summary>
        public required NAudio.Wave.IWaveProvider Wp { get; init; }
        public required MeteringSampleProvider Meter { get; init; }
        public required VolumeSampleProvider Vol { get; init; }
        public required VolumeSampleProvider Duck { get; init; }
        public required IsoFilter Iso { get; init; }
        public required FftAnalyser Fft { get; init; }
        public required DeckTap Tap { get; init; }
        public required HealthTap Health { get; init; }

        /// <summary>Black-box registration token; disposing removes this
        /// deck's capture ring from the dump set.</summary>
        public IDisposable? BlackBoxReg { get; set; }

        public string Label { get; set; } = "A";
        public float BaseVolume { get; set; } = 1f;
        /// <summary>This deck is playing a jingle: it runs at full output rather
        /// than the master volume, and whatever follows it is a plain Normal
        /// crossfade. Set when the deck is built, from the designated jingle
        /// playlist's membership — so a jingle behaves the same whether it was
        /// fired by hand or queued and reached normally.</summary>
        public bool IsJingle { get; set; }
        public bool SilentPreroll { get; set; } // auto warm-up pump running; suppress its UI events
        public double InPointSec { get; set; } // musical in-point (Deck B crossfade marker)

        public int TrackId { get; init; }
        public string FilePath { get; init; } = "";
        public string? Title { get; init; }
        public string? Artist { get; init; }
        public string? Album { get; init; }
        public double? Bpm { get; init; }
        public double? BeatPhaseOffsetSec { get; init; }
        public double DurationSec { get; init; }
        /// <summary>Last audible sample (sec) from analysis; null = unmeasured.
        /// The auto-next trigger treats this as the track's end so trailing
        /// silence never plays out.</summary>
        public double? LeadOutSec { get; init; }

        public EventHandler<StreamVolumeEventArgs>? MeterHandler { get; set; }
        public EventHandler<StoppedEventArgs>? StoppedHandler { get; set; }

        public long LastVuTicks { get; set; }
        public long LastProgTicks { get; set; }
        public bool StopRequested { get; set; }

        public void Dispose()
        {
            StopRequested = true;
            try { BlackBoxReg?.Dispose(); } catch { }
            try { if (MeterHandler != null) Meter.StreamVolume -= MeterHandler; } catch { }
            try { if (StoppedHandler != null) Out.PlaybackStopped -= StoppedHandler; } catch { }
            try { Out.Stop(); } catch { }
            try { Out.Dispose(); } catch { }
            try { Reader.Dispose(); } catch { }
        }
    }
}
