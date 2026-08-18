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
  bannerText?: string | null
  bannerColor?: string | null
  requireName?: boolean
}
interface StreamInfo { enabled: boolean; bitrate: number; listeners: number; showListenLive: boolean }
interface SearchItem { id: number; title: string | null; artist: string | null; album: string | null; durationSec: number }
interface PublicPlaylist { id: number; name: string; count: number }
interface PlaylistsInfo { showSelector: boolean; playlists: PublicPlaylist[] }
interface AlbumHit { album: string; artist: string | null; trackId: number; count: number }
interface SearchResp { items: SearchItem[]; albums?: AlbumHit[]; fallbackQuery?: string | null }
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
/**
 * Display-only split for untagged files: many rips have an empty Artist tag and
 * "Kim Larsen - Bell'star" sitting in the title. Nothing is written back — this
 * only decides what the row shows, so search and the queue are unaffected.
 */
const splitArtistTitle = (artist: string | null | undefined, title: string | null | undefined)
  : { artist: string | null; title: string } => {
  const a = (artist ?? '').trim()
  const t = (title ?? '').trim()
  if (a) return { artist: a, title: t || '(untitled)' }
  const m = /^(.{2,60}?)\s+[-–—]\s+(.+)$/.exec(t)
  return m ? { artist: m[1].trim(), title: m[2].trim() } : { artist: null, title: t || '(untitled)' }
}

