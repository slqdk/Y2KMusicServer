import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import './listener.css'

/* ── Types ───────────────────────────────────────────────────────────── */
interface NowPlaying {
  trackId: number | null
  title: string | null
  artist: string | null
  album: string | null
  positionSec: number
  durationSec: number
  playing: boolean
  allowNext: boolean
  bpm: number | null
  genre: string | null
  year: number | null
  type: string | null
}
interface StreamInfo { enabled: boolean; bitrate: number; listeners: number }
interface SearchItem { id: number; title: string | null; artist: string | null; album: string | null; durationSec: number }
interface CatItem { id: number; name: string; count: number }
interface CatState { showSelector: boolean; selected: number[]; categories: CatItem[] }
interface PlaylistRow { position: number; trackId: number; title: string | null; artist: string | null; durationSec: number; source: string | null }

const THEMES: [string, string][] = [
  ['dark', 'Dark'],
  ['win2k', 'Windows 2000'],
  ['winxp', 'Windows XP'],
  ['win7', 'Windows 7'],
  ['win10', 'Windows 10'],
  ['win11', 'Windows 11'],
]

const STREAM_URL = '/stream?format=mp3'

/* ── Helpers ─────────────────────────────────────────────────────────── */
const j = async <T,>(url: string, init?: RequestInit): Promise<T> => {
  const r = await fetch(url, init)
  if (!r.ok) throw new Error(String(r.status))
  return r.json() as Promise<T>
}
const fmt = (s: number) => {
  if (!isFinite(s) || s < 0) return '--:--'
  const t = Math.floor(s); return `${Math.floor(t / 60)}:${String(t % 60).padStart(2, '0')}`
}
const readTheme = (): string => {
  try { return localStorage.getItem('y2k-listener-theme') || 'dark' } catch { return 'dark' }
}
const readRecent = (): string[] => {
  try { const v = JSON.parse(localStorage.getItem('y2k-recent-searches') || '[]'); return Array.isArray(v) ? v.slice(0, 6) : [] }
  catch { return [] }
}

