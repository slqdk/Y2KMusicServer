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
interface PublicPlaylist { id: number; name: string; count: number }
interface PlaylistsInfo { showSelector: boolean; playlists: PublicPlaylist[] }
interface AlbumHit { album: string; artist: string | null; trackId: number; count: number }
interface SearchResp { items: SearchItem[]; albums?: AlbumHit[] }
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
/** Album cover with a ♪ placeholder when the representative track has no
 *  embedded art (albums stay in the row either way — only songs demote). */
function AlbumArtTile({ trackId }: { trackId: number }) {
  const [ok, setOk] = useState(true)
  useEffect(() => { setOk(true) }, [trackId])
  return ok
    ? <img className="lz-album-art" src={`/api/albumart?trackId=${trackId}`} alt="" loading="lazy" onError={() => setOk(false)} />
    : <div className="lz-album-art lz-album-art-empty">♪</div>
}

export default function App() {
  const [theme, setTheme] = useState<string>(readTheme)
  const [np, setNp] = useState<NowPlaying | null>(null)
  const [stream, setStream] = useState<StreamInfo | null>(null)
  const [pls, setPls] = useState<PlaylistsInfo | null>(null)
  const [selPl, setSelPl] = useState<number | null>(null)
  const [albums, setAlbums] = useState<AlbumHit[]>([])
  const [albumView, setAlbumView] = useState<AlbumHit | null>(null)
  const [artFail, setArtFail] = useState<Set<number>>(new Set())
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
    j<PlaylistsInfo>('/api/playlists').then(setPls).catch(() => {})
    const id = setInterval(refresh, 3000)
    return () => clearInterval(id)
  }, [refresh])

  useEffect(() => { setArtOk(true) }, [np?.trackId])

  // Debounced search / browse. Modes, first match wins: an opened album (its
  // songs), a selected playlist (playlist order, optionally narrowed by the
  // text), or free text (songs + the album row). A settled text term is
  // recorded in recent searches. The server prefers FLAC over MP3 twins.
  useEffect(() => {
    window.clearTimeout(debounce.current)
    const term = q.trim()
    if (!albumView && !term && selPl == null) { setResults([]); setAlbums([]); setSelectedId(null); return }
    debounce.current = window.setTimeout(() => {
      const qs = new URLSearchParams()
      if (albumView) qs.set('albumName', albumView.album)
      else {
        if (term) qs.set('q', term)
        if (selPl != null) qs.set('playlist', String(selPl))
      }
      qs.set('take', '30')
      j<SearchResp>(`/api/search?${qs.toString()}`)
        .then(d => {
          setResults(d.items)
          setAlbums(albumView ? [] : (d.albums ?? []))
          setArtFail(new Set())
          if (term && !albumView) pushRecent(term)
        })
        .catch(() => { setResults([]); setAlbums([]) })
    }, 250)
    return () => window.clearTimeout(debounce.current)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [q, selPl, albumView])

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

  // Playlist chip (single-select toggle) and album navigation. Opening an
  // album replaces the results; Back returns to whatever search/browse was
  // active. Typing again also leaves the album.
  const togglePlaylist = (id: number) => { setAlbumView(null); setSelPl(prev => prev === id ? null : id) }
  const openAlbum = (a: AlbumHit) => { setAlbumView(a); setSelectedId(null) }
  const backToSearch = () => { setAlbumView(null); setSelectedId(null) }
  const onQueryChange = (v: string) => { setAlbumView(null); setQ(v) }

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

      {/* ── Browse band: saved-playlist chips ────────────────────────── */}
      {pls?.showSelector && pls.playlists.length > 0 && (
        <div className="lz-catband">
          <div className="lz-label">♫ Browse the music</div>
          <div className="lz-chips">
            {pls.playlists.map(p => (
              <button key={p.id} className={`lz-chip${selPl === p.id ? ' is-on' : ''}`} onClick={() => togglePlaylist(p.id)}>
                {p.name}<span className="lz-chip-count">{p.count}</span>
              </button>
            ))}
          </div>
          <div className="lz-cathint">
            {selPl == null
              ? 'Pick a playlist to browse, or just search.'
              : 'Showing that playlist’s songs — tap one to request it.'}
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
          <input className="lz-input lz-input-search" type="search" value={q} onChange={e => onQueryChange(e.target.value)} placeholder="Songs or artists…" />

          <button className="lz-btn lz-primary lz-btn-block" onClick={requestSelected} disabled={!selected}>
            Request Selected Song
          </button>

          {recentBlock('lz-recent-desktop')}
        </aside>

        {/* Center: search results — albums row, song tiles, no-art list */}
        <section className="lz-panel lz-results">
          <div className="lz-panel-head">
            {albumView
              ? <span className="lz-albhead">
                  <button className="lz-btn lz-back" onClick={backToSearch}>← Back</button>
                  <span className="lz-albhead-name">{albumView.album}</span>
                  {albumView.artist && <span className="lz-albhead-artist">{albumView.artist}</span>}
                </span>
              : <>Search results{results.length > 0 && <span style={{ fontWeight: 400, opacity: .8 }}>{results.length}</span>}</>}
          </div>
          <div className="lz-panel-body">
            {!albumView && !q.trim() && selPl == null
              ? <div className="lz-empty">Start typing to search, or pick a playlist above…</div>
              : results.length === 0 && albums.length === 0
                ? <div className="lz-empty">No matches.</div>
                : (() => {
                    const tiles = results.filter(t => !artFail.has(t.id))
                    const plain = results.filter(t => artFail.has(t.id))
                    const failArt = (id: number) =>
                      setArtFail(prev => { const n = new Set(prev); n.add(id); return n })
                    const pick = (id: number) => setSelectedId(id)
                    const pickAndRequest = (id: number) => { setSelectedId(id); requestSelected() }
                    return <>
                      {albums.length > 0 && (
                        <div className="lz-sect">
                          <div className="lz-sect-label">Albums</div>
                          <div className="lz-albums">
                            {albums.map(a => (
                              <div key={a.album} className="lz-album" onClick={() => openAlbum(a)} title={`Open “${a.album}”`}>
                                <AlbumArtTile trackId={a.trackId} />
                                <div className="lz-album-name">{a.album}</div>
                                <div className="lz-album-artist">{a.artist ?? ''} · {a.count}</div>
                              </div>
                            ))}
                          </div>
                        </div>
                      )}
                      {tiles.length > 0 && (
                        <div className="lz-sect">
                          {albums.length > 0 && <div className="lz-sect-label">Songs</div>}
                          <div className="lz-grid">
                            {tiles.map(t => (
                              <div
                                key={t.id}
                                className={`lz-tile${selectedId === t.id ? ' is-selected' : ''}`}
                                onClick={() => pick(t.id)}
                                onDoubleClick={() => pickAndRequest(t.id)}
                              >
                                <img className="lz-tile-art" src={`/api/albumart?trackId=${t.id}`} alt=""
                                  loading="lazy" onError={() => failArt(t.id)} />
                                <div className="lz-tile-title">{t.title ?? '(untitled)'}</div>
                                {t.artist && <div className="lz-tile-artist">{t.artist}</div>}
                                <div className="lz-tile-dur">{fmt(t.durationSec)}</div>
                              </div>
                            ))}
                          </div>
                        </div>
                      )}
                      {plain.length > 0 && (
                        <div className="lz-sect">
                          <div className="lz-sect-label">More songs</div>
                          <ul className="lz-results-list">
                            {plain.map(t => (
                              <li
                                key={t.id}
                                className={`lz-result lz-result-icon${selectedId === t.id ? ' is-selected' : ''}`}
                                onClick={() => pick(t.id)}
                                onDoubleClick={() => pickAndRequest(t.id)}
                              >
                                <span className="lz-mini-icon" aria-hidden="true">♪</span>
                                <div className="lz-result-main">
                                  <div className="lz-result-title">{t.title ?? '(untitled)'}</div>
                                  {t.artist && <div className="lz-result-artist">{t.artist}</div>}
                                </div>
                                <div className="lz-result-dur">{fmt(t.durationSec)}</div>
                              </li>
                            ))}
                          </ul>
                        </div>
                      )}
                    </>
                  })()}
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
