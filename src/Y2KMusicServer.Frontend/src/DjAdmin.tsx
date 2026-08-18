import { useCallback, useEffect, useRef, useState } from 'react'
import './djadmin.css'

/**
 * /DJAdmin — the phone console for a DJ standing in the crowd.
 *
 * Two ideas shape it:
 *  - Nothing destructive happens on a tap. Every action button must be HELD for
 *    500 ms, with a fill animation showing the progress, so a phone in a pocket
 *    or a brush against the screen can't skip a song or delete a queue entry.
 *  - The talk-over button is the exception in the other direction: it acts on
 *    press and release (press = duck, release = back up), because that's the
 *    whole point of a hold-to-talk control.
 */

const HOLD_MS = 500
const POLL_MS = 2000

type QueueRow = {
  id: number; trackId: number; title: string | null; artist: string | null
  durationSec: number; source: string; addedBy: string | null
}
type FeedRow = {
  id: number; name: string; feed: boolean; scheduledNow: boolean; trackCount: number
}
type DjState = {
  playing: boolean
  trackId: number | null
  title: string | null
  artist: string | null
  positionSec: number
  durationSec: number
  crossfading: boolean
  duckGain: number
  ducked: boolean
  fadePaused: boolean
  duckLevelPercent: number
  fadeSeconds: number
  upcoming: QueueRow[]
  playlists: FeedRow[]
}

const fmt = (s: number) => {
  if (!isFinite(s) || s <= 0) return '0:00'
  const m = Math.floor(s / 60)
  return `${m}:${String(Math.floor(s % 60)).padStart(2, '0')}`
}

/** Untagged rips carry "Artist - Title" in the title; split for display only. */
const split = (artist: string | null, title: string | null) => {
  const a = (artist ?? '').trim()
  const t = (title ?? '').trim()
  if (a) return { artist: a, title: t || '(untitled)' }
  const m = /^(.{2,60}?)\s+[-–—]\s+(.+)$/.exec(t)
  return m ? { artist: m[1].trim(), title: m[2].trim() } : { artist: null, title: t || '(untitled)' }
}

/**
 * A button that only fires after HOLD_MS of continuous press. Releasing early
 * cancels, and the fill resets — so a mis-tap is visibly a non-event.
 */
function HoldButton({
  label, sub, onFire, className = '', disabled = false
}: {
  label: string; sub?: string; onFire: () => void; className?: string; disabled?: boolean
}) {
  const [held, setHeld] = useState(false)
  const timer = useRef<number | undefined>(undefined)

  const start = () => {
    if (disabled) return
    setHeld(true)
    window.clearTimeout(timer.current)
    timer.current = window.setTimeout(() => { setHeld(false); onFire() }, HOLD_MS)
  }
  const cancel = () => {
    window.clearTimeout(timer.current)
    setHeld(false)
  }
  useEffect(() => () => window.clearTimeout(timer.current), [])

  return (
    <button
      className={`dj-btn ${className}${held ? ' is-holding' : ''}`}
      disabled={disabled}
      onPointerDown={start}
      onPointerUp={cancel}
      onPointerLeave={cancel}
      onPointerCancel={cancel}
      onContextMenu={e => e.preventDefault()}
    >
      <span className="dj-btn-fill" style={{ animationDuration: `${HOLD_MS}ms` }} />
      <span className="dj-btn-label">{label}</span>
      {sub && <span className="dj-btn-sub">{sub}</span>}
    </button>
  )
}

