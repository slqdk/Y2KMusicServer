import { useCallback, useEffect, useRef, useState } from 'react'

interface NowPlaying {
  trackId: number | null
  title: string | null
  artist: string | null
  album: string | null
  positionSec: number
  durationSec: number
  playing: boolean
  allowNext: boolean
}
interface StreamInfo { enabled: boolean; bitrate: number; listeners: number }
interface SearchItem { id: number; title: string | null; artist: string | null; album: string | null; durationSec: number }
interface CatState { showSelector: boolean; selected: number[]; categories: { id: number; name: string }[] }

const j = async <T,>(url: string, init?: RequestInit): Promise<T> => {
  const r = await fetch(url, init)
  if (!r.ok) throw new Error(String(r.status))
  return r.json() as Promise<T>
}
const fmt = (s: number) => {
  if (!isFinite(s) || s < 0) return '--:--'
  const t = Math.floor(s); return `${Math.floor(t / 60)}:${String(t % 60).padStart(2, '0')}`
}

export default function App() {
  const [np, setNp] = useState<NowPlaying | null>(null)
  const [stream, setStream] = useState<StreamInfo | null>(null)
  const [cats, setCats] = useState<CatState | null>(null)
  const [q, setQ] = useState('')
  const [results, setResults] = useState<SearchItem[]>([])
  const [name, setName] = useState('')
  const [toast, setToast] = useState<string | null>(null)
  const [artOk, setArtOk] = useState(true)
  const debounce = useRef<number | undefined>(undefined)

  const refresh = useCallback(() => {
    j<NowPlaying>('/api/nowplaying').then(setNp).catch(() => {})
    j<StreamInfo>('/api/stream/info').then(setStream).catch(() => {})
  }, [])

  useEffect(() => {
    refresh()
    j<CatState>('/api/categories').then(setCats).catch(() => {})
    const id = setInterval(refresh, 3000)
    return () => clearInterval(id)
  }, [refresh])

  useEffect(() => { setArtOk(true) }, [np?.trackId])

  useEffect(() => {
    window.clearTimeout(debounce.current)
    if (!q.trim()) { setResults([]); return }
    debounce.current = window.setTimeout(() => {
      j<{ items: SearchItem[] }>(`/api/search?q=${encodeURIComponent(q.trim())}`)
        .then(d => setResults(d.items)).catch(() => setResults([]))
    }, 250)
    return () => window.clearTimeout(debounce.current)
  }, [q])

  const flash = (m: string) => { setToast(m); window.setTimeout(() => setToast(null), 2500) }

  const request = async (t: SearchItem) => {
    try {
      await j('/api/request', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ trackId: t.id, requesterName: name.trim() || null })
      })
      flash(`Requested "${t.title ?? 'track'}" — the DJ will see it.`)
    } catch { flash('Request failed. Try again.') }
  }

  const skip = async () => {
    try { await j('/api/next', { method: 'POST' }); flash('Skip sent.'); setTimeout(refresh, 600) }
    catch { flash('Skip is disabled right now.') }
  }

  const toggleCat = async (id: number) => {
    if (!cats) return
    const next = cats.selected.includes(id) ? cats.selected.filter(x => x !== id) : [...cats.selected, id]
    setCats({ ...cats, selected: next })
    try {
      const r = await j<{ selected: number[] }>('/api/category-select', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ categoryIds: next })
      })
      setCats(c => c ? { ...c, selected: r.selected } : c)
    } catch { /* ignore */ }
  }

  return (
    <div className="listener">
      <header>
        <h1>Y2K Radio</h1>
        <p className="subtitle">Live stream &amp; requests</p>
      </header>

      {/* Now playing */}
      <section className="np-card">
        {np?.trackId && artOk
          ? <img className="np-art" src={`/api/albumart?trackId=${np.trackId}`} alt="" onError={() => setArtOk(false)} />
          : <div className="np-art np-art-empty">♪</div>}
        <div className="np-meta">
          <div className="np-now">{np?.playing ? 'NOW PLAYING' : np?.trackId ? 'PAUSED' : 'OFF AIR'}</div>
          <div className="np-title">{np?.title ?? '—'}</div>
          <div className="np-artist">{np?.artist ?? ''}</div>
          {np?.album && <div className="np-album">{np.album}</div>}
          {np?.trackId != null && np.durationSec > 0 && (
            <div className="np-time">{fmt(np.positionSec)} / {fmt(np.durationSec)}</div>
          )}
        </div>
      </section>

      {/* Player */}
      <section className="player">
        {stream?.enabled ? (
          <>
            <audio controls preload="none" src="/stream?format=mp3" style={{ width: '100%' }} />
            <div className="hint">{stream.bitrate} kbps · {stream.listeners} listening</div>
          </>
        ) : (
          <div className="offline">Stream is currently off air.</div>
        )}
        {np?.allowNext && np.trackId != null && (
          <button className="btn-skip" onClick={skip}>Skip to next →</button>
        )}
      </section>

      {/* Category bar */}
      {cats?.showSelector && cats.categories.length > 0 && (
        <section>
          <h2>Tune the mix</h2>
          <div className="chips">
            {cats.categories.map(c => (
              <button key={c.id}
                className={`chip ${cats.selected.includes(c.id) ? 'chip-on' : ''}`}
                onClick={() => toggleCat(c.id)}>{c.name}</button>
            ))}
          </div>
          <div className="hint">{cats.selected.length === 0 ? 'Following the schedule.' : 'Auto DJ is drawing from your picks.'}</div>
        </section>
      )}

      {/* Search + request */}
      <section>
        <h2>Request a song</h2>
        <input className="search" type="search" value={q} onChange={e => setQ(e.target.value)}
          placeholder="Search title, artist, album" />
        <input className="search" type="text" value={name} onChange={e => setName(e.target.value)}
          placeholder="Your name (optional)" />
        <ul className="results">
          {results.map(t => (
            <li key={t.id}>
              <span className="r-title">{t.title ?? '(untitled)'}</span>
              <span className="r-artist">{t.artist ?? ''}</span>
              <span className="r-dur">{fmt(t.durationSec)}</span>
              <button className="btn-req" onClick={() => request(t)}>Request</button>
            </li>
          ))}
          {q.trim() && results.length === 0 && <li className="r-empty">No matches.</li>}
        </ul>
      </section>

      {toast && <div className="toast">{toast}</div>}
    </div>
  )
}
