import { useEffect, useState } from 'react'
import * as api from './api'
import { fmtTime, isoFromCode, type Iso } from './api'
import type { Live } from './useHub'
import BeatClock from './BeatClock'

// Human-readable names for every transition (server enum -> label, word-for-word).
const TRANSITION_LABEL: Record<api.Transition, string> = {
  NormalCrossfade: 'Normal Crossfade',
  BeatmatchingCrossfade: 'Beatmatching Crossfade',
  BeatDropCrossfade: 'Beat drop Crossfade',
  VocalTease: 'Vocal tease',
  BassSwap: 'Bass swap',
  BassBreakdown: 'Bass breakdown',
}

// The two control sections, each with its three functions (force = arm one).
const CROSSFADES: api.Transition[] = ['NormalCrossfade', 'BeatmatchingCrossfade', 'BeatDropCrossfade']
const MOVES: api.Transition[] = ['VocalTease', 'BassSwap', 'BassBreakdown']

// Album art for the on-air track (Deck A); placeholder glyph when none / 404.
function CoverArt({ trackId }: { trackId: number | null }) {
  const [failed, setFailed] = useState(false)
  useEffect(() => { setFailed(false) }, [trackId]) // re-attempt on track change
  const show = trackId != null && !failed
  return (
    <div className="w-cover">
      {show
        ? <img src={`/api/albumart?trackId=${trackId}`} alt="" onError={() => setFailed(true)} />
        : <span className="w-cover-ph">♪</span>}
    </div>
  )
}

// Vertical L/R VU (content level, pre-fader — moves even while a deck is silent).
function VertVu({ vu }: { vu: { left: number; right: number } }) {
  const h = (v: number) => `${Math.max(0, Math.min(1, v)) * 100}%`
  return (
    <div className="w-vvu">
      <div className="w-vvu-track"><div className="w-vvu-fill" style={{ height: h(vu.left) }} /></div>
      <div className="w-vvu-track"><div className="w-vvu-fill" style={{ height: h(vu.right) }} /></div>
    </div>
  )
}

// Per-deck EQ isolator toggles (DJ-mixer style — NOT stem separation). Wired to
// the iso-a / iso-b endpoints; the highlighted mode reflects server status, and
// Bass/Vocal are mutually exclusive (tapping the lit one returns to None).
function IsoButtons(
  { iso, onSet, disabled }: { iso: Iso; onSet: (i: Iso) => void; disabled: boolean }
) {
  const toggle = (m: Iso) => onSet(iso === m ? 'none' : m)
  return (
    <span className="w-iso-group">
      <button className={`w-btn w-iso ${iso === 'bass' ? 'w-iso-on' : ''}`}
        title="Isolate bass (low-pass)" disabled={disabled}
        onClick={() => toggle('bass')}>Bass Only</button>
      <button className={`w-btn w-iso ${iso === 'nobass' ? 'w-iso-on' : ''}`}
        title="Cut bass (high-pass — for bass swaps)" disabled={disabled}
        onClick={() => toggle('nobass')}>No Bass</button>
      <button className={`w-btn w-iso ${iso === 'vocal' ? 'w-iso-on' : ''}`}
        title="Isolate vocals (centre-band — approximate, not a true stem)" disabled={disabled}
        onClick={() => toggle('vocal')}>Vocal Only</button>
    </span>
  )
}