export default function DjAdmin() {
  const [st, setSt] = useState<DjState | null>(null)
  const [msg, setMsg] = useState('')

  const post = useCallback(async (url: string, body?: unknown) => {
    try {
      const r = await fetch(url, {
        method: 'POST',
        headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body)
      })
      if (!r.ok) {
        const d = await r.json().catch(() => null)
        setMsg(d?.error ?? 'That did not work.')
      }
      return r.ok
    } catch {
      setMsg('No connection to the server.')
      return false
    }
  }, [])

  const refresh = useCallback(async () => {
    try {
      const r = await fetch('/api/dj/state')
      if (r.ok) setSt(await r.json())
    } catch { /* keep the last good screen */ }
  }, [])

  useEffect(() => {
    void refresh()
    const id = window.setInterval(() => { void refresh() }, POLL_MS)
    return () => window.clearInterval(id)
  }, [refresh])

  useEffect(() => {
    if (!msg) return
    const t = window.setTimeout(() => setMsg(''), 2500)
    return () => window.clearTimeout(t)
  }, [msg])

  const np = split(st?.artist ?? null, st?.title ?? null)
  const gainPct = Math.round((st?.duckGain ?? 1) * 100)

  return (
    <div className="dj">
      <header className="dj-head">
        <span className="dj-title">DJ console</span>
        <span className={`dj-gain${gainPct < 100 ? ' is-down' : ''}`}>{gainPct}%</span>
      </header>

      <section className="dj-now">
        <div className="dj-now-state">
          {st?.fadePaused ? '❚❚ PAUSED' : st?.playing ? (st.crossfading ? '⇄ MIXING' : '● ON AIR') : '■ STOPPED'}
        </div>
        <div className="dj-now-artist">{np.artist ?? '—'}</div>
        <div className="dj-now-title">{st?.trackId ? np.title : 'Nothing loaded'}</div>
        <div className="dj-now-time">
          {fmt(st?.positionSec ?? 0)} / {fmt(st?.durationSec ?? 0)}
        </div>
      </section>

      {/* Talk-over latches: hold to duck, hold again to bring the music back —
          so the DJ can put the phone in a pocket while talking. */}
      <HoldButton
        className={`dj-talk${st?.ducked ? ' is-on' : ''}`}
        label={st?.ducked ? '🎤 Talking — hold to restore' : '🎤 Hold to talk'}
        sub={st?.ducked
          ? `music is at ${st?.duckLevelPercent ?? 20}% · hold ½s to bring it back`
          : `hold ½s · drops to ${st?.duckLevelPercent ?? 20}% over ${st?.fadeSeconds ?? 5}s`}
        onFire={() => { void post('/api/dj/duck', { on: !st?.ducked }).then(refresh) }}
      />

      <div className="dj-row">
        <HoldButton
          className={`dj-pause${st?.fadePaused ? ' is-on' : ''}`}
          label={st?.fadePaused ? '▶ Fade in' : '❚❚ Fade out'}
          sub={`hold · ${st?.fadeSeconds ?? 5}s ramp`}
          onFire={() => { void post('/api/dj/fade-pause', { on: !st?.fadePaused }).then(refresh) }}
        />
        <HoldButton
          className="dj-next"
          label="⏭ Next song"
          sub="hold · normal crossfade"
          disabled={!st?.playing}
          onFire={() => { void post('/api/dj/next').then(refresh) }}
        />
      </div>

      <section className="dj-sect">
        <h2 className="dj-sect-head">Auto DJ playlists</h2>
        <div className="dj-feeds">
          {(st?.playlists ?? []).map(p => (
            <HoldButton
              key={p.id}
              className={`dj-feed${p.feed ? ' is-on' : p.scheduledNow ? ' is-sched' : ''}`}
              label={p.name}
              sub={p.feed ? `ON · ${p.trackCount} songs` : p.scheduledNow ? `timeslot · ${p.trackCount}` : `off · ${p.trackCount}`}
              onFire={() => { void post(`/api/dj/feed/${p.id}?value=${!p.feed}`).then(refresh) }}
            />
          ))}
          {(st?.playlists.length ?? 0) === 0 && <div className="dj-empty">No playlists yet.</div>}
        </div>
      </section>

      <section className="dj-sect">
        <h2 className="dj-sect-head">Next up</h2>
        <ul className="dj-queue">
          {(st?.upcoming ?? []).map(q => {
            const d = split(q.artist, q.title)
            return (
              <li key={q.id} className="dj-qrow">
                <div className="dj-qmain">
                  <span className="dj-qartist">{d.artist ?? '—'}</span>
                  <span className="dj-qtitle">{d.title}</span>
                </div>
                <span className="dj-qdur">{fmt(q.durationSec)}</span>
                <HoldButton
                  className="dj-del"
                  label="✕"
                  onFire={() => {
                    void fetch(`/api/dj/queue/${q.id}`, { method: 'DELETE' })
                      .then(() => refresh())
                      .catch(() => setMsg('Could not remove that entry.'))
                  }}
                />
              </li>
            )
          })}
          {(st?.upcoming.length ?? 0) === 0 && <li className="dj-empty">Queue is empty.</li>}
        </ul>
      </section>

      {msg && <div className="dj-toast">{msg}</div>}
    </div>
  )
}