/* ── Component ───────────────────────────────────────────────────────── */
export default function App() {
  const [theme, setTheme] = useState<string>(readTheme)
  const [np, setNp] = useState<NowPlaying | null>(null)
  const [stream, setStream] = useState<StreamInfo | null>(null)
  const [cats, setCats] = useState<CatState | null>(null)
  const [playlist, setPlaylist] = useState<PlaylistRow[]>([])
  const [q, setQ] = useState('')
  const [results, setResults] = useState<SearchItem[]>([])
  const [selectedId, setSelectedId] = useState<number | null>(null)
  const [name, setName] = useState('')
  const [recent, setRecent] = useState<string[]>(readRecent)
  const [toast, setToast] = useState<string | null>(null)
  const [artOk, setArtOk] = useState(true)
  const [live, setLive] = useState(false)

  const audioRef = useRef<HTMLAudioElement | null>(null)
  const debounce = useRef<number | undefined>(undefined)
  const catTimer = useRef<number | undefined>(undefined)
  const selectedRef = useRef<number[]>([])
  const appliedKey = useRef<string | null>(null)
  const catKey = (ids: number[]) => [...ids].sort((a, b) => a - b).join(',')

  useEffect(() => { try { localStorage.setItem('y2k-listener-theme', theme) } catch { /* ignore */ } }, [theme])

  const refresh = useCallback(() => {
    j<NowPlaying>('/api/nowplaying').then(setNp).catch(() => {})
    j<StreamInfo>('/api/stream/info').then(setStream).catch(() => {})
    j<PlaylistRow[]>('/api/playlist').then(setPlaylist).catch(() => {})
  }, [])

  useEffect(() => {
    refresh()
    j<CatState>('/api/categories').then(setCats).catch(() => {})
    const id = setInterval(refresh, 3000)
    return () => clearInterval(id)
  }, [refresh])

  useEffect(() => { setArtOk(true) }, [np?.trackId])

  // Mirror the live category selection into a ref the debounce timer can read,
  // and seed the "last applied" baseline the first time categories load so an
  // unchanged selection never re-applies.
  useEffect(() => {
    if (!cats) return
    selectedRef.current = cats.selected
    if (appliedKey.current === null) appliedKey.current = catKey(cats.selected)
  }, [cats])

  useEffect(() => () => window.clearTimeout(catTimer.current), [])

  // Debounced search; also records the (settled) term in recent searches.
  useEffect(() => {
    window.clearTimeout(debounce.current)
    const term = q.trim()
    if (!term) { setResults([]); setSelectedId(null); return }
    debounce.current = window.setTimeout(() => {
      j<{ items: SearchItem[] }>(`/api/search?q=${encodeURIComponent(term)}`)
        .then(d => { setResults(d.items); pushRecent(term) })
        .catch(() => setResults([]))
    }, 250)
    return () => window.clearTimeout(debounce.current)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [q])

  // If the broadcast drops while we're listening, stop the player.
  useEffect(() => { if (stream && !stream.enabled && live) stopStream() // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [stream, live])

  const flash = (m: string) => { setToast(m); window.setTimeout(() => setToast(null), 2500) }

  const pushRecent = (term: string) => {
    setRecent(prev => {
      const next = [term, ...prev.filter(t => t.toLowerCase() !== term.toLowerCase())].slice(0, 6)
      try { localStorage.setItem('y2k-recent-searches', JSON.stringify(next)) } catch { /* ignore */ }
      return next
    })
  }

  const stopStream = () => {
    const a = audioRef.current; if (!a) return
    a.pause(); a.removeAttribute('src'); a.load()
  }
  const toggleLive = () => {
    const a = audioRef.current; if (!a) return
    if (live) { stopStream(); return }
    a.src = STREAM_URL
    a.play().catch(() => flash('Could not start the stream.'))
  }

  const skip = async () => {
    try { await j('/api/next', { method: 'POST' }); flash('Skip sent.'); setTimeout(refresh, 600) }
    catch { flash('Skip is disabled right now.') }
  }

  const selected = useMemo(() => results.find(r => r.id === selectedId) ?? null, [results, selectedId])
  const requestSelected = async () => {
    if (!selected) return
    try {
      await j('/api/request', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ trackId: selected.id, requesterName: name.trim() || null })
      })
      flash(`Requested “${selected.title ?? 'track'}” — the DJ will see it.`)
      setSelectedId(null)
    } catch { flash('Request failed. Try again.') }
  }

  // Commit a category selection: the server sets the override, rebuilds the
  // queue from those categories (or the schedule when empty), and crossfades to
  // the first new track. Optimistic toast for immediate feedback.
  const applyCats = useCallback(async (ids: number[]) => {
    appliedKey.current = catKey(ids)
    flash(ids.length ? 'Switching to your picks…' : 'Back to the schedule…')
    try {
      const r = await j<{ selected: number[] }>('/api/category-select', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ categoryIds: ids }),
      })
      setCats(c => (c ? { ...c, selected: r.selected } : c))
      appliedKey.current = catKey(r.selected)
    } catch { /* the change just didn't take; leave the chips as they are */ }
  }, [])

  // Pick/unpick a category: update the chips instantly, then apply 3s after the
  // last touch — and only if the selection actually changed — so the listener
  // can keep toggling before it commits and the music crossfades over.
  const toggleCat = (id: number) => {
    setCats(prev => {
      if (!prev) return prev
      const next = prev.selected.includes(id) ? prev.selected.filter(x => x !== id) : [...prev.selected, id]
      selectedRef.current = next
      window.clearTimeout(catTimer.current)
      catTimer.current = window.setTimeout(() => {
        if (catKey(selectedRef.current) !== appliedKey.current) applyCats(selectedRef.current)
      }, 3000)
      return { ...prev, selected: next }
    })
  }

  const stateLabel = np?.playing ? 'NOW PLAYING' : np?.trackId ? 'PAUSED' : 'OFF AIR'

  // Playlist rows: the entry matching the on-air track is the "now playing"
  // row. If nothing in the queue matches (operator loaded an off-playlist
  // track), prepend a synthetic now-playing row so the panel still leads
  // with what's on air.
  const npId = np?.trackId ?? null
  const plRows = useMemo<PlaylistRow[]>(() => {
    if (npId != null && np && !playlist.some(p => p.trackId === npId)) {
      return [{ position: -1, trackId: npId, title: np.title, artist: np.artist, durationSec: np.durationSec, source: null }, ...playlist]
    }
    return playlist
  }, [playlist, npId, np])

  return (
    <div className={`lz lz-${theme}`}>
      {/* ── Top bar ──────────────────────────────────────────────────── */}
      <div className="lz-top">
        <div className="lz-live-wrap" style={{ display: 'flex', flexDirection: 'column', gap: 3, alignItems: 'flex-start' }}>
          <button
            className={`lz-btn${live ? ' is-live' : ''}`}
            onClick={toggleLive}
            disabled={!stream?.enabled}
            title={stream?.enabled ? 'Listen to the live stream' : 'The stream is off air'}
          >
            {!stream?.enabled ? 'Off air' : live ? '● LIVE' : '▶ Listen Live'}
          </button>
          {stream?.enabled && (
            <span style={{ fontSize: '.66rem', color: 'var(--lz-faint)' }}>
              {stream.bitrate} kbps{live ? ` · ${stream.listeners} listening` : ''}
            </span>
          )}
        </div>

        <div className="lz-np">
          {np?.trackId && artOk
            ? <img className="lz-np-art" src={`/api/albumart?trackId=${np.trackId}`} alt="" onError={() => setArtOk(false)} />
            : <div className="lz-np-art lz-np-art-empty">♪</div>}
          <div className="lz-np-body">
            <div className="lz-np-state">● {stateLabel}</div>
            <div className="lz-np-title">{np?.title ?? '—'}</div>
            {np?.artist && <div className="lz-np-artist">{np.artist}</div>}
            <div className="lz-tags">
              {np?.bpm != null && <span className="lz-tag lz-tag-bpm">♩ {Math.round(np.bpm)} BPM</span>}
              {np?.genre && <span className="lz-tag lz-tag-genre">{np.genre}</span>}
              {np?.year != null && <span className="lz-tag lz-tag-year">{np.year}</span>}
              {np?.trackId != null && np.durationSec > 0 && <span className="lz-tag">⏱ {fmt(np.durationSec)}</span>}
              {np?.album && <span className="lz-tag">{np.album}</span>}
            </div>
          </div>
        </div>

        <div className="lz-top-right">
          <select className="lz-theme" value={theme} onChange={e => setTheme(e.target.value)} title="Theme" aria-label="Theme">
            {THEMES.map(([v, label]) => <option key={v} value={v}>{label}</option>)}
          </select>
          <button className="lz-btn" onClick={skip} disabled={!np?.allowNext || np?.trackId == null} title={np?.allowNext ? 'Skip to the next track' : 'Skip is disabled'}>
            Next ⏭
          </button>
        </div>
      </div>

      {/* ── Category band ────────────────────────────────────────────── */}
      {cats?.showSelector && cats.categories.length > 0 && (
        <div className="lz-catband">
          <div className="lz-label">♫ Play by category</div>
          <div className="lz-chips">
            {cats.categories.map(c => (
              <button key={c.id} className={`lz-chip${cats.selected.includes(c.id) ? ' is-on' : ''}`} onClick={() => toggleCat(c.id)}>
                {c.name}<span className="lz-chip-count">{c.count}</span>
              </button>
            ))}
          </div>
          <div className="lz-cathint">{cats.selected.length === 0 ? 'Following the schedule.' : 'Auto DJ is drawing from your picks.'}</div>
        </div>
      )}

      {/* ── Work area ────────────────────────────────────────────────── */}
      <div className="lz-work">
        {/* Left: name + search + request + recent */}
        <aside className="lz-side">
          <div className="lz-field-label">Your name <span className="lz-req">*</span></div>
          <input className="lz-input" type="text" value={name} onChange={e => setName(e.target.value)} placeholder="Enter your name…" maxLength={40} />

          <div className="lz-field-label">Search songs</div>
          <input className="lz-input lz-input-search" type="search" value={q} onChange={e => setQ(e.target.value)} placeholder="Songs or artists…" />

          <button className="lz-btn lz-primary lz-btn-block" onClick={requestSelected} disabled={!selected}>
            Request Selected Song
          </button>

          <div className="lz-field-label">Recent searches</div>
          {recent.length === 0
            ? <div className="lz-recent-empty">Nothing yet.</div>
            : <ul className="lz-recent">{recent.map(t => <li key={t} onClick={() => setQ(t)} title={`Search “${t}”`}>{t}</li>)}</ul>}
        </aside>

        {/* Center: search results */}
        <section className="lz-panel lz-results">
          <div className="lz-panel-head">Search results{results.length > 0 && <span style={{ fontWeight: 400, opacity: .8 }}>{results.length}</span>}</div>
          <div className="lz-panel-body">
            {!q.trim()
              ? <div className="lz-empty">Start typing to search…</div>
              : results.length === 0
                ? <div className="lz-empty">No matches.</div>
                : <ul className="lz-results-list">
                    {results.map(t => (
                      <li
                        key={t.id}
                        className={`lz-result${selectedId === t.id ? ' is-selected' : ''}`}
                        onClick={() => setSelectedId(t.id)}
                        onDoubleClick={() => { setSelectedId(t.id); requestSelected() }}
                      >
                        <div className="lz-result-main">
                          <div className="lz-result-title">{t.title ?? '(untitled)'}</div>
                          {t.artist && <div className="lz-result-artist">{t.artist}</div>}
                        </div>
                        <div className="lz-result-dur">{fmt(t.durationSec)}</div>
                      </li>
                    ))}
                  </ul>}
          </div>
        </section>

        {/* Right: playlist */}
        <section className="lz-panel lz-playlist">
          <div className="lz-panel-head">Playlist</div>
          <div className="lz-panel-body">
            <table className="lz-table">
              <thead>
                <tr>
                  <th className="lz-col-num">#</th>
                  <th>Title</th>
                  <th className="lz-col-dur">Duration</th>
                  <th className="lz-col-by">Added by</th>
                </tr>
              </thead>
              <tbody>
                {plRows.length === 0
                  ? <tr className="lz-pl-empty"><td colSpan={4}>Playlist is empty.</td></tr>
                  : plRows.map((p, i) => {
                      const isNow = p.trackId === npId
                      return (
                        <tr key={`${p.trackId}-${p.position}-${i}`} className={isNow ? 'lz-row-now' : ''}>
                          <td className="lz-col-num">{isNow ? <span className="lz-now-mark">▶</span> : (p.position >= 0 ? p.position : '')}</td>
                          <td>
                            <div className="lz-pl-title">{p.title ?? '(untitled)'}</div>
                            {p.artist && <div className="lz-pl-artist">{p.artist}</div>}
                          </td>
                          <td className="lz-col-dur">{fmt(p.durationSec)}</td>
                          <td className="lz-col-by">{p.source ?? (isNow ? 'On air' : '—')}</td>
                        </tr>
                      )
                    })}
              </tbody>
            </table>
          </div>
        </section>
      </div>

      {toast && <div className="lz-toast">{toast}</div>}
      <audio ref={audioRef} preload="none" onPlay={() => setLive(true)} onPause={() => setLive(false)} style={{ display: 'none' }} />
    </div>
  )
}
