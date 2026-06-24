import { useEffect, useRef } from 'react'
import type { DeckProgress } from './useHub'

interface Props {
  progress: DeckProgress | null
  advancing: boolean   // is this deck's position currently moving?
  colorRgb: string     // "r,g,b" for the beat bars
  label: string        // e.g. "A: Title  [120 BPM]" or "DECK A"
}

// Scrolling beat-clock: a synthetic beat grid drawn from BPM + live position +
// the analysed phase offset (no audio peaks needed). ~12 beats span the width;
// "now" is fixed at 15% from the left so future beats scroll in from the right.
// Downbeats are tall + bright, off-beats shorter; a beat flashes white on the
// hit then decays over one beat. Ported from the legacy WinForms DrawBeatStrip,
// with the phase offset added. Self-animated via rAF; position is extrapolated
// by wall-clock between the ~4 Hz progress pushes so the scroll stays smooth.
export default function BeatClock({ progress, advancing, colorRgb, label }: Props) {
  const cv = useRef<HTMLCanvasElement>(null)
  const props = useRef<Props>({ progress, advancing, colorRgb, label })
  props.current = { progress, advancing, colorRgb, label }

  useEffect(() => {
    const c = cv.current
    if (!c) return
    const ctx = c.getContext('2d')
    if (!ctx) return

    let raf = 0
    let anchorSrc: DeckProgress | null = null
    let anchorPos = 0
    let anchorAt = performance.now()

    const draw = () => {
      const { progress: p, advancing: adv, colorRgb: col, label: lab } = props.current
      const dpr = window.devicePixelRatio || 1
      const w = c.clientWidth || 1
      const h = c.clientHeight || 1
      const pw = Math.round(w * dpr)
      const ph = Math.round(h * dpr)
      if (c.width !== pw || c.height !== ph) { c.width = pw; c.height = ph }
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
      ctx.clearRect(0, 0, w, h)
      ctx.fillStyle = '#141414'
      ctx.fillRect(0, 0, w, h)

      const now = performance.now()
      ctx.textBaseline = 'top'

      const bpm = p?.bpm ?? 0
      if (!p || bpm < 20) {
        ctx.fillStyle = '#cfcfcf'
        ctx.font = 'bold 10px Tahoma, "Segoe UI", sans-serif'
        ctx.fillText(lab, 4, 3)
        if (p) {
          ctx.fillStyle = '#6a6a6a'
          ctx.textAlign = 'center'
          ctx.fillText('No BPM data', w / 2, h / 2 - 6)
          ctx.textAlign = 'left'
        }
        raf = requestAnimationFrame(draw)
        return
      }

      // Re-anchor on each fresh progress object (a new reference per push);
      // extrapolate by wall-clock while advancing so it scrolls at 60fps.
      if (p !== anchorSrc) { anchorSrc = p; anchorPos = p.positionSec; anchorAt = now }
      const nowSec = adv ? anchorPos + (now - anchorAt) / 1000 : anchorPos

      const phase = p.phaseOffsetSec ?? 0
      const beatSec = 60 / bpm
      const windowSec = beatSec * 12
      const pxPerSec = (w - 2) / windowSec
      const barMaxH = h - 8

      const viewStart = nowSec - windowSec * 0.15
      const viewEnd = nowSec + windowSec * 0.85
      const firstBeat = Math.floor((viewStart - phase) / beatSec)
      const lastBeat = Math.ceil((viewEnd - phase) / beatSec)

      const parts = col.split(',')
      const cr = parts[0], cg = parts[1], cb = parts[2]

      for (let bi = firstBeat; bi <= lastBeat; bi++) {
        const beatTimeSec = phase + bi * beatSec
        const x = Math.round(1 + (beatTimeSec - viewStart) * pxPerSec)
        if (x < 1 || x > w - 2) continue
        const ageSec = nowSec - beatTimeSec
        const isDown = (((bi % 4) + 4) % 4) === 0
        const barH = Math.round((isDown ? 1 : 0.6) * barMaxH)
        const barY = h - 3 - barH
        const barW = isDown ? 3 : 2

        let alpha: number
        if (ageSec < 0) alpha = isDown ? 55 : 30
        else alpha = Math.max(0, (1 - ageSec / beatSec) * (isDown ? 255 : 180))
        if (alpha <= 0) continue

        if (ageSec >= 0 && ageSec < beatSec * 0.3) {
          const glow = Math.round((1 - ageSec / (beatSec * 0.3)) * 60)
          ctx.fillStyle = `rgba(${cr},${cg},${cb},${glow / 255})`
          ctx.fillRect(x - 2, barY, barW + 4, barH)
        }
        ctx.fillStyle = `rgba(${cr},${cg},${cb},${alpha / 255})`
        ctx.fillRect(x, barY, barW, barH)

        if (ageSec >= 0 && ageSec < beatSec * 0.15) {
          const cap = 1 - ageSec / (beatSec * 0.15)
          ctx.fillStyle = `rgba(255,255,255,${cap})`
          ctx.fillRect(x, barY, barW, 2)
        }
      }

      // "now" cursor at 15% from the left
      const nowX = Math.round(1 + w * 0.15)
      ctx.fillStyle = 'rgba(255,255,255,0.32)'
      ctx.fillRect(nowX, 2, 1, h - 4)

      // phase dot along the bottom (0..1 within the current beat)
      const ph01 = (((((nowSec - phase) % beatSec) + beatSec) % beatSec)) / beatSec
      const dotX = Math.round(1 + ph01 * (w - 4))
      ctx.fillStyle = `rgba(${cr},${cg},${cb},0.7)`
      ctx.fillRect(dotX, h - 4, 4, 3)

      // label
      ctx.fillStyle = '#cfcfcf'
      ctx.font = 'bold 10px Tahoma, "Segoe UI", sans-serif'
      ctx.fillText(lab, 4, 3)

      raf = requestAnimationFrame(draw)
    }
    raf = requestAnimationFrame(draw)
    return () => cancelAnimationFrame(raf)
  }, [])

  return <canvas ref={cv} />
}