export default function DeckPanel(
  { live, status, refresh, mixRules, onToggleSection }: {
    live: Live
    status: api.PlaybackStatus | null
    refresh: () => void
    mixRules: api.MixRulesDto | null
    onToggleSection: (section: 'crossfadeAuto' | 'mixingAuto') => Promise<void> | void
  }
) {
  const isoA = isoFromCode(status?.isoA)
  const isoB = isoFromCode(status?.isoB)
  const [busy, setBusy] = useState(false)

  const run = async (fn: () => Promise<unknown>) => {
    setBusy(true)
    try { await fn() } catch { /* surfaced elsewhere */ } finally { setBusy(false); refresh() }
  }

  const playing = status?.state === 1
  const crossfading = !!status?.crossfading
  const aId = status?.trackId ?? null
  const bId = status?.nextTrackId ?? null

  // On-air track + master transport (Deck A).
  const aProg = live.progressA && live.progressA.trackId === aId ? live.progressA : null
  const aBpm = aProg?.bpm ?? null
  const np = status ?? live.nowPlaying
  const position = aProg?.positionSec ?? status?.positionSec ?? 0
  const duration = status?.durationSec ?? aProg?.durationSec ?? 0
  const labelA = aId != null
    ? `A: ${np?.title ?? '—'}${aBpm ? `  [${Math.round(aBpm)} BPM]` : ''}`
    : 'DECK A'

  // Deck B.
  const bStarted = !!status?.nextStarted
  const bTitle = status?.nextTitle ?? null
  const bProg = live.progressB && live.progressB.trackId === bId ? live.progressB : null
  const bBpm = bProg?.bpm ?? null
  const labelB = bId != null
    ? `B: ${bTitle ?? '—'}${bBpm ? `  [${Math.round(bBpm)} BPM]` : ''}`
    : 'DECK B'

  const advancingA = playing && aId != null
  const advancingB = playing && bId != null && bStarted

  const canStartB = bId != null && !bStarted && !crossfading && playing && !busy
  const canStopB = bId != null && bStarted && !crossfading && !busy
  const canNudge = bId != null && !crossfading && !busy
  const canEjectB = bId != null && !crossfading && !busy
  const canCrossfade = bId != null && !crossfading && !busy
  const canIsoA = aId != null && !busy
  const canIsoB = bId != null && !busy
  const bTag = bId == null ? 'EMPTY' : crossfading ? 'MIXING' : bStarted ? 'PLAYING' : 'LOADED'

  // The transition planned for the next crossfade (or running during a mix), and
  // the operator's one-shot armed force (if any). Arming is allowed whenever a
  // track is on air — it fires on the next A→B (manual or auto), then clears.
  const planned = status?.plannedTransition ?? null
  const plannedLabel = planned ? TRANSITION_LABEL[planned] : '—'
  const armed = status?.armedTransition ?? null
  const canArm = aId != null && !busy

  return (
    <div className="w-panel w-raised w-deckpanel">
      <div className="w-panelhead">Decks</div>

      {/* On-air track + master transport (formerly the bottom bar) */}
      <div className="w-display w-npbar">
        <CoverArt trackId={aId} />
        <div className="w-np-text">
          <div className="w-np-title">{np?.title ?? '[ No track loaded ]'}</div>
          <div className="w-np-row"><span className="w-np-label">ARTIST </span>{np?.artist ?? '---'}</div>
          <div className="w-np-row"><span className="w-np-label">ALBUM  </span>{np?.album ?? '---'}</div>
          <div className="w-np-row">
            <span className="w-np-label">STATE  </span>
            {crossfading ? 'CROSSFADING' : playing ? 'PLAYING' : status?.state === 2 ? 'PAUSED' : 'STOPPED'}
          </div>
        </div>
        <div className="w-np-transport">
          <div className="w-seek">
            <span className="w-muted">{fmtTime(position)}</span>
            <input type="range" min={0} max={Math.max(1, Math.floor(duration))} value={Math.floor(position)}
              onChange={e => run(() => api.seek(Number(e.target.value)))} disabled={!aId} />
            <span className="w-muted">{fmtTime(duration)}</span>
          </div>
          <div className="w-transport">
            <button className="w-btn" title="Restart" disabled={!aId || busy}
              onClick={() => run(() => api.seek(0))}>⏮</button>
            <button className="w-btn w-play w-primary" disabled={!aId || busy}
              onClick={() => run(() => (playing ? api.pause() : api.play()))}>{playing ? 'Pause' : 'Play'}</button>
            <button className="w-btn" title="Stop" disabled={!aId || busy}
              onClick={() => run(api.stop)}>■</button>
            <button className="w-btn" title="Next (crossfade now)" disabled={!aId || busy}
              onClick={() => run(() => api.next())}>⏭</button>
          </div>
        </div>
      </div>

      {/* Deck A */}
      <div className="w-deckpanel-lane">
        <div className="w-deck-ctrls">
          <span className="w-deck-tag">{crossfading ? 'MIXING' : 'ON AIR'}</span>
          <span style={{ flex: 1 }} />
          <IsoButtons iso={isoA} onSet={(m) => run(() => api.setIsoA(m))} disabled={!canIsoA} />
        </div>
        <div className="w-deck-clockrow">
          <div className="w-deckclock">
            <BeatClock progress={aProg} advancing={advancingA} colorRgb="0,200,255" label={labelA} />
          </div>
          <VertVu vu={live.vuA} />
        </div>
      </div>

      {/* Crossfade section — Auto picks the best crossfade for the pair (Normal is
          the floor). Each Force button ARMS that crossfade for the next A→B only. */}
      <div className="w-mode-bar">
        <span className="w-sect-label">Crossfade</span>
        <button className={`w-btn w-iso ${mixRules?.crossfadeAuto ? 'w-iso-on' : ''}`}
          title="Auto-pick the best crossfade (Normal / Beatmatching / Beat drop) for each pair"
          disabled={mixRules == null || busy}
          onClick={() => run(() => Promise.resolve(onToggleSection('crossfadeAuto')))}>Auto</button>
        <span className="w-mode-label">Force:</span>
        {CROSSFADES.map(t => (
          <button key={t} className={`w-btn w-iso ${armed === t ? 'w-iso-on' : ''}`}
            title={`Arm ${TRANSITION_LABEL[t]} for the next A→B (click again to disarm)`}
            disabled={!canArm} onClick={() => run(() => api.armTransition(t))}>
            {TRANSITION_LABEL[t]}</button>
        ))}
      </div>

      {/* Mixing section — Auto picks a musical move when the pair allows it,
          otherwise the Crossfade section's pick. Each Force button ARMS that move. */}
      <div className="w-mode-bar">
        <span className="w-sect-label">Mixing</span>
        <button className={`w-btn w-iso ${mixRules?.mixingAuto ? 'w-iso-on' : ''}`}
          title="Auto-pick a move (Vocal tease / Bass swap / Bass breakdown) when the music allows"
          disabled={mixRules == null || busy}
          onClick={() => run(() => Promise.resolve(onToggleSection('mixingAuto')))}>Auto</button>
        <span className="w-mode-label">Force:</span>
        {MOVES.map(t => (
          <button key={t} className={`w-btn w-iso ${armed === t ? 'w-iso-on' : ''}`}
            title={`Arm ${TRANSITION_LABEL[t]} for the next A→B (click again to disarm)`}
            disabled={!canArm} onClick={() => run(() => api.armTransition(t))}>
            {TRANSITION_LABEL[t]}</button>
        ))}
      </div>

      {/* Crossfade bar — fire the manual A→B now; the armed transition (if any)
          fires on it. "Next:" names exactly what the next transition will do. */}
      <div className="w-xfade-bar">
        <button className="w-btn w-primary" disabled={!canCrossfade} onClick={() => run(api.crossfadeNow)}>
          Crossfade A → B
        </button>
        <span className="w-xfade-plan" title={status?.plannedReason ?? ''}>
          {crossfading ? 'Mixing: ' : 'Next: '}<b>{plannedLabel}</b>
          {armed && !crossfading ? ' (armed)' : ''}
        </span>
        <span style={{ flex: 1 }} />
        <span className="w-xfade-state">
          {crossfading
            ? 'Mixing…'
            : bId == null
              ? 'Cue a track to B with →B in the library'
              : bStarted
                ? 'Align the beats, then crossfade'
                : 'Press Start B to preview'}
        </span>
      </div>

      {/* Deck B */}
      <div className="w-deckpanel-lane">
        <div className="w-deck-ctrls">
          <span className="w-deck-tag">{bTag}</span>
          {bStarted
            ? <button className="w-btn w-deckbtn" disabled={!canStopB} title="Pause Deck B's preview (stays cued)"
                onClick={() => run(api.pauseDeckB)}>⏸ Stop B</button>
            : <button className="w-btn w-deckbtn" disabled={!canStartB} title="Start Deck B's silent preview"
                onClick={() => run(api.playDeckB)}>▶ Start B</button>}
          <span className="w-nudge">
            <button className="w-btn w-deckbtn" disabled={!canNudge} title="Nudge −50 ms"
              onClick={() => run(() => api.nudgeDeckB(-50))}>⟪</button>
            <button className="w-btn w-deckbtn" disabled={!canNudge} title="Nudge −10 ms"
              onClick={() => run(() => api.nudgeDeckB(-10))}>◀</button>
            <button className="w-btn w-deckbtn" disabled={!canNudge} title="Nudge +10 ms"
              onClick={() => run(() => api.nudgeDeckB(10))}>▶</button>
            <button className="w-btn w-deckbtn" disabled={!canNudge} title="Nudge +50 ms"
              onClick={() => run(() => api.nudgeDeckB(50))}>⟫</button>
          </span>
          <button className="w-btn w-deckbtn" disabled={!canEjectB} title="Clear Deck B"
            onClick={() => run(api.ejectDeckB)}>⏏ Eject</button>
          <span style={{ flex: 1 }} />
          <IsoButtons iso={isoB} onSet={(m) => run(() => api.setIsoB(m))} disabled={!canIsoB} />
        </div>
        <div className="w-deck-clockrow">
          <div className="w-deckclock">
            <BeatClock progress={bProg} advancing={advancingB} colorRgb="255,140,0" label={labelB} />
          </div>
          <VertVu vu={live.vuB} />
        </div>
      </div>
    </div>
  )
}
