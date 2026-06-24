using Microsoft.EntityFrameworkCore;
using NAudio.CoreAudioApi;
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
    // Auto-mix strategy for the next transition (or the one running during a
    // crossfade). Null when there is no cued/active Deck B. PlannedReason is the
    // planner's human-readable explanation.
    public string? PlannedStrategy { get; init; }
    public string? PlannedReason { get; init; }
}

/// <summary>
/// Dual-deck playback engine. Deck A is current; Deck B is the incoming track
/// during a crossfade. A ~50 ms tick loop advances the fade ramp and fires the
/// scheduled transition. Smart Mix vs a plain fade is chosen by
/// <c>Settings.SmartMix</c>; mix points come from <c>MixCache</c> (computed via
/// <see cref="MixAnalyser"/> on first use). The fade is shortened if needed so
/// Deck B reaches full volume by Deck A's end (EOF contract, policy a).
///
/// SmartBeat (Ship 2.3): when a scheduled Smart Mix crossfade starts and
/// <c>Settings.SmartBeatFader</c> is on and Deck A has a live kick at that
/// instant, Deck B is held silent through A's last beats and faded in only once
/// A goes quiet. It does not apply to a manual Next. Auto-advance / Auto DJ is
/// Phase 3 — a transition only happens when one is queued.
/// </summary>
public sealed class AudioEngine
{
    private const double TickMs = 50.0;
    private const float CrossFadeMinVol = 0.001f;

    // Fixed format the stream mixes at. Every deck is normalised to this rate
    // (and to stereo) ahead of its DeckTap, so the broadcast header stays
    // constant no matter what the source files are.
    private const int StreamSampleRate = 44100;

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
    private bool _bManualStarted;   // operator started the silent Deck B preview (pump running)
    private double _crossFadePos;
    private double _crossFadeStep;
    private float _fadeStartVolA;
    private float _deckBTargetVol;
    private bool _deckBFading;

    // SmartBeat fader state (guarded by _gate).
    private bool _smartBeatActive;
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

    private volatile bool _tickRunning = true;

    public AudioEngine(IDbContextFactory<Y2KDbContext> dbf, ILogger<AudioEngine> log, IConfiguration cfg)
    {
        _dbf = dbf;
        _log = log;
        _cfg = cfg;

        var tick = new Thread(TickLoop)
        {
            IsBackground = true,
            Name = "AudioEngineTick",
            Priority = ThreadPriority.AboveNormal
        };
        tick.Start();
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
        await using (var db = await _dbf.CreateDbContextAsync(ct))
        {
            track = await db.Tracks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == trackId, ct);
            settings = db.Settings.AsNoTracking().FirstOrDefault() ?? new Settings { Volume = 80 };
        }

        if (track == null) return LoadResult.NotFound;
        if (!File.Exists(track.FilePath)) return LoadResult.FileMissing;

        Deck deck;
        try
        {
            deck = BuildDeck(track, NormalizedVolume(track, settings), 0, "A", settings.StreamingEnabled);
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
        }

