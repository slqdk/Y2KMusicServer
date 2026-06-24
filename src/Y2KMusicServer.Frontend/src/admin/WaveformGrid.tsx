import { useEffect, useRef, useState } from 'react'
import * as api from './api'

// Waveform + beat-grid canvas for the cued/mixing deck.
//
// Read-only by default: a min/max envelope (mirrored around the centre line)
// with vertical beat ticks (60/bpm from the phase offset) and brighter/taller
// bar lines every 4th beat, plus an optional playhead for the mixing deck.
//
// When `editable` (the cued deck, at rest), it becomes a beat-grid editor:
//   • drag the waveform left/right to shift the downbeat phase (live preview),
//   • ½× / −0.1 / +0.1 / 2× buttons to adjust tempo.
// On release / button press it PUTs the grid to the track (persisted globally;
// the server clears the affected MixCache rows). Editing needs an existing BPM.
//
// Times map on the waveform's own sample axis (index·samplesPerPoint/sampleRate),
// so the grid/playhead line up with the drawn audio rather than the DB duration.
export default function WaveformGrid({
  trackId,
  playheadSec = null,
  editable = false,
}: {
  trackId: number | null
  playheadSec?: number | null
  editable?: boolean
}) {
  const [wf, setWf] = useState<api.WaveformDto | null>(null)
  const [failed, setFailed] = useState(false)
  const [saving, setSaving] = useState(false)

  const cv = useRef<HTMLCanvasElement>(null)
  const box = useRef<HTMLDivElement>(null)

  const wfRef = useRef<api.WaveformDto | null>(wf)
  const failRef = useRef(failed)
  const playRef = useRef<number | null>(playheadSec)
  const phaseDraftRef = useRef<number | null>(null) // live phase while dragging
  wfRef.current = wf
  failRef.current = failed
  playRef.current = playheadSec

  const drag = useRef<{ active: boolean; startX: number; startPhase: number }>(
    { active: false, startX: 0, startPhase: 0 })

  const spanSecOf = (w: api.WaveformDto) =>
    ((w.peaks.length >> 1) * w.samplesPerPoint) / w.sampleRate || 1
  const canEdit = editable && !saving && !!wf && !!wf.bpm && wf.bpm > 0

  // Fetch on track change.
  useEffect(() => {
    let cancelled = false
    setWf(null)
    setFailed(false)
    phaseDraftRef.current = null
    drag.current.active = false
    if (trackId == null) return
    api.getWaveform(trackId)
      .then(d => { if (!cancelled) setWf(d) })
      .catch(() => { if (!cancelled) setFailed(true) })
    return () => { cancelled = true }
  }, [trackId])

  // Draw loop (mount once; reads latest from refs).
  useEffect(() => {
    const c = cv.current
    const b = box.current
    if (!c || !b) return
    const ctx = c.getContext('2d')
    if (!ctx) return
    let raf = 0

    const draw = () => {
      const dpr = window.devicePixelRatio || 1
      const w = b.clientWidth || 1
      const h = b.clientHeight || 1
      const pw = Math.round(w * dpr)
      const ph = Math.round(h * dpr)
      if (c.width !== pw || c.height !== ph) { c.width = pw; c.height = ph }
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
      ctx.clearRect(0, 0, w, h)

      const mid = h / 2
      const data = wfRef.current

      if (!data || data.peaks.length < 2) {
        ctx.fillStyle = 'rgba(79,143,120,0.55)'
        ctx.font = '11px "Lucida Console", Consolas, monospace'
        ctx.fillText(failRef.current ? 'waveform unavailable' : 'loading waveform…', 6, mid + 4)
        raf = requestAnimationFrame(draw)
        return
      }

      const windows = data.peaks.length >> 1
      const span = (windows * data.samplesPerPoint) / data.sampleRate || 1
      const xOf = (t: number) => (t / span) * w

      // Waveform: aggregate windows down to one bar per pixel column.
      ctx.fillStyle = '#2f7d5a'
      for (let x = 0; x < w; x++) {
        const i0 = Math.floor((x / w) * windows)
        let i1 = Math.floor(((x + 1) / w) * windows)
        if (i1 <= i0) i1 = i0 + 1
        let lo = 127
        let hi = -127
        for (let i = i0; i < i1 && i < windows; i++) {
          const mn = data.peaks[i * 2]
          const mx = data.peaks[i * 2 + 1]
          if (mn < lo) lo = mn
          if (mx > hi) hi = mx
        }
        if (hi < lo) { lo = 0; hi = 0 }
        const yTop = mid - (hi / 127) * (mid - 1)
        const yBot = mid - (lo / 127) * (mid - 1)
        ctx.fillRect(x, yTop, 1, Math.max(1, yBot - yTop))
      }

      // Beat grid (4/4 bar emphasis from the phase downbeat; draft phase wins
      // during a drag so the move previews live).
      if (data.bpm && data.bpm > 0) {
        const beat = 60 / data.bpm
        const phase = phaseDraftRef.current ?? (data.phaseOffsetSec ?? 0)
        let t = phase
        while (t - beat >= 0) t -= beat
        let kRel = Math.round((t - phase) / beat)
        for (; t <= span + 1e-6; t += beat, kRel++) {
          const x = Math.round(xOf(t))
          const isBar = (((kRel % 4) + 4) % 4) === 0
          ctx.fillStyle = isBar ? 'rgba(227,208,70,0.80)' : 'rgba(227,208,70,0.30)'
          const top = isBar ? 0 : h * 0.18
          const bot = isBar ? h : h * 0.82
          ctx.fillRect(x, top, isBar ? 2 : 1, bot - top)
        }
      }

      // Playhead (mixing deck only).
      const p = playRef.current
      if (p != null && p >= 0) {
        ctx.fillStyle = '#ff5a3c'
        ctx.fillRect(Math.round(xOf(p)), 0, 2, h)
      }

      raf = requestAnimationFrame(draw)
    }

    raf = requestAnimationFrame(draw)
    return () => cancelAnimationFrame(raf)
  }, [])

  const commit = (bpm: number, phase: number) => {
    if (trackId == null) return
    const bar = 4 * (60 / bpm)
    let p = ((phase % bar) + bar) % bar
    if (!isFinite(p) || p < 0) p = 0
    setSaving(true)
    api.putBeatGrid(trackId, { bpm, phaseOffsetSec: p })
      .then(r => setWf(prev => (prev ? { ...prev, bpm: r.bpm, phaseOffsetSec: r.phaseOffsetSec } : prev)))
      .catch(() => { /* keep previous grid on failure */ })
      .finally(() => { phaseDraftRef.current = null; setSaving(false) })
  }

  // ── Phase drag ──
  const onDown = (e: React.PointerEvent<HTMLCanvasElement>) => {
    if (!canEdit || !wf) return
    const phase = phaseDraftRef.current ?? (wf.phaseOffsetSec ?? 0)
    drag.current = { active: true, startX: e.clientX, startPhase: phase }
    phaseDraftRef.current = phase
    e.currentTarget.setPointerCapture(e.pointerId)
  }
  const onMove = (e: React.PointerEvent<HTMLCanvasElement>) => {
    if (!drag.current.active || !wf || !box.current) return
    const w = box.current.clientWidth || 1
    const dt = ((e.clientX - drag.current.startX) / w) * spanSecOf(wf)
    phaseDraftRef.current = drag.current.startPhase + dt
  }
  const onUp = (e: React.PointerEvent<HTMLCanvasElement>) => {
    if (!drag.current.active) return
    drag.current.active = false
    try { e.currentTarget.releasePointerCapture(e.pointerId) } catch { /* ignore */ }
    if (wf && wf.bpm && wf.bpm > 0) commit(wf.bpm, phaseDraftRef.current ?? (wf.phaseOffsetSec ?? 0))
    else phaseDraftRef.current = null
  }

  // ── Tempo nudge: newBpm = bpm*factor + delta ──
  const nudge = (factor: number, delta: number) => {
    if (!wf || !wf.bpm) return
    const bpm = Math.max(20, Math.min(400, wf.bpm * factor + delta))
    commit(bpm, phaseDraftRef.current ?? (wf.phaseOffsetSec ?? 0))
  }

  return (
    <div className="w-wavewrap">
      <div className="w-wave" ref={box} style={{ cursor: canEdit ? 'ew-resize' : 'default' }}>
        <canvas
          ref={cv}
          style={{ touchAction: 'none' }}
          onPointerDown={onDown}
          onPointerMove={onMove}
          onPointerUp={onUp}
          onPointerCancel={onUp}
        />
      </div>
      {editable && wf && (
        <div className="w-wavectl">
          {wf.bpm && wf.bpm > 0 ? (
            <>
              <span className="w-muted">drag to align ·</span>
              <button className="w-btn" disabled={saving} onClick={() => nudge(0.5, 0)} title="Half tempo">½×</button>
              <button className="w-btn" disabled={saving} onClick={() => nudge(1, -0.1)} title="-0.1 BPM">−</button>
              <span className="w-wavebpm">{wf.bpm.toFixed(1)} BPM</span>
              <button className="w-btn" disabled={saving} onClick={() => nudge(1, 0.1)} title="+0.1 BPM">+</button>
              <button className="w-btn" disabled={saving} onClick={() => nudge(2, 0)} title="Double tempo">2×</button>
              {saving && <span className="w-muted">saving…</span>}
            </>
          ) : (
            <span className="w-muted">no BPM — analyse this track to edit its grid</span>
          )}
        </div>
      )}
    </div>
  )
}
