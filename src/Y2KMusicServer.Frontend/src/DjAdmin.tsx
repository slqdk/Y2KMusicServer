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
type YtJob = {
  id: number; videoId: string; title: string; artist: string | null
  state: 'queued' | 'downloading' | 'indexing' | 'done' | 'failed' | 'cancelled'
  percent: number; message: string | null; trackId: number | null
}
type YtDownloads = { folder: string; busy: boolean; jobs: YtJob[] }

type JingleRow = { id: number; title: string | null; artist: string | null; durationSec: number }
type Jingles = { designated: boolean; name: string | null; items: JingleRow[] }

/** The three screens. Control is what you need mid-song; the rest is planning. */
type Tab = 'control' | 'playlist' | 'jingles'

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
  // Paste-a-link downloads. Same server-side queue the admin page drives, so a
  // job started on the phone shows up on the desktop and vice versa.
  const [ytUrl, setYtUrl] = useState('')
  const [yt, setYt] = useState<YtDownloads | null>(null)
  const [jingles, setJingles] = useState<Jingles | null>(null)
  // The open screen survives a reload — a DJ who refreshes mid-set shouldn't
  // land back on a tab they weren't using.
  const [tab, setTab] = useState<Tab>(() => {
    try { return (localStorage.getItem('y2k-dj-tab') as Tab) || 'control' } catch { return 'control' }
  })
  useEffect(() => { try { localStorage.setItem('y2k-dj-tab', tab) } catch { /* ignore */ } }, [tab])

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
    try {
      const y = await fetch('/api/admin/integrations/youtube/downloads')
      if (y.ok) setYt(await y.json())
    } catch { /* the console still works without the download list */ }
    try {
      const g = await fetch('/api/dj/jingles')
      if (g.ok) setJingles(await g.json())
    } catch { /* keep the last good list */ }
  }, [])

  // Queue whatever is in the box: a pasted link, an album link, or just words
  // (which take YouTube's first hit — quicker than hunting a URL on a phone).
  const queueDownload = useCallback(async () => {
    const text = ytUrl.trim()
    if (!text) return
    const ok = await post('/api/admin/integrations/youtube/downloads', { urls: text })
    if (ok) { setYtUrl(''); setMsg('Downloading…') }
    void refresh()
  }, [ytUrl, post, refresh])

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

  const fireJingle = useCallback(async (j: JingleRow) => {
    const ok = await post(`/api/dj/jingles/${j.id}`)
    if (ok) setMsg(`Firing “${j.title ?? 'jingle'}”…`)
    void refresh()
  }, [post, refresh])

  const queueJingle = useCallback(async (j: JingleRow) => {
    const ok = await post(`/api/dj/jingles/${j.id}/queue`)
    if (ok) setMsg(`“${j.title ?? 'Jingle'}” is next.`)
    void refresh()
  }, [post, refresh])

  const np = split(st?.artist ?? null, st?.title ?? null)
  const gainPct = Math.round((st?.duckGain ?? 1) * 100)

  return (
    <div className="dj">
      <header className="dj-head">
        <span className="dj-title">DJ console</span>
        <span className={`dj-gain${gainPct < 100 ? ' is-down' : ''}`}>{gainPct}%</span>
      </header>

      <nav className="dj-tabs" role="tablist">
        {([['control', '🎛 Control'], ['playlist', '♫ Playlist'], ['jingles', '🔔 Jingles']] as [Tab, string][])
          .map(([id, label]) => (
            <button key={id} role="tab" aria-selected={tab === id}
              className={`dj-tab${tab === id ? ' is-on' : ''}`}
              onClick={() => setTab(id)}>{label}</button>
          ))}
      </nav>

      {/* Now playing stays above the tabs' content on every screen: it is the
          one thing a DJ glances at regardless of what they came here to do. */}
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

      {tab === 'control' && (<>
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

      </>)}

      {tab === 'playlist' && (<>
      <section className="dj-sect">
        <h2 className="dj-sect-head">Auto DJ playlists</h2>
        <div className="dj-feeds">
          {(st?.playlists ?? []).map(p => (
            <HoldButton
              key={p.id}
              className={`dj-feed${p.feed ? (p.scheduledNow ? ' is-sched' : ' is-on') : ''}`}
              label={p.name}
              sub={p.feed
                ? (p.scheduledNow ? `on · timeslot · ${p.trackCount}` : `on · ${p.trackCount} songs`)
                : `off · ${p.trackCount}`}
              onFire={() => {
                // Same function as the listener chips: set the whole selection,
                // sweep the queue 5s later and crossfade into the new music.
                const cur = (st?.playlists ?? []).filter(x => x.feed).map(x => x.id)
                const next = p.feed ? cur.filter(id => id !== p.id) : [...cur, p.id]
                void post('/api/dj/selection', { playlistIds: next }).then(refresh)
              }}
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

      <section className="dj-sect">
        <h2 className="dj-sect-head">Add from YouTube</h2>
        <input
          className="dj-yt-input"
          type="text"
          value={ytUrl}
          spellCheck={false}
          placeholder="Paste a link, or type a song"
          onChange={e => setYtUrl(e.target.value)}
        />
        <HoldButton
          className="dj-yt-go"
          label="⬇ Download to library"
          sub={yt?.folder ? `hold · lands in ${yt.folder}` : 'hold ½s'}
          disabled={!ytUrl.trim()}
          onFire={() => { void queueDownload() }}
        />
        <ul className="dj-yt-list">
          {(yt?.jobs ?? []).slice(0, 6).map(j => (
            <li key={j.id} className={`dj-yt-row is-${j.state}`}>
              <div className="dj-qmain">
                <span className="dj-qartist">{j.artist ?? '—'}</span>
                <span className="dj-qtitle">{j.title}</span>
              </div>
              <span className="dj-yt-state">
                {j.state === 'downloading' ? `${Math.round(j.percent)}%` : j.state}
              </span>
            </li>
          ))}
          {(yt?.jobs.length ?? 0) === 0 && <li className="dj-empty">No downloads yet.</li>}
        </ul>
      </section>

      </>)}

      {tab === 'jingles' && (
        <section className="dj-sect">
          <h2 className="dj-sect-head">
            {jingles?.designated ? (jingles.name ?? 'Jingles') : 'Jingles'}
          </h2>

          {jingles && !jingles.designated && (
            <div className="dj-empty">
              No jingle playlist yet. On the admin page, open a playlist&apos;s ▾ menu
              and choose “Use as the jingle playlist”.
            </div>
          )}

          {jingles?.designated && jingles.items.length === 0 && (
            <div className="dj-empty">“{jingles.name}” is empty — add tracks to it from the admin page.</div>
          )}

          {/* Two actions per jingle, both held like everything else here: FIRE
              crossfades into it straight away, NEXT parks it after the current
              song. Fire is the wider, louder one — it is what this screen is for. */}
          <ul className="dj-jingles">
            {(jingles?.items ?? []).map(j => {
              const d = split(j.artist, j.title)
              return (
                <li key={j.id} className="dj-jrow">
                  <div className="dj-qmain">
                    <span className="dj-qartist">{d.title}</span>
                    <span className="dj-qtitle">{d.artist ?? '—'} · {fmt(j.durationSec)}</span>
                  </div>
                  <HoldButton
                    className="dj-jqueue"
                    label="＋"
                    onFire={() => { void queueJingle(j) }}
                  />
                  <HoldButton
                    className="dj-jfire"
                    label="▶ FIRE"
                    onFire={() => { void fireJingle(j) }}
                  />
                </li>
              )
            })}
          </ul>
        </section>
      )}

      {msg && <div className="dj-toast">{msg}</div>}
    </div>
  )
}
