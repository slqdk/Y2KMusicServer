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
  const [name, setName] = useState('')

  // The search bar stays locked until the guest has typed at least 3 letters
  // of their name; the unlock settles 500 ms after they stop typing. Dropping
  // below 3 letters locks again immediately.
  const [nameOk, setNameOk] = useState(false)
  useEffect(() => {
    if (name.trim().length < 3) { setNameOk(false); return }
    const t = window.setTimeout(() => setNameOk(true), 500)
    return () => window.clearTimeout(t)
  }, [name])

  // The results panel is hidden entirely until a search/browse has actually
  // been issued (same 500 ms settle as the fetch).
  const [showResults, setShowResults] = useState(false)

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
    if (!nameOk || (!albumView && !term && selPl == null)) {
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

  // The fixed bottom bar shows what's on air plus the next two queued songs.
  const nextTwo = useMemo(() => {
    const idx = npId != null ? plRows.findIndex(p => p.trackId === npId) : -1
    return (idx >= 0 ? plRows.slice(idx + 1) : plRows).slice(0, 2)
  }, [plRows, npId])

  const themeSelect = (cls: string) => (
    <select className={`lz-theme ${cls}`} value={theme} onChange={e => setTheme(e.target.value)} title="Theme" aria-label="Theme">
      {THEMES.map(([v, label]) => <option key={v} value={v}>{label}</option>)}
    </select>
  )

  return (
    <div className={`lz lz-${theme}${cd ? ' lz-has-cd' : ''}`}>
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

      {/* Theme picker, alone in the top-right corner */}
      <div className="lz-topright">{themeSelect('lz-theme-corner')}</div>

      {/* ── Main area: left sidebar + centered results ─────────────────── */}
      <div className="lz-main">

        {/* Left: play controls + name + search + playlists + recent */}
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

        {/* Center/right: top search box + results (hidden until a search) */}
        <div className="lz-rescol">
          <input
            className="lz-input lz-input-search lz-search-top"
            type="search"
            value={q}
            onChange={e => onQueryChange(e.target.value)}
            disabled={!nameOk}
            placeholder={nameOk ? 'Search songs or artists…' : 'Enter your name first (min. 3 letters)…'}
            title={nameOk ? 'Search songs' : 'Type at least 3 letters of your name to unlock the search'}
          />

          {showResults && (
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
                                <div className="lz-tile-title">{t.title ?? '(untitled)'}</div>
                                {t.artist && <div className="lz-tile-artist">{t.artist}</div>}
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
                          <ul className="lz-results-list">
                            {plain.map(t => (
                              <li key={t.id} className="lz-result lz-result-icon">
                                <span className="lz-mini-icon" aria-hidden="true">♪</span>
                                <div className="lz-result-main">
                                  <div className="lz-result-title">{t.title ?? '(untitled)'}</div>
                                  {t.artist && <div className="lz-result-artist">{t.artist}</div>}
                                </div>
                                <span className="lz-result-dur">{fmt(t.durationSec)}</span>
                                {!cd && <button className="lz-btn lz-req-btn" onClick={() => requestTrack(t)} title="Request this song">Request</button>}
                              </li>
                            ))}
                          </ul>
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
          <button className="lz-btn" onClick={skip} disabled={!np?.allowNext || np?.trackId == null} title={np?.allowNext ? 'Skip to the next track' : 'Skip is disabled'}>
            Next ⏭
          </button>
        </div>
        <div className="lz-np">
          {np?.trackId && artOk
            ? <img className="lz-np-art" src={`/api/albumart?trackId=${np.trackId}`} alt="" onError={() => setArtOk(false)} />
            : <div className="lz-np-art lz-np-art-empty">♪</div>}
          <div className="lz-np-body">
            <div className="lz-np-state">● {stateLabel}</div>
            <div className="lz-np-title">{np?.title ?? '—'}</div>
            {np?.artist && <div className="lz-np-artist">{np.artist}</div>}
          </div>
        </div>
        <div className="lz-nextup">
          <div className="lz-field-label">Next up</div>
          {nextTwo.length === 0
            ? <div className="lz-nextup-empty">Nothing queued.</div>
            : nextTwo.map(r => (
                <div key={`${r.position}-${r.trackId}`} className="lz-nextup-row">
                  <span className="lz-nextup-title">{r.title ?? '(untitled)'}</span>
                  {r.artist && <span className="lz-nextup-artist">{r.artist}</span>}
                  <span className="lz-nextup-dur">{fmt(r.durationSec)}</span>
                </div>
              ))}
        </div>
      </div>

      {toast && <div className="lz-toast">{toast}</div>}
      <audio ref={audioRef} preload="none" onPlaying={() => setLive(true)} onPause={() => setLive(false)} onError={() => setLive(false)} />
    </div>
  )
}
