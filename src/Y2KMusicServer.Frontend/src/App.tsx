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
interface StreamInfo { enabled: boolean; bitrate: number; listeners: number; showListenLive: boolean }
interface SearchItem { id: number; title: string | null; artist: string | null; album: string | null; durationSec: number }
interface FilterCount { name: string; count: number }
interface DecadeCount { decade: number; count: number } // 0 = unknown decade
interface BrowseFilters { showSelector: boolean; genres: FilterCount[]; decades: DecadeCount[] }
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
// A stable per-device id for request throttling, kept in localStorage. Not
// crypto.randomUUID — the listener page is served over plain http, where the
// Web Crypto API is unavailable; this token only needs to be stable per device.
const DEVICE_ID = ((): string => {
  try {
    let id = localStorage.getItem('y2k-device-id')
    if (!id) {
      id = `d-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`
      localStorage.setItem('y2k-device-id', id)
    }
    return id
  } catch { return 'anon' }
})()

/* ── Component ───────────────────────────────────────────────────────── */
export default function App() {
  const [theme, setTheme] = useState<string>(readTheme)
  const [np, setNp] = useState<NowPlaying | null>(null)
  const [stream, setStream] = useState<StreamInfo | null>(null)
  const [filters, setFilters] = useState<BrowseFilters | null>(null)
  const [selGenres, setSelGenres] = useState<string[]>([])
  const [selDecades, setSelDecades] = useState<number[]>([])
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

  useEffect(() => { try { localStorage.setItem('y2k-listener-theme', theme) } catch { /* ignore */ } }, [theme])

  const refresh = useCallback(() => {
    j<NowPlaying>('/api/nowplaying').then(setNp).catch(() => {})
    j<StreamInfo>('/api/stream/info').then(setStream).catch(() => {})
    j<PlaylistRow[]>('/api/playlist').then(setPlaylist).catch(() => {})
  }, [])

  useEffect(() => {
    refresh()
    j<BrowseFilters>('/api/browse-filters').then(setFilters).catch(() => {})
    const id = setInterval(refresh, 3000)
    return () => clearInterval(id)
  }, [refresh])

  useEffect(() => { setArtOk(true) }, [np?.trackId])

  // Debounced search / browse: a text term, the genre/decade chips, or both.
  // With chips set and no term, the filtered library is browsed. A settled
  // text term is recorded in recent searches.
  useEffect(() => {
    window.clearTimeout(debounce.current)
    const term = q.trim()
    const filtered = selGenres.length > 0 || selDecades.length > 0
    if (!term && !filtered) { setResults([]); setSelectedId(null); return }
    debounce.current = window.setTimeout(() => {
      const qs = new URLSearchParams()
      if (term) qs.set('q', term)
      if (selGenres.length > 0) qs.set('genre', selGenres.join(','))
      if (selDecades.length > 0) qs.set('decade', selDecades.join(','))
      qs.set('take', term ? '10' : '24')
      j<{ items: SearchItem[] }>(`/api/search?${qs.toString()}`)
        .then(d => { setResults(d.items); if (term) pushRecent(term) })
        .catch(() => setResults([]))
    }, 250)
    return () => window.clearTimeout(debounce.current)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [q, selGenres, selDecades])

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
      const r = await fetch('/api/request', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ trackId: selected.id, requesterName: name.trim() || null, deviceId: DEVICE_ID })
      })
      if (r.status === 429) {
        const d = await r.json().catch(() => null)
        const mins = Math.ceil((d?.retryAfterSec ?? 0) / 60)
        flash(mins > 1 ? `Please wait about ${mins} min before requesting again.` : 'Please wait a moment before requesting again.')
        return
      }
      if (!r.ok) { flash('Request failed. Try again.'); return }
      const d = await r.json().catch(() => null)
      flash(d?.accepted
        ? `Added “${selected.title ?? 'track'}” to the queue!`
        : `Requested “${selected.title ?? 'track'}” — the DJ will see it.`)
      setSelectedId(null)
    } catch { flash('Request failed. Try again.') }
  }

  // Toggle a browse chip (multi-select; both axes combine as AND).
  const toggleGenre = (name: string) =>
    setSelGenres(prev => prev.includes(name) ? prev.filter(g => g !== name) : [...prev, name])
  const toggleDecade = (d: number) =>
    setSelDecades(prev => prev.includes(d) ? prev.filter(x => x !== d) : [...prev, d])
  const decadeLabel = (d: number) => (d === 0 ? '?' : `${String(d).slice(2)}'s`)

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

  // The theme picker and the recent-searches list each render in two spots; CSS
  // shows one per breakpoint (top bar / side on desktop, the foot on phones).
  const themeSelect = (cls: string) => (
    <select className={`lz-theme ${cls}`} value={theme} onChange={e => setTheme(e.target.value)} title="Theme" aria-label="Theme">
      {THEMES.map(([v, label]) => <option key={v} value={v}>{label}</option>)}
    </select>
  )
  const recentBlock = (cls: string) => (
    <div className={cls}>
      <div className="lz-field-label">Recent searches</div>
      {recent.length === 0
        ? <div className="lz-recent-empty">Nothing yet.</div>
        : <ul className="lz-recent">{recent.map(t => <li key={t} onClick={() => setQ(t)} title={`Search “${t}”`}>{t}</li>)}</ul>}
    </div>
  )

  return (
    <div className={`lz lz-${theme}`}>
      {/* ── Top bar ──────────────────────────────────────────────────── */}
      <div className="lz-top">
        {stream?.showListenLive && (
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
        )}

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
          {themeSelect('lz-theme-top')}
          <button className="lz-btn" onClick={skip} disabled={!np?.allowNext || np?.trackId == null} title={np?.allowNext ? 'Skip to the next track' : 'Skip is disabled'}>
            Next ⏭
          </button>
        </div>
      </div>

      {/* ── Browse band: genre + decade filters (mirrors the admin) ──── */}
      {filters?.showSelector && (filters.genres.length > 0 || filters.decades.length > 0) && (
        <div className="lz-catband">
          <div className="lz-label">♫ Browse the music</div>
          <div className="lz-chips">
            {filters.genres.map(g => (
              <button key={g.name} className={`lz-chip${selGenres.includes(g.name) ? ' is-on' : ''}`} onClick={() => toggleGenre(g.name)}>
                {g.name}<span className="lz-chip-count">{g.count}</span>
              </button>
            ))}
            {filters.decades.length > 0 && <span className="lz-chipgap" aria-hidden="true" />}
            {filters.decades.map(d => (
              <button key={d.decade} className={`lz-chip${selDecades.includes(d.decade) ? ' is-on' : ''}`} onClick={() => toggleDecade(d.decade)}>
                {decadeLabel(d.decade)}<span className="lz-chip-count">{d.count}</span>
              </button>
            ))}
          </div>
          <div className="lz-cathint">
            {selGenres.length === 0 && selDecades.length === 0
              ? 'Pick a genre or decade to browse, or just search.'
              : 'Showing songs matching your picks — tap one to request it.'}
          </div>
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

          {recentBlock('lz-recent-desktop')}
        </aside>

        {/* Center: search results */}
        <section className="lz-panel lz-results">
          <div className="lz-panel-head">Search results{results.length > 0 && <span style={{ fontWeight: 400, opacity: .8 }}>{results.length}</span>}</div>
          <div className="lz-panel-body">
            {!q.trim() && selGenres.length === 0 && selDecades.length === 0
              ? <div className="lz-empty">Start typing to search, or pick a genre / decade above…</div>
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

      {/* ── Foot (phones only): recent searches, then the theme picker ──── */}
      <div className="lz-foot">
        {recentBlock('lz-recent-mobile')}
        {themeSelect('lz-theme-foot')}
      </div>

      {toast && <div className="lz-toast">{toast}</div>}
      <audio ref={audioRef} preload="none" onPlay={() => setLive(true)} onPause={() => setLive(false)} style={{ display: 'none' }} />
    </div>
  )
}