        DisposeOffThread(oldA, oldB, oldPrepared);
        EmitNowPlaying();
        EmitTaps();
        return LoadResult.Ok;
    }

    public bool Play()
    {
        lock (_gate)
        {
            if (_deckA == null) return false;
            if (_state == PlaybackEngineState.Playing) return true;
            _deckA.StopRequested = false;
            _deckA.Out.Play();
            if (_crossfading) _deckB?.Out.Play();
            if (_prepared?.Manual == true && _bManualStarted) _prepared.DeckB.Out.Play();
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
        }

        if (next == null) return QueueResult.NotFound;
        if (!File.Exists(next.FilePath)) return QueueResult.FileMissing;

        bool smartMix = settings.SmartMix;
        double configuredFade = settings.NextFadeSeconds;

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
                configuredFade, ct, smartMix);

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
                inPoint = 0;
                fadeSec = smartMix ? MixAnalyser.SmartFadeDuration(0, 0) : configuredFade;
                score = 0;
                beatAligned = false;
                reason = "fallback (analysis unavailable)";
                mixSource = "fallback (analysis unavailable)";
            }
        }

        // ── Auto-mix plan (phase 4) ──────────────────────────────────────────
        // When the rules are enabled and this isn't a manual operator cue, decide
        // a transition strategy now and carry it on the prepared transition. The
        // structure caches build here (off the audio thread); the planner is pure.
        // The plan only RUNS on an auto-trigger (see StartCrossfade_Locked) — a
        // manual Next or hand-fired crossfade stays a plain crossfade.
        MixPlan? plan = null;
        if (!manual)
        {
            var rules = MixRules.Load(_cfg);
            if (rules.Enabled)
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

                // Surface the decision now so the operator sees the planned
                // transition ahead of the actual crossfade (and it lands in the log).
                _log.LogInformation("Auto-mix planned: {Strategy} | {Reason}",
                    plan.StrategyName, plan.Reason);
            }
        }

        float targetVol = NormalizedVolume(next, settings);

        Deck deckB;
        try
        {
            deckB = BuildDeck(next, smartMix ? 0f : targetVol, inPoint, "B", settings.StreamingEnabled);
            deckB.BaseVolume = targetVol;
            deckB.InPointSec = inPoint;
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

            double trigger = outPoint > 0
                ? Math.Clamp(outPoint, 0, _deckA.DurationSec)
                : _deckA.DurationSec * Math.Clamp(settings.NextTriggerPct / 100.0, 0.05, 0.99);

            oldPrepared = _prepared?.DeckB;
            _prepared = new PreparedNext
            {
                DeckB = deckB,
                TriggerSec = trigger,
                FadeSec = fadeSec,
                TargetVol = targetVol,
                SmartMix = smartMix,
                SmartBeatFader = settings.SmartBeatFader,
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
                fadeSec, smartMix ? "Smart Mix" : "plain fade", mixSource,
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

            // Planned (or active) auto-mix strategy for the readout/log. During a
            // crossfade, report what is actually executing (_activePlan; null means
            // a plain fade). Otherwise report the plan carried on the cued Deck B —
            // or PlainCrossfade with a reason when no plan was attached (manual cue
            // or auto-mix disabled).
            string? plannedStrategy = null, plannedReason = null;
            if (_crossfading)
            {
                plannedStrategy = (_activePlan?.Strategy ?? MixStrategy.PlainCrossfade).ToString();
                plannedReason = _activePlan?.Reason ?? "plain crossfade";
            }
            else if (_prepared != null)
            {
                if (_prepared.Plan != null)
                {
                    plannedStrategy = _prepared.Plan.StrategyName;
                    plannedReason = _prepared.Plan.Reason;
                }
                else
                {
                    plannedStrategy = MixStrategy.PlainCrossfade.ToString();
                    plannedReason = _prepared.Manual ? "plain (manual cue)" : "plain (auto-mix off)";
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
                PlannedStrategy = plannedStrategy,
                PlannedReason = plannedReason
            };
        }
    }

    // ── Tick loop ───────────────────────────────────────────────────────────

    private void TickLoop()
    {
        while (_tickRunning)
        {
            Thread.Sleep((int)TickMs);

            NowPlayingInfo? np = null;
            TransitionInfo? tr = null;
            Deck? toDispose = null;
            float onsetA = 0f, onsetB = 0f;

            lock (_gate)
            {
                if (_deckA == null) continue;

                onsetA = _deckA.Fft.BassOnset;

                if (_crossfading)
                {
                    onsetB = _deckB?.Fft.BassOnset ?? 0f;

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
                        if (_deckBFading && _deckB != null)
                            _deckB.Vol.Volume = CrossfadeMath.VolB(_deckBTargetVol, _crossFadePos);

                        // SmartBeat: hold B silent until A is quiet, then fade B in.
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
                else if (_prepared != null && _state == PlaybackEngineState.Playing)
                {
                    double pos = 0;
                    try { pos = _deckA.Reader.CurrentTime.TotalSeconds; } catch { }
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

    /// <summary>Operator-forced crossfade using a specific auto-mix strategy, for
    /// testing each transition type. Builds the forced plan for the current
    /// (Deck A -> cued Deck B) pair — bypassing the auto-selection, the
    /// preconditions, the per-strategy toggles, and the master enable flag — and
    /// runs it now. False when there is nothing to mix (no Deck A, no cued Deck B,
    /// or a crossfade already running).</summary>
    public bool ForceCrossfade(MixStrategy strategy)
    {
        TransitionInfo? tr = null;
        lock (_gate)
        {
            if (_deckA == null || _crossfading || _prepared == null) return false;

            var basePoints = new MixPoints
            {
                OutPoint = _prepared.OutPoint,
                InPoint = _prepared.InPoint,
                FadeDuration = _prepared.FadeSec,
                BeatAligned = _prepared.BeatAligned
            };
            TrackStructureData? aStruct = TryStructure(_deckA.TrackId, _deckA.FilePath);
            TrackStructureData? bStruct = TryStructure(_prepared.DeckB.TrackId, _prepared.DeckB.FilePath);
            var forced = MixPlanner.Plan(
                basePoints,
                _deckA.Bpm > 0 ? _deckA.Bpm : (double?)null,
                _prepared.DeckB.Bpm, _prepared.DeckB.BeatPhaseOffsetSec,
                aStruct, bStruct, MixRules.Load(_cfg), force: strategy);

            _log.LogInformation("Auto-mix (forced by operator): {Strategy} | {Reason}",
                forced.StrategyName, forced.Reason);

            tr = StartCrossfade_Locked(_prepared, fromNext: false, forcedPlan: forced);
        }
        if (tr != null) { TransitionStarted?.Invoke(tr); EmitTaps(); }
        return true;
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

    private TransitionInfo StartCrossfade_Locked(PreparedNext p, bool fromNext, MixPlan? forcedPlan = null)
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
        // A manually-started B has been pumping silently; its tap ring holds ~8s of
        // buffered silence. Clear it so the stream drains live samples (not the
        // backlog) the instant the crossfade publishes B's tap. No-op for an auto B
        // (never pumped, ring empty).
        _deckB.Tap.Reset();
        _crossFadePos = 0;
        _crossFadeStep = CrossfadeMath.StepPerTick(TickMs, effFade);

        if (p.SmartMix)
        {
            _deckBFading = true;
            _deckB.Vol.Volume = 0f;
        }
        else
        {
            _deckBFading = false;
            _deckB.Vol.Volume = p.TargetVol;
        }

        // ── Auto-mix plan executor (phase 4) ─────────────────────────────────
        // The plan runs only on an auto-trigger; a manual Next (fromNext) stays a
        // plain crossfade. When the plan owns B's volume the auto B-ramp is
        // suspended; either way the fade-start steps set the isolators (and B's
        // start volume). SmartBeat is suspended whenever a plan is active.
        // The plan runs only on an auto-trigger, and only when it actually does
        // something (has steps) — a PlainCrossfade decision or a manual Next
        // (fromNext) falls through to today's behaviour, SmartBeat included.
        // A forced plan (operator test button) overrides the prepared plan for
        // this run; otherwise the prepared (auto) plan is used. Either way an
        // empty-step plan or a manual Next falls through to a plain crossfade.
        var planForRun = forcedPlan ?? p.Plan;
        _activePlan = (fromNext || planForRun == null || planForRun.Steps.Length == 0) ? null : planForRun;
        _planOwnsB = _activePlan != null && PlanOwnsB(_activePlan);
        _planASilentFired = false;
        _planDownbeatFired = false;
        _planSwapAtSec = double.MaxValue;
        if (_activePlan != null && PlanHasTrigger(_activePlan, "downbeat"))
            _planSwapAtSec = ComputeSwapAt(triggerSec, effFade, _activePlan.SwapHoldSec, _deckA.Bpm, _deckA.BeatPhaseOffsetSec);
        if (_planOwnsB) _deckBFading = false;

        // ── SmartBeat fader ──────────────────────────────────────────────────
        _smartBeatActive = false;
        _beatFadeInPos = 0f;
        _beatFadeInStep = 0f;
        string smartBeatState;
        if (_activePlan != null) smartBeatState = $"skip (auto-mix {_activePlan.StrategyName})";
        else if (!p.SmartBeatFader) smartBeatState = "disabled";
        else if (fromNext) smartBeatState = "skip (manual next)";
        else if (!p.SmartMix) smartBeatState = "skip (not smart mix)";
        else
        {
            float onset = _deckA.Fft.BassOnset; // set by the audio thread; volatile
            if (onset > 0.1f)
            {
                // Kill B now; SmartBeat controls it exclusively until A is silent.
                _deckB.Vol.Volume = 0f;
                _deckBFading = false;
                _deckBTargetVol = 0f;
                _smartBeatActive = true;
                _beatFadeInPos = 0f;
                _beatFadeInStep = (float)CrossfadeMath.StepPerTick(TickMs, effFade);
                smartBeatState = $"active (onset {onset:F2})";
            }
            else
            {
                smartBeatState = $"skip (no beat {onset:F2})";
            }
        }

        // Apply the plan's fade-start steps (isolators + any B start volume) and
        // announce the strategy in the activity log.
        if (_activePlan != null)
        {
            ApplyPlanSteps_Locked("fadeStart");
            _log.LogInformation("Auto-mix: {Strategy} | {Reason}", _activePlan.StrategyName, _activePlan.Reason);
        }

        if (_state == PlaybackEngineState.Playing) _deckB.Out.Play();
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
                p.SmartMix ? "Smart Mix" : "plain fade", fromNext ? ", immediate" : "",
                FmtDesc(_deckA.Reader.WaveFormat), FmtDesc(_deckA.Out.OutputWaveFormat),
                FmtDesc(_deckB.Reader.WaveFormat), FmtDesc(_deckB.Out.OutputWaveFormat));

            _log.LogDebug("{MixCard}", BuildMixCard(
                _deckA, _deckB, triggerSec, effFade, p.SmartMix, smartBeatState,
                p.OutPoint, p.InPoint, p.PairScore, p.MixSource, p.BeatAligned, p.Reason));
        }

        return new TransitionInfo
        {
            FromTrackId = fromId,
            ToTrackId = toId,
            TriggerSec = triggerSec,
            FadeSeconds = effFade,
            SmartMix = p.SmartMix,
            BeatAligned = p.BeatAligned,
            FadeShortened = shortened,
            SmartBeatState = smartBeatState,
            Reason = p.Reason
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

        var old = _deckA;
        _deckA = _deckB;
        _deckB = null;

        if (_deckA != null)
        {
            _deckA.Label = "A";
            // Promote to the incoming track's real target — NOT _deckBTargetVol,
            // which SmartBeat sets to 0 while holding B silent.
            _deckA.Vol.Volume = _deckA.BaseVolume;
            _deckA.Iso.SetMode(IsoMode.None); // policy: a finished mix starts on a clean isolator
        }

        if (dbg)
            _log.LogDebug(
                "Finish: promote Deck B -> A | (pre) A={PreA} B={PreB} | (post) A={PostA} (VU handler rewired)",
                preA, preB, FmtDesc(_deckA?.Reader.WaveFormat));

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
    /// Picks a deck's audio output. While streaming, decks pump silently and the
    /// live stream taps the chain. With streaming off, play to the default sound
    /// card so the operator hears the decks locally — but only when a device is
    /// actually reachable. A LocalSystem Windows Service runs in Session 0 with no
    /// audio endpoint, so there the probe reports no devices and we stay silent;
    /// run the server interactively (console / as the logged-in user) for local
    /// sound. Changing the streaming setting takes effect on the next deck build
    /// (next load / crossfade), not the deck already playing.
    /// </summary>
    private IWavePlayer CreateDeckOutput(bool streaming, string label)
    {
        if (!streaming)
        {
            try
            {
                using var devices = new MMDeviceEnumerator();
                if (devices.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console))
                    return new WaveOutEvent { DesiredLatency = 60, NumberOfBuffers = 3 };
                _log.LogInformation(
                    "Deck {Deck}: streaming off but no default render device (headless / LocalSystem service?); using silent output.",
                    label);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Deck {Deck}: audio device probe failed; using silent output.", label);
            }
        }
        return new SilentWavePlayer();
    }

    private Deck BuildDeck(Track track, float volume, double seekToSec, string label, bool streaming)
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
        var tap = new DeckTap(vol);                         // post-fader capture for the live stream

        var wp = new SampleToWaveProvider(tap);
        IWavePlayer outDev = CreateDeckOutput(streaming, label);
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
            Meter = meter,
            Vol = vol,
            Iso = iso,
            Fft = fft,
            Tap = tap,
            Label = label,
            BaseVolume = volume,
            TrackId = track.Id,
            FilePath = track.FilePath,
            Title = track.Title,
            Artist = track.Artist,
            Album = track.Album,
            Bpm = track.Bpm,
            BeatPhaseOffsetSec = track.BeatPhaseOffsetSec,
            DurationSec = reader.TotalTime.TotalSeconds
        };

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

    private float NormalizedVolume(Track t, Settings s)
    {
        float baseVol = Math.Clamp(s.Volume / 100f, 0f, 1f);
        if (!s.NormalizeEnabled || t.LufsIntegrated is null or 0) return baseVol;

        double gainDb = Math.Clamp(s.TargetLufs - t.LufsIntegrated.Value, -12.0, 12.0);
        float gain = (float)Math.Pow(10.0, gainDb / 20.0);
        return Math.Min(1f, baseVol * gain);
    }

    private void OnMeter(Deck deck, StreamVolumeEventArgs ev)
    {
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
            else if (ReferenceEquals(deck, _deckA))
            {
                _state = PlaybackEngineState.Stopped;
                np = BuildNowPlaying_Locked();
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
        public required bool SmartMix { get; init; }
        public required bool SmartBeatFader { get; init; }
        public required bool BeatAligned { get; init; }
        public bool Manual { get; init; }   // operator-started Deck B (silent preview): skip auto-fire, crossfade on demand
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
        public required IWavePlayer Out { get; init; }
        public required MeteringSampleProvider Meter { get; init; }
        public required VolumeSampleProvider Vol { get; init; }
        public required IsoFilter Iso { get; init; }
        public required FftAnalyser Fft { get; init; }
        public required DeckTap Tap { get; init; }

        public string Label { get; set; } = "A";
        public float BaseVolume { get; set; } = 1f;
        public double InPointSec { get; set; } // musical in-point (Deck B crossfade marker)

        public int TrackId { get; init; }
        public string FilePath { get; init; } = "";
        public string? Title { get; init; }
        public string? Artist { get; init; }
        public string? Album { get; init; }
        public double? Bpm { get; init; }
        public double? BeatPhaseOffsetSec { get; init; }
        public double DurationSec { get; init; }

        public EventHandler<StreamVolumeEventArgs>? MeterHandler { get; set; }
        public EventHandler<StoppedEventArgs>? StoppedHandler { get; set; }

        public long LastVuTicks { get; set; }
        public long LastProgTicks { get; set; }
        public bool StopRequested { get; set; }

        public void Dispose()
        {
            StopRequested = true;
            try { if (MeterHandler != null) Meter.StreamVolume -= MeterHandler; } catch { }
            try { if (StoppedHandler != null) Out.PlaybackStopped -= StoppedHandler; } catch { }
            try { Out.Stop(); } catch { }
            try { Out.Dispose(); } catch { }
            try { Reader.Dispose(); } catch { }
        }
    }
}