const readName = (): string => {
  try { return localStorage.getItem('y2k-listener-name') || '' } catch { return '' }
}
/** Black or white banner text, whichever reads against the chosen color. */
const bannerFg = (hex: string): string => {
  const m = /^#([0-9a-f]{6})$/i.exec(hex)
  if (!m) return '#fff'
  const v = parseInt(m[1], 16)
  const luma = 0.299 * (v >> 16) + 0.587 * ((v >> 8) & 255) + 0.114 * (v & 255)
  return luma > 150 ? '#1a1a1a' : '#fff'
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
  const [fallbackQ, setFallbackQ] = useState<string | null>(null)
  const [name, setName] = useState<string>(readName)
  useEffect(() => {
    try { localStorage.setItem('y2k-listener-name', name) } catch { /* ignore */ }
  }, [name])

  // The search bar stays locked until the guest has typed at least 3 letters
  // of their name; the unlock settles 500 ms after they stop typing. Dropping
  // below 3 letters locks again immediately.
  // The name requirement is an admin setting; default to requiring it until
  // the first nowplaying poll answers, so the gate can't flash open.
  const requireName = np?.requireName !== false
  const [nameTyped, setNameTyped] = useState(false)
  useEffect(() => {
    if (name.trim().length < 3) { setNameTyped(false); return }
    const t = window.setTimeout(() => setNameTyped(true), 500)
    return () => window.clearTimeout(t)
  }, [name])
  const nameOk = !requireName || nameTyped

  // The results panel is hidden entirely until a search/browse has actually
  // been issued (same 500 ms settle as the fetch).
  const [showResults, setShowResults] = useState(false)

  // Speakers the party may start (Google Cast). Empty unless the DJ enabled
  // guest control AND ticked individual speakers, so the button simply does
  // not exist otherwise.
  type Speaker = { id: string; name: string; casting: boolean }
  const [speakers, setSpeakers] = useState<Speaker[]>([])
  const [spOpen, setSpOpen] = useState(false)
  const [spBusy, setSpBusy] = useState(false)
  const loadSpeakers = () =>
    fetch('/api/cast/speakers')
      .then(r => r.json())
      .then(d => setSpeakers(Array.isArray(d?.speakers) ? d.speakers : []))
      .catch(() => {})
  useEffect(() => {
    void loadSpeakers()
    const id = window.setInterval(() => { void loadSpeakers() }, 10000)
    return () => window.clearInterval(id)
  }, [])
  const castTo = async (s: Speaker) => {
    setSpBusy(true)
    try {
      const r = await fetch(s.casting ? '/api/cast/stop' : '/api/cast/play', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ deviceId: s.id })
      })
      const d = await r.json().catch(() => null)
      flash(r.ok
        ? (s.casting ? `Stopped ${s.name}.` : `Playing on ${s.name}!`)
        : (d?.error ?? 'That speaker could not be started.'))
      await loadSpeakers()
    } catch {
      flash('That speaker could not be reached.')
    } finally {
      setSpBusy(false)
    }
  }

  // Mobile burger drawer (name + theme + playlists). CSS hides the burger on
  // desktop, where the sidebar stays as-is.
  const [drawerOpen, setDrawerOpen] = useState(false)

  // Per-device request cooldown. While active, every Request button is gone
  // and the countdown line at the very top fills as the wait elapses. Seeded
  // from the server on load (so F5 can't dodge it), then from each request's
  // response. cdNow just drives re-renders while the line is filling.
  const [cd, setCd] = useState<{ until: number; total: number } | null>(null)
  const [, setCdNow] = useState(0)
  useEffect(() => {
    fetch(`/api/request/cooldown?deviceId=${encodeURIComponent(DEVICE_ID)}`)
      .then(r => r.json())
      .then(d => { if (d?.remainingSec > 0) setCd({ until: Date.now() + d.remainingSec * 1000, total: d.totalSec || d.remainingSec }) })
      .catch(() => {})
  }, [])
  useEffect(() => {
    if (!cd) return
    const id = window.setInterval(() => {
      if (Date.now() >= cd.until) setCd(null)
      else setCdNow(n => n + 1)
    }, 250)
    return () => window.clearInterval(id)
  }, [cd])
  const startCooldown = (sec: number, total?: number) => {
    if (sec > 0) setCd({ until: Date.now() + sec * 1000, total: total && total > 0 ? total : sec })
  }
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
  // text), or free text (songs + the album row). Everything waits on the
  // name gate; the fetch and the panel's appearance settle together after
  // 500 ms. The server prefers FLAC over MP3 twins.
  useEffect(() => {
    window.clearTimeout(debounce.current)
    const term = q.trim()
    if (!nameOk) {
      setResults([]); setAlbums([]); setFallbackQ(null); setShowResults(false)
      return
    }
    debounce.current = window.setTimeout(() => {
      setShowResults(true)
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
          setFallbackQ(albumView ? null : (d.fallbackQuery ?? null))
          setArtFail(new Set())
        })
        .catch(() => { setResults([]); setAlbums([]); setFallbackQ(null) })
    }, 500)
    return () => window.clearTimeout(debounce.current)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [q, selPl, albumView, nameOk])

  // If the broadcast drops while we're listening, stop the player.
  useEffect(() => { if (stream && !stream.enabled && live) stopStream() // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [stream, live])

  const flash = (m: string) => { setToast(m); window.setTimeout(() => setToast(null), 2500) }



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

  // Per-song request — fired from the Request button on the tile / row itself.
  const requestTrack = async (t: SearchItem) => {
    if (cd) return   // buttons are hidden during cooldown; belt and braces
    try {
      const r = await fetch('/api/request', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ trackId: t.id, requesterName: name.trim() || null, deviceId: DEVICE_ID })
      })
      if (r.status === 429) {
        const d = await r.json().catch(() => null)
        startCooldown(d?.retryAfterSec ?? 0, d?.totalSec)
        const mins = Math.ceil((d?.retryAfterSec ?? 0) / 60)
        flash(mins > 1 ? `Please wait about ${mins} min before requesting again.` : 'Please wait a moment before requesting again.')
        return
      }
      if (!r.ok) { flash('Request failed. Try again.'); return }
      const d = await r.json().catch(() => null)
      startCooldown(d?.cooldownSec ?? 0)
      flash(d?.accepted
        ? `Added “${t.title ?? 'track'}” to the queue!`
        : `Requested “${t.title ?? 'track'}” — the DJ will see it.`)
    } catch { flash('Request failed. Try again.') }
  }

  // Playlist chip (single-select toggle) and album navigation. Opening an
  // album replaces the results; Back returns to whatever search/browse was
  // active. Typing again also leaves the album.
  const togglePlaylist = (id: number) => { setAlbumView(null); setSelPl(prev => prev === id ? null : id) }
  const openAlbum = (a: AlbumHit) => setAlbumView(a)
  const backToSearch = () => setAlbumView(null)
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

  // The fixed bottom bar shows what's on air plus the next queued songs — one
  // card each. Three are rendered; narrow screens hide the later ones in CSS.
  const nextUp = useMemo(() => {
    const idx = npId != null ? plRows.findIndex(p => p.trackId === npId) : -1
    return (idx >= 0 ? plRows.slice(idx + 1) : plRows).slice(0, 3)
  }, [plRows, npId])

  // Cover art for a queue card; falls back to the note glyph when the track
  // has none (each card tracks its own failure, unlike the now-playing art).
  const QueueArt = ({ trackId }: { trackId: number }) => {
    const [ok, setOk] = useState(true)
    return ok
      ? <img className="lz-np-art" src={`/api/albumart?trackId=${trackId}`} alt="" loading="lazy" onError={() => setOk(false)} />
      : <div className="lz-np-art lz-np-art-empty">♪</div>
  }

  const themeSelect = (cls: string) => (
    <select className={`lz-theme ${cls}`} value={theme} onChange={e => setTheme(e.target.value)} title="Theme" aria-label="Theme">
      {THEMES.map(([v, label]) => <option key={v} value={v}>{label}</option>)}
    </select>
  )

  return (
    <div className={`lz lz-${theme}${cd ? ' lz-has-cd' : ''}${np?.bannerText ? ' lz-has-banner' : ''}`}>
      {/* Party banner — text + color come from the admin settings */}
      {np?.bannerText && (
        <div className="lz-banner" style={{
          background: `linear-gradient(90deg, color-mix(in srgb, ${np.bannerColor ?? '#0A246A'} 65%, black), ${np.bannerColor ?? '#0A246A'} 30%, ${np.bannerColor ?? '#0A246A'} 70%, color-mix(in srgb, ${np.bannerColor ?? '#0A246A'} 65%, black))`,
          color: bannerFg(np.bannerColor ?? '#0A246A')
        }}>
          <span className="lz-banner-orn" aria-hidden="true">✦</span>
          <span className="lz-banner-text">{np.bannerText}</span>
          <span className="lz-banner-orn" aria-hidden="true">✦</span>
        </div>
      )}

      {/* Countdown line: fills left→right while the request cooldown runs */}
      {cd && (() => {
        const remaining = Math.max(0, cd.until - Date.now())
        const pct = Math.min(100, 100 * (1 - remaining / (cd.total * 1000)))
        return (
          <div className="lz-cdline" role="status">
            <div className="lz-cdline-fill" style={{ width: `${pct}%` }} />
            <div className="lz-cdline-text">Song requested — you can request again in {fmt(Math.ceil(remaining / 1000))}</div>
          </div>
        )
      })()}



      {/* Mobile burger + slide-in drawer: name, theme, playlists. The inputs
          are the same controlled state as the desktop sidebar, so the two
          renderings can never disagree. */}
      <button className="lz-burger" aria-label="Menu" onClick={() => setDrawerOpen(true)}>☰</button>
      {drawerOpen && (
        <div className="lz-drawer-backdrop" onMouseDown={() => setDrawerOpen(false)}>
          <div className="lz-drawer" onMouseDown={e => e.stopPropagation()}>
            <div className="lz-drawer-head">
              <span className="lz-drawer-title">Menu</span>
              <button className="lz-btn lz-drawer-close" aria-label="Close menu" onClick={() => setDrawerOpen(false)}>✕</button>
            </div>
            <div className="lz-field-label">Your name <span className="lz-req">*</span></div>
            <input className="lz-input" type="text" value={name} onChange={e => setName(e.target.value)} placeholder="Enter your name…" maxLength={40} />
            <div className="lz-field-label" style={{ marginTop: 14 }}>Theme</div>
            {themeSelect('lz-theme-drawer')}
            {pls?.showSelector && pls.playlists.length > 0 && (
              <>
                <div className="lz-field-label" style={{ marginTop: 14 }}>♫ Playlists</div>
                <div className="lz-chips lz-chips-side">
                  {pls.playlists.map(p => (
                    <button key={p.id} className={`lz-chip${selPl === p.id ? ' is-on' : ''}`}
                      onClick={() => { togglePlaylist(p.id); setDrawerOpen(false) }}>
                      <span className="lz-chip-name">{p.name}</span><span className="lz-chip-count">{p.count}</span>
                    </button>
                  ))}
                </div>
              </>
            )}
          </div>
        </div>
      )}

      {/* ── Main area: left sidebar + centered results ─────────────────── */}
      <div className="lz-main">

        {/* Left column: only exists when the DJ asks visitors for a name. */}
        {requireName && (
        <aside className="lz-side">
          <div className="lz-field-label">Your name <span className="lz-req">*</span></div>
          <input className="lz-input" type="text" value={name} onChange={e => setName(e.target.value)} placeholder="Enter your name…" maxLength={40} />

          {pls?.showSelector && pls.playlists.length > 0 && (
            <div className="lz-side-bottom">
              <div className="lz-field-label">♫ Playlists</div>
              <div className="lz-chips lz-chips-side">
                {pls.playlists.map(p => (
                  <button key={p.id} className={`lz-chip${selPl === p.id ? ' is-on' : ''}`} onClick={() => togglePlaylist(p.id)}>
                    <span className="lz-chip-name">{p.name}</span><span className="lz-chip-count">{p.count}</span>
                  </button>
                ))}
              </div>
            </div>
          )}
        </aside>
        )}

        {/* Center/right: top search box + results */}
        <div className="lz-rescol">
          {!requireName && pls?.showSelector && pls.playlists.length > 0 && (
            <div className="lz-chips lz-chips-top">
              {pls.playlists.map(p => (
                <button key={p.id} className={`lz-chip${selPl === p.id ? ' is-on' : ''}`} onClick={() => togglePlaylist(p.id)}>
                  <span className="lz-chip-name">{p.name}</span><span className="lz-chip-count">{p.count}</span>
                </button>
              ))}
            </div>
          )}
          {/* Search box and the player/theme controls share one row: laid out
              side by side rather than floating over each other. */}
          <div className="lz-searchrow">
            <input
              className="lz-input lz-input-search lz-search-top"
              type="search"
              value={q}
              onChange={e => onQueryChange(e.target.value)}
              disabled={!nameOk}
              placeholder={nameOk ? 'Search songs or artists…' : 'Enter your name first (min. 3 letters)…'}
              title={nameOk ? 'Search songs' : 'Type at least 3 letters of your name to unlock the search'}
            />
            <div className="lz-topctrls">
              {stream?.showListenLive && (
                <button
                  className={`lz-btn${live ? ' is-live' : ''}`}
                  onClick={toggleLive}
                  disabled={!stream?.enabled}
                  title={stream?.enabled ? 'Listen to the live stream' : 'The stream is off air'}
                >
                  {!stream?.enabled ? 'Off air' : live ? '● LIVE' : '▶ Listen Live'}
                </button>
              )}
              {stream?.showListenLive && stream?.enabled && (
                <span className="lz-kbps">{stream.bitrate} kbps{live ? ` · ${stream.listeners} listening` : ''}</span>
              )}
              {themeSelect('lz-theme-corner')}
            </div>
          </div>

          {showResults && (
          <section className="lz-panel lz-results lz-results-bare">
          {albumView && (
            <div className="lz-panel-head">
              <span className="lz-albhead">
                <button className="lz-btn lz-back" onClick={backToSearch}>← Back</button>
                <span className="lz-albhead-name">{albumView.album}</span>
                {albumView.artist && <span className="lz-albhead-artist">{albumView.artist}</span>}
              </span>
            </div>
          )}
          <div className="lz-panel-body">
            {results.length === 0 && albums.length === 0
              ? <div className="lz-empty">No matches.</div>
              : (() => {
                    const tiles = results.filter(t => !artFail.has(t.id))
                    const plain = results.filter(t => artFail.has(t.id))
                    const failArt = (id: number) =>
                      setArtFail(prev => { const n = new Set(prev); n.add(id); return n })
                    return <>
                      {fallbackQ && (
                        <div className="lz-fallback">
                          No matches for “{q.trim()}” — showing “{fallbackQ}” instead.
                        </div>
                      )}
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
                              <div key={t.id} className="lz-tile">
                                <img className="lz-tile-art" src={`/api/albumart?trackId=${t.id}`} alt=""
                                  loading="lazy" onError={() => failArt(t.id)} />
                                {(() => {
                                  const d = splitArtistTitle(t.artist, t.title)
                                  return <>
                                    {d.artist && <div className="lz-tile-artist">{d.artist}</div>}
                                    <div className="lz-tile-title">{d.title}</div>
                                  </>
                                })()}
                                <div className="lz-tile-foot">
                                  <span className="lz-tile-dur">{fmt(t.durationSec)}</span>
                                  {!cd && <button className="lz-btn lz-req-btn" onClick={() => requestTrack(t)} title="Request this song">Request</button>}
                                </div>
                              </div>
                            ))}
                          </div>
                        </div>
                      )}
                      {plain.length > 0 && (
                        <div className="lz-sect">
                          <div className="lz-sect-label">More songs</div>
                          <div className="lz-results-wrap">
                          <ul className="lz-results-list">
                            {plain.map(t => (
                              <li key={t.id} className="lz-result lz-result-icon">
                                <span className="lz-mini-icon" aria-hidden="true">♪</span>
                                <div className="lz-result-main">
                                  {(() => {
                                    const d = splitArtistTitle(t.artist, t.title)
                                    return <>
                                      {d.artist && <>
                                        <span className="lz-result-artist">{d.artist}</span>
                                        <span className="lz-result-sep">–</span>
                                      </>}
                                      <span className="lz-result-title">{d.title}</span>
                                    </>
                                  })()}
                                </div>
                                <span className="lz-result-dur">{fmt(t.durationSec)}</span>
                                {!cd && <button className="lz-btn lz-req-btn" onClick={() => requestTrack(t)} title="Request this song">Request</button>}
                              </li>
                            ))}
                          </ul>
                          </div>
                        </div>
                      )}
                    </>
                  })()}
          </div>
        </section>
          )}
        </div>
      </div>

      {/* ── Fixed bottom bar: now playing + the next two songs ─────────── */}
      <div className="lz-nowbar">
        <div className="lz-nowbar-controls">
          {speakers.length > 0 && (
            <div className="lz-spwrap">
              <button className={`lz-btn${speakers.some(s => s.casting) ? ' is-live' : ''}`}
                onClick={() => setSpOpen(o => !o)}
                title="Play the music on a speaker in the house">
                🔊 Speakers
              </button>
              {spOpen && (
                <>
                  <div className="lz-sp-backdrop" onMouseDown={() => setSpOpen(false)} />
                  <div className="lz-sppop">
                    <div className="lz-sppop-head">Play on…</div>
                    {speakers.map(s => (
                      <button key={s.id} className={`lz-sprow${s.casting ? ' is-on' : ''}`}
                        disabled={spBusy} onClick={() => castTo(s)}>
                        <span className="lz-sprow-name">{s.name}</span>
                        <span className="lz-sprow-act">{s.casting ? '■ Stop' : '▶ Play'}</span>
                      </button>
                    ))}
                  </div>
                </>
              )}
            </div>
          )}
          {np?.allowNext && np?.trackId != null && (
            <button className="lz-btn" onClick={skip} title="Skip to the next track">
              Next ⏭
            </button>
          )}
        </div>
        {/* On air — the loud card. */}
        <div className="lz-np lz-barcard lz-barcard-now">
          {np?.trackId && artOk
            ? <img className="lz-np-art" src={`/api/albumart?trackId=${np.trackId}`} alt="" onError={() => setArtOk(false)} />
            : <div className="lz-np-art lz-np-art-empty">♪</div>}
          <div className="lz-np-body">
            <div className="lz-np-state">● {stateLabel}</div>
            {(() => {
              const d = splitArtistTitle(np?.artist, np?.title)
              return <>
                {d.artist && <div className="lz-np-artist">{d.artist}</div>}
                <div className="lz-np-title">{np?.trackId ? d.title : '—'}</div>
              </>
            })()}
          </div>
        </div>

        {/* Queue — one dimmed card per song, so it reads as "coming later". */}
        {nextUp.length === 0
          ? <div className="lz-barcard lz-barcard-next lz-barcard-empty">Nothing queued.</div>
          : nextUp.map((r, i) => (
              <div key={`${r.position}-${r.trackId}`}
                className={`lz-barcard lz-barcard-next lz-barcard-n${i + 1}`}>
                <QueueArt trackId={r.trackId} />
                <div className="lz-np-body">
                  <div className="lz-np-state lz-next-label">{i === 0 ? 'Next up' : `In ${i + 1}`}</div>
                  {(() => {
                    const d = splitArtistTitle(r.artist, r.title)
                    return <>
                      {d.artist && <div className="lz-np-artist">{d.artist}</div>}
                      <div className="lz-np-title">{d.title}</div>
                    </>
                  })()}
                </div>
                <span className="lz-nextup-dur">{fmt(r.durationSec)}</span>
              </div>
            ))}
      </div>

      {toast && <div className="lz-toast">{toast}</div>}
      <audio ref={audioRef} preload="none" onPlaying={() => setLive(true)} onPause={() => setLive(false)} onError={() => setLive(false)} />
    </div>
  )
}
