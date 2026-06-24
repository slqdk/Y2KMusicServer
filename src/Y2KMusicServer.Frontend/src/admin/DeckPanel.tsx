import { useEffect, useState } from 'react'
import * as api from './api'
import { fmtTime, isoFromCode, type Iso } from './api'
import type { Live } from './useHub'
import BeatClock from './BeatClock'

// Human-readable names for the auto-mix strategies (server enum -> label).
const MIX_LABEL: Record<api.MixStrategy, string> = {
  PlainCrossfade: 'Plain crossfade',
  VocalTease: 'Vocal tease',
  BassSwap: 'Bass swap',
  BassBreakdown: 'Bass breakdown',
}

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

export type MixModes = { smartMix: boolean; smartBeatFader: boolean; autoMix: boolean }

export default function DeckPanel(
  { live, status, refresh, modes, onToggleMode }: {
    live: Live
    status: api.PlaybackStatus | null
    refresh: () => void
    modes: MixModes | null
    onToggleMode: (m: keyof MixModes) => Promise<void> | void
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

  // Auto-mix strategy planned for the next transition (or running during a mix).
  const planned = status?.plannedStrategy ?? null
  const plannedLabel = planned ? MIX_LABEL[planned] : '—'

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

      {/* Mixing modes — persisted toggles, mirror the Settings dialog. */}
      <div className="w-mode-bar">
        <span className="w-mode-label">Modes:</span>
        <button className={`w-btn w-iso ${modes?.smartMix ? 'w-iso-on' : ''}`}
          title="Smart Mix — true beat-matched crossfade instead of a plain fade"
          disabled={modes == null || busy} onClick={() => run(() => Promise.resolve(onToggleMode('smartMix')))}>
          Smart Mix</button>
        <button className={`w-btn w-iso ${modes?.smartBeatFader ? 'w-iso-on' : ''}`}
          title="SmartBeat Fader — hold B silent until A's kick, then drop B in on the beat"
          disabled={modes == null || busy} onClick={() => run(() => Promise.resolve(onToggleMode('smartBeatFader')))}>
          SmartBeat</button>
        <button className={`w-btn w-iso ${modes?.autoMix ? 'w-iso-on' : ''}`}
          title="Intelligent auto-mix — choose vocal-tease / bass-swap / bass-breakdown per pair on auto transitions"
          disabled={modes == null || busy} onClick={() => run(() => Promise.resolve(onToggleMode('autoMix')))}>
          Auto-Mix</button>
      </div>

      <div className="w-xfade-bar">
        <button className="w-btn w-primary" disabled={!canCrossfade} onClick={() => run(api.crossfadeNow)}>
          Crossfade A → B
        </button>
        <button className="w-btn" title="Force a vocal-tease transition now (test)" disabled={!canCrossfade}
          onClick={() => run(() => api.forceMix('VocalTease'))}>Vocal Tease</button>
        <button className="w-btn" title="Force a bass-swap transition now (test)" disabled={!canCrossfade}
          onClick={() => run(() => api.forceMix('BassSwap'))}>Bass Swap</button>
        <button className="w-btn" title="Force a bass-breakdown transition now (test)" disabled={!canCrossfade}
          onClick={() => run(() => api.forceMix('BassBreakdown'))}>Bass Breakdown</button>
        <span className="w-xfade-plan" title={status?.plannedReason ?? ''}>
          {crossfading ? 'Mixing: ' : 'Next mix: '}<b>{plannedLabel}</b>
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
